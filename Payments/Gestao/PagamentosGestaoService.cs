using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using APIBack.Payments.Conexao;
using APIBack.Payments.MercadoPago;
using Dapper;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Npgsql;

namespace APIBack.Payments.Gestao
{
    /// <summary>
    /// Gestao de Pagamentos da Zippy (so super admin; a checagem fica no controller). Le e grava a aplicacao MP e a conta recebedora da
    /// Zippy de cada ambiente, verifica tudo de verdade no MP, libera o ambiente e mostra contas, transacoes (Zippy e lojas separadas),
    /// reembolsos, webhooks, disputas, politicas e auditoria. Nenhuma resposta ou log carrega token ou segredo.
    /// </summary>
    public interface IPagamentosGestaoService
    {
        Task<ConfiguracaoDto> ConfiguracaoAsync(string? ambiente, CancellationToken ct = default);
        Task<AplicacaoDto> SalvarAplicacaoAsync(SalvarAplicacaoRequest r, int usuario, CancellationToken ct = default);
        Task<ContaZippyDto> SalvarContaZippyAsync(SalvarContaZippyRequest r, int usuario, CancellationToken ct = default);
        Task<TesteContaZippyDto> TestarContaZippyAsync(string? ambiente, int usuario, CancellationToken ct = default);
        Task<VerificacaoDto> VerificarAsync(string? ambiente, int usuario, CancellationToken ct = default);
        Task<ConfiguracaoDto> AtualizarAmbienteAsync(AtualizarAmbienteRequest r, int usuario, CancellationToken ct = default);
        Task<ResumoGestaoDto> ResumoAsync(DateOnly? mes = null, CancellationToken ct = default);
        Task<Pagina<ConexaoLojaGestaoDto>> ConexoesAsync(string? busca, string? estado, int pagina, CancellationToken ct = default);
        Task<TransacoesGestaoDto> TransacoesAsync(string? conta, Guid? loja, string? origem, string? status, DateTime? de, DateTime? ate, int pagina, CancellationToken ct = default);
        Task<TransacoesGestaoDto> AssinaturasAsync(string? status, int pagina, CancellationToken ct = default);
        Task<Pagina<ReembolsoGestaoDto>> ReembolsosAsync(string? status, string? conta, int pagina, CancellationToken ct = default);
        Task<WebhooksGestaoDto> WebhooksAsync(string? status, string? topico, int pagina, CancellationToken ct = default);
        Task<string> ReprocessarWebhookAsync(Guid id, int usuario, CancellationToken ct = default);
        Task<ReconciliacaoDto> ReconciliarAsync(int usuario, CancellationToken ct = default);
        Task<List<DisputaGestaoDto>> DisputasAsync(string? status, CancellationToken ct = default);
        Task<Pagina<AuditoriaGestaoDto>> AuditoriaAsync(string? escopo, string? entidade, string? entidadeId, int pagina, CancellationToken ct = default);
        Task<PoliticasDto> PoliticasAsync(CancellationToken ct = default);
        Task<PoliticasDto> SalvarPoliticasAsync(PoliticasDto p, int usuario, CancellationToken ct = default);
    }

    public sealed class PagamentosGestaoService : IPagamentosGestaoService
    {
        public const string CaminhoCallback = "/api/integracoes/mercadopago/callback";
        public const string CaminhoWebhook = "/api/webhooks/mercadopago";
        private const int TamanhoPagina = 25;
        /// <summary>Entidades de auditoria que contam como "alteracao da Gestao" no historico.</summary>
        private static readonly string[] EntidadesGestao = { "aplicacao", "conta_zippy", "ambiente", "politica", "verificacao", "webhook", "conciliacao" };

        private readonly NpgsqlDataSource _db;
        private readonly IMercadoPagoCredencialResolver _credenciais;
        private readonly IMercadoPagoClient _client;
        private readonly IPagamentoSegredoProtector _protector;
        private readonly IMercadoPagoWebhookService _webhook;
        private readonly IServiceProvider _services;
        private readonly MercadoPagoOptions _options;
        private readonly ILogger<PagamentosGestaoService> _logger;

        public PagamentosGestaoService(NpgsqlDataSource db, IMercadoPagoCredencialResolver credenciais, IMercadoPagoClient client, IPagamentoSegredoProtector protector,
            IMercadoPagoWebhookService webhook, IServiceProvider services, IOptions<MercadoPagoOptions> options, ILogger<PagamentosGestaoService> logger)
        {
            _db = db;
            _credenciais = credenciais;
            _client = client;
            _protector = protector;
            _webhook = webhook;
            _services = services;
            _options = options.Value;
            _logger = logger;
        }

        private string Amb(string? ambiente) => string.IsNullOrWhiteSpace(ambiente) ? _credenciais.AmbienteAtivo
            : Ambientes.Valido(ambiente.Trim()) ? ambiente.Trim() : throw PagamentoException.Invalida("Ambiente deve ser sandbox ou producao.", "ambiente");

        /// <summary>https://api... publico desta API: configurado, ou a NotificationUrl sem o caminho do webhook.</summary>
        public static string? ApiPublica(MercadoPagoOptions o)
        {
            var url = o.ApiPublicaUrl?.Trim().TrimEnd('/');
            if (!string.IsNullOrWhiteSpace(url)) return url;
            var n = o.NotificationUrl?.Trim();
            if (string.IsNullOrWhiteSpace(n) || !Uri.TryCreate(n, UriKind.Absolute, out var u)) return null;
            return u.GetLeftPart(UriPartial.Authority);
        }

        // ---- configuracao ----------------------------------------------------------------------------------------------

        public async Task<ConfiguracaoDto> ConfiguracaoAsync(string? ambiente, CancellationToken ct = default)
        {
            var amb = Amb(ambiente);
            await using var cx = await _db.OpenConnectionAsync(ct);
            var api = ApiPublica(_options);
            var dto = new ConfiguracaoDto
            {
                AmbienteAtivo = _credenciais.AmbienteAtivo,
                Ambiente = amb,
                ChaveMestraConfigurada = _protector.Configurado,
                Aplicacao = await AplicacaoAsync(cx, amb, ct),
                ContaZippy = await ContaZippyAsync(cx, amb, ct),
                AmbienteEstado = await AmbienteEstadoAsync(cx, amb, ct),
                UrlCallback = api == null ? null : api + CaminhoCallback,
                UrlWebhook = api == null ? null : api + CaminhoWebhook,
                UltimaVerificacao = await UltimaVerificacaoAsync(cx, amb, ct),
                Webhook = await WebhookSaudeAsync(cx, amb, ct),
                LojasConectadas = await cx.ExecuteScalarAsync<int>(new CommandDefinition(
                    "SELECT count(*)::int FROM mp_conta_loja WHERE ambiente = @Amb AND estado = 'ativa'", new { Amb = amb }, cancellationToken: ct))
            };
            dto.Etapas = MontarEtapas(dto);
            dto.Pronto = dto.Etapas.All(e => e.Status == "ok");
            return dto;
        }

