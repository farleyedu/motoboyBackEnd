using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using APIBack.DTOs.Cardapio;
using APIBack.Model.Cardapio;
using APIBack.Payments.Controllers;
using APIBack.Payments.MercadoPago;
using APIBack.Repository.Interface;
using APIBack.Service.Interface;
using Microsoft.Extensions.Logging;

namespace APIBack.Service
{
    public sealed class CardapioPagamentoService : ICardapioPagamentoService
    {
        private static readonly string[] CancelaveisPeloCliente =
        {
            CardapioPedidoStatus.Pendente, CardapioPedidoStatus.AguardandoCodigo, CardapioPedidoStatus.Expirado,
            CardapioPedidoStatus.AguardandoPagamento, CardapioPedidoStatus.AguardandoAceite
        };

        private static readonly string[] AbandonaveisDepoisDePagar =
        {
            CardapioPedidoStatus.Pendente, CardapioPedidoStatus.AguardandoCodigo, CardapioPedidoStatus.Expirado
        };

        private readonly ICardapioPedidoWebRepository _repository;
        private readonly IPixChargeService _pix;
        private readonly ICardPaymentService _cartao;
        private readonly IPagamentoEstornoService _estornos;
        private readonly ILogger<CardapioPagamentoService> _logger;

        public CardapioPagamentoService(ICardapioPedidoWebRepository repository, IPixChargeService pix, ICardPaymentService cartao,
            IPagamentoEstornoService estornos, ILogger<CardapioPagamentoService> logger)
        {
            _repository = repository;
            _pix = pix;
            _cartao = cartao;
            _estornos = estornos;
            _logger = logger;
        }

        // =====================================================================
        // Cliente
        // =====================================================================

        public async Task<PublicPixDto> CriarPixAsync(Guid prePedidoId, string? codigo, CancellationToken ct = default)
        {
            var pedido = await PagavelAsync(prePedidoId, codigo);
            var intents = await _repository.ListarIntentsAsync(prePedidoId);
            if (intents.Any(i => StatusIntent.FamiliaPaga(i.Status)))
                throw new PagamentoException(PagamentoCodigos.JaPago, 409, "Este pedido ja foi pago.");
            if (intents.Any(i => CardapioPagamentoRules.EhChaveCartao(i.IdempotencyKey) && i.Status == StatusIntent.Processando))
                throw PagamentoException.Estado("O pagamento com cartao deste pedido esta em analise. Aguarde a resposta.", StatusIntent.Processando);

            var pixes = intents.Where(i => !CardapioPagamentoRules.EhChaveCartao(i.IdempotencyKey)).ToList();
            var ultimo = pixes.LastOrDefault();
            // Pix em aberto (ou recusado na geracao) e reaproveitado; vencido/cancelado = Pix novo com outra chave.
            var chave = ultimo != null && ultimo.Status is not (StatusIntent.Expirado or StatusIntent.Cancelado)
                ? ultimo.IdempotencyKey
                : CardapioPagamentoRules.ChavePix(prePedidoId, pixes.Count + 1);

            var r = await _pix.CriarAsync(new PixChargeRequest
            {
                IdEstabelecimento = pedido.IdEstabelecimento, Origem = OrigensPagamento.Cardapio, Valor = pedido.Total,
                Descricao = $"Pedido {pedido.Codigo}", IdempotencyKey = chave, PrePedidoId = prePedidoId,
                Pagador = new PagadorInfo { Email = pedido.EmailCliente, Nome = pedido.NomeCliente }
            }, ct);
            await _repository.VincularIntentAsync(prePedidoId, r.IntentId);
            _logger.LogInformation("[cardapio.pag] ev=pix pre={Pre} intent={Intent} reaproveitado={Reaproveitado}", prePedidoId, r.IntentId, r.Reaproveitado);
            return Publico(r);
        }

