using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using System.Web;
using APIBack.Payments.Conexao;
using APIBack.Payments.Gestao;
using APIBack.Payments.MercadoPago;
using Dapper;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Npgsql;
using Xunit;

namespace APIBack.Tests.Integration;

/// <summary>
/// OAuth da loja e Gestao contra PostgreSQL real (TEST_DELIVERY_DATABASE) e um MP falso. Prova: state de uso unico amarrado a loja que
/// iniciou, PKCE enviado na troca, expiracao, conta MP de uma loja nao vira de outra, desconectar preserva transacoes, mp-connect revoga,
/// e a Gestao so libera o ambiente com verificacao aprovada.
/// </summary>
public sealed class MercadoPagoConexaoDatabaseTests
{
    [DeliveryDatabaseFact]
    public async Task CallbackSoConcluiATentativaDaPropriaLojaUmaVezSoEComPkce()
    {
        await using var db = await Banco.Criar();
        var svc = db.Get<IMercadoPagoConexaoLojaService>();

        // Ambiente ainda nao liberado pela Gestao: conectar e recusado.
        var bloqueado = await Assert.ThrowsAsync<PagamentoException>(() => svc.ConectarAsync(db.LojaA, 10, false));
        Assert.Equal(ConexaoCodigos.AmbienteNaoLiberado, bloqueado.Codigo);
        await db.Liberar();

        var a = await svc.ConectarAsync(db.LojaA, 10, false);
        var b = await svc.ConectarAsync(db.LojaB, 20, false);
        var stateA = Query(a.UrlAutorizacao, "state");
        var stateB = Query(b.UrlAutorizacao, "state");
        Assert.Equal("S256", Query(a.UrlAutorizacao, "code_challenge_method"));
        // So o hash do state fica no banco.
        Assert.Equal(0L, await db.Scalar<long>($"SELECT count(*) FROM mp_oauth_tentativa WHERE state_hash = '{stateA}'"));
        Assert.Equal(1L, await db.Scalar<long>($"SELECT count(*) FROM mp_oauth_tentativa WHERE state_hash = '{OAuthPkce.HashState(stateA)}'"));

        db.Mp.UsuarioPorCodigo["code-a"] = 111;
        var ok = await svc.ConcluirAsync("code-a", stateA, null);
        Assert.Contains("mercadopago=ok", ok);
        // PKCE: o verifier enviado na troca gera o challenge da URL.
        Assert.Equal(Query(a.UrlAutorizacao, "code_challenge"), OAuthPkce.Challenge(db.Mp.UltimoVerifier!));
        Assert.Equal(db.LojaA, await db.Scalar<Guid>("SELECT id_estabelecimento FROM mp_conta_loja WHERE mp_user_id = 111"));
        Assert.Equal("ativa", await db.Scalar<string>("SELECT estado FROM mp_conta_loja WHERE mp_user_id = 111"));
        // Nada de token em texto claro no banco nem no estado devolvido.
        Assert.Equal(0L, await db.Scalar<long>("SELECT count(*) FROM mp_conta_loja WHERE access_token_cifrado LIKE '%TOKEN-%'"));
        var estado = await svc.EstadoAsync(db.LojaA);
        Assert.True(estado.Conectada);
        Assert.Equal("****", estado.MpUserIdMascarado);
        Assert.DoesNotContain("TOKEN", JsonSerializer.Serialize(estado));

        // Replay do mesmo state: recusado, sem nova troca.
        var trocas = db.Mp.Trocas;
        Assert.Contains(ConexaoCodigos.OAuthStateInvalido, await svc.ConcluirAsync("code-a", stateA, null));
        Assert.Equal(trocas, db.Mp.Trocas);

        // A loja B autoriza a MESMA conta MP da loja A: recusado, a conta continua so na A.
        db.Mp.UsuarioPorCodigo["code-b"] = 111;
        Assert.Contains(ConexaoCodigos.ContaDeOutraLoja, await svc.ConcluirAsync("code-b", stateB, null));
        Assert.Equal(0L, await db.Scalar<long>($"SELECT count(*) FROM mp_conta_loja WHERE id_estabelecimento = '{db.LojaB}'"));
        Assert.Equal("erro", await db.Scalar<string>($"SELECT estado FROM mp_oauth_tentativa WHERE id = '{b.TentativaId}'"));
        // A loja B nao enxerga a tentativa da A.
        await Assert.ThrowsAsync<PagamentoException>(() => svc.TentativaAsync(db.LojaB, a.TentativaId));
        Assert.Equal("concluida", (await svc.TentativaAsync(db.LojaA, a.TentativaId)).Estado);

        // State inventado ou malformado nao toca o banco.
        Assert.Contains(ConexaoCodigos.OAuthStateInvalido, await svc.ConcluirAsync("x", OAuthPkce.NovoState(), null));
        Assert.Contains(ConexaoCodigos.OAuthStateInvalido, await svc.ConcluirAsync("x", "abc", null));
        Assert.Equal(1L, await db.Scalar<long>("SELECT count(*) FROM mp_conta_loja"));
    }

