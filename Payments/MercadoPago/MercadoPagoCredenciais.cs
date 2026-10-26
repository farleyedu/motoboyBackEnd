using System;
using System.Threading;
using System.Threading.Tasks;
using Dapper;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Npgsql;

namespace APIBack.Payments.MercadoPago
{
    /// <summary>De quem e a credencial. Aplicacao so faz OAuth; RecebedorZippy e Loja recebem pagamentos.</summary>
    public enum ContextoCredencial
    {
        Aplicacao,
        RecebedorZippy,
        Loja
    }

    /// <summary>Credencial da APLICACAO MP da Zippy. Nao serve para criar ou consultar pagamentos (o cliente nem aceita este tipo).</summary>
    public sealed class CredencialAplicacao
    {
        public string Ambiente { get; init; } = Ambientes.Sandbox;
        public string ClientId { get; init; } = string.Empty;
        internal string ClientSecret { get; init; } = string.Empty;
        public string? PublicKey { get; init; }
        internal string? WebhookSecret { get; init; }
        public string? RedirectUri { get; init; }

        public bool TemWebhookSecret => !string.IsNullOrEmpty(WebhookSecret);

        public override string ToString() => $"CredencialAplicacao({Ambiente}, client={ClientId})";
    }

    /// <summary>Credencial de uma conta que RECEBE: a da Zippy ou a de uma loja. O token so e lido pelo cliente HTTP.</summary>
    public sealed class CredencialRecebedora
    {
        public ContextoCredencial Contexto { get; init; }
        public string Ambiente { get; init; } = Ambientes.Sandbox;
        public Guid? IdEstabelecimento { get; init; }
        public Guid? IdContaLoja { get; init; }
        public Guid? IdContaZippy { get; init; }
        /// <summary>user_id da conta no MP = collector_id esperado nos pagamentos dela.</summary>
        public long MpUserId { get; init; }
        public string? PublicKey { get; init; }
        internal string AccessToken { get; init; } = string.Empty;

        public override string ToString() => $"CredencialRecebedora({Contexto}, {Ambiente}, mp={MascararId(MpUserId)})";

        public static string MascararId(long id)
        {
            var texto = id.ToString();
            return texto.Length <= 4 ? "****" : new string('*', texto.Length - 4) + texto[^4..];
        }
    }

    /// <summary>Origem do pagamento -> qual conta recebe. Unica fonte da regra (o banco repete no ck_pagamento_intent_conta).</summary>
    public static class CredencialRegras
    {
        public static ContextoCredencial ContextoDaOrigem(string origem) => origem switch
        {
            OrigensPagamento.Cardapio or OrigensPagamento.Chat => ContextoCredencial.Loja,
            OrigensPagamento.Assinatura or OrigensPagamento.CompraExtra => ContextoCredencial.RecebedorZippy,
            _ => throw PagamentoException.Invalida($"Origem de pagamento desconhecida: {origem}.", "origem")
        };

        public static string ContaTipo(ContextoCredencial contexto) => contexto switch
        {
            ContextoCredencial.Loja => "loja",
            ContextoCredencial.RecebedorZippy => "zippy",
            _ => throw new PagamentoException(PagamentoCodigos.CredencialIncompativel, 500, "A aplicacao MP nao recebe pagamentos.")
        };

        /// <summary>Barra o uso do token errado: origem x contexto e, para loja, a loja e a conta exatas da intencao.</summary>
        public static void Exigir(CredencialRecebedora credencial, string origem, Guid idEstabelecimento, Guid? idContaLoja = null, Guid? idContaZippy = null)
        {
            var esperado = ContextoDaOrigem(origem);
            var ok = credencial.Contexto == esperado && esperado switch
            {
                ContextoCredencial.Loja => credencial.IdEstabelecimento == idEstabelecimento &&
                                          (idContaLoja == null || credencial.IdContaLoja == idContaLoja),
                ContextoCredencial.RecebedorZippy => idContaZippy == null || credencial.IdContaZippy == idContaZippy,
                _ => false
            };
            if (!ok)
                throw new PagamentoException(PagamentoCodigos.CredencialIncompativel, 500, "Credencial de pagamento incompativel com a operacao.");
        }
    }