        public async Task<CardPaymentResult> PagarComCartaoAsync(Guid prePedidoId, PublicCartaoPedidoRequest request, CancellationToken ct = default)
        {
            var pedido = await PagavelAsync(prePedidoId, request.Codigo);
            var intents = await _repository.ListarIntentsAsync(prePedidoId);
            if (intents.Any(i => StatusIntent.FamiliaPaga(i.Status)))
                throw new PagamentoException(PagamentoCodigos.JaPago, 409, "Este pedido ja foi pago.");

            // Trocou de Pix para cartao: o Pix aberto e cancelado antes (se foi pago nesse instante, o pedido ja esta pago).
            foreach (var pix in intents.Where(i => !CardapioPagamentoRules.EhChaveCartao(i.IdempotencyKey) && StatusIntent.Aberto(i.Status)))
            {
                await _pix.CancelarAsync(pix.Id, pedido.IdEstabelecimento, "cliente", null, ct);
            }

            var cartoes = intents.Where(i => CardapioPagamentoRules.EhChaveCartao(i.IdempotencyKey)).ToList();
            var ultimo = cartoes.LastOrDefault();
            // Cartao recusado tenta de novo na mesma intencao (outro token); encerrada = intencao nova.
            var chave = ultimo != null && ultimo.Status is not (StatusIntent.Expirado or StatusIntent.Cancelado)
                ? ultimo.IdempotencyKey
                : CardapioPagamentoRules.ChaveCartao(prePedidoId, cartoes.Count + 1);

            var r = await _cartao.CriarAsync(new CardPaymentRequest
            {
                IdEstabelecimento = pedido.IdEstabelecimento, Origem = OrigensPagamento.Cardapio, Valor = pedido.Total,
                Descricao = $"Pedido {pedido.Codigo}", IdempotencyKey = chave, PrePedidoId = prePedidoId,
                Token = request.Token ?? string.Empty, PaymentMethodId = request.PaymentMethodId ?? string.Empty, IssuerId = request.IssuerId,
                Parcelas = request.Parcelas, Pagador = request.Pagador
            }, ct);
            await _repository.VincularIntentAsync(prePedidoId, r.IntentId);
            _logger.LogInformation("[cardapio.pag] ev=cartao pre={Pre} intent={Intent} status={Status} detalhe={Detalhe}",
                prePedidoId, r.IntentId, r.Status, r.StatusDetail);

            if (r.Status == StatusIntent.Recusado)
                throw new PagamentoException(PagamentoCodigos.CartaoRecusado, 422, "O cartao foi recusado.",
                    new Dictionary<string, object?> { ["statusDetail"] = r.StatusDetail, ["intentId"] = r.IntentId });
            return r;
        }

        public async Task<object> ObterPagamentoPublicoAsync(Guid prePedidoId, string? codigo, CancellationToken ct = default)
        {
            var pedido = await DoClienteAsync(prePedidoId, codigo);
            var intent = pedido.IdIntentPago ?? pedido.IdIntentAtual;
            if (intent == null) return new { status = "sem_pagamento", statusPagamentoPedido = pedido.StatusPagamento, versao = 0 };
            return Publico(await _pix.ObterAsync(intent.Value, pedido.IdEstabelecimento, ct));
        }

        public async Task<CardapioPagamentoPublicoDto?> ResumoAsync(CardapioPedidoPublico pedido)
        {
            if (!pedido.PagamentoOnline) return null;
            var r = await _repository.ObterResumoPagamentoAsync(pedido.Id) ?? new CardapioPagamentoResumo();
            return Mapear(pedido, r);
        }

        public async Task CancelarPeloClienteAsync(Guid prePedidoId, string? codigo, string? motivo, CancellationToken ct = default)
        {
            await DoClienteAsync(prePedidoId, codigo);
            var texto = CardapioConfirmacaoRules.NormalizarMotivo(motivo) ?? "cancelado pelo cliente";
            var decisao = await _repository.CancelarAsync(prePedidoId, texto, CardapioReembolsoMotivo.CancelamentoCliente, CancelaveisPeloCliente);
            if (!decisao.Mudou)
            {
                if (decisao.Status == CardapioPedidoStatus.Cancelado) return;
                if (decisao.Status is CardapioPedidoStatus.Aceitando or CardapioPedidoStatus.Aceito)
                    throw PagamentoException.Estado("O restaurante ja aceitou o pedido. Para cancelar, fale com a loja pelo WhatsApp.", decisao.Status);
                throw PagamentoException.Estado("Este pedido nao pode mais ser cancelado.", decisao.Status);
            }

            _logger.LogInformation("[cardapio.pag] ev=cancelado_cliente pre={Pre} reembolso={Reembolso}", prePedidoId, decisao.ReembolsoId);
            await AposEncerrarAsync(decisao, ct);
        }

        public async Task AposEncerrarAsync(CardapioDecisao decisao, CancellationToken ct = default)
        {
            if (!decisao.Mudou || decisao.Pedido is not { PagamentoOnline: true } pedido) return;

            if (!pedido.Pago)
            {
                // Antes de pagar: so cancela a intencao. Se o Pix for pago mesmo assim, o efeito do pagamento devolve o dinheiro.
                foreach (var intent in (await _repository.ListarIntentsAsync(pedido.Id)).Where(i => StatusIntent.Aberto(i.Status)))
                {
                    try
                    {
                        await _pix.CancelarAsync(intent.Id, pedido.IdEstabelecimento, "sistema", null, ct);
                    }
                    catch (PagamentoException ex)
                    {
                        _logger.LogWarning("[cardapio.pag] ev=cancelar_intencao_falhou pre={Pre} intent={Intent} codigo={Codigo}", pedido.Id, intent.Id, ex.Codigo);
                    }
                }
            }

            if (decisao.ReembolsoId.HasValue) await ProcessarReembolsosAsync(decisao.ReembolsoId, ct);
        }

