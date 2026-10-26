using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Dapper;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Npgsql;

namespace APIBack.Payments.MercadoPago
{
    public sealed class PagadorInfo
    {
        public string? Email { get; set; }
        public string? Nome { get; set; }
        /// <summary>CPF ou CNPJ.</summary>
        public string? TipoDocumento { get; set; }
        public string? NumeroDocumento { get; set; }
    }

    /// <summary>Campos comuns a toda intencao. O VALOR vem sempre de quem chama no servidor (pedido, cobranca, ciclo), nunca do navegador.</summary>
    public abstract class NovaCobrancaRequest
    {
        public Guid IdEstabelecimento { get; set; }
        public string Origem { get; set; } = OrigensPagamento.Cardapio;
        public decimal Valor { get; set; }
        public string Descricao { get; set; } = string.Empty;
        /// <summary>Chave estavel do chamador (ex.: cardapio:{prePedidoId}, chat:{cobrancaId}). Mesma chave = mesma intencao.</summary>
        public string IdempotencyKey { get; set; } = string.Empty;
        public int? PedidoId { get; set; }
        public Guid? PrePedidoId { get; set; }
        public Guid? ConversaId { get; set; }
        public Guid? ClienteId { get; set; }
        public Guid? OrigemRefId { get; set; }
        public PagadorInfo? Pagador { get; set; }
        public int? CriadoPor { get; set; }
    }

    public sealed class PixChargeRequest : NovaCobrancaRequest
    {
        /// <summary>Null = preferencia da loja (pagamento_preferencia_loja.pix_validade_minutos) ou o padrao do servidor.</summary>
        public int? ValidadeMinutos { get; set; }
        /// <summary>Gera o token opaco da pagina Pix publica (devolvido UMA vez em <see cref="PixChargeResult.TokenPublico"/>).</summary>
        public bool GerarLinkPublico { get; set; }
    }

    public sealed class PixChargeResult
    {
        public Guid IntentId { get; set; }
        public Guid? PagamentoId { get; set; }
        public long? MpPaymentId { get; set; }
        public string Status { get; set; } = string.Empty;
        public string? StatusPagamento { get; set; }
        public int Versao { get; set; }
        public decimal Valor { get; set; }
        public string Descricao { get; set; } = string.Empty;
        public string? CopiaECola { get; set; }
        public string? QrCodeBase64 { get; set; }
        public string? TicketUrl { get; set; }
        public DateTime? ExpiraEm { get; set; }
        public string ExternalReference { get; set; } = string.Empty;
        /// <summary>So na chamada que gerou o link. Depois disso so existe o hash.</summary>
        public string? TokenPublico { get; set; }
        /// <summary>True quando a chamada devolveu um Pix que ja existia (idempotencia).</summary>
        public bool Reaproveitado { get; set; }
    }

    /// <summary>
    /// O UNICO servico Pix: cardapio web, cobranca do chat e (sessao 4) mensalidade/extra chamam este mesmo servico. Cria o Pix na conta
    /// recebedora certa (loja por OAuth ou Zippy), com QR, copia e cola, validade, idempotencia e external_reference proprio.
    /// </summary>
    public interface IPixChargeService
    {
        Task<PixChargeResult> CriarAsync(PixChargeRequest request, CancellationToken ct = default);
        Task<PixChargeResult> ObterAsync(Guid intentId, Guid idEstabelecimento, CancellationToken ct = default);
        /// <summary>Troca o token do link publico (o anterior deixa de abrir). Devolve o novo token.</summary>
        Task<string> GerarLinkPublicoAsync(Guid intentId, Guid idEstabelecimento, CancellationToken ct = default);
        /// <summary>Cancela o Pix aberto no MP e a intencao. Idempotente; 409 PAG_JA_PAGO se o dinheiro ja entrou.</summary>
        Task<PixChargeResult> CancelarAsync(Guid intentId, Guid idEstabelecimento, string ator, int? usuarioId, CancellationToken ct = default);
    }

    public sealed class CardPaymentRequest : NovaCobrancaRequest
    {
        /// <summary>Token do cartao gerado pelo Payment Brick no navegador (o numero do cartao nunca passa pelo servidor).</summary>
        public string Token { get; set; } = string.Empty;
        public string PaymentMethodId { get; set; } = string.Empty;
        public string? IssuerId { get; set; }
        public int Parcelas { get; set; } = 1;
    }

    public sealed class CardPaymentResult
    {
        public Guid IntentId { get; set; }
        public Guid? PagamentoId { get; set; }
        public string Status { get; set; } = string.Empty;
        public string? StatusPagamento { get; set; }
        /// <summary>status_detail do MP (cc_rejected_insufficient_amount...): o front escolhe a mensagem por ele.</summary>
        public string? StatusDetail { get; set; }
        public bool Aprovado { get; set; }
        public int Versao { get; set; }
        public decimal Valor { get; set; }
    }