    public interface IMercadoPagoCredencialResolver
    {
        string AmbienteAtivo { get; }
        Task<CredencialAplicacao> AplicacaoAsync(string? ambiente = null, CancellationToken ct = default);
        Task<CredencialRecebedora> RecebedorZippyAsync(string? ambiente = null, CancellationToken ct = default);
        /// <summary>Conta ativa da loja no ambiente (renova o token se estiver para vencer).</summary>
        Task<CredencialRecebedora> LojaAsync(Guid idEstabelecimento, string? ambiente = null, CancellationToken ct = default);
        /// <summary>Exatamente a conta gravada na intencao (mesmo que a loja tenha conectado outra depois).</summary>
        Task<CredencialRecebedora> ParaIntentAsync(PagamentoIntent intent, CancellationToken ct = default);
        /// <summary>Conta recebedora dona do user_id do MP (webhook sem pagamento local). Null quando nao e nossa.</summary>
        Task<CredencialRecebedora?> PorMpUserIdAsync(string ambiente, long mpUserId, CancellationToken ct = default);
    }

    public sealed class MercadoPagoCredencialResolver : IMercadoPagoCredencialResolver
    {
        private static readonly TimeSpan MargemRenovacao = TimeSpan.FromDays(7);
        private readonly NpgsqlDataSource _db;
        private readonly IPagamentoSegredoProtector _protector;
        private readonly MercadoPagoOptions _options;
        private readonly IServiceProvider _services;
        private readonly ILogger<MercadoPagoCredencialResolver> _logger;

        public MercadoPagoCredencialResolver(NpgsqlDataSource db, IPagamentoSegredoProtector protector, IOptions<MercadoPagoOptions> options,
            IServiceProvider services, ILogger<MercadoPagoCredencialResolver> logger)
        {
            _db = db;
            _protector = protector;
            _options = options.Value;
            _services = services;
            _logger = logger;
        }

        public string AmbienteAtivo => _options.AmbienteAtivo;