        // =====================================================================
        // Reembolsos (trabalhador e chamadas diretas)
        // =====================================================================

        public async Task<int> ProcessarReembolsosAsync(Guid? somente = null, CancellationToken ct = default)
        {
            var reservados = await _repository.ReservarReembolsosPendentesAsync(20, somente);
            foreach (var r in reservados)
            {
                try
                {
                    var res = await _estornos.EstornarAsync(new EstornoRequest
                    {
                        IntentId = r.IdIntent, IdEstabelecimento = r.IdEstabelecimento, Valor = r.Valor, Motivo = $"cardapio:{r.Motivo}",
                        UsuarioId = r.DecididoPor ?? r.SolicitadoPor, IdempotencyKey = r.IdempotencyKey
                    }, ct);
                    // So o Mercado Pago confirmando o estorno vira reembolsado; sem resposta (solicitado) tenta de novo depois.
                    var (status, tentar) = res.Status switch
                    {
                        StatusEstorno.Concluido => (CardapioReembolsoStatus.Reembolsado, (TimeSpan?)null),
                        StatusEstorno.Processando => (CardapioReembolsoStatus.EmProcessamento, null),
                        StatusEstorno.Recusado or StatusEstorno.Falhou => (CardapioReembolsoStatus.Falhou, null),
                        _ => (CardapioReembolsoStatus.Pendente, Espera(r.Tentativas))
                    };
                    await _repository.AtualizarReembolsoAsync(r.Id, status, res.EstornoId, null, tentar);
                    _logger.LogInformation("[cardapio.pag] ev=reembolso id={Id} pre={Pre} status={Status} valor={Valor}", r.Id, r.PrePedidoId, status, r.Valor);
                }
                catch (PagamentoException ex) when (ex.Http >= 500)
                {
                    await _repository.AtualizarReembolsoAsync(r.Id, CardapioReembolsoStatus.Pendente, null, ex.Message, Espera(r.Tentativas));
                    _logger.LogWarning("[cardapio.pag] ev=reembolso_adiado id={Id} codigo={Codigo}", r.Id, ex.Codigo);
                }
                catch (PagamentoException ex)
                {
                    await _repository.AtualizarReembolsoAsync(r.Id, CardapioReembolsoStatus.Falhou, null, $"{ex.Codigo}: {ex.Message}", null);
                    _logger.LogError("[cardapio.pag] ev=reembolso_falhou id={Id} pre={Pre} codigo={Codigo}", r.Id, r.PrePedidoId, ex.Codigo);
                }
            }
            return reservados.Count;
        }

        public async Task<int> DevolverAbandonadosAsync(CancellationToken ct = default)
        {
            var ids = await _repository.ListarPagosSemConfirmacaoAsync(CardapioPagamentoRules.PrazoConfirmacaoDepoisDePagar, 20);
            var devolvidos = 0;
            foreach (var id in ids)
            {
                var decisao = await _repository.CancelarAsync(id, "confirmacao pelo WhatsApp nao concluida",
                    CardapioReembolsoMotivo.ConfirmacaoExpirada, AbandonaveisDepoisDePagar);
                if (!decisao.Mudou) continue;
                devolvidos++;
                _logger.LogInformation("[cardapio.pag] ev=abandonado_devolvido pre={Pre} reembolso={Reembolso}", id, decisao.ReembolsoId);
                await AposEncerrarAsync(decisao, ct);
            }
            return devolvidos;
        }

        private static TimeSpan Espera(int tentativas) => TimeSpan.FromMinutes(Math.Min(60, Math.Pow(2, Math.Clamp(tentativas, 0, 6))));

        // =====================================================================
        // Loja
        // =====================================================================

        public async Task<CardapioPagamentoLojaDto> ObterParaLojaAsync(Guid estabelecimentoId, Guid prePedidoId)
        {
            var pedido = await _repository.ObterAsync(estabelecimentoId, prePedidoId) ?? throw NaoEncontrado();
            return new CardapioPagamentoLojaDto
            {
                Online = pedido.PagamentoOnline,
                Pagamento = await ResumoAsync(pedido),
                Fase = NomeDaFase(await FaseAsync(pedido)),
                Reembolsos = (await _repository.ListarReembolsosAsync(estabelecimentoId, prePedidoId)).Select(MapearReembolso).ToList()
            };
        }

