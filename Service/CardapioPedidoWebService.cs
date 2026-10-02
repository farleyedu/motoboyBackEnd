using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using APIBack.Automation.Interfaces;
using APIBack.DTOs.Cardapio;
using APIBack.DTOs.Delivery;
using APIBack.Model.Cardapio;
using APIBack.Model.Delivery;
using APIBack.Repository.Interface;
using APIBack.Service.Interface;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging;

namespace APIBack.Service
{
    public sealed class CardapioPedidoWebService : ICardapioPedidoWebService
    {
        private const int LimiteFila = 50;
        private const string TipoEntrega = "entrega";
        private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

        private readonly ICardapioPedidoWebRepository _repository;
        private readonly ICardapioRepository _cardapio;
        private readonly IConversationRepository _conversas;
        private readonly IClienteRepository _clientes;
        private readonly IWabaPhoneRepository _waba;
        private readonly ITrackingNoticeSender _sender;
        private readonly IPedidoCoreService _core;
        private readonly IMemoryCache _cache;
        private readonly ILogger<CardapioPedidoWebService> _logger;

        public CardapioPedidoWebService(
            ICardapioPedidoWebRepository repository,
            ICardapioRepository cardapio,
            IConversationRepository conversas,
            IClienteRepository clientes,
            IWabaPhoneRepository waba,
            ITrackingNoticeSender sender,
            IPedidoCoreService core,
            IMemoryCache cache,
            ILogger<CardapioPedidoWebService> logger)
        {
            _repository = repository;
            _cardapio = cardapio;
            _conversas = conversas;
            _clientes = clientes;
            _waba = waba;
            _sender = sender;
            _core = core;
            _cache = cache;
            _logger = logger;
        }

        // =====================================================================
        // Confirmacao pelo cliente
        // =====================================================================

        public async Task<CardapioConfirmacaoDto> IniciarConfirmacaoAsync(CardapioPedidoPublico pedido, string nomeLoja)
        {
            await _repository.ExpirarCodigosVencidosAsync(pedido.IdEstabelecimento);

            var variantes = CardapioConfirmacaoRules.VariantesTelefone(pedido.TelefoneCliente);
            var conversa = await _repository.ObterConversaPorTelefoneAsync(pedido.IdEstabelecimento, variantes);
            if (conversa is { JanelaAberta: true })
            {
                var texto = CardapioConfirmacaoRules.PedidoRecebido(pedido.NomeCliente, nomeLoja, LinhasDoPedido(pedido), pedido.Total);
                // O envio so e aceito com a janela de 24h aberta: aceitar o envio e a prova de que o numero ja falou com a loja.
                if (await TentarEnviarAsync(conversa.ConversaId, pedido.IdEstabelecimento, texto))
                {
                    var telefone = PhoneKey.ToE164(pedido.TelefoneCliente) ?? pedido.TelefoneCliente;
                    if (await _repository.MarcarAguardandoAceiteAsync(pedido.Id, telefone, conversa.ConversaId))
                    {
                        return new CardapioConfirmacaoDto { Modo = "mensagem_enviada" };
                    }
                }
            }

            return await GerarCodigoAsync(pedido.Id);
        }

        public async Task<CardapioConfirmacaoDto> GerarNovoCodigoAsync(Guid id)
        {
            var pedido = await _repository.ObterAsync(id)
                ?? throw new KeyNotFoundException("Pedido nao encontrado.");
            await _repository.ExpirarCodigosVencidosAsync(pedido.IdEstabelecimento);
            return await GerarCodigoAsync(id);
        }

        private async Task<CardapioConfirmacaoDto> GerarCodigoAsync(Guid pedidoId)
        {
            // Dois pre-pedidos da mesma loja podem sortear o mesmo numero: o indice unico recusa e tentamos outro.
            for (var tentativa = 0; tentativa < 10; tentativa++)
            {
                var (atualizado, colisao) = await _repository.DefinirCodigoAsync(
                    pedidoId, CardapioConfirmacaoRules.GerarCodigo(), CardapioConfirmacaoRules.CodigoValidade);
                if (atualizado != null) return await ConfirmacaoPorCodigoAsync(atualizado);
                if (!colisao) throw new InvalidOperationException("Este pedido nao aceita um novo codigo.");
            }

            throw new InvalidOperationException("Nao foi possivel gerar o codigo agora. Tente novamente.");
        }

        private async Task<CardapioConfirmacaoDto> ConfirmacaoPorCodigoAsync(CardapioPedidoPublico pedido)
        {
            var telefoneDaLoja = await _waba.ObterDisplayPhonePorEstabelecimentoAsync(pedido.IdEstabelecimento);
            return new CardapioConfirmacaoDto
            {
                Modo = CardapioCanalConfirmacao.Codigo,
                Codigo = pedido.CodigoConfirmacao,
                ExpiraEm = pedido.CodigoExpiraEm,
                WhatsappUrl = pedido.CodigoConfirmacao == null
                    ? null
                    : CardapioConfirmacaoRules.LinkWhatsapp(telefoneDaLoja, pedido.CodigoConfirmacao)
            };
        }

