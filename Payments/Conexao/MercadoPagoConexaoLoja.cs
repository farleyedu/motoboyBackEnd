using System;
using System.Collections.Generic;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using APIBack.Payments.MercadoPago;
using Dapper;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Npgsql;

namespace APIBack.Payments.Conexao
{
    /// <summary>Codigos da Gestao e da conexao OAuth (somam-se a PagamentoCodigos; tabela em CONTRATOS.md §7).</summary>
    public static class ConexaoCodigos
    {
        public const string ContaJaConectada = "PAG_CONTA_JA_CONECTADA";               // 409
        public const string AmbienteNaoLiberado = "PAG_AMBIENTE_NAO_LIBERADO";         // 422: a Zippy ainda nao liberou as conexoes
        public const string OAuthStateInvalido = "PAG_OAUTH_STATE_INVALIDO";           // callback: state desconhecido ou ja usado
        public const string OAuthExpirado = "PAG_OAUTH_EXPIRADO";                      // callback: passou dos 10 minutos
        public const string OAuthCancelado = "PAG_OAUTH_CANCELADO";                    // callback: a loja recusou no MP
        public const string OAuthFalhou = "PAG_OAUTH_FALHOU";                          // callback: troca do codigo recusada
        public const string ContaDeOutraLoja = "PAG_CONTA_MP_DE_OUTRA_LOJA";           // callback: conta MP ja recebe por outra loja
        public const string AmbienteDivergente = "PAG_OAUTH_AMBIENTE_DIVERGENTE";      // callback: conta de teste em producao (ou o contrario)
        public const string VerificacaoPendente = "PAG_VERIFICACAO_PENDENTE";          // 422: habilitar ambiente sem verificacao aprovada
    }