        public async Task<CardapioReembolsoDto> SolicitarReembolsoAsync(Guid estabelecimentoId, int usuarioId, Guid prePedidoId, SolicitarReembolsoCardapioRequest request)
        {
            var pedido = await _repository.ObterAsync(estabelecimentoId, prePedidoId) ?? throw NaoEncontrado();
            if (!pedido.Pago)
                throw new DeliveryDomainException(409, "CARDAPIO_SEM_PAGAMENTO_ONLINE", "Este pedido nao tem pagamento online confirmado.");
            if (string.IsNullOrWhiteSpace(request.IdempotencyKey) || request.IdempotencyKey.Length > 80)
                throw new DeliveryDomainException(400, "CARDAPIO_REEMBOLSO_CHAVE", "Informe a chave de idempotencia do pedido de reembolso.");
            if (request.Valor is { } v && (v <= 0 || decimal.Round(v, 2) != v))
                throw new DeliveryDomainException(422, "CARDAPIO_REEMBOLSO_VALOR", "Valor do reembolso invalido.");

            var justificativa = request.Justificativa?.Trim();
            string tipo;
            switch (await FaseAsync(pedido))
            {
                case FaseCancelamento.AntesDoAceite:
                    throw new DeliveryDomainException(409, "CARDAPIO_PEDIDO_STATUS",
                        "Pedido ainda nao aceito: recuse o pedido e o valor volta integralmente para o cliente.");
                case FaseCancelamento.Encerrado:
                    throw new DeliveryDomainException(409, "CARDAPIO_PEDIDO_STATUS", "Este pedido ja foi encerrado.");
                case FaseCancelamento.AceitoAntesDoPreparo:
                    // Antes do preparo o estorno e integral (valor nulo ou o saldo inteiro); o gerente aprova.
                    tipo = CardapioReembolsoTipo.AprovacaoGerente;
                    if (request.Valor.HasValue)
                        throw new DeliveryDomainException(422, "CARDAPIO_REEMBOLSO_INTEGRAL", "Antes do preparo o reembolso e sempre integral.");
                    break;
                default:
                    tipo = CardapioReembolsoTipo.Analise;
                    if (justificativa is not { Length: >= 5 })
                        throw new DeliveryDomainException(400, "CARDAPIO_REEMBOLSO_JUSTIFICATIVA",
                            "Pedido em preparo ou entrega: escreva a justificativa do reembolso (minimo 5 caracteres).");
                    break;
            }

            var chave = $"cardapio:{prePedidoId:N}:loja:{request.IdempotencyKey.Trim()}";
            var r = await _repository.SolicitarReembolsoAsync(estabelecimentoId, prePedidoId, tipo, request.Valor, justificativa, usuarioId, chave)
                ?? throw new DeliveryDomainException(422, "CARDAPIO_REEMBOLSO_ACIMA_DO_PAGO",
                    "O reembolso passa do valor pago (somando os reembolsos ja pedidos).");
            _logger.LogInformation("[cardapio.pag] ev=reembolso_solicitado pre={Pre} tipo={Tipo} valor={Valor} usuario={Usuario}",
                prePedidoId, r.Tipo, r.Valor, usuarioId);
            return MapearReembolso(r);
        }

        public async Task<CardapioReembolsoDto> DecidirReembolsoAsync(Guid estabelecimentoId, int usuarioId, Guid reembolsoId, bool aprovar, string? justificativa)
        {
            var antes = await _repository.ObterReembolsoAsync(estabelecimentoId, reembolsoId) ?? throw NaoEncontrado("Reembolso nao encontrado.");
            if (!aprovar && string.IsNullOrWhiteSpace(justificativa))
                throw new DeliveryDomainException(400, "CARDAPIO_REEMBOLSO_JUSTIFICATIVA", "Explique por que o reembolso foi negado.");
            if (!await _repository.DecidirReembolsoAsync(estabelecimentoId, reembolsoId, aprovar, usuarioId, justificativa?.Trim()))
                throw new DeliveryDomainException(409, "CARDAPIO_REEMBOLSO_DECIDIDO", "Este reembolso ja foi decidido. Atualize a tela.");

            _logger.LogInformation("[cardapio.pag] ev=reembolso_decidido id={Id} pre={Pre} aprovado={Aprovado} usuario={Usuario}",
                reembolsoId, antes.PrePedidoId, aprovar, usuarioId);
            if (aprovar) await ProcessarReembolsosAsync(reembolsoId);
            return MapearReembolso((await _repository.ObterReembolsoAsync(estabelecimentoId, reembolsoId))!);
        }