        /// <summary>As 8 etapas do assistente. Nunca diz "ok" so porque existe uma chave: cada uma depende de prova (verificacao real ou evento recebido).</summary>
        public static List<EtapaImplantacaoDto> MontarEtapas(ConfiguracaoDto c)
        {
            var v = c.UltimaVerificacao is { Desatualizada: false } ? c.UltimaVerificacao : null;
            bool Passou(string etapa) => v?.Etapas.Any(e => e.Etapa == etapa && e.Ok) == true;
            string? Erro(string etapa) => v?.Etapas.FirstOrDefault(e => e.Etapa == etapa && !e.Ok)?.Mensagem;
            var lista = new List<EtapaImplantacaoDto>();
            void Add(string chave, string titulo, string status, string? falta) =>
                lista.Add(new EtapaImplantacaoDto { Numero = lista.Count + 1, Chave = chave, Titulo = titulo, Status = status, Falta = status == "ok" ? null : falta });

            var temCred = c.ChaveMestraConfigurada && c.Aplicacao.Origem != "nenhuma" && c.Aplicacao.TemClientSecret && c.ContaZippy.TemToken;
            Add("credenciais", "Cadastrar credenciais", temCred ? "ok" : "pendente",
                !c.ChaveMestraConfigurada ? "Falta a chave mestra no servidor (PAGAMENTOS_CHAVE_MESTRA). Peça ao suporte técnico."
                : c.Aplicacao.Origem == "nenhuma" || !c.Aplicacao.TemClientSecret ? "Falta cadastrar o Client ID e o Client Secret da aplicação."
                : "Falta cadastrar o Access Token da conta que recebe as mensalidades da Zippy.");

            string Status(bool pre, string etapa) => !pre ? "bloqueada" : Passou(etapa) ? "ok" : Erro(etapa) != null ? "erro" : "pendente";
            var sApp = Status(temCred, "aplicacao");
            Add("aplicacao", "Validar aplicação", sApp, Erro("aplicacao") ?? (temCred ? "Clique em \"Verificar agora\" para testar a aplicação no Mercado Pago." : "Conclua a etapa 1."));

            var sZippy = temCred ? (Passou("recebedor_zippy") && Passou("recebedor_pix") ? "ok" : Erro("recebedor_zippy") != null || Erro("recebedor_pix") != null ? "erro" : "pendente") : "bloqueada";
            Add("recebedor_zippy", "Validar conta recebedora Zippy", sZippy,
                Erro("recebedor_zippy") ?? Erro("recebedor_pix") ?? (temCred ? "Clique em \"Verificar agora\" para testar a conta recebedora." : "Conclua a etapa 1."));

            var urlsOk = Passou("callback") && Passou("webhook_segredo");
            Add("urls", "Callback e webhook", sApp != "ok" ? "bloqueada" : urlsOk ? "ok" : Erro("callback") != null || Erro("webhook_segredo") != null ? "erro" : "pendente",
                Erro("callback") ?? Erro("webhook_segredo") ?? "Cadastre no painel do Mercado Pago a URL de retorno e a URL do webhook mostradas aqui e copie a assinatura secreta.");

            var comunicacao = Passou("webhook_recebido");
            Add("comunicacao", "Testar comunicação", !urlsOk ? "bloqueada" : comunicacao ? "ok" : "pendente",
                c.Ambiente != c.AmbienteAtivo ? "O servidor está em outro ambiente: o webhook de teste só pode ser recebido com o servidor neste ambiente."
                : Erro("webhook_recebido") ?? "Use \"Simular notificação\" no painel do Mercado Pago e depois verifique de novo.");

            var verificado = v is { Aprovada: true };
            Add("ambiente", "Habilitar ambiente", !verificado ? "bloqueada" : c.AmbienteEstado.Habilitado ? "ok" : "pendente",
                verificado ? "Tudo verificado. Falta habilitar o ambiente." : c.UltimaVerificacao is { Desatualizada: true }
                    ? "As credenciais mudaram depois da última verificação. Verifique de novo." : "Conclua as etapas anteriores.");

            Add("lojas", "Restaurantes podem conectar", !c.AmbienteEstado.Habilitado ? "bloqueada" : c.AmbienteEstado.LojasPodemConectar ? "ok" : "pendente",
                c.AmbienteEstado.Habilitado ? "Libere o botão \"Conectar Mercado Pago\" para os restaurantes." : "Habilite o ambiente primeiro.");

            var anterioresOk = lista.All(e => e.Status == "ok");
            Add("prontidao", "Prontidão final", anterioresOk ? "ok" : "bloqueada",
                anterioresOk ? null : $"Faltam {lista.Count(e => e.Status != "ok")} etapa(s).");
            return lista;
        }

        public async Task<AplicacaoDto> SalvarAplicacaoAsync(SalvarAplicacaoRequest r, int usuario, CancellationToken ct = default)
        {
            if (string.IsNullOrWhiteSpace(r.Ambiente)) throw PagamentoException.Invalida("Informe o ambiente.", "ambiente");
            var amb = Amb(r.Ambiente);
            ExigirChaveMestra();
            var clientId = r.ClientId?.Trim() ?? string.Empty;
            if (clientId.Length is 0 or > 32 || !clientId.All(char.IsAsciiDigit)) throw PagamentoException.Invalida("O Client ID tem só números.", "clientId");
            var redirect = r.RedirectUri?.Trim();
            if (string.IsNullOrWhiteSpace(redirect) || !UrlSegura(redirect))
                throw PagamentoException.Invalida("A URL de retorno precisa começar com https://.", "redirectUri");
            var secret = string.IsNullOrWhiteSpace(r.ClientSecret) ? null : r.ClientSecret.Trim();
            var webhook = string.IsNullOrWhiteSpace(r.WebhookSecret) ? null : r.WebhookSecret.Trim();
            if (secret is { Length: > 256 } || webhook is { Length: > 256 }) throw PagamentoException.Invalida("Segredo grande demais.");

            await using var cx = await _db.OpenConnectionAsync(ct);
            await using var tx = await cx.BeginTransactionAsync(ct);
            var atual = await cx.QueryFirstOrDefaultAsync<(Guid Id, string ClientId, string? PublicKey, string? RedirectUri, bool TemWebhook)?>(new CommandDefinition(
                "SELECT id, client_id, public_key, redirect_uri, webhook_secret_cifrado IS NOT NULL FROM mp_aplicacao WHERE ambiente = @Amb FOR UPDATE",
                new { Amb = amb }, tx, cancellationToken: ct));
            if (atual == null && secret == null) throw PagamentoException.Invalida("Informe o Client Secret.", "clientSecret");

            var id = atual?.Id ?? Guid.NewGuid();
            var publicKey = string.IsNullOrWhiteSpace(r.PublicKey) ? null : r.PublicKey.Trim();
            await cx.ExecuteAsync(new CommandDefinition(@"
INSERT INTO mp_aplicacao (id, ambiente, client_id, client_secret_cifrado, public_key, webhook_secret_cifrado, redirect_uri, ativo, atualizado_por)
VALUES (@Id, @Amb, @ClientId, @Secret, @Pk, @Webhook, @Redirect, TRUE, @Usuario)
ON CONFLICT (ambiente) DO UPDATE SET client_id = EXCLUDED.client_id,
       client_secret_cifrado = COALESCE(@SecretNovo, mp_aplicacao.client_secret_cifrado), public_key = EXCLUDED.public_key,
       webhook_secret_cifrado = COALESCE(@WebhookNovo, mp_aplicacao.webhook_secret_cifrado), redirect_uri = EXCLUDED.redirect_uri,
       ativo = TRUE, atualizado_por = EXCLUDED.atualizado_por, updated_at = NOW()", new
            {
                Id = id, Amb = amb, ClientId = clientId, Pk = publicKey, Redirect = redirect, Usuario = usuario,
                Secret = secret == null ? string.Empty : _protector.Proteger(secret, SegredoContexto.AplicacaoClientSecret(id)),
                SecretNovo = secret == null ? null : _protector.Proteger(secret, SegredoContexto.AplicacaoClientSecret(id)),
                Webhook = webhook == null ? null : _protector.Proteger(webhook, SegredoContexto.AplicacaoWebhookSecret(id)),
                WebhookNovo = webhook == null ? null : _protector.Proteger(webhook, SegredoContexto.AplicacaoWebhookSecret(id))
            }, tx, cancellationToken: ct));

            // Auditoria diz O QUE mudou, nunca o valor de segredo.
            var mudou = new List<string>();
            if (atual == null) mudou.Add("criada");
            if (atual != null && atual.Value.ClientId != clientId) mudou.Add("client_id");
            if (secret != null) mudou.Add("client_secret");
            if (webhook != null) mudou.Add("webhook_secret");
            if (atual != null && atual.Value.PublicKey != publicKey) mudou.Add("public_key");
            if (atual != null && atual.Value.RedirectUri != redirect) mudou.Add("redirect_uri");
            await PagamentoAuditoria.RegistrarAsync(cx, tx, null, "aplicacao", amb, "aplicacao_alterada", "usuario", usuario,
                new { ambiente = amb, campos = mudou, clientId }, ct);
            await tx.CommitAsync(ct);
            _logger.LogInformation("[pag.gestao] ev=aplicacao_alterada amb={Amb} campos={Campos}", amb, string.Join(",", mudou));
            return await AplicacaoAsync(cx, amb, ct);
        }