    [DeliveryDatabaseFact]
    public async Task TentativaExpiradaOuCanceladaNaoConectaEJaConectadaPedeReconectar()
    {
        await using var db = await Banco.Criar();
        await db.Liberar();
        var svc = db.Get<IMercadoPagoConexaoLojaService>();

        var t = await svc.ConectarAsync(db.LojaA, 10, false);
        await db.Execute($"UPDATE mp_oauth_tentativa SET expira_em = NOW() - INTERVAL '1 second' WHERE id = '{t.TentativaId}'");
        db.Mp.UsuarioPorCodigo["c1"] = 222;
        Assert.Contains(ConexaoCodigos.OAuthExpirado, await svc.ConcluirAsync("c1", Query(t.UrlAutorizacao, "state"), null));

        var cancel = await svc.ConectarAsync(db.LojaA, 10, false);
        Assert.Contains(ConexaoCodigos.OAuthCancelado, await svc.ConcluirAsync(null, Query(cancel.UrlAutorizacao, "state"), "access_denied"));
        Assert.Equal(0, db.Mp.Trocas);

        // Nova tentativa invalida a anterior (link velho deixa de valer).
        var t1 = await svc.ConectarAsync(db.LojaA, 10, false);
        var t2 = await svc.ConectarAsync(db.LojaA, 10, false);
        Assert.Contains(ConexaoCodigos.OAuthStateInvalido, await svc.ConcluirAsync("c1", Query(t1.UrlAutorizacao, "state"), null));
        Assert.Contains("mercadopago=ok", await svc.ConcluirAsync("c1", Query(t2.UrlAutorizacao, "state"), null));

        var ja = await Assert.ThrowsAsync<PagamentoException>(() => svc.ConectarAsync(db.LojaA, 10, false));
        Assert.Equal(409, ja.Http);

        // Reconectar com outra conta: a anterior vira desconectada (linha preservada), a nova fica viva.
        var r = await svc.ConectarAsync(db.LojaA, 10, true);
        db.Mp.UsuarioPorCodigo["c2"] = 333;
        Assert.Contains("mercadopago=ok", await svc.ConcluirAsync("c2", Query(r.UrlAutorizacao, "state"), null));
        Assert.Equal("desconectada", await db.Scalar<string>("SELECT estado FROM mp_conta_loja WHERE mp_user_id = 222"));
        Assert.Equal("ativa", await db.Scalar<string>("SELECT estado FROM mp_conta_loja WHERE mp_user_id = 333"));
    }