    /// <summary>State e PKCE (S256) do OAuth. Só o hash do state vai para o banco; o verifier vai cifrado.</summary>
    public static class OAuthPkce
    {
        public static string NovoState() => Base64Url(RandomNumberGenerator.GetBytes(32));
        public static string NovoVerifier() => Base64Url(RandomNumberGenerator.GetBytes(48));
        public static string Challenge(string verifier) => Base64Url(SHA256.HashData(Encoding.ASCII.GetBytes(verifier)));
        public static string HashState(string state) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(state))).ToLowerInvariant();

        /// <summary>43 caracteres base64url (32 bytes). Qualquer outra coisa nem chega ao banco.</summary>
        public static bool StateValido(string? state) =>
            state is { Length: 43 } && state.All(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_');

        public static string UrlAutorizacao(string clientId, string redirectUri, string state, string challenge) =>
            "https://auth.mercadopago.com.br/authorization?client_id=" + Uri.EscapeDataString(clientId) +
            "&response_type=code&platform_id=mp&state=" + Uri.EscapeDataString(state) +
            "&redirect_uri=" + Uri.EscapeDataString(redirectUri) +
            "&code_challenge=" + Uri.EscapeDataString(challenge) + "&code_challenge_method=S256";

        private static string Base64Url(byte[] bytes) => Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');
    }

    public sealed class PreferenciasPagamentoDto
    {
        public bool PixAtivo { get; set; }
        public bool CartaoAtivo { get; set; }
        public bool PresencialAtivo { get; set; } = true;
        public int PixValidadeMinutos { get; set; } = 30;
        public int CartaoParcelasMax { get; set; } = 1;
    }

    public sealed class ConexaoLojaDto
    {
        public bool Conectada { get; set; }
        /// <summary>nao_conectada | ativa | expirada | erro | revogada | desconectada.</summary>
        public string Estado { get; set; } = "nao_conectada";
        public string? MpUserIdMascarado { get; set; }
        public bool? LiveMode { get; set; }
        public DateTime? TokenExpiraEm { get; set; }
        public DateTime? ConectadoEm { get; set; }
        public DateTime? UltimaVerificacaoEm { get; set; }
        /// <summary>Codigo curto do ultimo problema (token_invalido, conta_suspensa, pix_indisponivel...). Nunca texto do MP.</summary>
        public string? UltimoErro { get; set; }
        public string Ambiente { get; set; } = Ambientes.Sandbox;
        /// <summary>A Zippy ja liberou "Conectar Mercado Pago" neste ambiente.</summary>
        public bool ConexaoDisponivel { get; set; }
        public PreferenciasPagamentoDto Preferencias { get; set; } = new();
    }

    public sealed record ConectarResultado(string UrlAutorizacao, Guid TentativaId, DateTime ExpiraEm);

    public sealed record TentativaDto(string Estado, string? ErroCodigo);

    public sealed class EtapaVerificacao
    {
        public string Etapa { get; set; } = string.Empty;
        public bool Ok { get; set; }
        public string? Codigo { get; set; }
        public string Mensagem { get; set; } = string.Empty;
    }

    public sealed class VerificacaoLojaDto
    {
        public bool Ok { get; set; }
        public string Estado { get; set; } = string.Empty;
        public bool PixDisponivel { get; set; }
        public bool CartaoDisponivel { get; set; }
        public List<EtapaVerificacao> Etapas { get; set; } = new();
    }

    public sealed record DesconectarResultado(string Estado);

    public sealed class TransacaoLojaDto
    {
        public Guid Id { get; set; }
        public string Origem { get; set; } = string.Empty;
        public string Status { get; set; } = string.Empty;
        public decimal Valor { get; set; }
        public decimal ValorPago { get; set; }
        public decimal ValorEstornado { get; set; }
        public string Descricao { get; set; } = string.Empty;
        public string? Metodo { get; set; }
        public int? PedidoId { get; set; }
        public DateTime? PagoEm { get; set; }
        public DateTime CriadoEm { get; set; }
    }

    /// <summary>Conexao OAuth do Mercado Pago de uma loja (CONTRATOS.md §8.2). Quem pode chamar e decidido no controller.</summary>
    public interface IMercadoPagoConexaoLojaService
    {
        Task<ConexaoLojaDto> EstadoAsync(Guid loja, CancellationToken ct = default);
        Task<ConectarResultado> ConectarAsync(Guid loja, int usuarioId, bool reconectar, CancellationToken ct = default);
        /// <summary>Callback anonimo: devolve para onde mandar o navegador (painel) com resultado e codigo. Nunca lanca.</summary>
        Task<string> ConcluirAsync(string? code, string? state, string? erro, CancellationToken ct = default);
        Task<TentativaDto> TentativaAsync(Guid loja, Guid tentativaId, CancellationToken ct = default);
        Task<DesconectarResultado> DesconectarAsync(Guid loja, int usuarioId, bool confirmar, CancellationToken ct = default);
        Task<VerificacaoLojaDto> VerificarAsync(Guid loja, int? usuarioId, CancellationToken ct = default);
        Task<PreferenciasPagamentoDto> PreferenciasAsync(Guid loja, CancellationToken ct = default);
        Task<PreferenciasPagamentoDto> SalvarPreferenciasAsync(Guid loja, int usuarioId, PreferenciasPagamentoDto prefs, CancellationToken ct = default);
        Task<IReadOnlyList<TransacaoLojaDto>> TransacoesAsync(Guid loja, int pagina, CancellationToken ct = default);
    }

    public sealed class MercadoPagoConexaoLojaService : IMercadoPagoConexaoLojaService
    {
        public static readonly TimeSpan ValidadeTentativa = TimeSpan.FromMinutes(10);
        private readonly NpgsqlDataSource _db;
        private readonly IMercadoPagoCredencialResolver _credenciais;
        private readonly IMercadoPagoClient _client;
        private readonly IPagamentoSegredoProtector _protector;
        private readonly MercadoPagoOptions _options;
        private readonly IConfiguration _config;
        private readonly ILogger<MercadoPagoConexaoLojaService> _logger;

        public MercadoPagoConexaoLojaService(NpgsqlDataSource db, IMercadoPagoCredencialResolver credenciais, IMercadoPagoClient client,
            IPagamentoSegredoProtector protector, IOptions<MercadoPagoOptions> options, IConfiguration config, ILogger<MercadoPagoConexaoLojaService> logger)
        {
            _db = db;
            _credenciais = credenciais;
            _client = client;
            _protector = protector;
            _options = options.Value;
            _config = config;
            _logger = logger;
        }

        private string Ambiente => _credenciais.AmbienteAtivo;

        public async Task<ConexaoLojaDto> EstadoAsync(Guid loja, CancellationToken ct = default)
        {
            await using var cx = await _db.OpenConnectionAsync(ct);
            var conta = await ContaVivaAsync(cx, null, loja, false, ct)
                        ?? await cx.QueryFirstOrDefaultAsync<LinhaConta>(new CommandDefinition(SqlConta +
                            " WHERE id_estabelecimento = @Loja AND ambiente = @Amb ORDER BY updated_at DESC LIMIT 1", new { Loja = loja, Amb = Ambiente }, cancellationToken: ct));
            var dto = new ConexaoLojaDto
            {
                Ambiente = Ambiente,
                ConexaoDisponivel = await ConexaoLiberadaAsync(cx, Ambiente, ct),
                Preferencias = await PreferenciasAsync(cx, loja, ct)
            };
            if (conta != null)
            {
                dto.Estado = conta.Estado;
                dto.Conectada = conta.Estado is "ativa" or "expirada" or "erro";
                dto.MpUserIdMascarado = conta.MpUserIdMascarado;
                dto.LiveMode = conta.LiveMode;
                dto.TokenExpiraEm = dto.Conectada ? conta.TokenExpiraEm : null;
                dto.ConectadoEm = conta.ConectadoEm;
                dto.UltimoErro = conta.UltimoErro;
                dto.UltimaVerificacaoEm = conta.UltimaVerificacaoEm;
            }
            return dto;
        }

        public async Task<ConectarResultado> ConectarAsync(Guid loja, int usuarioId, bool reconectar, CancellationToken ct = default)
        {
            var amb = Ambiente;
            if (!_protector.Configurado) throw PagamentoException.NaoConfigurado("Pagamentos ainda nao configurados pela Zippy.");
            await using var cx = await _db.OpenConnectionAsync(ct);
            if (!await ConexaoLiberadaAsync(cx, amb, ct))
                throw PagamentoException.Regra(ConexaoCodigos.AmbienteNaoLiberado, "A Zippy ainda esta preparando os pagamentos online. Tente mais tarde.");

            var viva = await ContaVivaAsync(cx, null, loja, false, ct);
            if (viva is { Estado: "ativa" } && !reconectar)
                throw new PagamentoException(ConexaoCodigos.ContaJaConectada, 409, "A loja ja tem uma conta Mercado Pago conectada.");

            var app = await _credenciais.AplicacaoAsync(amb, ct);
            if (string.IsNullOrWhiteSpace(app.ClientId) || string.IsNullOrWhiteSpace(app.RedirectUri))
                throw PagamentoException.NaoConfigurado("Aplicacao Mercado Pago sem endereco de retorno configurado.");

            var state = OAuthPkce.NovoState();
            var verifier = OAuthPkce.NovoVerifier();
            var id = Guid.NewGuid();
            var expira = DateTime.UtcNow.Add(ValidadeTentativa);
            await using var tx = await cx.BeginTransactionAsync(ct);
            // Uma tentativa aberta por loja: a nova cancela as anteriores (link velho deixa de valer).
            await cx.ExecuteAsync(new CommandDefinition(@"
UPDATE mp_oauth_tentativa SET estado = 'cancelada', erro_codigo = 'substituida' WHERE id_estabelecimento = @Loja AND estado = 'iniciada'",
                new { Loja = loja }, tx, cancellationToken: ct));
            await cx.ExecuteAsync(new CommandDefinition(@"
INSERT INTO mp_oauth_tentativa (id, id_estabelecimento, ambiente, usuario_id, state_hash, code_verifier_cifrado, expira_em)
VALUES (@Id, @Loja, @Amb, @Usuario, @Hash, @Verifier, @Expira)", new
            {
                Id = id, Loja = loja, Amb = amb, Usuario = usuarioId, Hash = OAuthPkce.HashState(state),
                Verifier = _protector.Proteger(verifier, SegredoContexto.OAuthCodeVerifier(id)), Expira = expira
            }, tx, cancellationToken: ct));
            await PagamentoAuditoria.RegistrarAsync(cx, tx, loja, "conta_loja", id.ToString(), "oauth_iniciado", "usuario", usuarioId,
                new { ambiente = amb, reconectar }, ct);
            await tx.CommitAsync(ct);
            return new ConectarResultado(OAuthPkce.UrlAutorizacao(app.ClientId, app.RedirectUri!, state, OAuthPkce.Challenge(verifier)), id, expira);
        }

        public async Task<string> ConcluirAsync(string? code, string? state, string? erro, CancellationToken ct = default)
        {
            try
            {
                var codigo = await ConcluirInternoAsync(code, state, erro, ct);
                return Retorno(codigo == null, codigo);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _logger.LogWarning("[mp.oauth] ev=callback_falhou erro={Erro}", ex.GetType().Name);
                return Retorno(false, ConexaoCodigos.OAuthFalhou);
            }
        }

        /// <summary>Null = conectado. Senao, o codigo do erro que o painel mostra.</summary>
        private async Task<string?> ConcluirInternoAsync(string? code, string? state, string? erro, CancellationToken ct)
        {
            if (!OAuthPkce.StateValido(state)) return ConexaoCodigos.OAuthStateInvalido;
            await using var cx = await _db.OpenConnectionAsync(ct);

            // Consome a tentativa uma vez so (estado iniciada -> erro provisoriamente). Replay do mesmo state nao acha nada.
            var t = await cx.QueryFirstOrDefaultAsync<LinhaTentativa>(new CommandDefinition(@"
UPDATE mp_oauth_tentativa SET estado = 'erro', erro_codigo = 'em_andamento'
 WHERE state_hash = @Hash AND estado = 'iniciada'
RETURNING id, id_estabelecimento, ambiente, usuario_id, code_verifier_cifrado, expira_em", new { Hash = OAuthPkce.HashState(state!) }, cancellationToken: ct));
            if (t == null) return ConexaoCodigos.OAuthStateInvalido;

            async Task<string> Falhar(string estado, string codigo)
            {
                await cx.ExecuteAsync(new CommandDefinition("UPDATE mp_oauth_tentativa SET estado = @E, erro_codigo = @C, concluida_em = NOW() WHERE id = @Id",
                    new { Id = t.Id, E = estado, C = codigo }, cancellationToken: ct));
                await PagamentoAuditoria.RegistrarAsync(cx, null, t.IdEstabelecimento, "conta_loja", t.Id.ToString(), "oauth_falhou", "sistema", null,
                    new { codigo }, ct);
                return codigo;
            }

            if (t.ExpiraEm <= DateTime.UtcNow) return await Falhar("expirada", ConexaoCodigos.OAuthExpirado);
            if (!string.IsNullOrWhiteSpace(erro)) return await Falhar("cancelada", ConexaoCodigos.OAuthCancelado);
            if (string.IsNullOrWhiteSpace(code) || code.Length > 512) return await Falhar("erro", ConexaoCodigos.OAuthFalhou);
            if (t.Ambiente != Ambiente) return await Falhar("erro", ConexaoCodigos.AmbienteDivergente);

            var verifier = _protector.Revelar(t.CodeVerifierCifrado, SegredoContexto.OAuthCodeVerifier(t.Id));
            if (verifier == null) return await Falhar("erro", ConexaoCodigos.OAuthFalhou);

            MpOAuthToken token;
            try
            {
                var app = await _credenciais.AplicacaoAsync(t.Ambiente, ct);
                token = await _client.TrocarCodigoOAuthAsync(app, code, app.RedirectUri ?? string.Empty, verifier, ct);
            }
            catch (MercadoPagoApiException ex)
            {
                _logger.LogWarning("[mp.oauth] ev=troca_recusada tentativa={Tentativa} http={Http}", t.Id, ex.StatusHttp);
                return await Falhar("erro", ex.Indisponivel ? PagamentoCodigos.MercadoPagoIndisponivel : ConexaoCodigos.OAuthFalhou);
            }
            if (token.UserId <= 0 || string.IsNullOrWhiteSpace(token.AccessToken) || string.IsNullOrWhiteSpace(token.RefreshToken))
                return await Falhar("erro", PagamentoCodigos.MercadoPagoRespostaInconsistente);
            if (_options.ExigirLiveModeDoAmbiente && token.LiveMode != Ambientes.LiveMode(t.Ambiente))
                return await Falhar("erro", ConexaoCodigos.AmbienteDivergente);

            var r = await GravarContaAsync(cx, t, token, ct);
            if (r != null) return await Falhar("erro", r);
            _logger.LogInformation("[mp.oauth] ev=conectada loja={Loja} mp={Mp}", t.IdEstabelecimento, CredencialRecebedora.MascararId(token.UserId));
            return null;
        }

        /// <summary>Grava a conta numa transacao (loja travada). Null = ok; senao o codigo do erro.</summary>
        private async Task<string?> GravarContaAsync(NpgsqlConnection cx, LinhaTentativa t, MpOAuthToken token, CancellationToken ct)
        {
            await using var tx = await cx.BeginTransactionAsync(ct);
            await cx.ExecuteAsync(new CommandDefinition("SELECT pg_advisory_xact_lock(hashtext('mp_conta_loja:' || @Loja::text))",
                new { Loja = t.IdEstabelecimento }, tx, cancellationToken: ct));

            // A mesma conta MP nao recebe por duas lojas: o dinheiro de uma loja nunca cai na conta de outra.
            var outra = await cx.ExecuteScalarAsync<Guid?>(new CommandDefinition(@"
SELECT id_estabelecimento FROM mp_conta_loja
 WHERE ambiente = @Amb AND mp_user_id = @U AND id_estabelecimento <> @Loja AND estado IN ('ativa', 'expirada', 'erro') LIMIT 1",
                new { Amb = t.Ambiente, U = token.UserId, Loja = t.IdEstabelecimento }, tx, cancellationToken: ct));
            if (outra != null)
            {
                await tx.RollbackAsync(ct);
                return ConexaoCodigos.ContaDeOutraLoja;
            }

            var viva = await ContaVivaAsync(cx, tx, t.IdEstabelecimento, true, ct, t.Ambiente);
            var escopos = (token.Scope ?? string.Empty).Split(' ', StringSplitOptions.RemoveEmptyEntries);
            var expira = DateTime.UtcNow.AddSeconds(token.ExpiresIn > 0 ? token.ExpiresIn : 15_552_000);
            Guid idConta;
            string acao;
            if (viva != null && viva.MpUserId == token.UserId)
            {
                // Mesma conta autorizada de novo: so troca os tokens (a linha e os pagamentos dela continuam).
                idConta = viva.Id;
                acao = "conta_reautorizada";
                await cx.ExecuteAsync(new CommandDefinition(@"
UPDATE mp_conta_loja SET access_token_cifrado = @A, refresh_token_cifrado = @R, public_key = @Pk, escopos = @Esc, live_mode = @Live,
       token_expira_em = @Expira, estado = 'ativa', ultimo_erro = NULL, ultimo_refresh_em = NOW(), conectado_por = @Usuario, updated_at = NOW()
 WHERE id = @Id", new
                {
                    Id = idConta, A = _protector.Proteger(token.AccessToken, SegredoContexto.LojaAccessToken(idConta)),
                    R = _protector.Proteger(token.RefreshToken, SegredoContexto.LojaRefreshToken(idConta)), Pk = token.PublicKey, Esc = escopos,
                    Live = token.LiveMode, Expira = expira, Usuario = t.UsuarioId
                }, tx, cancellationToken: ct));
            }
            else
            {
                if (viva != null)
                {
                    // Outra conta MP: a anterior sai (tokens apagados), a linha fica para o historico dos pagamentos.
                    await cx.ExecuteAsync(new CommandDefinition(@"
UPDATE mp_conta_loja SET estado = 'desconectada', access_token_cifrado = NULL, refresh_token_cifrado = NULL, desconectado_em = NOW(),
       desconectado_por = @Usuario, ultimo_erro = 'substituida', updated_at = NOW()
 WHERE id = @Id", new { viva.Id, Usuario = t.UsuarioId }, tx, cancellationToken: ct));
                    await PagamentoAuditoria.RegistrarAsync(cx, tx, t.IdEstabelecimento, "conta_loja", viva.Id.ToString(), "conta_desconectada", "usuario", t.UsuarioId,
                        new { motivo = "substituida_por_outra_conta", mp = viva.MpUserIdMascarado }, ct);
                }

                idConta = Guid.NewGuid();
                acao = "conta_conectada";
                await cx.ExecuteAsync(new CommandDefinition(@"
INSERT INTO mp_conta_loja (id, id_estabelecimento, ambiente, mp_user_id, mp_user_id_mascarado, access_token_cifrado, refresh_token_cifrado, public_key,
                           escopos, live_mode, token_expira_em, estado, conectado_por, conectado_em)
VALUES (@Id, @Loja, @Amb, @U, @Mascara, @A, @R, @Pk, @Esc, @Live, @Expira, 'ativa', @Usuario, NOW())", new
                {
                    Id = idConta, Loja = t.IdEstabelecimento, Amb = t.Ambiente, U = token.UserId, Mascara = CredencialRecebedora.MascararId(token.UserId),
                    A = _protector.Proteger(token.AccessToken, SegredoContexto.LojaAccessToken(idConta)),
                    R = _protector.Proteger(token.RefreshToken, SegredoContexto.LojaRefreshToken(idConta)), Pk = token.PublicKey, Esc = escopos,
                    Live = token.LiveMode, Expira = expira, Usuario = t.UsuarioId
                }, tx, cancellationToken: ct));
            }

            await cx.ExecuteAsync(new CommandDefinition(@"
UPDATE mp_oauth_tentativa SET estado = 'concluida', erro_codigo = NULL, id_conta_loja = @Conta, concluida_em = NOW(), code_verifier_cifrado = NULL
 WHERE id = @Id", new { Id = t.Id, Conta = idConta }, tx, cancellationToken: ct));
            await PagamentoAuditoria.RegistrarAsync(cx, tx, t.IdEstabelecimento, "conta_loja", idConta.ToString(), acao, "usuario", t.UsuarioId,
                new { ambiente = t.Ambiente, mp = CredencialRecebedora.MascararId(token.UserId), escopos, liveMode = token.LiveMode }, ct);
            await tx.CommitAsync(ct);
            return null;
        }

        public async Task<TentativaDto> TentativaAsync(Guid loja, Guid tentativaId, CancellationToken ct = default)
        {
            await using var cx = await _db.OpenConnectionAsync(ct);
            var t = await cx.QueryFirstOrDefaultAsync<(string Estado, string? ErroCodigo, DateTime ExpiraEm)?>(new CommandDefinition(
                "SELECT estado, erro_codigo, expira_em FROM mp_oauth_tentativa WHERE id = @Id AND id_estabelecimento = @Loja", new { Id = tentativaId, Loja = loja }, cancellationToken: ct));
            if (t == null) throw PagamentoException.NaoEncontrado("Tentativa nao encontrada.");
            var (estado, erro, expira) = t.Value;
            if (estado == "iniciada" && expira <= DateTime.UtcNow) return new TentativaDto("expirada", ConexaoCodigos.OAuthExpirado);
            return new TentativaDto(estado, erro == "em_andamento" ? null : erro);
        }

        public async Task<DesconectarResultado> DesconectarAsync(Guid loja, int usuarioId, bool confirmar, CancellationToken ct = default)
        {
            await using var cx = await _db.OpenConnectionAsync(ct);
            await using var tx = await cx.BeginTransactionAsync(ct);
            var viva = await ContaVivaAsync(cx, tx, loja, true, ct) ?? throw PagamentoException.NaoEncontrado("Nenhuma conta Mercado Pago conectada.");

            var abertas = await cx.ExecuteScalarAsync<int>(new CommandDefinition(@"
SELECT count(*)::int FROM pagamento_intent WHERE id_conta_loja = @Conta AND status IN ('criado', 'aguardando_pagamento', 'processando')",
                new { Conta = viva.Id }, tx, cancellationToken: ct));
            if (abertas > 0 && !confirmar)
                throw new PagamentoException(PagamentoCodigos.CobrancaAberta, 409,
                    "Ha cobrancas aguardando pagamento nesta conta. Depois de desconectar, elas nao serao mais acompanhadas.",
                    new Dictionary<string, object?> { ["abertas"] = abertas });

            // Desconectar nunca apaga transacoes: a linha fica (pagamentos apontam para ela), so os tokens saem.
            await cx.ExecuteAsync(new CommandDefinition(@"
UPDATE mp_conta_loja SET estado = 'desconectada', access_token_cifrado = NULL, refresh_token_cifrado = NULL, desconectado_em = NOW(),
       desconectado_por = @Usuario, updated_at = NOW()
 WHERE id = @Id", new { viva.Id, Usuario = usuarioId }, tx, cancellationToken: ct));
            await cx.ExecuteAsync(new CommandDefinition(
                "UPDATE mp_oauth_tentativa SET estado = 'cancelada', erro_codigo = 'desconectada' WHERE id_estabelecimento = @Loja AND estado = 'iniciada'",
                new { Loja = loja }, tx, cancellationToken: ct));
            await PagamentoAuditoria.RegistrarAsync(cx, tx, loja, "conta_loja", viva.Id.ToString(), "conta_desconectada", "usuario", usuarioId,
                new { mp = viva.MpUserIdMascarado, abertas }, ct);
            await tx.CommitAsync(ct);
            return new DesconectarResultado("desconectada");
        }

        public async Task<VerificacaoLojaDto> VerificarAsync(Guid loja, int? usuarioId, CancellationToken ct = default)
        {
            var r = new VerificacaoLojaDto();
            await using var cx = await _db.OpenConnectionAsync(ct);
            var viva = await ContaVivaAsync(cx, null, loja, false, ct) ?? throw PagamentoException.Regra(PagamentoCodigos.ContaLojaNaoConectada,
                "A loja ainda nao conectou a conta Mercado Pago.");

            string? problema = null;
            CredencialRecebedora? cred = null;
            try
            {
                cred = await _credenciais.LojaAsync(loja, null, ct);
                r.Etapas.Add(Etapa("acesso", true, null, "Acesso a conta autorizado e dentro da validade."));
            }
            catch (PagamentoException ex) when (ex.Codigo == PagamentoCodigos.ContaLojaReautorizar)
            {
                problema = "token_expirado";
                r.Etapas.Add(Etapa("acesso", false, ex.Codigo, "O acesso a conta venceu ou foi retirado. Conecte de novo."));
            }

            if (cred != null)
            {
                try
                {
                    var me = await _client.ObterUsuarioAsync(cred, ct);
                    if (me.Id != viva.MpUserId)
                    {
                        problema = "conta_divergente";
                        r.Etapas.Add(Etapa("conta", false, PagamentoCodigos.CredencialIncompativel, "A conta respondeu com outro titular. Conecte de novo."));
                    }
                    else if (me.Status?.SiteStatus is { } situacao && !string.Equals(situacao, "active", StringComparison.OrdinalIgnoreCase))
                    {
                        problema = "conta_suspensa";
                        r.Etapas.Add(Etapa("conta", false, "conta_suspensa", "O Mercado Pago informa que a conta esta com restricao. Resolva no app do Mercado Pago."));
                    }
                    else r.Etapas.Add(Etapa("conta", true, null, "Conta ativa no Mercado Pago."));

                    var meios = await _client.MeiosDePagamentoAsync(cred, ct);
                    r.PixDisponivel = meios.Any(m => m.Id == "pix" && m.Status is null or "active");
                    r.CartaoDisponivel = meios.Any(m => m.PaymentTypeId is "credit_card" or "debit_card" && m.Status is null or "active");
                    r.Etapas.Add(Etapa("pix", r.PixDisponivel, r.PixDisponivel ? null : "pix_indisponivel",
                        r.PixDisponivel ? "Pix liberado na conta." : "Pix indisponivel: cadastre uma chave Pix no app do Mercado Pago."));
                    r.Etapas.Add(Etapa("cartao", r.CartaoDisponivel, r.CartaoDisponivel ? null : "cartao_indisponivel",
                        r.CartaoDisponivel ? "Cartao liberado na conta." : "Cartao indisponivel: aceite os termos de recebimento no Mercado Pago."));
                    if (problema == null && !r.PixDisponivel && !r.CartaoDisponivel) problema = "meios_indisponiveis";
                }
                catch (MercadoPagoApiException ex) when (ex.StatusHttp is 401 or 403)
                {
                    problema = "token_invalido";
                    r.Etapas.Add(Etapa("conta", false, PagamentoCodigos.ContaLojaReautorizar, "O Mercado Pago recusou o acesso. Conecte de novo."));
                }
                catch (MercadoPagoApiException ex)
                {
                    // MP fora do ar nao muda o estado da conta: so informa.
                    r.Etapas.Add(Etapa("conta", false, ex.Indisponivel ? PagamentoCodigos.MercadoPagoIndisponivel : PagamentoCodigos.MercadoPagoErro,
                        "Nao foi possivel falar com o Mercado Pago agora. Tente de novo em alguns minutos."));
                    r.Estado = viva.Estado;
                    return r;
                }
            }

            var novoEstado = problema switch
            {
                null or "meios_indisponiveis" => "ativa",
                "token_expirado" => "expirada",
                _ => "erro"
            };
            await cx.ExecuteAsync(new CommandDefinition(@"
UPDATE mp_conta_loja SET estado = CASE WHEN estado IN ('ativa', 'expirada', 'erro') THEN @Estado ELSE estado END, ultimo_erro = @Erro, updated_at = NOW()
 WHERE id = @Id", new { viva.Id, Estado = novoEstado, Erro = problema }, cancellationToken: ct));
            await PagamentoAuditoria.RegistrarAsync(cx, null, loja, "conta_loja", viva.Id.ToString(), "conta_verificada", usuarioId == null ? "sistema" : "usuario",
                usuarioId, new { ok = problema == null, problema, pix = r.PixDisponivel, cartao = r.CartaoDisponivel }, ct);
            r.Ok = problema == null;
            r.Estado = novoEstado;
            return r;
        }

        public async Task<PreferenciasPagamentoDto> PreferenciasAsync(Guid loja, CancellationToken ct = default)
        {
            await using var cx = await _db.OpenConnectionAsync(ct);
            return await PreferenciasAsync(cx, loja, ct);
        }

        public async Task<PreferenciasPagamentoDto> SalvarPreferenciasAsync(Guid loja, int usuarioId, PreferenciasPagamentoDto p, CancellationToken ct = default)
        {
            if (p.PixValidadeMinutos is < 5 or > 1440) throw PagamentoException.Invalida("A validade do Pix vai de 5 minutos a 24 horas.", "pixValidadeMinutos");
            if (p.CartaoParcelasMax is < 1 or > 12) throw PagamentoException.Invalida("Parcelas de 1 a 12.", "cartaoParcelasMax");
            await using var cx = await _db.OpenConnectionAsync(ct);
            await using var tx = await cx.BeginTransactionAsync(ct);
            if ((p.PixAtivo || p.CartaoAtivo) && await ContaVivaAsync(cx, tx, loja, false, ct) is not { Estado: "ativa" })
                throw PagamentoException.Regra(PagamentoCodigos.ContaLojaNaoConectada, "Conecte o Mercado Pago antes de ligar Pix ou cartao.");

            var antes = await PreferenciasAsync(cx, loja, ct, tx);
            await cx.ExecuteAsync(new CommandDefinition(@"
INSERT INTO pagamento_preferencia_loja (id_estabelecimento, pix_ativo, cartao_ativo, presencial_ativo, pix_validade_minutos, cartao_parcelas_max, atualizado_por, updated_at)
VALUES (@Loja, @Pix, @Cartao, @Presencial, @Validade, @Parcelas, @Usuario, NOW())
ON CONFLICT (id_estabelecimento) DO UPDATE SET pix_ativo = EXCLUDED.pix_ativo, cartao_ativo = EXCLUDED.cartao_ativo,
       presencial_ativo = EXCLUDED.presencial_ativo, pix_validade_minutos = EXCLUDED.pix_validade_minutos,
       cartao_parcelas_max = EXCLUDED.cartao_parcelas_max, atualizado_por = EXCLUDED.atualizado_por, updated_at = NOW()", new
            {
                Loja = loja, Pix = p.PixAtivo, Cartao = p.CartaoAtivo, Presencial = p.PresencialAtivo, Validade = p.PixValidadeMinutos,
                Parcelas = p.CartaoParcelasMax, Usuario = usuarioId
            }, tx, cancellationToken: ct));
            await PagamentoAuditoria.RegistrarAsync(cx, tx, loja, "preferencia_loja", loja.ToString(), "preferencia_alterada", "usuario", usuarioId,
                new { antes, depois = p }, ct);
            await tx.CommitAsync(ct);
            return p;
        }

        public async Task<IReadOnlyList<TransacaoLojaDto>> TransacoesAsync(Guid loja, int pagina, CancellationToken ct = default)
        {
            await using var cx = await _db.OpenConnectionAsync(ct);
            return (await cx.QueryAsync<TransacaoLojaDto>(new CommandDefinition(@"
SELECT i.id, i.origem, i.status, i.valor, i.valor_pago, i.valor_estornado, i.descricao, p.metodo, i.pedido_id, i.pago_em, i.created_at AS criado_em
  FROM pagamento_intent i
  LEFT JOIN LATERAL (SELECT metodo FROM pagamento x WHERE x.id_intent = i.id ORDER BY x.created_at DESC LIMIT 1) p ON TRUE
 WHERE i.id_estabelecimento = @Loja AND i.conta_tipo = 'loja' AND i.ambiente = @Amb
 ORDER BY i.created_at DESC LIMIT 20 OFFSET @Offset", new { Loja = loja, Amb = Ambiente, Offset = Math.Max(0, pagina - 1) * 20 }, cancellationToken: ct))).ToList();
        }

        // ---- apoio --------------------------------------------------------------------------------------------------

        private string Retorno(bool ok, string? codigo)
        {
            var painel = (_options.PainelUrl ?? _config["Rastreio:PublicBaseUrl"])?.Trim().TrimEnd('/');
            if (string.IsNullOrWhiteSpace(painel) || painel.StartsWith("__", StringComparison.Ordinal)) painel = string.Empty;
            var url = $"{painel}/apps/configuracoes/integracoes?mercadopago={(ok ? "ok" : "erro")}";
            return codigo == null ? url : url + "&codigo=" + Uri.EscapeDataString(codigo);
        }

        public static async Task<bool> ConexaoLiberadaAsync(NpgsqlConnection cx, string ambiente, CancellationToken ct)
        {
            try
            {
                return await cx.ExecuteScalarAsync<bool?>(new CommandDefinition(
                    "SELECT habilitado AND lojas_podem_conectar FROM mp_ambiente_estado WHERE ambiente = @Amb", new { Amb = ambiente }, cancellationToken: ct)) ?? false;
            }
            catch (PostgresException ex) when (ex.SqlState == PostgresErrorCodes.UndefinedTable)
            {
                return false;
            }
        }

        private static async Task<PreferenciasPagamentoDto> PreferenciasAsync(NpgsqlConnection cx, Guid loja, CancellationToken ct, NpgsqlTransaction? tx = null) =>
            await cx.QueryFirstOrDefaultAsync<PreferenciasPagamentoDto>(new CommandDefinition(@"
SELECT pix_ativo, cartao_ativo, presencial_ativo, pix_validade_minutos, cartao_parcelas_max FROM pagamento_preferencia_loja WHERE id_estabelecimento = @Loja",
                new { Loja = loja }, tx, cancellationToken: ct)) ?? new PreferenciasPagamentoDto();

        private const string SqlConta = @"
SELECT c.id, c.mp_user_id, c.mp_user_id_mascarado, c.estado, c.live_mode, c.token_expira_em, c.conectado_em, c.ultimo_erro,
       (SELECT max(a.created_at) FROM pagamento_auditoria a WHERE a.entidade = 'conta_loja' AND a.entidade_id = c.id::text AND a.acao = 'conta_verificada')
           AS ultima_verificacao_em
  FROM mp_conta_loja c";

        private Task<LinhaConta?> ContaVivaAsync(NpgsqlConnection cx, NpgsqlTransaction? tx, Guid loja, bool travar, CancellationToken ct, string? ambiente = null) =>
            cx.QueryFirstOrDefaultAsync<LinhaConta?>(new CommandDefinition(SqlConta + @"
 WHERE c.id_estabelecimento = @Loja AND c.ambiente = @Amb AND c.estado IN ('ativa', 'expirada', 'erro')" + (travar ? " FOR UPDATE OF c" : string.Empty),
                new { Loja = loja, Amb = ambiente ?? Ambiente }, tx, cancellationToken: ct));

        private static EtapaVerificacao Etapa(string etapa, bool ok, string? codigo, string mensagem) =>
            new() { Etapa = etapa, Ok = ok, Codigo = codigo, Mensagem = mensagem };

        private sealed class LinhaConta
        {
            public Guid Id { get; set; }
            public long MpUserId { get; set; }
            public string MpUserIdMascarado { get; set; } = string.Empty;
            public string Estado { get; set; } = string.Empty;
            public bool LiveMode { get; set; }
            public DateTime? TokenExpiraEm { get; set; }
            public DateTime ConectadoEm { get; set; }
            public string? UltimoErro { get; set; }
            public DateTime? UltimaVerificacaoEm { get; set; }
        }

        private sealed class LinhaTentativa
        {
            public Guid Id { get; set; }
            public Guid IdEstabelecimento { get; set; }
            public string Ambiente { get; set; } = string.Empty;
            public int UsuarioId { get; set; }
            public string? CodeVerifierCifrado { get; set; }
            public DateTime ExpiraEm { get; set; }
        }
    }
}