        public async Task<ContaZippyDto> SalvarContaZippyAsync(SalvarContaZippyRequest r, int usuario, CancellationToken ct = default)
        {
            if (string.IsNullOrWhiteSpace(r.Ambiente)) throw PagamentoException.Invalida("Informe o ambiente.", "ambiente");
            var amb = Amb(r.Ambiente);
            ExigirChaveMestra();
            var token = r.AccessToken?.Trim();
            if (string.IsNullOrWhiteSpace(token) || token.Length > 256) throw PagamentoException.Invalida("Informe o Access Token da conta Zippy.", "accessToken");

            // Valida o token antes de gravar: quem e o dono e se a conta esta ativa.
            MpUsuario me;
            try
            {
                me = await _client.ObterUsuarioAsync(new CredencialRecebedora { Contexto = ContextoCredencial.RecebedorZippy, Ambiente = amb, AccessToken = token }, ct);
            }
            catch (MercadoPagoApiException ex) when (ex.StatusHttp is 400 or 401 or 403)
            {
                throw PagamentoException.Regra(PagamentoCodigos.MercadoPagoErro, "O Mercado Pago recusou este Access Token. Confira se copiou o token do ambiente certo.");
            }
            catch (MercadoPagoApiException ex) { throw ex.ParaPagamento(); }
            if (me.Id <= 0) throw new PagamentoException(PagamentoCodigos.MercadoPagoRespostaInconsistente, 502, "O Mercado Pago nao informou o dono do token.");

            // A conta da Zippy nunca e a mesma de uma loja (o dinheiro das mensalidades nao se mistura com vendas).
            await using var cx = await _db.OpenConnectionAsync(ct);
            var deLoja = await cx.ExecuteScalarAsync<bool>(new CommandDefinition(
                "SELECT EXISTS (SELECT 1 FROM mp_conta_loja WHERE ambiente = @Amb AND mp_user_id = @U AND estado IN ('ativa', 'expirada', 'erro'))",
                new { Amb = amb, U = me.Id }, cancellationToken: ct));
            if (deLoja) throw PagamentoException.Regra(ConexaoCodigos.ContaDeOutraLoja, "Esta conta Mercado Pago ja esta conectada a um restaurante. Use a conta da Zippy.");

            await using var tx = await cx.BeginTransactionAsync(ct);
            var atual = await cx.QueryFirstOrDefaultAsync<(Guid Id, long MpUserId)?>(new CommandDefinition(
                "SELECT id, mp_user_id FROM mp_conta_zippy WHERE ambiente = @Amb FOR UPDATE", new { Amb = amb }, tx, cancellationToken: ct));
            var id = atual?.Id ?? Guid.NewGuid();
            var publicKey = string.IsNullOrWhiteSpace(r.PublicKey) ? null : r.PublicKey.Trim();
            var ativa = me.Status?.SiteStatus is null || string.Equals(me.Status.SiteStatus, "active", StringComparison.OrdinalIgnoreCase);
            await cx.ExecuteAsync(new CommandDefinition(@"
INSERT INTO mp_conta_zippy (id, ambiente, mp_user_id, mp_user_id_mascarado, access_token_cifrado, public_key, estado, ultimo_erro, validada_em, atualizado_por)
VALUES (@Id, @Amb, @U, @Mascara, @Token, @Pk, @Estado, @Erro, NOW(), @Usuario)
ON CONFLICT (ambiente) DO UPDATE SET mp_user_id = EXCLUDED.mp_user_id, mp_user_id_mascarado = EXCLUDED.mp_user_id_mascarado,
       access_token_cifrado = EXCLUDED.access_token_cifrado, public_key = COALESCE(EXCLUDED.public_key, mp_conta_zippy.public_key),
       estado = EXCLUDED.estado, ultimo_erro = EXCLUDED.ultimo_erro, validada_em = NOW(), atualizado_por = EXCLUDED.atualizado_por, updated_at = NOW()", new
            {
                Id = id, Amb = amb, U = me.Id, Mascara = CredencialRecebedora.MascararId(me.Id), Pk = publicKey, Usuario = usuario,
                Token = _protector.Proteger(token, SegredoContexto.ZippyAccessToken(id)), Estado = ativa ? "ativa" : "erro", Erro = ativa ? null : "conta_com_restricao"
            }, tx, cancellationToken: ct));
            await PagamentoAuditoria.RegistrarAsync(cx, tx, null, "conta_zippy", amb, "conta_zippy_alterada", "usuario", usuario,
                new { ambiente = amb, mp = CredencialRecebedora.MascararId(me.Id), trocouConta = atual != null && atual.Value.MpUserId != me.Id, ativa }, ct);
            await tx.CommitAsync(ct);
            return await ContaZippyAsync(cx, amb, ct);
        }

        public async Task<TesteContaZippyDto> TestarContaZippyAsync(string? ambiente, int usuario, CancellationToken ct = default)
        {
            var amb = Amb(ambiente);
            var etapas = new List<EtapaVerificacao>();
            await VerificarZippyAsync(amb, etapas, ct);
            await using var cx = await _db.OpenConnectionAsync(ct);
            var ok = etapas.All(e => e.Ok);
            await PagamentoAuditoria.RegistrarAsync(cx, null, null, "conta_zippy", amb, "conta_zippy_testada", "usuario", usuario, new { ok }, ct);
            if (!ok)
            {
                var falha = etapas.First(e => !e.Ok);
                throw falha.Codigo == PagamentoCodigos.NaoConfigurado ? PagamentoException.NaoConfigurado(falha.Mensagem) : PagamentoException.Regra(falha.Codigo ?? PagamentoCodigos.MercadoPagoErro, falha.Mensagem);
            }
            var conta = await ContaZippyAsync(cx, amb, ct);
            return new TesteContaZippyDto(true, conta.MpUserIdMascarado);
        }

        // ---- verificacao ----------------------------------------------------------------------------------------------