    [DeliveryDatabaseFact]
    public async Task DesconectarPreservaTransacoesEMpConnectRevoga()
    {
        await using var db = await Banco.Criar();
        await db.Liberar();
        var svc = db.Get<IMercadoPagoConexaoLojaService>();
        var t = await svc.ConectarAsync(db.LojaA, 10, false);
        db.Mp.UsuarioPorCodigo["c"] = 444;
        await svc.ConcluirAsync("c", Query(t.UrlAutorizacao, "state"), null);
        var conta = await db.Scalar<Guid>("SELECT id FROM mp_conta_loja WHERE mp_user_id = 444");

        // Verificar consulta o MP com o token da loja e grava o resultado.
        var v = await svc.VerificarAsync(db.LojaA, 10);
        Assert.True(v.Ok);
        Assert.True(v.PixDisponivel);

        // Pix ligado exige conta; com conta, liga.
        var prefs = await svc.SalvarPreferenciasAsync(db.LojaA, 10, new PreferenciasPagamentoDto { PixAtivo = true, PixValidadeMinutos = 30, CartaoParcelasMax = 1 });
        Assert.True(prefs.PixAtivo);
        var semConta = await Assert.ThrowsAsync<PagamentoException>(() => svc.SalvarPreferenciasAsync(db.LojaB, 20, new PreferenciasPagamentoDto { PixAtivo = true }));
        Assert.Equal(PagamentoCodigos.ContaLojaNaoConectada, semConta.Codigo);

        await db.Execute(@"
INSERT INTO pagamento_intent (id, id_estabelecimento, ambiente, origem, conta_tipo, id_conta_loja, valor, descricao, status, external_reference, idempotency_key)
VALUES (gen_random_uuid(), @L, 'sandbox', 'chat', 'loja', @C, 10, 'x', 'aguardando_pagamento', 'zp_s_chat_1', 'k1')", new { L = db.LojaA, C = conta });
        var aberta = await Assert.ThrowsAsync<PagamentoException>(() => svc.DesconectarAsync(db.LojaA, 10, false));
        Assert.Equal(409, aberta.Http);
        Assert.Equal(1, aberta.Detalhes!["abertas"]);

        await svc.DesconectarAsync(db.LojaA, 10, true);
        Assert.Equal("desconectada", await db.Scalar<string>($"SELECT estado FROM mp_conta_loja WHERE id = '{conta}'"));
        Assert.Equal(0L, await db.Scalar<long>($"SELECT count(*) FROM mp_conta_loja WHERE id = '{conta}' AND access_token_cifrado IS NOT NULL"));
        Assert.Equal(1L, await db.Scalar<long>("SELECT count(*) FROM pagamento_intent"));
        Assert.True(await db.Scalar<long>("SELECT count(*) FROM pagamento_auditoria WHERE acao = 'conta_desconectada'") >= 1);

        // Outra conta conectada e desautorizada pelo MP (webhook mp-connect).
        var t2 = await svc.ConectarAsync(db.LojaA, 10, false);
        db.Mp.UsuarioPorCodigo["d"] = 555;
        await svc.ConcluirAsync("d", Query(t2.UrlAutorizacao, "state"), null);
        var handler = db.Get<IEnumerable<IMercadoPagoTopicoHandler>>().Single(h => h.Atende("mp-connect"));
        var r = await handler.ProcessarAsync(new MpWebhookEvento { Ambiente = "sandbox", Topico = "mp-connect", Acao = "application.deauthorized", MpUserId = 555, DataId = "1" }, CancellationToken.None);
        Assert.Equal("processado", r.Status);
        Assert.Equal("revogada", await db.Scalar<string>("SELECT estado FROM mp_conta_loja WHERE mp_user_id = 555"));
        Assert.False((await svc.EstadoAsync(db.LojaA)).Conectada);
    }

    [DeliveryDatabaseFact]
    public async Task GestaoSoHabilitaAmbienteComVerificacaoAprovadaENaoDevolveSegredo()
    {
        await using var db = await Banco.Criar();
        var gestao = db.Get<IPagamentosGestaoService>();

        await gestao.SalvarAplicacaoAsync(new SalvarAplicacaoRequest
        {
            Ambiente = "sandbox", ClientId = "123", ClientSecret = "SEGREDO-APP", WebhookSecret = "SEGREDO-WH",
            RedirectUri = "https://api.teste/api/integracoes/mercadopago/callback"
        }, 1);
        var zippy = await gestao.SalvarContaZippyAsync(new SalvarContaZippyRequest { Ambiente = "sandbox", AccessToken = "TOKEN-ZIPPY" }, 1);
        Assert.Equal("ativa", zippy.Estado);

        var cfg = await gestao.ConfiguracaoAsync("sandbox");
        var json = JsonSerializer.Serialize(cfg);
        Assert.DoesNotContain("SEGREDO", json);
        Assert.DoesNotContain("TOKEN-ZIPPY", json);
        Assert.True(cfg.Aplicacao.TemClientSecret);
        Assert.Equal("https://api.teste/api/integracoes/mercadopago/callback", cfg.UrlCallback);
        Assert.False(cfg.Pronto);

        // Sem webhook recebido a verificacao nao aprova; habilitar e recusado.
        var v1 = await gestao.VerificarAsync("sandbox", 1);
        Assert.False(v1.Aprovada);
        Assert.Contains(v1.Etapas, e => e.Etapa == "webhook_recebido" && !e.Ok);
        var recusa = await Assert.ThrowsAsync<PagamentoException>(() => gestao.AtualizarAmbienteAsync(new AtualizarAmbienteRequest { Ambiente = "sandbox", Habilitado = true }, 1));
        Assert.Equal(ConexaoCodigos.VerificacaoPendente, recusa.Codigo);
        await Assert.ThrowsAsync<PagamentoException>(() => gestao.AtualizarAmbienteAsync(new AtualizarAmbienteRequest { Ambiente = "producao", Habilitado = true }, 1));

        // Webhook de teste recebido (gravado so com assinatura valida): agora aprova e habilita.
        await db.Execute("INSERT INTO mp_webhook_evento (id, ambiente, topico, data_id, chave_dedup, payload, status) VALUES (gen_random_uuid(), 'sandbox', 'payment', '123456', 'teste', '{}', 'ignorado')");
        var v2 = await gestao.VerificarAsync("sandbox", 1);
        Assert.True(v2.Aprovada, string.Join(" | ", v2.Etapas.Where(e => !e.Ok).Select(e => e.Mensagem)));
        var hab = await gestao.AtualizarAmbienteAsync(new AtualizarAmbienteRequest { Ambiente = "sandbox", Habilitado = true, LojasPodemConectar = true }, 1);
        Assert.True(hab.Pronto);

        // Trocar o segredo depois deixa a verificacao desatualizada (rotacao exige verificar de novo).
        await gestao.SalvarAplicacaoAsync(new SalvarAplicacaoRequest
        {
            Ambiente = "sandbox", ClientId = "123", ClientSecret = "SEGREDO-NOVO", RedirectUri = "https://api.teste/api/integracoes/mercadopago/callback"
        }, 1);
        Assert.True((await gestao.ConfiguracaoAsync("sandbox")).UltimaVerificacao!.Desatualizada);
        var auditoria = await db.Scalar<string>("SELECT string_agg(dados::text, ' ') FROM pagamento_auditoria");
        Assert.DoesNotContain("SEGREDO", auditoria);
        Assert.Contains("client_secret", auditoria);

        var pol = await gestao.PoliticasAsync();
        Assert.Equal(7, pol.ToleranciaDias);
        pol.ToleranciaDias = 10;
        Assert.Equal(10, (await gestao.SalvarPoliticasAsync(pol, 1)).ToleranciaDias);
        await Assert.ThrowsAsync<PagamentoException>(() => gestao.SalvarPoliticasAsync(new PoliticasDto { ToleranciaDias = 99 }, 1));
    }

    private static string Query(string url, string nome) => HttpUtility.ParseQueryString(new Uri(url).Query)[nome]!;

    private sealed class Banco : IAsyncDisposable
    {
        public Guid LojaA { get; } = Guid.NewGuid();
        public Guid LojaB { get; } = Guid.NewGuid();
        public MpFalso Mp { get; } = new();
        private ServiceProvider _sp = null!;
        private NpgsqlDataSource _source = null!;
        private readonly string _schema = "mp_conexao_" + Guid.NewGuid().ToString("N");
        private string _base = "";

        public T Get<T>() where T : notnull => _sp.CreateScope().ServiceProvider.GetRequiredService<T>();

        public static async Task<Banco> Criar()
        {
            APIBack.Infrastructure.DapperConfiguration.Configure();
            var options = new NpgsqlConnectionStringBuilder(Environment.GetEnvironmentVariable("TEST_DELIVERY_DATABASE"));
            if (options.Host is not ("localhost" or "127.0.0.1")) throw new InvalidOperationException("Use somente PostgreSQL local.");
            var db = new Banco { _base = options.ConnectionString };
            await using (var cx = new NpgsqlConnection(db._base)) { await cx.OpenAsync(); await cx.ExecuteAsync($"CREATE SCHEMA {db._schema}"); }
            options.SearchPath = db._schema;
            db._source = NpgsqlDataSource.Create(options.ConnectionString);

            await db.Execute(@"
CREATE TABLE estabelecimentos (id uuid PRIMARY KEY, nome_fantasia text);
CREATE TABLE conversas (id uuid PRIMARY KEY);
CREATE TABLE clientes (id uuid PRIMARY KEY);
CREATE TABLE usuario (id int PRIMARY KEY, nome text);
CREATE TABLE cardapio_pedido_publico (id uuid PRIMARY KEY, id_estabelecimento uuid, status_pagamento text NOT NULL DEFAULT 'pendente', updated_at timestamptz);
CREATE TABLE delivery_tracking_schema_versions (version text PRIMARY KEY);
INSERT INTO estabelecimentos VALUES (@A, 'Loja A'), (@B, 'Loja B');", new { A = db.LojaA, B = db.LojaB });
            var dir = Path.Combine(AppContext.BaseDirectory, "Migrations", "Delivery");
            await db.Execute(File.ReadAllText(Path.Combine(dir, "20261031_01_mercadopago_nucleo.sql")));
            await db.Execute(File.ReadAllText(Path.Combine(dir, "20261101_20_pagamentos_gestao_oauth.sql")));
            await db.Execute(File.ReadAllText(Path.Combine(dir, "20261101_21_pagamentos_gestao_oauth_verify.sql")));

            var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Payments:ChaveMestra"] = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32)),
                ["Payments:MercadoPago:Ambiente"] = "sandbox",
                ["Payments:MercadoPago:NotificationUrl"] = "https://api.teste/api/webhooks/mercadopago",
                ["Payments:MercadoPago:PainelUrl"] = "https://painel.teste",
                // Usuarios de teste do MP: o live_mode do token nao diz o ambiente (homologacao usa o mesmo ajuste).
                ["Payments:MercadoPago:ExigirLiveModeDoAmbiente"] = "false"
            }).Build();
            var services = new ServiceCollection();
            services.AddLogging(b => b.SetMinimumLevel(LogLevel.Warning));
            services.AddSingleton<IConfiguration>(config);
            services.AddSingleton(db._source);
            services.AddPagamentosMercadoPago(config);
            services.AddHttpClient(MercadoPagoClient.NomeHttpClient).ConfigurePrimaryHttpMessageHandler(() => db.Mp);
            db._sp = services.BuildServiceProvider();
            return db;
        }

        /// <summary>Aplicacao cadastrada e ambiente liberado para as lojas (o que a Gestao faz).</summary>
        public async Task Liberar()
        {
            var protector = _sp.GetRequiredService<IPagamentoSegredoProtector>();
            var app = Guid.NewGuid();
            await Execute(@"
INSERT INTO mp_aplicacao (id, ambiente, client_id, client_secret_cifrado, redirect_uri) VALUES (@App, 'sandbox', '123', @Cs, 'https://api.teste/api/integracoes/mercadopago/callback');
UPDATE mp_ambiente_estado SET habilitado = TRUE, lojas_podem_conectar = TRUE WHERE ambiente = 'sandbox';",
                new { App = app, Cs = protector.Proteger("client-secret", SegredoContexto.AplicacaoClientSecret(app)) });
        }

        public async Task Execute(string sql, object? args = null) { await using var cx = await _source.OpenConnectionAsync(); await cx.ExecuteAsync(sql, args); }
        public async Task<T> Scalar<T>(string sql) { await using var cx = await _source.OpenConnectionAsync(); return (await cx.ExecuteScalarAsync<T>(sql))!; }

        public async ValueTask DisposeAsync()
        {
            await _sp.DisposeAsync();
            await _source.DisposeAsync();
            await using var cx = new NpgsqlConnection(_base); await cx.OpenAsync(); await cx.ExecuteAsync($"DROP SCHEMA {_schema} CASCADE");
        }
    }