        public async Task<CardapioPedidoPublicoStatusDto?> ObterStatusAsync(Guid id)
        {
            var pedido = await _repository.ObterAsync(id);
            if (pedido == null) return null;

            var vencido = pedido.Status == CardapioPedidoStatus.AguardandoCodigo
                && (!pedido.CodigoExpiraEm.HasValue || pedido.CodigoExpiraEm.Value <= DateTimeOffset.UtcNow);
            var status = vencido ? CardapioPedidoStatus.Expirado : pedido.Status;

            return new CardapioPedidoPublicoStatusDto
            {
                Id = pedido.Id,
                Codigo = pedido.Codigo,
                Status = status,
                TipoEntrega = pedido.TipoEntrega,
                MotivoRecusa = status == CardapioPedidoStatus.Recusado ? pedido.MotivoRecusa : null,
                NumeroPedido = pedido.IdPedido,
                Confirmacao = status == CardapioPedidoStatus.AguardandoCodigo ? await ConfirmacaoPorCodigoAsync(pedido) : null
            };
        }

        // =====================================================================
        // Mensagem recebida pelo webhook
        // =====================================================================

        public async Task<bool> TentarConfirmarPorMensagemAsync(Guid conversaId, string? texto)
        {
            var extraido = CardapioConfirmacaoRules.ExtrairCodigo(texto);
            if (extraido == null) return false;

            var conversa = await _conversas.ObterPorIdAsync(conversaId);
            if (conversa == null) return false;

            var estabelecimentoId = conversa.IdEstabelecimento;
            var telefone = await _clientes.ObterTelefoneClienteAsync(conversa.IdCliente, estabelecimentoId);
            if (string.IsNullOrWhiteSpace(telefone)) return false;

            var chave = $"cardapio-codigo:{estabelecimentoId:N}:{PhoneKey.From(telefone) ?? conversaId.ToString("N")}";
            // Chutar codigos de 4 digitos: depois de poucos erros a mensagem segue como conversa normal.
            if (Bloqueado(chave)) return false;

            var telefoneContato = PhoneKey.ToE164(telefone) ?? telefone;
            var pedido = await _repository.ConfirmarPorCodigoAsync(estabelecimentoId, extraido.Value.Codigo, telefoneContato, conversaId);
            if (pedido == null)
            {
                RegistrarFalha(chave);
                // "4821" sozinho pode ser outra coisa (numero da casa); "codigo 4821" e claramente a confirmacao.
                if (!extraido.Value.Explicito) return false;

                await TentarEnviarAsync(conversaId, estabelecimentoId, CardapioConfirmacaoRules.CodigoInvalido());
                return true;
            }

            var nomeLoja = await NomeDaLojaAsync(estabelecimentoId);
            await TentarEnviarAsync(conversaId, estabelecimentoId,
                CardapioConfirmacaoRules.CodigoConfirmado(pedido.NomeCliente, nomeLoja, LinhasDoPedido(pedido), pedido.Total));
            return true;
        }

        private sealed class ContadorDeFalhas
        {
            public int Falhas;
        }

        private bool Bloqueado(string chave) =>
            _cache.TryGetValue(chave, out ContadorDeFalhas? contador)
            && contador is { Falhas: >= CardapioConfirmacaoRules.MaxTentativasErradas };

        private void RegistrarFalha(string chave)
        {
            var contador = _cache.GetOrCreate(chave, entry =>
            {
                entry.AbsoluteExpirationRelativeToNow = CardapioConfirmacaoRules.JanelaTentativas;
                return new ContadorDeFalhas();
            })!;
            Interlocked.Increment(ref contador.Falhas);
        }

        // =====================================================================
        // Fila e decisao do restaurante
        // =====================================================================

        public async Task<IReadOnlyList<CardapioPedidoAguardandoDto>> ListarAguardandoAsync(Guid estabelecimentoId) =>
            (await _repository.ListarAguardandoAceiteAsync(estabelecimentoId, LimiteFila)).Select(MapearAguardando).ToList();