        public async Task<VerificacaoDto> VerificarAsync(string? ambiente, int usuario, CancellationToken ct = default)
        {
            var amb = Amb(ambiente);
            var etapas = new List<EtapaVerificacao>();
            void Add(string etapa, bool ok, string? codigo, string msg) => etapas.Add(new EtapaVerificacao { Etapa = etapa, Ok = ok, Codigo = ok ? null : codigo, Mensagem = msg });

            Add("chave_mestra", _protector.Configurado, PagamentoCodigos.NaoConfigurado,
                _protector.Configurado ? "Chave mestra configurada no servidor." : "Falta a chave mestra (PAGAMENTOS_CHAVE_MESTRA) no servidor.");

            // Aplicacao: client_credentials prova que client_id e client_secret valem.
            CredencialAplicacao? app = null;
            try { app = await _credenciais.AplicacaoAsync(amb, ct); }
            catch (PagamentoException) { Add("aplicacao", false, PagamentoCodigos.NaoConfigurado, "Aplicação não cadastrada ou segredo não abre com a chave mestra atual."); }
            if (app != null)
            {
                if (string.IsNullOrWhiteSpace(app.ClientId)) Add("aplicacao", false, PagamentoCodigos.NaoConfigurado, "Aplicação sem Client ID.");
                else
                {
                    try
                    {
                        await _client.ValidarAplicacaoAsync(app, ct);
                        Add("aplicacao", true, null, "O Mercado Pago aceitou o Client ID e o Client Secret.");
                    }
                    catch (MercadoPagoApiException ex)
                    {
                        Add("aplicacao", false, ex.Indisponivel ? PagamentoCodigos.MercadoPagoIndisponivel : PagamentoCodigos.MercadoPagoErro,
                            ex.Indisponivel ? "O Mercado Pago não respondeu. Tente de novo em alguns minutos." : "O Mercado Pago recusou o Client ID ou o Client Secret.");
                    }
                }

                var esperado = ApiPublica(_options) is { } api ? api + CaminhoCallback : null;
                var redirect = app.RedirectUri?.Trim();
                var callbackOk = !string.IsNullOrWhiteSpace(redirect) && UrlSegura(redirect) &&
                                 (esperado == null || string.Equals(redirect, esperado, StringComparison.OrdinalIgnoreCase));
                Add("callback", callbackOk, "callback_divergente", callbackOk ? "URL de retorno cadastrada." : string.IsNullOrWhiteSpace(redirect)
                    ? "Falta cadastrar a URL de retorno (Redirect URL)."
                    : $"A URL de retorno precisa ser exatamente {esperado ?? "a URL https do callback desta API"}.");

                var notif = _options.NotificationUrl?.Trim();
                var webhookOk = app.TemWebhookSecret && !string.IsNullOrWhiteSpace(notif) && UrlSegura(notif);
                Add("webhook_segredo", webhookOk, "webhook_incompleto", webhookOk ? "Assinatura secreta do webhook cadastrada."
                    : !app.TemWebhookSecret ? "Falta copiar a assinatura secreta do webhook do painel do Mercado Pago."
                    : "O servidor está sem a URL pública do webhook (Payments__MercadoPago__NotificationUrl).");
            }

            await VerificarZippyAsync(amb, etapas, ct);

            await using var cx = await _db.OpenConnectionAsync(ct);
            // Comunicacao: um aviso com assinatura valida so e gravado se o segredo bate. Simular notificacao no painel basta.
            if (amb == _credenciais.AmbienteAtivo)
            {
                var ultimo = await cx.ExecuteScalarAsync<DateTime?>(new CommandDefinition(
                    "SELECT max(recebido_em) FROM mp_webhook_evento WHERE ambiente = @Amb AND recebido_em > NOW() - INTERVAL '7 days'", new { Amb = amb }, cancellationToken: ct));
                Add("webhook_recebido", ultimo != null, "webhook_sem_eventos", ultimo != null
                    ? $"Último aviso do Mercado Pago recebido e conferido em {ultimo:dd/MM HH:mm} (UTC)."
                    : "Nenhum aviso do Mercado Pago chegou nos últimos 7 dias. Use \"Simular notificação\" no painel e verifique de novo.");
            }
            else Add("webhook_recebido", false, "ambiente_inativo", "O servidor está em outro ambiente; o webhook deste ambiente não pode ser testado agora.");

            var v = new VerificacaoDto { Id = Guid.NewGuid(), Ambiente = amb, Aprovada = etapas.All(e => e.Ok), Etapas = etapas, CriadoEm = DateTime.UtcNow };
            await using var tx = await cx.BeginTransactionAsync(ct);
            await cx.ExecuteAsync(new CommandDefinition(@"
INSERT INTO mp_verificacao (id, ambiente, aprovada, etapas, executada_por, created_at) VALUES (@Id, @Amb, @Ok, CAST(@Etapas AS jsonb), @Usuario, @Em)",
                new { v.Id, Amb = amb, Ok = v.Aprovada, Etapas = JsonSerializer.Serialize(etapas, Json), Usuario = usuario, Em = v.CriadoEm }, tx, cancellationToken: ct));
            var zippyOk = etapas.Where(e => e.Etapa.StartsWith("recebedor_", StringComparison.Ordinal)).All(e => e.Ok);
            await cx.ExecuteAsync(new CommandDefinition(@"
UPDATE mp_conta_zippy SET estado = CASE WHEN @Ok THEN 'ativa' ELSE 'erro' END, ultimo_erro = @Erro, validada_em = CASE WHEN @Ok THEN NOW() ELSE validada_em END,
       updated_at = updated_at
 WHERE ambiente = @Amb AND estado <> 'inativa'", new
            {
                Amb = amb, Ok = zippyOk, Erro = zippyOk ? null : etapas.First(e => e.Etapa.StartsWith("recebedor_", StringComparison.Ordinal) && !e.Ok).Codigo
            }, tx, cancellationToken: ct));
            await PagamentoAuditoria.RegistrarAsync(cx, tx, null, "verificacao", v.Id.ToString(), "verificacao_executada", "usuario", usuario,
                new { ambiente = amb, aprovada = v.Aprovada, falhas = etapas.Where(e => !e.Ok).Select(e => e.Etapa).ToArray() }, ct);
            await tx.CommitAsync(ct);
            return v;
        }

        private async Task VerificarZippyAsync(string amb, List<EtapaVerificacao> etapas, CancellationToken ct)
        {
            void Add(string etapa, bool ok, string? codigo, string msg) => etapas.Add(new EtapaVerificacao { Etapa = etapa, Ok = ok, Codigo = ok ? null : codigo, Mensagem = msg });
            CredencialRecebedora cred;
            try { cred = await RecebedorZippyMesmoInativoAsync(amb, ct); }
            catch (PagamentoException ex)
            {
                Add("recebedor_zippy", false, PagamentoCodigos.NaoConfigurado, ex.Message.Contains("abre") ? "O token da conta Zippy não abre com a chave mestra atual. Cadastre de novo."
                    : "Conta recebedora da Zippy não cadastrada.");
                Add("recebedor_pix", false, PagamentoCodigos.NaoConfigurado, "Depende da conta recebedora.");
                return;
            }
            try
            {
                var me = await _client.ObterUsuarioAsync(cred, ct);
                if (me.Id != cred.MpUserId) Add("recebedor_zippy", false, PagamentoCodigos.CredencialIncompativel, "O token respondeu com outra conta. Cadastre o token de novo.");
                else if (me.Status?.SiteStatus is { } s && !string.Equals(s, "active", StringComparison.OrdinalIgnoreCase))
                    Add("recebedor_zippy", false, "conta_com_restricao", "O Mercado Pago informa restrição na conta da Zippy. Resolva no painel do Mercado Pago.");
                else Add("recebedor_zippy", true, null, $"Conta recebedora {CredencialRecebedora.MascararId(me.Id)} ativa.");

                var meios = await _client.MeiosDePagamentoAsync(cred, ct);
                var pix = meios.Any(m => m.Id == "pix" && m.Status is null or "active");
                Add("recebedor_pix", pix, "pix_indisponivel", pix ? "Pix liberado na conta da Zippy." : "Pix indisponível: cadastre uma chave Pix na conta da Zippy no Mercado Pago.");
            }
            catch (MercadoPagoApiException ex)
            {
                var msg = ex.StatusHttp is 401 or 403 ? "O Mercado Pago recusou o token da conta Zippy (vencido ou revogado). Cadastre um novo."
                    : "O Mercado Pago não respondeu. Tente de novo em alguns minutos.";
                Add("recebedor_zippy", false, ex.StatusHttp is 401 or 403 ? "token_invalido" : PagamentoCodigos.MercadoPagoIndisponivel, msg);
                Add("recebedor_pix", false, "nao_testado", "Depende da conta recebedora.");
            }
        }

        /// <summary>O resolvedor recusa conta 'erro'/'inativa'; a verificacao precisa testar mesmo assim para poder recuperar.</summary>
        private async Task<CredencialRecebedora> RecebedorZippyMesmoInativoAsync(string amb, CancellationToken ct)
        {
            await using var cx = await _db.OpenConnectionAsync(ct);
            var linha = await cx.QueryFirstOrDefaultAsync<(Guid Id, long MpUserId, string Token, string? Pk)?>(new CommandDefinition(
                "SELECT id, mp_user_id, access_token_cifrado, public_key FROM mp_conta_zippy WHERE ambiente = @Amb", new { Amb = amb }, cancellationToken: ct));
            if (linha == null) throw PagamentoException.NaoConfigurado("Conta recebedora da Zippy nao configurada.");
            var token = _protector.Revelar(linha.Value.Token, SegredoContexto.ZippyAccessToken(linha.Value.Id))
                        ?? throw PagamentoException.NaoConfigurado("Token da conta Zippy nao abre com a chave mestra atual.");
            return new CredencialRecebedora
            {
                Contexto = ContextoCredencial.RecebedorZippy, Ambiente = amb, IdContaZippy = linha.Value.Id, MpUserId = linha.Value.MpUserId,
                PublicKey = linha.Value.Pk, AccessToken = token
            };
        }