        // =====================================================================
        // Apoio
        // =====================================================================

        private async Task<FaseCancelamento> FaseAsync(CardapioPedidoPublico pedido)
        {
            var statusReal = pedido.IdPedido.HasValue ? await _repository.ObterStatusPedidoRealAsync(pedido.IdEstabelecimento, pedido.IdPedido.Value) : null;
            return CardapioPagamentoRules.Fase(pedido.Status, pedido.AceitoEm, statusReal, DateTimeOffset.UtcNow);
        }

        private static string NomeDaFase(FaseCancelamento fase) => fase switch
        {
            FaseCancelamento.AntesDoAceite => "antes_do_aceite",
            FaseCancelamento.AceitoAntesDoPreparo => "aceito_antes_do_preparo",
            FaseCancelamento.EmPreparoOuEntrega => "em_preparo_ou_entrega",
            _ => "encerrado"
        };

        /// <summary>Id + codigo do pedido (segundo fator). Id inexistente e codigo errado dao o mesmo 404.</summary>
        private async Task<CardapioPedidoPublico> DoClienteAsync(Guid prePedidoId, string? codigo)
        {
            var pedido = await _repository.ObterAsync(prePedidoId);
            if (pedido == null || string.IsNullOrWhiteSpace(codigo) || !string.Equals(pedido.Codigo, codigo.Trim(), StringComparison.OrdinalIgnoreCase))
                throw PagamentoException.NaoEncontrado("Pedido nao encontrado.");
            return pedido;
        }

        private async Task<CardapioPedidoPublico> PagavelAsync(Guid prePedidoId, string? codigo)
        {
            var pedido = await DoClienteAsync(prePedidoId, codigo);
            if (!pedido.PagamentoOnline)
                throw PagamentoException.Regra(PagamentoCodigos.PedidoNaoPagavel, "Este pedido e pago na entrega ou na retirada.");
            if (pedido.Pago) throw new PagamentoException(PagamentoCodigos.JaPago, 409, "Este pedido ja foi pago.");
            if (!CardapioPagamentoRules.AceitaPagamento(pedido.Status))
                throw PagamentoException.Regra(PagamentoCodigos.PedidoNaoPagavel, "Este pedido nao pode mais ser pago.");
            if (pedido.Total <= 0) throw PagamentoException.Regra(PagamentoCodigos.PedidoNaoPagavel, "Pedido sem valor a pagar.");
            return pedido;
        }

        private static PublicPixDto Publico(PixChargeResult r) => new()
        {
            Status = r.Status, Versao = r.Versao, Valor = r.Valor, Descricao = r.Descricao, CopiaECola = r.CopiaECola, QrCodeBase64 = r.QrCodeBase64,
            ExpiraEm = r.ExpiraEm
        };

        internal static CardapioPagamentoPublicoDto Mapear(CardapioPedidoPublico pedido, CardapioPagamentoResumo r) => new()
        {
            Metodo = r.Metodo ?? pedido.MetodoOnline,
            Status = CardapioPagamentoRules.StatusParaCliente(true, r.StatusIntent, r.StatusReembolso, r.ValorPago, r.ValorEstornado),
            Versao = r.Versao,
            Valor = r.IntentId.HasValue ? r.Valor : pedido.Total,
            ValorEstornado = r.ValorEstornado,
            ExpiraEm = r.StatusIntent is StatusIntent.AguardandoPagamento or StatusIntent.Criado ? r.ExpiraEm : null,
            PagoEm = r.PagoEm,
            StatusDetalhe = r.StatusIntent == StatusIntent.Recusado ? r.StatusDetalhe : null,
            CartaoBandeira = r.CartaoBandeira,
            CartaoFinal = r.CartaoFinal
        };

        private static CardapioReembolsoDto MapearReembolso(CardapioReembolso r) => new()
        {
            Id = r.Id, Tipo = r.Tipo, Motivo = r.Motivo, Valor = r.Valor, Justificativa = r.Justificativa, Status = r.Status,
            SolicitadoPor = r.SolicitadoPor, DecididoPor = r.DecididoPor, DecididoEm = r.DecididoEm, UltimoErro = r.UltimoErro,
            ConcluidoEm = r.ConcluidoEm, CriadoEm = r.CreatedAt
        };

        private static DeliveryDomainException NaoEncontrado(string mensagem = "Pedido do cardapio nao encontrado.") =>
            new(404, "CARDAPIO_PEDIDO_NOT_FOUND", mensagem);
    }
}