    /// <summary>MP falso: /oauth/token (codigo, refresh e client_credentials), /users/me e /v1/payment_methods.</summary>
    private sealed class MpFalso : HttpMessageHandler
    {
        public Dictionary<string, long> UsuarioPorCodigo { get; } = new();
        private readonly Dictionary<string, long> _donoDoToken = new();
        public string? UltimoVerifier { get; private set; }
        public int Trocas { get; private set; }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            var caminho = request.RequestUri!.AbsolutePath;
            if (caminho == "/oauth/token")
            {
                var corpo = JsonSerializer.Deserialize<Dictionary<string, string>>(await request.Content!.ReadAsStringAsync(ct))!;
                if (corpo["grant_type"] == "client_credentials") return Json(new { access_token = "APP", user_id = 1, expires_in = 21600 });
                if (corpo["grant_type"] != "authorization_code" || !UsuarioPorCodigo.TryGetValue(corpo["code"], out var user))
                    return new HttpResponseMessage(HttpStatusCode.BadRequest) { Content = new StringContent("{\"error\":\"invalid_grant\"}") };
                Trocas++;
                UltimoVerifier = corpo.GetValueOrDefault("code_verifier");
                var token = $"TOKEN-{user}-{Guid.NewGuid():N}";
                _donoDoToken[token] = user;
                return Json(new { access_token = token, refresh_token = "REFRESH-" + user, public_key = "TEST-pk", user_id = user, expires_in = 15552000, scope = "offline_access read write", live_mode = false });
            }

            var auth = request.Headers.Authorization?.Parameter ?? string.Empty;
            if (caminho == "/users/me")
            {
                var id = _donoDoToken.TryGetValue(auth, out var u) ? u : auth == "TOKEN-ZIPPY" ? 999 : 0;
                return id == 0 ? new HttpResponseMessage(HttpStatusCode.Unauthorized) : Json(new { id, site_id = "MLB", status = new { site_status = "active" } });
            }
            if (caminho == "/v1/payment_methods")
                return Json(new object[] { new { id = "pix", payment_type_id = "bank_transfer", status = "active" }, new { id = "visa", payment_type_id = "credit_card", status = "active" } });
            return new HttpResponseMessage(HttpStatusCode.NotFound);
        }

        private static HttpResponseMessage Json(object o) => new(HttpStatusCode.OK) { Content = new StringContent(JsonSerializer.Serialize(o), Encoding.UTF8, "application/json") };
    }
}