        public async Task<ConfiguracaoDto> AtualizarAmbienteAsync(AtualizarAmbienteRequest r, int usuario, CancellationToken ct = default)
        {
            if (string.IsNullOrWhiteSpace(r.Ambiente)) throw PagamentoException.Invalida("Informe o ambiente.", "ambiente");
            var amb = Amb(r.Ambiente);
            if (r.LojasPodemConectar && !r.Habilitado) throw PagamentoException.Invalida("Habilite o ambiente antes de liberar os restaurantes.", "lojasPodemConectar");

            await using var cx = await _db.OpenConnectionAsync(ct);
            await using var tx = await cx.BeginTransactionAsync(ct);
            var atual = await cx.QueryFirstOrDefaultAsync<AmbienteEstadoDto>(new CommandDefinition(
                "SELECT habilitado, lojas_podem_conectar, habilitado_em FROM mp_ambiente_estado WHERE ambiente = @Amb FOR UPDATE", new { Amb = amb }, tx, cancellationToken: ct))
                ?? new AmbienteEstadoDto();

            Guid? verificacao = null;
            if (r.Habilitado && !atual.Habilitado)
            {
                // Ligar exige a ultima verificacao aprovada e mais nova que qualquer troca de credencial (vale para sandbox e producao).
                var v = await UltimaVerificacaoAsync(cx, amb, ct, tx);
                if (v is not { Aprovada: true, Desatualizada: false })
                    throw PagamentoException.Regra(ConexaoCodigos.VerificacaoPendente, v is { Desatualizada: true }
                        ? "As credenciais mudaram depois da última verificação. Verifique de novo antes de habilitar."
                        : "Habilite só depois de uma verificação aprovada em todas as etapas.");
                verificacao = v.Id;
            }

            await cx.ExecuteAsync(new CommandDefinition(@"
INSERT INTO mp_ambiente_estado (ambiente, habilitado, lojas_podem_conectar, id_verificacao, habilitado_por, habilitado_em, updated_at)
VALUES (@Amb, @Hab, @Lojas, @Ver, @Usuario, CASE WHEN @Hab THEN NOW() END, NOW())
ON CONFLICT (ambiente) DO UPDATE SET habilitado = EXCLUDED.habilitado, lojas_podem_conectar = EXCLUDED.lojas_podem_conectar,
       id_verificacao = COALESCE(EXCLUDED.id_verificacao, mp_ambiente_estado.id_verificacao),
       habilitado_por = CASE WHEN EXCLUDED.habilitado AND NOT mp_ambiente_estado.habilitado THEN EXCLUDED.habilitado_por ELSE mp_ambiente_estado.habilitado_por END,
       habilitado_em = CASE WHEN NOT EXCLUDED.habilitado THEN NULL WHEN mp_ambiente_estado.habilitado THEN mp_ambiente_estado.habilitado_em ELSE NOW() END,
       updated_at = NOW()", new { Amb = amb, Hab = r.Habilitado, Lojas = r.LojasPodemConectar, Ver = verificacao, Usuario = usuario }, tx, cancellationToken: ct));
            await PagamentoAuditoria.RegistrarAsync(cx, tx, null, "ambiente", amb, "ambiente_alterado", "usuario", usuario, new
            {
                ambiente = amb, antes = new { atual.Habilitado, atual.LojasPodemConectar }, depois = new { r.Habilitado, r.LojasPodemConectar }, verificacao
            }, ct);
            await tx.CommitAsync(ct);
            return await ConfiguracaoAsync(amb, ct);
        }

        // ---- leitura ---------------------------------------------------------------------------------------------------

        public async Task<ResumoGestaoDto> ResumoAsync(DateOnly? mes = null, CancellationToken ct = default)
        {
            var amb = _credenciais.AmbienteAtivo;
            await using var cx = await _db.OpenConnectionAsync(ct);
            // Mes no fuso de Brasilia (o mesmo do Dinheiro da Gestao).
            var m = mes ?? DateOnly.FromDateTime(DateTime.UtcNow.AddHours(-3));
            var inicioMes = new DateTime(m.Year, m.Month, 1, 3, 0, 0, DateTimeKind.Utc);
            var fimMes = inicioMes.AddMonths(1);
            var r = await cx.QueryFirstAsync<ResumoGestaoDto>(new CommandDefinition(@"
SELECT
  COALESCE((SELECT sum(valor_pago) FROM pagamento_intent WHERE ambiente = @Amb AND conta_tipo = 'zippy' AND pago_em >= @Mes AND pago_em < @Fim), 0) AS recebido_zippy_mes,
  COALESCE((SELECT sum(valor_estornado) FROM pagamento_intent WHERE ambiente = @Amb AND conta_tipo = 'zippy' AND pago_em >= @Mes AND pago_em < @Fim), 0) AS estornado_zippy_mes,
  (SELECT count(*)::int FROM pagamento_intent WHERE ambiente = @Amb AND conta_tipo = 'zippy' AND status IN ('criado', 'aguardando_pagamento', 'processando')) AS cobrancas_zippy_abertas,
  COALESCE((SELECT sum(valor) FROM pagamento_intent WHERE ambiente = @Amb AND conta_tipo = 'zippy' AND status IN ('criado', 'aguardando_pagamento', 'processando')), 0) AS valor_zippy_aberto,
  COALESCE((SELECT sum(valor_pago) FROM pagamento_intent WHERE ambiente = @Amb AND conta_tipo = 'loja' AND pago_em >= @Mes AND pago_em < @Fim), 0) AS vendas_lojas_mes,
  (SELECT count(*)::int FROM pagamento_intent WHERE ambiente = @Amb AND conta_tipo = 'loja' AND pago_em >= @Mes AND pago_em < @Fim) AS transacoes_lojas_mes,
  (SELECT count(*)::int FROM mp_conta_loja WHERE ambiente = @Amb AND estado = 'ativa') AS lojas_conectadas,
  (SELECT count(*)::int FROM mp_conta_loja WHERE ambiente = @Amb AND (estado IN ('expirada', 'erro') OR (estado = 'ativa' AND ultimo_erro IS NOT NULL))) AS lojas_com_problema,
  (SELECT count(*)::int FROM mp_webhook_evento WHERE ambiente = @Amb AND status IN ('falhou', 'rejeitado') AND recebido_em > NOW() - INTERVAL '7 days') AS webhooks_com_falha,
  (SELECT count(*)::int FROM pagamento_disputa d JOIN pagamento p ON p.id = d.id_pagamento WHERE p.ambiente = @Amb AND d.status IN ('aberta', 'em_analise')) AS disputas_abertas,
  (SELECT count(*)::int FROM pagamento_estorno e JOIN pagamento p ON p.id = e.id_pagamento WHERE p.ambiente = @Amb AND e.status IN ('solicitado', 'processando')) AS reembolsos_pendentes",
                new { Amb = amb, Mes = inicioMes, Fim = fimMes }, cancellationToken: ct));
            r.Ambiente = amb;
            r.Mes = m.ToString("yyyy-MM");
            var cfg = await ConfiguracaoAsync(amb, ct);
            r.Pronto = cfg.Pronto;
            r.AmbienteHabilitado = cfg.AmbienteEstado.Habilitado;
            return r;
        }

        public async Task<Pagina<ConexaoLojaGestaoDto>> ConexoesAsync(string? busca, string? estado, int pagina, CancellationToken ct = default)
        {
            var amb = _credenciais.AmbienteAtivo;
            await using var cx = await _db.OpenConnectionAsync(ct);
            const string Base = @"
  FROM estabelecimentos e
  LEFT JOIN LATERAL (SELECT * FROM mp_conta_loja c WHERE c.id_estabelecimento = e.id AND c.ambiente = @Amb
                      ORDER BY (c.estado IN ('ativa', 'expirada', 'erro')) DESC, c.updated_at DESC LIMIT 1) c ON TRUE
  LEFT JOIN pagamento_preferencia_loja p ON p.id_estabelecimento = e.id
 WHERE (@Busca::text IS NULL OR e.nome_fantasia ILIKE '%' || @Busca || '%')
   AND (@Estado::text IS NULL OR COALESCE(c.estado, 'nao_conectada') = @Estado)";
            var args = new { Amb = amb, Busca = Limpo(busca), Estado = Limpo(estado), Offset = Offset(pagina), Limite = TamanhoPagina };
            return new Pagina<ConexaoLojaGestaoDto>
            {
                Total = await cx.ExecuteScalarAsync<int>(new CommandDefinition("SELECT count(*)::int" + Base, args, cancellationToken: ct)),
                Itens = (await cx.QueryAsync<ConexaoLojaGestaoDto>(new CommandDefinition(@"
SELECT e.id AS estabelecimento_id, COALESCE(e.nome_fantasia, 'Loja') AS nome, COALESCE(c.estado, 'nao_conectada') AS estado,
       c.mp_user_id_mascarado, CASE WHEN c.estado IN ('ativa', 'expirada', 'erro') THEN c.token_expira_em END AS token_expira_em,
       c.conectado_em, c.ultimo_erro, COALESCE(p.pix_ativo, FALSE) AS pix_ativo, COALESCE(p.cartao_ativo, FALSE) AS cartao_ativo" + Base + @"
 ORDER BY (c.estado IN ('expirada', 'erro')) DESC NULLS LAST, (c.estado = 'ativa') DESC NULLS LAST, e.nome_fantasia
 LIMIT @Limite OFFSET @Offset", args, cancellationToken: ct))).ToList()
            };
        }

        private const string SqlTransacao = @"
SELECT i.id, i.id_estabelecimento AS estabelecimento_id, e.nome_fantasia AS loja_nome, i.origem, i.conta_tipo, i.status, i.valor, i.valor_pago,
       i.valor_estornado, i.descricao, p.metodo, p.status AS status_pagamento, p.mp_status_detail AS status_detalhe, p.mp_payment_id,
       i.pago_em, i.created_at AS criado_em";

        public async Task<TransacoesGestaoDto> TransacoesAsync(string? conta, Guid? loja, string? origem, string? status, DateTime? de, DateTime? ate, int pagina,
            CancellationToken ct = default)
        {
            // Nunca mistura: a lista e as somas sao SEMPRE de uma conta so (dinheiro da Zippy ou das lojas).
            var tipo = string.IsNullOrWhiteSpace(conta) ? "loja" : conta.Trim();
            if (tipo is not ("loja" or "zippy")) throw PagamentoException.Invalida("Conta deve ser loja ou zippy.", "conta");
            if (de > ate) throw PagamentoException.Invalida("Periodo invalido.", "de");
            var org = Limpo(origem);
            if (org != null && !new[] { OrigensPagamento.Cardapio, OrigensPagamento.Chat, OrigensPagamento.Assinatura, OrigensPagamento.CompraExtra }.Contains(org))
                throw PagamentoException.Invalida("Origem desconhecida.", "origem");
            return await ListarTransacoesAsync(tipo, loja, org == null ? null : new[] { org }, Limpo(status), de, ate, pagina, ct);
        }

        public Task<TransacoesGestaoDto> AssinaturasAsync(string? status, int pagina, CancellationToken ct = default) =>
            ListarTransacoesAsync("zippy", null, new[] { OrigensPagamento.Assinatura, OrigensPagamento.CompraExtra }, Limpo(status), null, null, pagina, ct);

        private async Task<TransacoesGestaoDto> ListarTransacoesAsync(string conta, Guid? loja, string[]? origens, string? status, DateTime? de, DateTime? ate, int pagina,
            CancellationToken ct)
        {
            await using var cx = await _db.OpenConnectionAsync(ct);
            const string Filtro = @"
  FROM pagamento_intent i
  LEFT JOIN estabelecimentos e ON e.id = i.id_estabelecimento
  LEFT JOIN LATERAL (SELECT * FROM pagamento x WHERE x.id_intent = i.id ORDER BY x.created_at DESC LIMIT 1) p ON TRUE
 WHERE i.ambiente = @Amb AND i.conta_tipo = @Conta
   AND (@Loja::uuid IS NULL OR i.id_estabelecimento = @Loja)
   AND (@Origens::text[] IS NULL OR i.origem = ANY(@Origens))
   AND (@Status::text IS NULL OR i.status = @Status)
   AND (@De::timestamptz IS NULL OR i.created_at >= @De)
   AND (@Ate::timestamptz IS NULL OR i.created_at < @Ate)";
            var args = new { Amb = _credenciais.AmbienteAtivo, Conta = conta, Loja = loja, Origens = origens, Status = status, De = de, Ate = ate?.AddDays(1), Offset = Offset(pagina), Limite = TamanhoPagina };
            var somas = await cx.QueryFirstAsync<(int Total, decimal Paga, decimal Estornada)>(new CommandDefinition(
                "SELECT count(*)::int, COALESCE(sum(i.valor_pago), 0), COALESCE(sum(i.valor_estornado), 0)" + Filtro, args, cancellationToken: ct));
            return new TransacoesGestaoDto
            {
                Conta = conta, Total = somas.Total, SomaPaga = somas.Paga, SomaEstornada = somas.Estornada,
                Itens = (await cx.QueryAsync<TransacaoGestaoDto>(new CommandDefinition(SqlTransacao + Filtro + " ORDER BY i.created_at DESC LIMIT @Limite OFFSET @Offset",
                    args, cancellationToken: ct))).ToList()
            };
        }

        public async Task<Pagina<ReembolsoGestaoDto>> ReembolsosAsync(string? status, string? conta, int pagina, CancellationToken ct = default)
        {
            var tipo = Limpo(conta);
            if (tipo != null && tipo is not ("loja" or "zippy")) throw PagamentoException.Invalida("Conta deve ser loja ou zippy.", "conta");
            await using var cx = await _db.OpenConnectionAsync(ct);
            const string Filtro = @"
  FROM pagamento_estorno r
  JOIN pagamento p ON p.id = r.id_pagamento
  JOIN pagamento_intent i ON i.id = p.id_intent
  LEFT JOIN estabelecimentos e ON e.id = i.id_estabelecimento
 WHERE p.ambiente = @Amb AND (@Status::text IS NULL OR r.status = @Status) AND (@Conta::text IS NULL OR i.conta_tipo = @Conta)";
            var args = new { Amb = _credenciais.AmbienteAtivo, Status = Limpo(status), Conta = tipo, Offset = Offset(pagina), Limite = TamanhoPagina };
            return new Pagina<ReembolsoGestaoDto>
            {
                Total = await cx.ExecuteScalarAsync<int>(new CommandDefinition("SELECT count(*)::int" + Filtro, args, cancellationToken: ct)),
                Itens = (await cx.QueryAsync<ReembolsoGestaoDto>(new CommandDefinition(@"
SELECT r.id, i.id AS intent_id, i.id_estabelecimento AS estabelecimento_id, e.nome_fantasia AS loja_nome, i.conta_tipo, i.origem, r.valor, r.status,
       r.origem AS origem_estorno, r.motivo, r.erro_mensagem, r.created_at AS criado_em, r.concluido_em" + Filtro + @"
 ORDER BY r.created_at DESC LIMIT @Limite OFFSET @Offset", args, cancellationToken: ct))).ToList()
            };
        }

        public async Task<WebhooksGestaoDto> WebhooksAsync(string? status, string? topico, int pagina, CancellationToken ct = default)
        {
            var amb = _credenciais.AmbienteAtivo;
            await using var cx = await _db.OpenConnectionAsync(ct);
            const string Filtro = " FROM mp_webhook_evento WHERE ambiente = @Amb AND (@Status::text IS NULL OR status = @Status) AND (@Topico::text IS NULL OR topico = @Topico)";
            var args = new { Amb = amb, Status = Limpo(status), Topico = Limpo(topico), Offset = Offset(pagina), Limite = TamanhoPagina };
            return new WebhooksGestaoDto
            {
                Total = await cx.ExecuteScalarAsync<int>(new CommandDefinition("SELECT count(*)::int" + Filtro, args, cancellationToken: ct)),
                // Sem payload bruto: so o que identifica o aviso e o que aconteceu com ele.
                Itens = (await cx.QueryAsync<WebhookGestaoDto>(new CommandDefinition(@"
SELECT id, ambiente, topico, acao, data_id, status, tentativas, ultimo_erro, recebido_em, processado_em" + Filtro + @"
 ORDER BY recebido_em DESC LIMIT @Limite OFFSET @Offset", args, cancellationToken: ct))).ToList(),
                Saude = await WebhookSaudeAsync(cx, amb, ct)
            };
        }

        public async Task<string> ReprocessarWebhookAsync(Guid id, int usuario, CancellationToken ct = default)
        {
            await using var cx = await _db.OpenConnectionAsync(ct);
            var atual = await cx.ExecuteScalarAsync<string?>(new CommandDefinition("SELECT status FROM mp_webhook_evento WHERE id = @Id", new { Id = id }, cancellationToken: ct))
                        ?? throw PagamentoException.NaoEncontrado("Aviso nao encontrado.");
            // Rejeitado (assinatura/dono/valor divergente) nunca volta: e protecao, nao falha.
            if (atual is not ("falhou" or "ignorado" or "recebido")) throw PagamentoException.Estado("Este aviso nao pode ser reprocessado.", atual);
            await cx.ExecuteAsync(new CommandDefinition(
                "UPDATE mp_webhook_evento SET status = 'recebido', proxima_tentativa_em = NOW() WHERE id = @Id AND status IN ('falhou', 'ignorado', 'recebido')",
                new { Id = id }, cancellationToken: ct));
            string status;
            try { status = await _webhook.ProcessarAsync(id, ct); }
            catch (Exception ex) when (ex is not OperationCanceledException) { status = "falhou"; }
            await PagamentoAuditoria.RegistrarAsync(cx, null, null, "webhook", id.ToString(), "webhook_reprocessado", "usuario", usuario,
                new { antes = atual, depois = status }, ct);
            return status;
        }

        public async Task<ReconciliacaoDto> ReconciliarAsync(int usuario, CancellationToken ct = default)
        {
            await using var cx = await _db.OpenConnectionAsync(ct);
            const string Contagem = @"
SELECT (SELECT count(*)::int FROM pagamento WHERE status IN ('pendente', 'em_processo', 'autorizado', 'em_mediacao')),
       (SELECT count(*)::int FROM mp_webhook_evento WHERE status IN ('recebido', 'falhou', 'processando'))";
            var antes = await cx.QueryFirstAsync<(int Pag, int Web)>(new CommandDefinition(Contagem, cancellationToken: ct));
            // Mesmo ciclo do conciliador automatico (trava entre instancias); "forca" a idade minima ao chamar agora.
            await cx.ExecuteAsync(new CommandDefinition(
                "UPDATE mp_webhook_evento SET proxima_tentativa_em = NOW() WHERE status IN ('recebido', 'falhou') AND proxima_tentativa_em > NOW()", cancellationToken: ct));
            var worker = ActivatorUtilities.CreateInstance<PagamentoConciliadorWorker>(_services);
            await worker.RodarAsync(ct);
            var depois = await cx.QueryFirstAsync<(int Pag, int Web)>(new CommandDefinition(Contagem, cancellationToken: ct));
            var r = new ReconciliacaoDto
            {
                PagamentosAbertosAntes = antes.Pag, PagamentosAbertosDepois = depois.Pag, WebhooksPendentesAntes = antes.Web, WebhooksPendentesDepois = depois.Web,
                ExecutadaEm = DateTime.UtcNow
            };
            await PagamentoAuditoria.RegistrarAsync(cx, null, null, "conciliacao", r.ExecutadaEm.ToString("O"), "conciliacao_manual", "usuario", usuario, r, ct);
            return r;
        }

        public async Task<List<DisputaGestaoDto>> DisputasAsync(string? status, CancellationToken ct = default)
        {
            await using var cx = await _db.OpenConnectionAsync(ct);
            return (await cx.QueryAsync<DisputaGestaoDto>(new CommandDefinition(@"
SELECT d.id, d.tipo, d.status, d.valor, d.prazo_resposta_em, p.id_intent AS intent_id, d.id_estabelecimento AS estabelecimento_id, e.nome_fantasia AS loja_nome,
       d.created_at AS criado_em
  FROM pagamento_disputa d JOIN pagamento p ON p.id = d.id_pagamento LEFT JOIN estabelecimentos e ON e.id = d.id_estabelecimento
 WHERE p.ambiente = @Amb AND (@Status::text IS NULL OR d.status = @Status)
 ORDER BY d.prazo_resposta_em NULLS LAST, d.created_at DESC LIMIT 200", new { Amb = _credenciais.AmbienteAtivo, Status = Limpo(status) }, cancellationToken: ct))).ToList();
        }

        public async Task<Pagina<AuditoriaGestaoDto>> AuditoriaAsync(string? escopo, string? entidade, string? entidadeId, int pagina, CancellationToken ct = default)
        {
            var gestao = string.Equals(escopo, "gestao", StringComparison.OrdinalIgnoreCase);
            if (!gestao && entidade == null) throw PagamentoException.Invalida("Informe escopo=gestao ou a entidade.", "entidade");
            await using var cx = await _db.OpenConnectionAsync(ct);
            const string Filtro = @"
  FROM pagamento_auditoria a LEFT JOIN usuario u ON u.id = a.ator_id AND a.ator_tipo = 'usuario'
 WHERE (@Entidades::text[] IS NULL OR a.entidade = ANY(@Entidades)) AND (@Entidade::text IS NULL OR a.entidade = @Entidade)
   AND (@EntidadeId::text IS NULL OR a.entidade_id = @EntidadeId)";
            var args = new
            {
                Entidades = gestao ? EntidadesGestao : null, Entidade = Limpo(entidade), EntidadeId = Limpo(entidadeId), Offset = Offset(pagina), Limite = TamanhoPagina
            };
            return new Pagina<AuditoriaGestaoDto>
            {
                Total = await cx.ExecuteScalarAsync<int>(new CommandDefinition("SELECT count(*)::int" + Filtro, args, cancellationToken: ct)),
                Itens = (await cx.QueryAsync<AuditoriaGestaoDto>(new CommandDefinition(@"
SELECT a.id, a.id_estabelecimento AS estabelecimento_id, a.entidade, a.entidade_id, a.acao, a.ator_tipo, a.ator_id, u.nome AS ator_nome,
       a.dados::text AS dados, a.created_at AS criado_em" + Filtro + " ORDER BY a.id DESC LIMIT @Limite OFFSET @Offset", args, cancellationToken: ct))).ToList()
            };
        }

        public async Task<PoliticasDto> PoliticasAsync(CancellationToken ct = default)
        {
            await using var cx = await _db.OpenConnectionAsync(ct);
            return await PoliticasAsync(cx, null, ct);
        }

        public async Task<PoliticasDto> SalvarPoliticasAsync(PoliticasDto p, int usuario, CancellationToken ct = default)
        {
            if (p.ToleranciaDias is < 0 or > 30) throw PagamentoException.Invalida("A tolerância vai de 0 a 30 dias.", "toleranciaDias");
            int[] Dias(int[]? v, string campo)
            {
                var d = (v ?? Array.Empty<int>()).Distinct().OrderBy(x => x).ToArray();
                if (d.Length > 6 || d.Any(x => x is < 0 or > 30)) throw PagamentoException.Invalida("Use até 6 avisos, de 0 a 30 dias.", campo);
                return d;
            }
            var antes = Dias(p.AvisosAntesDias, "avisosAntesDias").OrderByDescending(x => x).ToArray();
            var depois = Dias(p.AvisosDepoisDias, "avisosDepoisDias");
            if (p.AlcadaEstornoGerente is < 0 or > 999_999.99m) throw PagamentoException.Invalida("Alçada inválida.", "alcadaEstornoGerente");
            if (p.AlcadaEstornoConfirmacao is < 0 or > 999_999.99m) throw PagamentoException.Invalida("Alçada inválida.", "alcadaEstornoConfirmacao");
            if (p.AlertaFalhasWebhook is < 1 or > 1000) throw PagamentoException.Invalida("Alerta de 1 a 1000 falhas.", "alertaFalhasWebhook");

            await using var cx = await _db.OpenConnectionAsync(ct);
            await using var tx = await cx.BeginTransactionAsync(ct);
            var anterior = await PoliticasAsync(cx, tx, ct);
            await cx.ExecuteAsync(new CommandDefinition(@"
INSERT INTO pagamento_politica (id, tolerancia_dias, avisos_antes_dias, avisos_depois_dias, alcada_estorno_gerente, alcada_estorno_confirmacao, alerta_falhas_webhook,
                                atualizado_por, updated_at)
VALUES (1, @Tol, @Antes, @Depois, @Gerente, @Confirmacao, @Alerta, @Usuario, NOW())
ON CONFLICT (id) DO UPDATE SET tolerancia_dias = EXCLUDED.tolerancia_dias, avisos_antes_dias = EXCLUDED.avisos_antes_dias,
       avisos_depois_dias = EXCLUDED.avisos_depois_dias, alcada_estorno_gerente = EXCLUDED.alcada_estorno_gerente,
       alcada_estorno_confirmacao = EXCLUDED.alcada_estorno_confirmacao, alerta_falhas_webhook = EXCLUDED.alerta_falhas_webhook,
       atualizado_por = EXCLUDED.atualizado_por, updated_at = NOW()", new
            {
                Tol = p.ToleranciaDias, Antes = antes, Depois = depois, Gerente = decimal.Round(p.AlcadaEstornoGerente, 2),
                Confirmacao = decimal.Round(p.AlcadaEstornoConfirmacao, 2), Alerta = p.AlertaFalhasWebhook, Usuario = usuario
            }, tx, cancellationToken: ct));
            var novo = await PoliticasAsync(cx, tx, ct);
            await PagamentoAuditoria.RegistrarAsync(cx, tx, null, "politica", "1", "politica_alterada", "usuario", usuario,
                new { antes = anterior, depois = novo }, ct);
            await tx.CommitAsync(ct);
            return novo;
        }

        // ---- apoio -----------------------------------------------------------------------------------------------------

        private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

        private void ExigirChaveMestra()
        {
            if (!_protector.Configurado) throw PagamentoException.NaoConfigurado("Falta a chave mestra (PAGAMENTOS_CHAVE_MESTRA) no servidor.");
        }

        public static bool UrlSegura(string url) =>
            Uri.TryCreate(url, UriKind.Absolute, out var u) && (u.Scheme == Uri.UriSchemeHttps || (u.Scheme == Uri.UriSchemeHttp && u.IsLoopback));

        private static string? Limpo(string? s) => string.IsNullOrWhiteSpace(s) ? null : s.Trim();
        private static int Offset(int pagina) => Math.Max(0, pagina - 1) * TamanhoPagina;

        private async Task<AplicacaoDto> AplicacaoAsync(NpgsqlConnection cx, string amb, CancellationToken ct)
        {
            var dto = await cx.QueryFirstOrDefaultAsync<AplicacaoDto>(new CommandDefinition(@"
SELECT ambiente, client_id, public_key, redirect_uri, client_secret_cifrado <> '' AS tem_client_secret, webhook_secret_cifrado IS NOT NULL AS tem_webhook_secret,
       ativo, 'banco' AS origem, updated_at AS atualizado_em
  FROM mp_aplicacao WHERE ambiente = @Amb", new { Amb = amb }, cancellationToken: ct));
            if (dto != null) return dto;
            if (amb == _credenciais.AmbienteAtivo && !string.IsNullOrWhiteSpace(_options.BootstrapClientId))
                return new AplicacaoDto
                {
                    Ambiente = amb, ClientId = _options.BootstrapClientId, PublicKey = _options.BootstrapPublicKey, RedirectUri = _options.BootstrapRedirectUri,
                    TemClientSecret = !string.IsNullOrWhiteSpace(_options.BootstrapClientSecret), TemWebhookSecret = !string.IsNullOrWhiteSpace(_options.BootstrapWebhookSecret),
                    Ativo = true, Origem = "configuracao"
                };
            return new AplicacaoDto { Ambiente = amb };
        }

        private static async Task<ContaZippyDto> ContaZippyAsync(NpgsqlConnection cx, string amb, CancellationToken ct) =>
            await cx.QueryFirstOrDefaultAsync<ContaZippyDto>(new CommandDefinition(@"
SELECT ambiente, mp_user_id_mascarado, public_key, estado, TRUE AS tem_token, ultimo_erro, validada_em, updated_at AS atualizado_em
  FROM mp_conta_zippy WHERE ambiente = @Amb", new { Amb = amb }, cancellationToken: ct)) ?? new ContaZippyDto { Ambiente = amb };

        private static async Task<AmbienteEstadoDto> AmbienteEstadoAsync(NpgsqlConnection cx, string amb, CancellationToken ct) =>
            await cx.QueryFirstOrDefaultAsync<AmbienteEstadoDto>(new CommandDefinition(
                "SELECT habilitado, lojas_podem_conectar, habilitado_em FROM mp_ambiente_estado WHERE ambiente = @Amb", new { Amb = amb }, cancellationToken: ct))
            ?? new AmbienteEstadoDto();

        private static async Task<VerificacaoDto?> UltimaVerificacaoAsync(NpgsqlConnection cx, string amb, CancellationToken ct, NpgsqlTransaction? tx = null)
        {
            var linha = await cx.QueryFirstOrDefaultAsync<(Guid Id, bool Aprovada, string Etapas, DateTime CriadoEm, bool Desatualizada)?>(new CommandDefinition(@"
SELECT v.id, v.aprovada, v.etapas::text, v.created_at,
       v.created_at < GREATEST((SELECT updated_at FROM mp_aplicacao WHERE ambiente = @Amb), (SELECT updated_at FROM mp_conta_zippy WHERE ambiente = @Amb))
  FROM mp_verificacao v WHERE v.ambiente = @Amb ORDER BY v.created_at DESC LIMIT 1", new { Amb = amb }, tx, cancellationToken: ct));
            if (linha == null) return null;
            var (id, aprovada, etapas, criado, desatualizada) = linha.Value;
            return new VerificacaoDto
            {
                Id = id, Ambiente = amb, Aprovada = aprovada, CriadoEm = criado, Desatualizada = desatualizada,
                Etapas = JsonSerializer.Deserialize<List<EtapaVerificacao>>(etapas, Json) ?? new()
            };
        }

        private static async Task<WebhookSaudeDto> WebhookSaudeAsync(NpgsqlConnection cx, string amb, CancellationToken ct) =>
            await cx.QueryFirstAsync<WebhookSaudeDto>(new CommandDefinition(@"
SELECT max(recebido_em) AS ultimo_recebido_em,
       count(*) FILTER (WHERE recebido_em > NOW() - INTERVAL '24 hours')::int AS recebidos24h,
       count(*) FILTER (WHERE recebido_em > NOW() - INTERVAL '24 hours' AND status IN ('falhou', 'rejeitado'))::int AS falhas24h,
       count(*) FILTER (WHERE status IN ('recebido', 'falhou', 'processando'))::int AS pendentes
  FROM mp_webhook_evento WHERE ambiente = @Amb", new { Amb = amb }, cancellationToken: ct));

        private static async Task<PoliticasDto> PoliticasAsync(NpgsqlConnection cx, NpgsqlTransaction? tx, CancellationToken ct) =>
            await cx.QueryFirstOrDefaultAsync<PoliticasDto>(new CommandDefinition(@"
SELECT tolerancia_dias, avisos_antes_dias, avisos_depois_dias, alcada_estorno_gerente, alcada_estorno_confirmacao, alerta_falhas_webhook, updated_at AS atualizado_em
  FROM pagamento_politica WHERE id = 1", transaction: tx, cancellationToken: ct)) ?? new PoliticasDto();
    }
}