        public async Task<AceitarCardapioPedidoResultDto> AceitarAsync(Guid estabelecimentoId, int actorUserId, Guid id)
        {
            var pedido = await ObterAguardandoAsync(estabelecimentoId, id);

            int? pedidoId = null;
            decimal? totalPedido = null;
            var avisos = new List<string>();
            if (EhEntrega(pedido))
            {
                // O pedido real nasce pelo nucleo, com a origem cardapio_web e o codigo do pedido como origemRef:
                // aceitar de novo (clique duplo, dois atendentes) devolve o mesmo pedido em vez de criar outro.
                var criado = await _core.CreateAsync(estabelecimentoId, actorUserId, MontarPedido(pedido), idempotencyKey: null);
                if (string.Equals(criado.Status, "rascunho", StringComparison.OrdinalIgnoreCase))
                {
                    // A loja pode exigir confirmacao dos pedidos de cliente; aqui o atendente acabou de aceitar.
                    criado = await _core.ConfirmAsync(estabelecimentoId, actorUserId, criado.Id);
                }
                pedidoId = criado.Id;
                totalPedido = criado.Total;
                avisos.AddRange(criado.Avisos);
            }

            var resultado = new AceitarCardapioPedidoResultDto
            {
                Id = pedido.Id,
                PedidoId = pedidoId,
                TotalCliente = pedido.Total,
                TotalPedido = totalPedido,
                Avisos = avisos
            };

            // So quem muda o status de fato avisa o cliente: dois cliques nao mandam duas mensagens.
            if (!await _repository.MarcarAceitoAsync(estabelecimentoId, id, pedidoId)) return resultado;

            if (pedido.IdConversa.HasValue)
            {
                var nomeLoja = await NomeDaLojaAsync(estabelecimentoId);
                resultado.ClienteAvisado = await TentarEnviarAsync(pedido.IdConversa.Value, estabelecimentoId,
                    CardapioConfirmacaoRules.PedidoAceito(pedido.NomeCliente, nomeLoja, pedidoId, EhEntrega(pedido)));
            }
            return resultado;
        }

        public async Task RecusarAsync(Guid estabelecimentoId, Guid id, string? motivo)
        {
            var pedido = await ObterAguardandoAsync(estabelecimentoId, id);
            var motivoLimpo = CardapioConfirmacaoRules.NormalizarMotivo(motivo);

            if (!await _repository.MarcarRecusadoAsync(estabelecimentoId, id, motivoLimpo)) return;

            if (pedido.IdConversa.HasValue)
            {
                var nomeLoja = await NomeDaLojaAsync(estabelecimentoId);
                await TentarEnviarAsync(pedido.IdConversa.Value, estabelecimentoId,
                    CardapioConfirmacaoRules.PedidoRecusado(pedido.NomeCliente, nomeLoja, motivoLimpo));
            }
        }

        private async Task<CardapioPedidoPublico> ObterAguardandoAsync(Guid estabelecimentoId, Guid id)
        {
            var pedido = await _repository.ObterAsync(estabelecimentoId, id)
                ?? throw new DeliveryDomainException(404, "CARDAPIO_PEDIDO_NOT_FOUND", "Pedido do cardapio nao encontrado.");
            if (pedido.Status != CardapioPedidoStatus.AguardandoAceite)
            {
                throw new DeliveryDomainException(409, "CARDAPIO_PEDIDO_STATUS",
                    "Este pedido nao esta mais aguardando aceite. Atualize a lista.");
            }
            return pedido;
        }

        // =====================================================================
        // Montagem dos dados
        // =====================================================================

        private static bool EhEntrega(CardapioPedidoPublico pedido) =>
            string.Equals(pedido.TipoEntrega, TipoEntrega, StringComparison.OrdinalIgnoreCase);

        private sealed class ItensArmazenados
        {
            public List<CardapioPedidoPublicoItemRequest>? Solicitado { get; set; }
            public List<CardapioCotacaoItemDto>? Cotacao { get; set; }
        }

        private static ItensArmazenados LerItens(string? json)
        {
            try
            {
                return JsonSerializer.Deserialize<ItensArmazenados>(string.IsNullOrWhiteSpace(json) ? "{}" : json, Json)
                    ?? new ItensArmazenados();
            }
            catch (JsonException)
            {
                return new ItensArmazenados();
            }
        }

        private static CardapioEnderecoArmazenado? LerEndereco(string? json)
        {
            if (string.IsNullOrWhiteSpace(json)) return null;
            try
            {
                return JsonSerializer.Deserialize<CardapioEnderecoArmazenado>(json, Json);
            }
            catch (JsonException)
            {
                return null;
            }
        }

        private static IEnumerable<string> LinhasDoPedido(CardapioPedidoPublico pedido) =>
            (LerItens(pedido.ItensJson).Cotacao ?? new List<CardapioCotacaoItemDto>())
                .Select(item => CardapioConfirmacaoRules.LinhaDoItem(
                    item.Quantidade, item.ProdutoNome, item.AdicionaisSelecionados.Select(a => a.Nome)));