    public sealed class BrickConfiguracao
    {
        public bool PixAtivo { get; set; }
        public bool CartaoAtivo { get; set; }
        public bool PresencialAtivo { get; set; }
        /// <summary>Public key da conta da loja (nao e segredo; o Brick precisa dela).</summary>
        public string? PublicKey { get; set; }
        public int ParcelasMax { get; set; } = 1;
        public bool ContaConectada { get; set; }
    }

    /// <summary>Base do cartao (Payment Brick). Proibido para origem chat: a cobranca do atendente e so Pix.</summary>
    public interface ICardPaymentService
    {
        Task<CardPaymentResult> CriarAsync(CardPaymentRequest request, CancellationToken ct = default);
        Task<BrickConfiguracao> ConfiguracaoAsync(Guid idEstabelecimento, CancellationToken ct = default);
    }

    public sealed class PreferenciasPagamento
    {
        public bool PixAtivo { get; set; }
        public bool CartaoAtivo { get; set; }
        public bool PresencialAtivo { get; set; } = true;
        public int PixValidadeMinutos { get; set; } = 30;
        public int CartaoParcelasMax { get; set; } = 1;

        public static async Task<PreferenciasPagamento> LerAsync(NpgsqlConnection cx, Guid loja, CancellationToken ct) =>
            await cx.QueryFirstOrDefaultAsync<PreferenciasPagamento>(new CommandDefinition(@"
SELECT pix_ativo, cartao_ativo, presencial_ativo, pix_validade_minutos, cartao_parcelas_max
  FROM pagamento_preferencia_loja WHERE id_estabelecimento = @Loja", new { Loja = loja }, cancellationToken: ct)) ?? new PreferenciasPagamento();
    }

    /// <summary>Partes comuns de Pix e cartao: validacao, credencial, intencao idempotente e tentativa.</summary>
    public sealed class PagamentoCriacaoBase
    {
        private readonly NpgsqlDataSource _db;
        private readonly IMercadoPagoCredencialResolver _credenciais;
        private readonly MercadoPagoOptions _options;

        public PagamentoCriacaoBase(NpgsqlDataSource db, IMercadoPagoCredencialResolver credenciais, IOptions<MercadoPagoOptions> options)
        {
            _db = db;
            _credenciais = credenciais;
            _options = options.Value;
        }

        public NpgsqlDataSource Db => _db;
        public MercadoPagoOptions Options => _options;

        public static void Validar(NovaCobrancaRequest r)
        {
            if (r.IdEstabelecimento == Guid.Empty) throw PagamentoException.Invalida("Loja obrigatoria.", "estabelecimentoId");
            CredencialRegras.ContextoDaOrigem(r.Origem);
            if (r.Valor <= 0 || r.Valor > 999_999.99m || decimal.Round(r.Valor, 2) != r.Valor)
                throw PagamentoException.Regra(PagamentoCodigos.ValorInvalido, "Valor deve ser positivo, com no maximo 2 casas e ate R$ 999.999,99.");
            var descricao = r.Descricao?.Trim() ?? string.Empty;
            if (descricao.Length is < 1 or > 140) throw PagamentoException.Invalida("Descricao obrigatoria (ate 140 caracteres).", "descricao");
            if (string.IsNullOrWhiteSpace(r.IdempotencyKey) || r.IdempotencyKey.Length > 160)
                throw PagamentoException.Invalida("Chave de idempotencia obrigatoria (ate 160 caracteres).", "idempotencyKey");
            r.Descricao = descricao;
        }

        public Task<CredencialRecebedora> CredencialAsync(NovaCobrancaRequest r, CancellationToken ct) =>
            CredencialRegras.ContextoDaOrigem(r.Origem) == ContextoCredencial.Loja
                ? _credenciais.LojaAsync(r.IdEstabelecimento, null, ct)
                : _credenciais.RecebedorZippyAsync(null, ct);

        public Task<CredencialRecebedora> CredencialAsync(PagamentoIntent intent, CancellationToken ct) => _credenciais.ParaIntentAsync(intent, ct);

        /// <summary>Insere a intencao ou devolve a ja existente para a mesma chave (travada). Chave repetida com outro conteudo = 409.</summary>
        public async Task<PagamentoIntent> ObterOuCriarIntentAsync(NpgsqlConnection cx, NpgsqlTransaction tx, NovaCobrancaRequest r, CredencialRecebedora cred, CancellationToken ct)
        {
            CredencialRegras.Exigir(cred, r.Origem, r.IdEstabelecimento);
            var id = Guid.NewGuid();
            var novo = await cx.ExecuteScalarAsync<Guid?>(new CommandDefinition(@"
INSERT INTO pagamento_intent (id, id_estabelecimento, ambiente, origem, conta_tipo, id_conta_loja, id_conta_zippy, pedido_id, pre_pedido_id, conversa_id,
                              cliente_id, origem_ref_id, valor, descricao, external_reference, idempotency_key, criado_por)
VALUES (@Id, @Loja, @Amb, @Origem, @ContaTipo, @ContaLoja, @ContaZippy, @PedidoId, @PrePedidoId, @ConversaId, @ClienteId, @OrigemRefId, @Valor, @Descricao,
        @Ref, @Chave, @CriadoPor)
ON CONFLICT (id_estabelecimento, idempotency_key) DO NOTHING
RETURNING id", new
            {
                Id = id, Loja = r.IdEstabelecimento, Amb = cred.Ambiente, r.Origem, ContaTipo = CredencialRegras.ContaTipo(cred.Contexto),
                ContaLoja = cred.IdContaLoja, ContaZippy = cred.IdContaZippy, r.PedidoId, r.PrePedidoId, r.ConversaId, r.ClienteId, r.OrigemRefId,
                r.Valor, r.Descricao, Ref = PagamentoIntentSql.ExternalReference(cred.Ambiente, r.Origem, id), Chave = r.IdempotencyKey, r.CriadoPor
            }, tx, cancellationToken: ct));

            var intent = await cx.QueryFirstAsync<PagamentoIntent>(new CommandDefinition(
                $"SELECT {PagamentoIntentSql.Colunas} FROM pagamento_intent WHERE id_estabelecimento = @Loja AND idempotency_key = @Chave FOR UPDATE",
                new { Loja = r.IdEstabelecimento, Chave = r.IdempotencyKey }, tx, cancellationToken: ct));

            if (novo.HasValue)
            {
                await PagamentoAuditoria.RegistrarAsync(cx, tx, intent.IdEstabelecimento, "pagamento_intent", intent.Id.ToString(), "criada", r.CriadoPor.HasValue ? "usuario" : "sistema",
                    r.CriadoPor, new { intent.Origem, intent.Valor, intent.PedidoId, intent.PrePedidoId, intent.ConversaId }, ct);
                return intent;
            }

            if (intent.Valor != r.Valor || intent.Origem != r.Origem || intent.PedidoId != r.PedidoId || intent.PrePedidoId != r.PrePedidoId ||
                intent.ConversaId != r.ConversaId || intent.Ambiente != cred.Ambiente)
                throw new PagamentoException(PagamentoCodigos.IdempotenciaConflito, 409, "Esta chave de idempotencia ja foi usada com outros dados.");
            return intent;
        }

        /// <summary>Tentativa 'criando' com a chave dada, ou a ja existente com a mesma chave (repeticao segura no MP).</summary>
        public async Task<(Guid Id, string Status, long? MpPaymentId)> ObterOuCriarTentativaAsync(NpgsqlConnection cx, NpgsqlTransaction tx, Guid intentId,
            string metodo, string chave, CancellationToken ct)
        {
            await cx.ExecuteAsync(new CommandDefinition(@"
INSERT INTO pagamento_tentativa (id, id_intent, numero, metodo, idempotency_key)
SELECT @Id, @Intent, COALESCE(MAX(numero), 0) + 1, @Metodo, @Chave FROM pagamento_tentativa WHERE id_intent = @Intent
ON CONFLICT (idempotency_key) DO NOTHING", new { Id = Guid.NewGuid(), Intent = intentId, Metodo = metodo, Chave = chave }, tx, cancellationToken: ct));
            return await cx.QueryFirstAsync<(Guid, string, long?)>(new CommandDefinition(
                "SELECT id, status, mp_payment_id FROM pagamento_tentativa WHERE idempotency_key = @Chave AND id_intent = @Intent",
                new { Chave = chave, Intent = intentId }, tx, cancellationToken: ct));
        }

        public async Task MarcarTentativaFalhaAsync(Guid tentativaId, string? codigo, string mensagem, CancellationToken ct)
        {
            await using var cx = await _db.OpenConnectionAsync(ct);
            await cx.ExecuteAsync(new CommandDefinition(@"
UPDATE pagamento_tentativa SET status = 'falhou', erro_codigo = @C, erro_mensagem = @M, concluida_em = NOW() WHERE id = @Id AND status = 'criando'",
                new { Id = tentativaId, C = codigo, M = mensagem.Length > 300 ? mensagem[..300] : mensagem }, cancellationToken: ct));
        }

        public MpCriarPagamentoRequest NovoPedidoMp(PagamentoIntent intent, NovaCobrancaRequest r, string metodo)
        {
            var email = r.Pagador?.Email?.Trim();
            var pedido = new MpCriarPagamentoRequest
            {
                TransactionAmount = intent.Valor,
                Description = intent.Descricao,
                ExternalReference = intent.ExternalReference,
                NotificationUrl = string.IsNullOrWhiteSpace(_options.NotificationUrl) ? null : _options.NotificationUrl,
                Payer = new MpPayer
                {
                    Email = EmailValido(email) ? email! : _options.EmailPagadorPadrao,
                    FirstName = string.IsNullOrWhiteSpace(r.Pagador?.Nome) ? null : r.Pagador!.Nome!.Trim(),
                    Identification = string.IsNullOrWhiteSpace(r.Pagador?.NumeroDocumento) ? null : new MpIdentification
                    {
                        Type = string.Equals(r.Pagador!.TipoDocumento, "CNPJ", StringComparison.OrdinalIgnoreCase) ? "CNPJ" : "CPF",
                        Number = new string(r.Pagador.NumeroDocumento!.Where(char.IsDigit).ToArray())
                    }
                },
                // Sem dado pessoal: so ids para conferencia.
                Metadata = new Dictionary<string, string>
                {
                    ["intent_id"] = intent.Id.ToString("N"), ["estabelecimento_id"] = intent.IdEstabelecimento.ToString("N"), ["origem"] = intent.Origem
                }
            };
            if (_options.SplitHabilitado && _options.ComissaoPercentual > 0 && intent.ContaTipo == "loja")
                pedido.ApplicationFee = decimal.Round(intent.Valor * _options.ComissaoPercentual / 100m, 2, MidpointRounding.ToZero);
            return pedido;
        }

        private static bool EmailValido(string? email) =>
            !string.IsNullOrWhiteSpace(email) && email.Length <= 254 && email.IndexOf('@') > 0 && email.IndexOf('@') < email.Length - 3 && !email.Contains(' ');

        public static string DataMp(DateTime utc) =>
            utc.ToUniversalTime().ToString("yyyy-MM-dd'T'HH:mm:ss.fff'+00:00'", CultureInfo.InvariantCulture);
    }

    public sealed class PixChargeService : IPixChargeService
    {
        private readonly PagamentoCriacaoBase _base;
        private readonly IMercadoPagoClient _client;
        private readonly IPagamentoSincronizador _sync;
        private readonly ILogger<PixChargeService> _logger;

        public PixChargeService(PagamentoCriacaoBase @base, IMercadoPagoClient client, IPagamentoSincronizador sync, ILogger<PixChargeService> logger)
        {
            _base = @base;
            _client = client;
            _sync = sync;
            _logger = logger;
        }

        public async Task<PixChargeResult> CriarAsync(PixChargeRequest r, CancellationToken ct = default)
        {
            PagamentoCriacaoBase.Validar(r);
            await using var cx = await _base.Db.OpenConnectionAsync(ct);
            var validade = r.ValidadeMinutos ?? _base.Options.PixValidadePadraoMinutos;
            if (CredencialRegras.ContextoDaOrigem(r.Origem) == ContextoCredencial.Loja)
            {
                var prefs = await PreferenciasPagamento.LerAsync(cx, r.IdEstabelecimento, ct);
                if (!prefs.PixAtivo) throw PagamentoException.Regra(PagamentoCodigos.PixDesativado, "A loja nao aceita Pix online.");
                validade = r.ValidadeMinutos ?? prefs.PixValidadeMinutos;
            }
            if (validade is < 5 or > 1440) throw PagamentoException.Invalida("Validade do Pix entre 5 e 1440 minutos.", "validadeMinutos");

            var cred = await _base.CredencialAsync(r, ct);

            PagamentoIntent intent;
            Guid tentativaId;
            string? token = null;
            await using (var tx = await cx.BeginTransactionAsync(ct))
            {
                intent = await _base.ObterOuCriarIntentAsync(cx, tx, r, cred, ct);
                var ultimo = await PagamentoIntentSql.UltimoPagamentoAsync(cx, tx, intent.Id, ct);

                if (StatusIntent.FamiliaPaga(intent.Status) ||
                    (ultimo is { Metodo: MetodosPagamento.Pix, Status: StatusPagamento.Pendente } && (ultimo.ExpiraEm == null || ultimo.ExpiraEm > DateTime.UtcNow.AddMinutes(1))))
                {
                    if (r.GerarLinkPublico && intent.TokenPublicoHash == null && !StatusIntent.FamiliaPaga(intent.Status))
                        token = await TrocarTokenAsync(cx, tx, intent, ct);
                    await tx.CommitAsync(ct);
                    var reaproveitado = Resultado(intent, ultimo, token);
                    reaproveitado.Reaproveitado = true;
                    return reaproveitado;
                }
                if (intent.Status is StatusIntent.Expirado or StatusIntent.Cancelado)
                    throw PagamentoException.Estado("Esta cobranca ja foi encerrada. Gere outra (nova chave de idempotencia).", intent.Status);
                if (intent.Status == StatusIntent.Processando)
                    throw PagamentoException.Estado("Ha um pagamento com cartao em analise para este pedido.", intent.Status);

                // Tentativa 'criando' sobra de uma chamada sem resposta: repete com a MESMA chave (o MP devolve o mesmo pagamento).
                var pendente = await cx.QueryFirstOrDefaultAsync<(Guid Id, string Chave)?>(new CommandDefinition(@"
SELECT id, idempotency_key FROM pagamento_tentativa WHERE id_intent = @Id AND metodo = 'pix' AND status = 'criando' ORDER BY numero DESC LIMIT 1",
                    new { intent.Id }, tx, cancellationToken: ct));
                if (pendente.HasValue)
                {
                    tentativaId = pendente.Value.Id;
                }
                else
                {
                    var n = await cx.ExecuteScalarAsync<int>(new CommandDefinition(
                        "SELECT COUNT(*) FROM pagamento_tentativa WHERE id_intent = @Id", new { intent.Id }, tx, cancellationToken: ct));
                    tentativaId = (await _base.ObterOuCriarTentativaAsync(cx, tx, intent.Id, MetodosPagamento.Pix, $"{intent.Id:N}:pix:{n + 1}", ct)).Id;
                }
                await tx.CommitAsync(ct);
            }

            var chave = await cx.ExecuteScalarAsync<string>(new CommandDefinition(
                "SELECT idempotency_key FROM pagamento_tentativa WHERE id = @Id", new { Id = tentativaId }, cancellationToken: ct));
            var pedido = _base.NovoPedidoMp(intent, r, MetodosPagamento.Pix);
            pedido.PaymentMethodId = "pix";
            pedido.DateOfExpiration = PagamentoCriacaoBase.DataMp(DateTime.UtcNow.AddMinutes(validade));

            MpPagamento mp;
            try
            {
                mp = await _client.CriarPagamentoAsync(cred, pedido, chave!, ct);
            }
            catch (MercadoPagoApiException ex)
            {
                // Sem resposta: a tentativa fica 'criando' e o conciliador (ou a proxima chamada) recupera pelo external_reference.
                if (!ex.Indisponivel) await _base.MarcarTentativaFalhaAsync(tentativaId, ex.MpErro, ex.Message, ct);
                throw ex.ParaPagamento();
            }

            var motivo = PagamentoSincronizador.ValidarRetrato(intent, cred, mp, _base.Options.ExigirLiveModeDoAmbiente);
            if (motivo != null || string.IsNullOrEmpty(mp.TransactionData?.QrCode))
            {
                await _base.MarcarTentativaFalhaAsync(tentativaId, motivo ?? "sem_qr_code", "Resposta do MP inconsistente.", ct);
                _logger.LogError("[mp.pix] ev=resposta_inconsistente intent={Intent} mp={Mp} motivo={Motivo}", intent.Id, mp.Id, motivo ?? "sem_qr_code");
                throw new PagamentoException(PagamentoCodigos.MercadoPagoRespostaInconsistente, 502, "O Mercado Pago devolveu um Pix que nao confere.");
            }

            await using (var tx = await cx.BeginTransactionAsync(ct))
            {
                intent = (await PagamentoIntentSql.ObterAsync(cx, tx, intent.Id, true, ct))!;
                if (r.GerarLinkPublico) token = await TrocarTokenAsync(cx, tx, intent, ct);
                await _sync.AplicarNaTransacaoAsync(cx, tx, intent, cred, mp, tentativaId, "sistema", ct);
                await tx.CommitAsync(ct);
            }

            _logger.LogInformation("[mp.pix] ev=criado intent={Intent} mp={Mp} origem={Origem} loja={Loja}", intent.Id, mp.Id, intent.Origem, intent.IdEstabelecimento);
            return await ObterInternoAsync(cx, intent.Id, token, ct);
        }

        public async Task<PixChargeResult> ObterAsync(Guid intentId, Guid idEstabelecimento, CancellationToken ct = default)
        {
            await using var cx = await _base.Db.OpenConnectionAsync(ct);
            var r = await ObterInternoAsync(cx, intentId, null, ct);
            var loja = await cx.ExecuteScalarAsync<Guid>(new CommandDefinition("SELECT id_estabelecimento FROM pagamento_intent WHERE id = @Id", new { Id = intentId }, cancellationToken: ct));
            if (loja != idEstabelecimento) throw PagamentoException.NaoEncontrado();
            return r;
        }

        public async Task<string> GerarLinkPublicoAsync(Guid intentId, Guid idEstabelecimento, CancellationToken ct = default)
        {
            await using var cx = await _base.Db.OpenConnectionAsync(ct);
            await using var tx = await cx.BeginTransactionAsync(ct);
            var intent = await PagamentoIntentSql.ObterAsync(cx, tx, intentId, true, ct);
            if (intent == null || intent.IdEstabelecimento != idEstabelecimento) throw PagamentoException.NaoEncontrado();
            if (!StatusIntent.Aberto(intent.Status)) throw PagamentoException.Estado("So cobranca em aberto tem link de pagamento.", intent.Status);
            var token = await TrocarTokenAsync(cx, tx, intent, ct);
            await tx.CommitAsync(ct);
            return token;
        }

        public async Task<PixChargeResult> CancelarAsync(Guid intentId, Guid idEstabelecimento, string ator, int? usuarioId, CancellationToken ct = default)
        {
            await using var cx = await _base.Db.OpenConnectionAsync(ct);
            PagamentoIntent? intent;
            PagamentoRegistro? ultimo;
            await using (var tx = await cx.BeginTransactionAsync(ct))
            {
                intent = await PagamentoIntentSql.ObterAsync(cx, tx, intentId, true, ct);
                if (intent == null || intent.IdEstabelecimento != idEstabelecimento) throw PagamentoException.NaoEncontrado();
                if (StatusIntent.FamiliaPaga(intent.Status)) throw new PagamentoException(PagamentoCodigos.JaPago, 409, "Este pagamento ja foi feito; use o estorno.");
                ultimo = await PagamentoIntentSql.UltimoPagamentoAsync(cx, tx, intentId, ct);
                if (!StatusIntent.Aberto(intent.Status) && intent.Status != StatusIntent.Recusado)
                {
                    await tx.CommitAsync(ct);
                    return Resultado(intent, ultimo, null);
                }
                if (ultimo == null || !StatusPagamento.Aberto(ultimo.Status))
                {
                    await _sync.MarcarIntentAsync(cx, tx, intent, StatusIntent.Cancelado, ator, usuarioId, ct);
                    await tx.CommitAsync(ct);
                    return Resultado(intent, ultimo, null);
                }
                await tx.CommitAsync(ct);
            }

            var cred = await _base.CredencialAsync(intent, ct);
            MpPagamento mp;
            try
            {
                mp = await _client.CancelarPagamentoAsync(cred, ultimo.MpPaymentId, ct);
            }
            catch (MercadoPagoApiException ex) when (!ex.Indisponivel)
            {
                // Ex.: o cliente pagou no mesmo instante. O retrato atual decide.
                mp = await _client.ObterPagamentoAsync(cred, ultimo.MpPaymentId, ct);
            }
            catch (MercadoPagoApiException ex)
            {
                throw ex.ParaPagamento();
            }

            await _sync.AplicarAsync(cred, mp, null, ator, ct);
            var final = await ObterInternoAsync(cx, intentId, null, ct);
            if (StatusIntent.FamiliaPaga(final.Status)) throw new PagamentoException(PagamentoCodigos.JaPago, 409, "Este pagamento ja foi feito; use o estorno.");
            return final;
        }

        private static async Task<string> TrocarTokenAsync(NpgsqlConnection cx, NpgsqlTransaction tx, PagamentoIntent intent, CancellationToken ct)
        {
            var token = TokenPublico.Gerar();
            intent.TokenPublicoHash = TokenPublico.Hash(token);
            await cx.ExecuteAsync(new CommandDefinition("UPDATE pagamento_intent SET token_publico_hash = @H, updated_at = NOW() WHERE id = @Id",
                new { H = intent.TokenPublicoHash, intent.Id }, tx, cancellationToken: ct));
            return token;
        }

        private static async Task<PixChargeResult> ObterInternoAsync(NpgsqlConnection cx, Guid intentId, string? token, CancellationToken ct)
        {
            var intent = await PagamentoIntentSql.ObterAsync(cx, null, intentId, false, ct) ?? throw PagamentoException.NaoEncontrado();
            var ultimo = await PagamentoIntentSql.UltimoPagamentoAsync(cx, null, intentId, ct);
            return Resultado(intent, ultimo, token);
        }

        internal static PixChargeResult Resultado(PagamentoIntent intent, PagamentoRegistro? pag, string? token)
        {
            var pix = pag?.Metodo == MetodosPagamento.Pix ? pag : null;
            // QR so enquanto pode ser pago: expirado/cancelado nao mostra codigo velho.
            var mostraQr = pix != null && pix.Status == StatusPagamento.Pendente;
            return new PixChargeResult
            {
                IntentId = intent.Id, PagamentoId = pag?.Id, MpPaymentId = pag?.MpPaymentId, Status = intent.Status, StatusPagamento = pag?.Status,
                Versao = intent.Versao, Valor = intent.Valor, Descricao = intent.Descricao, ExternalReference = intent.ExternalReference,
                CopiaECola = mostraQr ? pix!.PixCopiaCola : null, QrCodeBase64 = mostraQr ? pix!.PixQrCodeBase64 : null,
                TicketUrl = mostraQr ? pix!.PixTicketUrl : null, ExpiraEm = pix?.ExpiraEm ?? intent.ExpiraEm, TokenPublico = token
            };
        }
    }

    public sealed class CardPaymentService : ICardPaymentService
    {
        private readonly PagamentoCriacaoBase _base;
        private readonly IMercadoPagoClient _client;
        private readonly IPagamentoSincronizador _sync;
        private readonly IMercadoPagoCredencialResolver _credenciais;
        private readonly ILogger<CardPaymentService> _logger;

        public CardPaymentService(PagamentoCriacaoBase @base, IMercadoPagoClient client, IPagamentoSincronizador sync, IMercadoPagoCredencialResolver credenciais,
            ILogger<CardPaymentService> logger)
        {
            _base = @base;
            _client = client;
            _sync = sync;
            _credenciais = credenciais;
            _logger = logger;
        }

        public async Task<BrickConfiguracao> ConfiguracaoAsync(Guid idEstabelecimento, CancellationToken ct = default)
        {
            await using var cx = await _base.Db.OpenConnectionAsync(ct);
            var prefs = await PreferenciasPagamento.LerAsync(cx, idEstabelecimento, ct);
            var conta = await cx.QueryFirstOrDefaultAsync<(string? PublicKey, string Estado)?>(new CommandDefinition(@"
SELECT public_key, estado FROM mp_conta_loja WHERE id_estabelecimento = @Loja AND ambiente = @Amb AND estado IN ('ativa', 'expirada', 'erro')",
                new { Loja = idEstabelecimento, Amb = _credenciais.AmbienteAtivo }, cancellationToken: ct));
            var conectada = conta is { Estado: "ativa" };
            return new BrickConfiguracao
            {
                ContaConectada = conectada, PixAtivo = conectada && prefs.PixAtivo, CartaoAtivo = conectada && prefs.CartaoAtivo && !string.IsNullOrEmpty(conta?.PublicKey),
                PresencialAtivo = prefs.PresencialAtivo, PublicKey = conectada ? conta?.PublicKey : null, ParcelasMax = prefs.CartaoParcelasMax
            };
        }

        public async Task<CardPaymentResult> CriarAsync(CardPaymentRequest r, CancellationToken ct = default)
        {
            if (r.Origem == OrigensPagamento.Chat)
                throw PagamentoException.Regra(PagamentoCodigos.CartaoNaoPermitidoNoChat, "A cobranca pelo chat e so por Pix.");
            PagamentoCriacaoBase.Validar(r);
            if (string.IsNullOrWhiteSpace(r.Token) || r.Token.Length > 200) throw PagamentoException.Invalida("Token do cartao obrigatorio.", "token");
            if (string.IsNullOrWhiteSpace(r.PaymentMethodId) || r.PaymentMethodId.Length > 40) throw PagamentoException.Invalida("Bandeira obrigatoria.", "paymentMethodId");
            if (string.IsNullOrWhiteSpace(r.Pagador?.Email)) throw PagamentoException.Invalida("E-mail do pagador obrigatorio no cartao.", "pagador.email");

            await using var cx = await _base.Db.OpenConnectionAsync(ct);
            if (CredencialRegras.ContextoDaOrigem(r.Origem) == ContextoCredencial.Loja)
            {
                var prefs = await PreferenciasPagamento.LerAsync(cx, r.IdEstabelecimento, ct);
                if (!prefs.CartaoAtivo) throw PagamentoException.Regra(PagamentoCodigos.CartaoDesativado, "A loja nao aceita cartao online.");
                if (r.Parcelas < 1 || r.Parcelas > prefs.CartaoParcelasMax) throw PagamentoException.Invalida($"Parcelas entre 1 e {prefs.CartaoParcelasMax}.", "parcelas");
            }
            else if (r.Parcelas is < 1 or > 12) throw PagamentoException.Invalida("Parcelas entre 1 e 12.", "parcelas");

            var cred = await _base.CredencialAsync(r, ct);
            PagamentoIntent intent;
            (Guid Id, string Status, long? MpPaymentId) tentativa;
            // Mesmo token enviado duas vezes (duplo clique) = mesma tentativa = um so pagamento no MP.
            var chave = $"{{0:N}}:cartao:{Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(r.Token)))[..24].ToLowerInvariant()}";
            await using (var tx = await cx.BeginTransactionAsync(ct))
            {
                intent = await _base.ObterOuCriarIntentAsync(cx, tx, r, cred, ct);
                if (StatusIntent.FamiliaPaga(intent.Status)) throw new PagamentoException(PagamentoCodigos.JaPago, 409, "Este pedido ja foi pago.");
                if (intent.Status is StatusIntent.Expirado or StatusIntent.Cancelado) throw PagamentoException.Estado("Esta cobranca ja foi encerrada.", intent.Status);
                if (intent.Status == StatusIntent.Processando) throw PagamentoException.Estado("Ha um pagamento com cartao em analise para este pedido.", intent.Status);
                var ultimo = await PagamentoIntentSql.UltimoPagamentoAsync(cx, tx, intent.Id, ct);
                if (ultimo is { Metodo: MetodosPagamento.Pix, Status: StatusPagamento.Pendente })
                    throw PagamentoException.Estado("Ha um Pix em aberto para este pedido; cancele-o antes de pagar com cartao.", intent.Status);
                tentativa = await _base.ObterOuCriarTentativaAsync(cx, tx, intent.Id, MetodosPagamento.Cartao, string.Format(chave, intent.Id), ct);
                await tx.CommitAsync(ct);
            }

            if (tentativa.Status == "criada" && tentativa.MpPaymentId.HasValue)
            {
                await _sync.SincronizarAsync(cred, tentativa.MpPaymentId.Value, "sistema", ct);
                return await ResultadoAsync(cx, intent.Id, ct);
            }
            if (tentativa.Status == "falhou") throw PagamentoException.Estado("Este cartao ja foi tentado; gere um novo token.", intent.Status);

            var pedido = _base.NovoPedidoMp(intent, r, MetodosPagamento.Cartao);
            pedido.PaymentMethodId = r.PaymentMethodId.Trim();
            pedido.Token = r.Token.Trim();
            pedido.Installments = r.Parcelas;
            pedido.IssuerId = string.IsNullOrWhiteSpace(r.IssuerId) ? null : r.IssuerId.Trim();
            pedido.StatementDescriptor = _base.Options.StatementDescriptor;

            MpPagamento mp;
            try
            {
                mp = await _client.CriarPagamentoAsync(cred, pedido, string.Format(chave, intent.Id), ct);
            }
            catch (MercadoPagoApiException ex)
            {
                if (!ex.Indisponivel) await _base.MarcarTentativaFalhaAsync(tentativa.Id, ex.MpErro, ex.Message, ct);
                throw ex.ParaPagamento();
            }

            await using (var tx = await cx.BeginTransactionAsync(ct))
            {
                intent = (await PagamentoIntentSql.ObterAsync(cx, tx, intent.Id, true, ct))!;
                var s = await _sync.AplicarNaTransacaoAsync(cx, tx, intent, cred, mp, tentativa.Id, "sistema", ct);
                await tx.CommitAsync(ct);
                if (s.Resultado == ResultadoSincronizacao.Rejeitado)
                {
                    _logger.LogError("[mp.cartao] ev=resposta_inconsistente intent={Intent} mp={Mp} motivo={Motivo}", intent.Id, mp.Id, s.Motivo);
                    throw new PagamentoException(PagamentoCodigos.MercadoPagoRespostaInconsistente, 502, "O Mercado Pago devolveu um pagamento que nao confere.");
                }
            }

            var resultado = await ResultadoAsync(cx, intent.Id, ct);
            resultado.StatusDetail = mp.StatusDetail;
            return resultado;
        }

        private static async Task<CardPaymentResult> ResultadoAsync(NpgsqlConnection cx, Guid intentId, CancellationToken ct)
        {
            var intent = (await PagamentoIntentSql.ObterAsync(cx, null, intentId, false, ct))!;
            var pag = await PagamentoIntentSql.UltimoPagamentoAsync(cx, null, intentId, ct);
            return new CardPaymentResult
            {
                IntentId = intent.Id, PagamentoId = pag?.Id, Status = intent.Status, StatusPagamento = pag?.Status, StatusDetail = pag?.MpStatusDetail,
                Aprovado = StatusIntent.FamiliaPaga(intent.Status), Versao = intent.Versao, Valor = intent.Valor
            };
        }
    }
}