        public async Task<CredencialAplicacao> AplicacaoAsync(string? ambiente = null, CancellationToken ct = default)
        {
            var amb = ambiente ?? AmbienteAtivo;
            await using var cx = await _db.OpenConnectionAsync(ct);
            var linha = await cx.QueryFirstOrDefaultAsync<LinhaAplicacao>(new CommandDefinition(@"
SELECT id, client_id, client_secret_cifrado, public_key, webhook_secret_cifrado, redirect_uri
  FROM mp_aplicacao WHERE ambiente = @Amb AND ativo", new { Amb = amb }, cancellationToken: ct));
            if (linha != null)
            {
                var secret = _protector.Revelar(linha.ClientSecretCifrado, SegredoContexto.AplicacaoClientSecret(linha.Id))
                    ?? throw PagamentoException.NaoConfigurado("Segredo da aplicacao Mercado Pago nao abre com a chave mestra atual.");
                return new CredencialAplicacao
                {
                    Ambiente = amb, ClientId = linha.ClientId, ClientSecret = secret, PublicKey = linha.PublicKey, RedirectUri = linha.RedirectUri,
                    WebhookSecret = _protector.Revelar(linha.WebhookSecretCifrado, SegredoContexto.AplicacaoWebhookSecret(linha.Id))
                };
            }

            // Sem linha no banco: bootstrap da configuracao, so do ambiente ativo.
            if (amb == AmbienteAtivo && !string.IsNullOrWhiteSpace(_options.BootstrapClientId) && !string.IsNullOrWhiteSpace(_options.BootstrapClientSecret))
            {
                return new CredencialAplicacao
                {
                    Ambiente = amb, ClientId = _options.BootstrapClientId!, ClientSecret = _options.BootstrapClientSecret!,
                    PublicKey = _options.BootstrapPublicKey, WebhookSecret = _options.BootstrapWebhookSecret, RedirectUri = _options.BootstrapRedirectUri
                };
            }

            if (amb == AmbienteAtivo && !string.IsNullOrWhiteSpace(_options.BootstrapWebhookSecret))
                return new CredencialAplicacao { Ambiente = amb, WebhookSecret = _options.BootstrapWebhookSecret };

            throw PagamentoException.NaoConfigurado("Aplicacao Mercado Pago nao configurada para este ambiente.");
        }

        public async Task<CredencialRecebedora> RecebedorZippyAsync(string? ambiente = null, CancellationToken ct = default)
        {
            var amb = ambiente ?? AmbienteAtivo;
            await using var cx = await _db.OpenConnectionAsync(ct);
            var linha = await cx.QueryFirstOrDefaultAsync<LinhaConta>(new CommandDefinition(@"
SELECT id, NULL::uuid AS id_estabelecimento, mp_user_id, access_token_cifrado, NULL::text AS refresh_token_cifrado, public_key,
       NULL::timestamptz AS token_expira_em, estado
  FROM mp_conta_zippy WHERE ambiente = @Amb", new { Amb = amb }, cancellationToken: ct));
            if (linha != null)
            {
                if (linha.Estado != "ativa") throw PagamentoException.NaoConfigurado("Conta recebedora da Zippy inativa.");
                var token = _protector.Revelar(linha.AccessTokenCifrado, SegredoContexto.ZippyAccessToken(linha.Id))
                    ?? throw PagamentoException.NaoConfigurado("Token da conta Zippy nao abre com a chave mestra atual.");
                return new CredencialRecebedora
                {
                    Contexto = ContextoCredencial.RecebedorZippy, Ambiente = amb, IdContaZippy = linha.Id, MpUserId = linha.MpUserId,
                    PublicKey = linha.PublicKey, AccessToken = token
                };
            }

            throw PagamentoException.NaoConfigurado("Conta recebedora da Zippy nao configurada para este ambiente.");
        }

        public async Task<CredencialRecebedora> LojaAsync(Guid idEstabelecimento, string? ambiente = null, CancellationToken ct = default)
        {
            var amb = ambiente ?? AmbienteAtivo;
            await using var cx = await _db.OpenConnectionAsync(ct);
            var linha = await cx.QueryFirstOrDefaultAsync<LinhaConta>(new CommandDefinition(SqlContaLoja + @"
 WHERE id_estabelecimento = @Loja AND ambiente = @Amb AND estado IN ('ativa', 'expirada', 'erro')", new { Loja = idEstabelecimento, Amb = amb }, cancellationToken: ct));
            if (linha == null)
                throw PagamentoException.Regra(PagamentoCodigos.ContaLojaNaoConectada, "A loja ainda nao conectou a conta Mercado Pago.");
            return await AbrirContaLojaAsync(linha, amb, ct);
        }

        public async Task<CredencialRecebedora> ParaIntentAsync(PagamentoIntent intent, CancellationToken ct = default)
        {
            var contexto = CredencialRegras.ContextoDaOrigem(intent.Origem);
            if (CredencialRegras.ContaTipo(contexto) != intent.ContaTipo)
                throw new PagamentoException(PagamentoCodigos.CredencialIncompativel, 500, "Intencao com conta recebedora incoerente.");

            CredencialRecebedora credencial;
            if (contexto == ContextoCredencial.RecebedorZippy)
            {
                credencial = await RecebedorZippyAsync(intent.Ambiente, ct);
            }
            else
            {
                await using var cx = await _db.OpenConnectionAsync(ct);
                var linha = await cx.QueryFirstOrDefaultAsync<LinhaConta>(new CommandDefinition(SqlContaLoja + " WHERE id = @Id",
                    new { Id = intent.IdContaLoja }, cancellationToken: ct));
                if (linha == null || linha.AccessTokenCifrado == null)
                    throw PagamentoException.Regra(PagamentoCodigos.ContaLojaReautorizar, "A conta Mercado Pago que recebeu este pagamento foi desconectada.");
                credencial = await AbrirContaLojaAsync(linha, intent.Ambiente, ct);
            }

            CredencialRegras.Exigir(credencial, intent.Origem, intent.IdEstabelecimento, intent.IdContaLoja, intent.IdContaZippy);
            return credencial;
        }

        public async Task<CredencialRecebedora?> PorMpUserIdAsync(string ambiente, long mpUserId, CancellationToken ct = default)
        {
            await using var cx = await _db.OpenConnectionAsync(ct);
            var zippy = await cx.ExecuteScalarAsync<long?>(new CommandDefinition(
                "SELECT mp_user_id FROM mp_conta_zippy WHERE ambiente = @Amb AND mp_user_id = @U", new { Amb = ambiente, U = mpUserId }, cancellationToken: ct));
            if (zippy != null) return await RecebedorZippyAsync(ambiente, ct);

            var linha = await cx.QueryFirstOrDefaultAsync<LinhaConta>(new CommandDefinition(SqlContaLoja + @"
 WHERE ambiente = @Amb AND mp_user_id = @U AND access_token_cifrado IS NOT NULL ORDER BY conectado_em DESC LIMIT 1",
                new { Amb = ambiente, U = mpUserId }, cancellationToken: ct));
            return linha == null ? null : await AbrirContaLojaAsync(linha, ambiente, ct);
        }

        private const string SqlContaLoja = @"
SELECT id, id_estabelecimento, mp_user_id, access_token_cifrado, refresh_token_cifrado, public_key, token_expira_em, estado
  FROM mp_conta_loja";

        private async Task<CredencialRecebedora> AbrirContaLojaAsync(LinhaConta linha, string ambiente, CancellationToken ct)
        {
            if (linha.Estado is "revogada" or "desconectada")
                throw PagamentoException.Regra(PagamentoCodigos.ContaLojaReautorizar, "A conta Mercado Pago da loja precisa ser conectada de novo.");

            var token = _protector.Revelar(linha.AccessTokenCifrado, SegredoContexto.LojaAccessToken(linha.Id));
            if (token == null)
                throw PagamentoException.Regra(PagamentoCodigos.ContaLojaReautorizar, "A conta Mercado Pago da loja precisa ser conectada de novo.");

            if (linha.TokenExpiraEm is { } expira && expira - DateTime.UtcNow < MargemRenovacao)
            {
                var renovador = _services.GetService(typeof(IContaLojaTokenRenovador)) as IContaLojaTokenRenovador;
                var novo = renovador == null ? null : await renovador.RenovarAsync(linha.Id, ct);
                if (novo != null) token = novo;
                else if (expira <= DateTime.UtcNow)
                    throw PagamentoException.Regra(PagamentoCodigos.ContaLojaReautorizar, "O acesso a conta Mercado Pago da loja venceu. Conecte de novo.");
                else _logger.LogWarning("[mp.cred] ev=renovacao_falhou conta={Conta}", linha.Id);
            }

            return new CredencialRecebedora
            {
                Contexto = ContextoCredencial.Loja, Ambiente = ambiente, IdEstabelecimento = linha.IdEstabelecimento, IdContaLoja = linha.Id,
                MpUserId = linha.MpUserId, PublicKey = linha.PublicKey, AccessToken = token
            };
        }

        private sealed class LinhaAplicacao
        {
            public Guid Id { get; set; }
            public string ClientId { get; set; } = string.Empty;
            public string ClientSecretCifrado { get; set; } = string.Empty;
            public string? PublicKey { get; set; }
            public string? WebhookSecretCifrado { get; set; }
            public string? RedirectUri { get; set; }
        }

        private sealed class LinhaConta
        {
            public Guid Id { get; set; }
            public Guid? IdEstabelecimento { get; set; }
            public long MpUserId { get; set; }
            public string? AccessTokenCifrado { get; set; }
            public string? RefreshTokenCifrado { get; set; }
            public string? PublicKey { get; set; }
            public DateTime? TokenExpiraEm { get; set; }
            public string Estado { get; set; } = string.Empty;
        }
    }

    /// <summary>Renova o token OAuth de uma loja (refresh_token gira a cada uso: uma renovacao por vez, com a linha travada).</summary>
    public interface IContaLojaTokenRenovador
    {
        /// <summary>Novo access token, ou null se a renovacao falhou (a conta fica 'expirada' ou 'revogada').</summary>
        Task<string?> RenovarAsync(Guid idContaLoja, CancellationToken ct = default);
    }

    public sealed class ContaLojaTokenRenovador : IContaLojaTokenRenovador
    {
        private readonly NpgsqlDataSource _db;
        private readonly IPagamentoSegredoProtector _protector;
        private readonly IMercadoPagoClient _client;
        private readonly Lazy<IMercadoPagoCredencialResolver> _resolver;
        private readonly ILogger<ContaLojaTokenRenovador> _logger;

        public ContaLojaTokenRenovador(NpgsqlDataSource db, IPagamentoSegredoProtector protector, IMercadoPagoClient client,
            IServiceProvider services, ILogger<ContaLojaTokenRenovador> logger)
        {
            _db = db;
            _protector = protector;
            _client = client;
            _resolver = new Lazy<IMercadoPagoCredencialResolver>(() => (IMercadoPagoCredencialResolver)services.GetService(typeof(IMercadoPagoCredencialResolver))!);
            _logger = logger;
        }

        public async Task<string?> RenovarAsync(Guid idContaLoja, CancellationToken ct = default)
        {
            await using var cx = await _db.OpenConnectionAsync(ct);
            await using var tx = await cx.BeginTransactionAsync(ct);
            var linha = await cx.QueryFirstOrDefaultAsync<(string Ambiente, string? Refresh, string? Access, DateTime? Expira, Guid Loja)>(new CommandDefinition(@"
SELECT ambiente, refresh_token_cifrado, access_token_cifrado, token_expira_em, id_estabelecimento
  FROM mp_conta_loja WHERE id = @Id AND estado IN ('ativa', 'expirada', 'erro') FOR UPDATE", new { Id = idContaLoja }, tx, cancellationToken: ct));
            if (linha.Refresh == null) return null;

            // Outra requisicao ja renovou enquanto esperavamos a trava.
            if (linha.Expira is { } e && e - DateTime.UtcNow > TimeSpan.FromDays(7))
                return _protector.Revelar(linha.Access, SegredoContexto.LojaAccessToken(idContaLoja));

            var refresh = _protector.Revelar(linha.Refresh, SegredoContexto.LojaRefreshToken(idContaLoja));
            if (refresh == null) return null;
            try
            {
                var app = await _resolver.Value.AplicacaoAsync(linha.Ambiente, ct);
                var token = await _client.RenovarTokenAsync(app, refresh, ct);
                await cx.ExecuteAsync(new CommandDefinition(@"
UPDATE mp_conta_loja SET access_token_cifrado = @A, refresh_token_cifrado = @R, token_expira_em = @E, estado = 'ativa',
       ultimo_refresh_em = NOW(), ultimo_erro = NULL, updated_at = NOW()
 WHERE id = @Id", new
                {
                    Id = idContaLoja,
                    A = _protector.Proteger(token.AccessToken, SegredoContexto.LojaAccessToken(idContaLoja)),
                    R = _protector.Proteger(token.RefreshToken, SegredoContexto.LojaRefreshToken(idContaLoja)),
                    E = DateTime.UtcNow.AddSeconds(token.ExpiresIn)
                }, tx, cancellationToken: ct));
                await PagamentoAuditoria.RegistrarAsync(cx, tx, linha.Loja, "conta_loja", idContaLoja.ToString(), "token_renovado", "sistema", null, null, ct);
                await tx.CommitAsync(ct);
                return token.AccessToken;
            }
            catch (MercadoPagoApiException ex) when (ex.StatusHttp is 400 or 401 or 403)
            {
                // invalid_grant: a loja revogou o acesso no MP.
                await cx.ExecuteAsync(new CommandDefinition(
                    "UPDATE mp_conta_loja SET estado = 'expirada', ultimo_erro = @Erro, updated_at = NOW() WHERE id = @Id",
                    new { Id = idContaLoja, Erro = $"refresh {ex.StatusHttp}" }, tx, cancellationToken: ct));
                await tx.CommitAsync(ct);
                _logger.LogWarning("[mp.cred] ev=refresh_recusado conta={Conta} http={Http}", idContaLoja, ex.StatusHttp);
                return null;
            }
        }
    }
}