        private static string? TextoDoEndereco(CardapioEnderecoArmazenado? endereco)
        {
            if (endereco == null || string.IsNullOrWhiteSpace(endereco.Logradouro)) return null;
            var complemento = string.IsNullOrWhiteSpace(endereco.Complemento) ? string.Empty : $" ({endereco.Complemento.Trim()})";
            var bairro = string.IsNullOrWhiteSpace(endereco.Bairro) ? string.Empty : $" – {endereco.Bairro.Trim()}";
            return $"{endereco.Logradouro.Trim()}, {endereco.Numero?.Trim()}{complemento}{bairro}";
        }

        private static CardapioPedidoAguardandoDto MapearAguardando(CardapioPedidoPublico pedido)
        {
            var itens = LerItens(pedido.ItensJson);
            var solicitados = itens.Solicitado ?? new List<CardapioPedidoPublicoItemRequest>();
            var linhas = (itens.Cotacao ?? new List<CardapioCotacaoItemDto>())
                .Select(item => new CardapioPedidoAguardandoItemDto
                {
                    Quantidade = item.Quantidade,
                    Nome = item.ProdutoNome,
                    Adicionais = item.AdicionaisSelecionados.Select(a => a.Nome).ToList(),
                    Observacao = item.Observacao
                })
                .ToList();

            return new CardapioPedidoAguardandoDto
            {
                Id = pedido.Id,
                Codigo = pedido.Codigo,
                ConfirmadoEm = pedido.ConfirmadoEm ?? new DateTimeOffset(DateTime.SpecifyKind(pedido.CreatedAt, DateTimeKind.Utc)),
                NomeCliente = pedido.NomeCliente,
                Telefone = pedido.TelefoneContato ?? pedido.TelefoneCliente,
                TipoEntrega = pedido.TipoEntrega,
                Endereco = EhEntrega(pedido) ? TextoDoEndereco(LerEndereco(pedido.EnderecoEntregaJson)) : null,
                Itens = linhas.Count > 0
                    ? linhas
                    : solicitados.Select(item => new CardapioPedidoAguardandoItemDto { Quantidade = item.Quantidade, Nome = "Item" }).ToList(),
                Observacoes = pedido.Observacoes,
                FormaPagamento = pedido.FormaPagamento,
                Total = pedido.Total
            };
        }

        private static CreatePedidoRequest MontarPedido(CardapioPedidoPublico pedido)
        {
            var endereco = LerEndereco(pedido.EnderecoEntregaJson) ?? new CardapioEnderecoArmazenado();
            var itens = LerItens(pedido.ItensJson).Solicitado ?? new List<CardapioPedidoPublicoItemRequest>();

            var observacoes = pedido.Observacoes;
            if (!string.IsNullOrWhiteSpace(endereco.Referencia))
            {
                observacoes = string.IsNullOrWhiteSpace(observacoes)
                    ? $"Ref.: {endereco.Referencia.Trim()}"
                    : $"{observacoes.Trim()} | Ref.: {endereco.Referencia.Trim()}";
            }
            if (observacoes is { Length: > 500 }) observacoes = observacoes[..500];

            return new CreatePedidoRequest
            {
                NomeCliente = pedido.NomeCliente,
                TelefoneCliente = pedido.TelefoneContato ?? pedido.TelefoneCliente,
                Rua = endereco.Logradouro,
                Numero = endereco.Numero,
                Complemento = endereco.Complemento,
                Bairro = endereco.Bairro,
                Cidade = endereco.Cidade,
                Estado = endereco.Uf,
                Cep = endereco.Cep,
                Latitude = endereco.Latitude,
                Longitude = endereco.Longitude,
                TipoPagamento = pedido.FormaPagamento,
                Observacoes = observacoes,
                Origem = PedidoOrigem.CardapioWeb,
                OrigemRef = pedido.Codigo,
                ConversaId = pedido.IdConversa,
                Itens = itens.Select(item => new PedidoItemRequest
                {
                    ProdutoId = item.ProdutoId,
                    Quantidade = item.Quantidade,
                    Observacao = item.Observacao,
                    AdicionalItemIds = item.AdicionalItemIds
                }).ToList()
            };
        }

        // =====================================================================
        // Envio
        // =====================================================================

        private async Task<string> NomeDaLojaAsync(Guid estabelecimentoId)
        {
            var loja = await _cardapio.ObterEstabelecimentoPublicoAsync(estabelecimentoId, null);
            return string.IsNullOrWhiteSpace(loja?.NomePublico) ? loja?.NomeFantasia ?? "restaurante" : loja.NomePublico!;
        }

        /// <summary>Manda pela conversa do cliente; falha (janela fechada, WhatsApp fora) vira false, nunca excecao.</summary>
        private async Task<bool> TentarEnviarAsync(Guid conversaId, Guid estabelecimentoId, string texto)
        {
            try
            {
                await _sender.SendAsync(conversaId, estabelecimentoId, texto);
                return true;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Mensagem do pedido do cardapio nao enviada (conversa {Conversa}).", conversaId);
                return false;
            }
        }
    }
}
