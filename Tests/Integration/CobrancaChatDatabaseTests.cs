using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using APIBack.Payments.Chat;
using APIBack.Payments.MercadoPago;
using Dapper;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Npgsql;
using Xunit;

namespace APIBack.Tests.Integration;

/// <summary>
/// Cobranca Pix do atendente contra PostgreSQL real (TEST_DELIVERY_DATABASE) e um MP falso. Prova: permissao e limite de alcada,
/// idempotencia (clique duplo e webhook duplicado), avulsa sem efeito em pedido, vinculada quitando o pedido, isolamento entre lojas,
/// reemissao sem pagamento duplo (pagamento tardio cancela a reemitida) e envio pela janela de 24 h.
/// </summary>
public sealed class CobrancaChatDatabaseTests
{
    private const string WebhookSecret = "whsec-chat";
    private const long MpUserLoja = 777001;

    private static readonly AtorCobranca Atendente = new(7, "Ana", PodeAprovar: false, PodeReembolsar: false);
    private static readonly AtorCobranca Gerente = new(8, "Bia", PodeAprovar: true, PodeReembolsar: true);

    private static CriarCobrancaChatRequest Avulsa(decimal valor, string chave) => new() { Descricao = "Taxa extra", Valor = valor, IdempotencyKey = chave };

    [DeliveryDatabaseFact]
    public async Task LimiteExigeAprovacaoDeOutraPessoaEDepoisGeraOPix()
    {
        await using var db = await Banco.Criar();
        var s = db.Service();
        await db.Execute("INSERT INTO cobranca_chat_config (id_estabelecimento, limite_sem_aprovacao) VALUES (@L, 100)", new { L = db.Loja });

        var abaixo = await s.CriarAsync(db.Loja, db.Conversa, Avulsa(50m, "k-abaixo"), Atendente, default);
        Assert.Equal(StatusCobranca.Aguardando, abaixo.Status);
        Assert.False(string.IsNullOrEmpty(abaixo.Pix!.CopiaECola));
        Assert.Contains("/pix/", abaixo.Pix.LinkPublico);

        var acima = await s.CriarAsync(db.Loja, db.Conversa, Avulsa(150m, "k-acima"), Atendente, default);
        Assert.Equal(StatusCobranca.AguardandoAprovacao, acima.Status);
        Assert.Null(acima.Pix);
        Assert.NotNull(acima.AprovacaoPendente);
        Assert.Single(db.Mp.Criacoes); // so a de 50

        // Quem pediu nao aprova o proprio pedido (mesmo com a permissao).
        var propria = await Assert.ThrowsAsync<PagamentoException>(() =>
            s.AprovarAsync(db.Loja, acima.AprovacaoPendente!.Id, null, Atendente with { PodeAprovar = true }, default));
        Assert.Equal(CobrancaChatCodigos.AprovacaoPropria, propria.Codigo);

        var decisao = await s.AprovarAsync(db.Loja, acima.AprovacaoPendente.Id, null, Gerente, default);
        Assert.Equal(StatusAprovacao.Aprovada, decisao.Aprovacao.Status);
        Assert.Equal(StatusCobranca.Aguardando, decisao.Cobranca.Status);
        Assert.Equal(2, db.Mp.Criacoes.Count);

        // Gerente nao precisa de aprovacao.
        Assert.Equal(StatusCobranca.Aguardando, (await s.CriarAsync(db.Loja, db.Conversa, Avulsa(500m, "k-gerente"), Gerente, default)).Status);

        // Reembolso direto e do gerente.
        var semPoder = await Assert.ThrowsAsync<PagamentoException>(() =>
            s.ReembolsarAsync(db.Loja, abaixo.Id, new ReembolsarCobrancaChatRequest { IdempotencyKey = "r1" }, Atendente, default));
        Assert.Equal(CobrancaChatCodigos.ExigeAprovacao, semPoder.Codigo);

        await db.Execute("UPDATE cobranca_chat_config SET habilitada = FALSE");
        Assert.Equal(CobrancaChatCodigos.Desativada,
            (await Assert.ThrowsAsync<PagamentoException>(() => s.CriarAsync(db.Loja, db.Conversa, Avulsa(10m, "k-off"), Atendente, default))).Codigo);
        Assert.True(await db.Scalar<long>("SELECT count(*) FROM pagamento_auditoria WHERE entidade = 'cobranca_chat' AND ator_id = 7") > 0);
    }

    [DeliveryDatabaseFact]
    public async Task CliqueDuploEWebhookDuplicadoNaoDuplicamNada()
    {
        await using var db = await Banco.Criar();
        var s = db.Service();
        var pedido = new CriarCobrancaChatRequest { PedidoId = db.Pedido, IdempotencyKey = "dup-1" };

        var resultados = await Task.WhenAll(Enumerable.Range(0, 4).Select(_ => db.Service().CriarAsync(db.Loja, db.Conversa, pedido, Atendente, default)));
        Assert.Single(resultados.Select(r => r.Id).Distinct());
        Assert.Equal(1L, await db.Scalar<long>("SELECT count(*) FROM cobranca_chat"));
        Assert.Single(db.Mp.Pagamentos);
        Assert.Equal(80m, resultados[0].Valor); // total 80 do pedido, nunca digitado
        Assert.Equal(80m, resultados[0].SaldoPedido);

        // Outra chave para o mesmo pedido enquanto a primeira esta aberta.
        var aberta = await Assert.ThrowsAsync<PagamentoException>(() =>
            s.CriarAsync(db.Loja, db.Conversa, new CriarCobrancaChatRequest { PedidoId = db.Pedido, IdempotencyKey = "dup-2" }, Atendente, default));
        Assert.Equal(PagamentoCodigos.CobrancaAberta, aberta.Codigo);
        Assert.Equal(resultados[0].Id, aberta.Detalhes!["cobrancaId"]);
        // Valor digitado diferente do saldo.
        await db.Execute("UPDATE cobranca_chat SET status = 'cancelado'");
        var divergente = await Assert.ThrowsAsync<PagamentoException>(() =>
            s.CriarAsync(db.Loja, db.Conversa, new CriarCobrancaChatRequest { PedidoId = db.Pedido, Valor = 10m, IdempotencyKey = "dup-3" }, Atendente, default));
        Assert.Equal(CobrancaChatCodigos.ValorDivergente, divergente.Codigo);
        await db.Execute("UPDATE cobranca_chat SET status = 'aguardando'");

        var mp = db.Mp.Pagamentos.Keys.Single();
        db.Mp.Mudar(mp, "approved", "accredited", DateTimeOffset.UtcNow);
        var webhook = db.Get<IMercadoPagoWebhookService>();
        await webhook.ReceberAsync(db.Aviso(mp, "n1"));
        Assert.Equal(WebhookDesfecho.Duplicado, (await webhook.ReceberAsync(db.Aviso(mp, "n1"))).Desfecho);
        await webhook.ReceberAsync(db.Aviso(mp, "n2")); // outro aviso do mesmo pagamento

        Assert.Equal(StatusCobranca.Pago, await db.Scalar<string>("SELECT status FROM cobranca_chat"));
        Assert.Equal("", await db.Scalar<string>("SELECT COALESCE(string_agg(acao || ':' || dados::text, ' | '), '') FROM pagamento_auditoria WHERE entidade = 'pedido'")
            + await db.Scalar<string>("SELECT string_agg(status || ':' || COALESCE(pedido_id::text,'-') || ':' || valor_pago, ',') FROM pagamento_intent"));
        Assert.Equal("Pago", await db.Scalar<string>("SELECT status_pagamento FROM pedido WHERE id = 101"));
        Assert.Equal(1L, await db.Scalar<long>("SELECT count(*) FROM delivery_realtime_outbox WHERE event_name = 'delivery.order.updated'"));
        Assert.Equal(1L, await db.Scalar<long>("SELECT count(*) FROM cobranca_chat_tarefa WHERE tipo = 'avancar_pedido'"));
        Assert.Equal(1L, await db.Scalar<long>("SELECT count(*) FROM cobranca_chat_tarefa WHERE tipo = 'confirmar_cliente'"));
        Assert.Equal(1L, await db.Scalar<long>("SELECT count(*) FROM cobranca_chat_evento WHERE status_novo = 'pago'"));

        // Tarefas depois do commit: avisa o cliente (janela aberta) e avanca o rascunho pela regra da loja.
        await db.Manutencao().RodarAsync(default);
        Assert.Contains(db.Mensageiro.Textos, t => t.Contains("recebido"));
        Assert.Equal(new[] { db.Pedido }, db.Avancador.Pedidos);
        await db.Manutencao().RodarAsync(default);
        Assert.Single(db.Avancador.Pedidos);

        // Pedido quitado: nova cobranca e recusada.
        var quitado = await Assert.ThrowsAsync<PagamentoException>(() =>
            s.CriarAsync(db.Loja, db.Conversa, new CriarCobrancaChatRequest { PedidoId = db.Pedido, IdempotencyKey = "dup-4" }, Atendente, default));
        Assert.Equal(PagamentoCodigos.JaPago, quitado.Codigo);
    }

    [DeliveryDatabaseFact]
    public async Task AvulsaPagaNaoMexeEmPedido()
    {
        await using var db = await Banco.Criar();
        var c = await db.Service().CriarAsync(db.Loja, db.Conversa, Avulsa(25m, "av-1"), Atendente, default);
        Assert.Null(c.PedidoId);
        Assert.Null(c.SaldoPedido);

        var mp = db.Mp.Pagamentos.Keys.Single();
        db.Mp.Mudar(mp, "approved", "accredited", DateTimeOffset.UtcNow);
        await db.Get<IMercadoPagoWebhookService>().ReceberAsync(db.Aviso(mp, "a1"));

        Assert.Equal(StatusCobranca.Pago, await db.Scalar<string>("SELECT status FROM cobranca_chat"));
        Assert.Equal("Pendente", await db.Scalar<string>("SELECT status_pagamento FROM pedido"));
        Assert.Equal(6, await db.Scalar<int>("SELECT status_pedido FROM pedido"));
        Assert.Equal(0L, await db.Scalar<long>("SELECT count(*) FROM delivery_realtime_outbox"));
        Assert.Equal(0L, await db.Scalar<long>("SELECT count(*) FROM cobranca_chat_tarefa WHERE tipo = 'avancar_pedido'"));
        // O balao persiste como item da conversa.
        Assert.Equal(1L, await db.Scalar<long>("SELECT count(*) FROM conversa_eventos WHERE tipo = 'cobranca_pix'"));
    }

    [DeliveryDatabaseFact]
    public async Task OutraLojaNaoVeNemCobraNada()
    {
        await using var db = await Banco.Criar();
        var s = db.Service();
        var c = await s.CriarAsync(db.Loja, db.Conversa, Avulsa(10m, "iso-1"), Atendente, default);

        Assert.Equal(404, (await Assert.ThrowsAsync<PagamentoException>(() => s.ObterAsync(db.OutraLoja, c.Id, Atendente, default))).Http);
        Assert.Equal(404, (await Assert.ThrowsAsync<PagamentoException>(() => s.CancelarAsync(db.OutraLoja, c.Id, null, Atendente, default))).Http);
        Assert.Equal(404, (await Assert.ThrowsAsync<PagamentoException>(() => s.HistoricoAsync(db.OutraLoja, c.Id, default))).Http);
        Assert.Equal(404, (await Assert.ThrowsAsync<PagamentoException>(() => s.ListarAsync(db.OutraLoja, db.Conversa, null, Atendente, default))).Http);
        Assert.Equal(404, (await Assert.ThrowsAsync<PagamentoException>(() =>
            s.CriarAsync(db.OutraLoja, db.Conversa, Avulsa(10m, "iso-2"), Atendente, default))).Http);
        // Pedido de outra loja: 404; pedido de outro cliente da mesma loja: 422.
        Assert.Equal(404, (await Assert.ThrowsAsync<PagamentoException>(() =>
            s.CriarAsync(db.Loja, db.Conversa, new CriarCobrancaChatRequest { PedidoId = db.PedidoOutraLoja, IdempotencyKey = "iso-3" }, Atendente, default))).Http);
        Assert.Equal(CobrancaChatCodigos.PedidoDeOutroCliente, (await Assert.ThrowsAsync<PagamentoException>(() =>
            s.CriarAsync(db.Loja, db.Conversa, new CriarCobrancaChatRequest { PedidoId = db.PedidoOutroCliente, IdempotencyKey = "iso-4" }, Atendente, default))).Codigo);
        Assert.Single((await s.ListarAsync(db.Loja, db.Conversa, null, Atendente, default)).Itens);
        var payloads = await db.Scalar<string>("SELECT string_agg(grupo, ',') FROM pagamento_outbox");
        Assert.DoesNotContain(db.OutraLoja.ToString("N"), payloads);
    }

    [DeliveryDatabaseFact]
    public async Task ReemissaoInvalidaAAnteriorEPagamentoTardioCancelaAReemitida()
    {
        await using var db = await Banco.Criar();
        var s = db.Service();
        var a = await s.CriarAsync(db.Loja, db.Conversa, new CriarCobrancaChatRequest { PedidoId = db.Pedido, IdempotencyKey = "re-a" }, Atendente, default);
        var mpA = db.Mp.Pagamentos.Keys.Single();

        var b = await s.ReemitirAsync(db.Loja, a.Id, new ReemitirCobrancaChatRequest { IdempotencyKey = "re-b" }, Atendente, default);
        Assert.Equal(a.Id, b.ReemitidaDe);
        Assert.Equal(StatusCobranca.Aguardando, b.Status);
        Assert.Equal(StatusCobranca.Cancelado, (await s.ObterAsync(db.Loja, a.Id, Atendente, default)).Status);
        Assert.Equal("cancelled", db.Mp.Pagamentos[mpA].Status);
        Assert.Equal(2, db.Mp.Pagamentos.Count);
        // Repetir a mesma reemissao devolve a mesma; reemitir de novo a anterior e barrado.
        Assert.Equal(b.Id, (await s.ReemitirAsync(db.Loja, a.Id, new ReemitirCobrancaChatRequest { IdempotencyKey = "re-b" }, Atendente, default)).Id);
        Assert.Equal(PagamentoCodigos.CobrancaAberta, (await Assert.ThrowsAsync<PagamentoException>(() =>
            s.ReemitirAsync(db.Loja, a.Id, new ReemitirCobrancaChatRequest { IdempotencyKey = "re-c" }, Atendente, default))).Codigo);

        // O cliente pagou o Pix antigo mesmo assim: a antiga vira paga e a reemitida e cancelada (nunca duas pagaveis).
        db.Mp.Mudar(mpA, "approved", "accredited", DateTimeOffset.UtcNow.AddSeconds(5));
        await db.Get<IMercadoPagoWebhookService>().ReceberAsync(db.Aviso(mpA, "t1"));
        Assert.Equal(StatusCobranca.Pago, (await s.ObterAsync(db.Loja, a.Id, Atendente, default)).Status);
        Assert.Equal(1L, await db.Scalar<long>("SELECT count(*) FROM cobranca_chat_tarefa WHERE tipo = 'cancelar_sucessora'"));
        await db.Manutencao().RodarAsync(default);
        Assert.Equal(StatusCobranca.Cancelado, (await s.ObterAsync(db.Loja, b.Id, Atendente, default)).Status);
        Assert.Equal("Pago", await db.Scalar<string>("SELECT status_pagamento FROM pedido"));
        var historico = await s.HistoricoAsync(db.Loja, a.Id, default);
        Assert.Contains(historico.Itens, i => i.StatusNovo == StatusCobranca.Pago && i.StatusAnterior == StatusCobranca.Cancelado);
    }

    [DeliveryDatabaseFact]
    public async Task EnvioRespeitaAJanelaDe24HorasEOCliqueDuplo()
    {
        await using var db = await Banco.Criar();
        var s = db.Service();
        var c = await s.CriarAsync(db.Loja, db.Conversa, Avulsa(30m, "env-1"), Atendente, default);

        var enviado = await s.EnviarAsync(db.Loja, c.Id, new EnviarCobrancaChatRequest { Formato = "ambos" }, Atendente, default);
        Assert.Equal(StatusCobranca.Enviado, enviado.Status);
        Assert.NotNull(enviado.IdMensagem);
        Assert.Equal(2, db.Mensageiro.Textos.Count);
        Assert.Contains(c.Pix!.LinkPublico!, db.Mensageiro.Textos[0]); // o mesmo link do POST
        // Segundo clique logo em seguida: nao manda de novo.
        await s.EnviarAsync(db.Loja, c.Id, null, Atendente, default);
        Assert.Equal(2, db.Mensageiro.Textos.Count);

        await db.Execute("UPDATE cobranca_chat SET enviado_em = NOW() - INTERVAL '1 minute'; UPDATE conversas SET janela_24h_fim = NOW() - INTERVAL '1 hour'");
        var fechada = await Assert.ThrowsAsync<PagamentoException>(() => s.EnviarAsync(db.Loja, c.Id, null, Atendente, default));
        Assert.Equal(CobrancaChatCodigos.JanelaFechada, fechada.Codigo);

        // Com modelo aprovado configurado, sai pelo modelo com o link.
        await db.Execute("INSERT INTO cobranca_chat_config (id_estabelecimento, template_nome) VALUES (@L, 'cobranca_pix')", new { L = db.Loja });
        db.Mensageiro.Modelo = new TemplateCobranca("cobranca_pix", "pt_BR", 3, false, "Valor {{1}} {{2}} {{3}}");
        await s.EnviarAsync(db.Loja, c.Id, null, Atendente, default);
        Assert.Equal(c.Pix.LinkPublico, db.Mensageiro.ParametrosModelo![2]);

        // Cancelada nao envia.
        await s.CancelarAsync(db.Loja, c.Id, new CancelarCobrancaChatRequest { Motivo = "cliente desistiu" }, Atendente, default);
        var cancelada = await s.ObterAsync(db.Loja, c.Id, Atendente, default);
        Assert.Equal(7, cancelada.CanceladoPor!.Id);
        Assert.Equal(PagamentoCodigos.EstadoInvalido, (await Assert.ThrowsAsync<PagamentoException>(() => s.EnviarAsync(db.Loja, c.Id, null, Atendente, default))).Codigo);
    }

    // ------------------------------------------------------------------------------------------------------------------------

    private sealed class MensageiroFalso : ICobrancaChatMensageiro
    {
        public List<string> Textos { get; } = new();
        public TemplateCobranca? Modelo { get; set; }
        public IReadOnlyList<string>? ParametrosModelo { get; private set; }

        public Task<Guid> EnviarTextoAsync(Guid conversaId, Guid loja, string texto, string criadaPor, CancellationToken ct)
        {
            lock (Textos) Textos.Add(texto);
            return Task.FromResult(Guid.NewGuid());
        }

        public Task<TemplateCobranca?> TemplateAprovadoAsync(Guid loja, Guid? canalId, string nome, string idioma, CancellationToken ct) =>
            Task.FromResult(Modelo?.Nome == nome ? Modelo : null);

        public Task<Guid> EnviarTemplateAsync(Guid conversaId, Guid loja, TemplateCobranca template, IReadOnlyList<string> corpo, string? sufixoBotaoUrl,
            string textoNoChat, string criadaPor, CancellationToken ct)
        {
            ParametrosModelo = corpo;
            return Task.FromResult(Guid.NewGuid());
        }
    }

    private sealed class AvancadorFalso : ICobrancaPedidoAvancador
    {
        public List<int> Pedidos { get; } = new();

        public Task<string> AvancarAsync(Guid loja, int pedidoId, int atorUsuarioId, CancellationToken ct)
        {
            Pedidos.Add(pedidoId);
            return Task.FromResult("confirmado");
        }
    }

    private sealed class Banco : IAsyncDisposable
    {
        public Guid Loja { get; } = Guid.NewGuid();
        public Guid OutraLoja { get; } = Guid.NewGuid();
        public Guid Conversa { get; } = Guid.NewGuid();
        public Guid Cliente { get; } = Guid.NewGuid();
        public int Pedido => 101;
        public int PedidoOutroCliente => 102;
        public int PedidoOutraLoja => 103;
        public MpFalso Mp { get; } = new();
        public MensageiroFalso Mensageiro { get; } = new();
        public AvancadorFalso Avancador { get; } = new();
        private ServiceProvider _sp = null!;
        private NpgsqlDataSource _source = null!;
        private readonly string _schema = "cobranca_chat_" + Guid.NewGuid().ToString("N");
        private string _base = "";

        public T Get<T>() where T : notnull => _sp.CreateScope().ServiceProvider.GetRequiredService<T>();
        public CobrancaChatService Service() => Get<CobrancaChatService>();
        public CobrancaChatManutencao Manutencao() => Get<CobrancaChatManutencao>();

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
CREATE TABLE clientes (id uuid PRIMARY KEY);
CREATE TABLE usuario (id int PRIMARY KEY, nome text);
CREATE TABLE conversas (id uuid PRIMARY KEY, id_estabelecimento uuid, id_cliente uuid, id_conversa_grupo uuid, id_canal uuid, estado text,
                        janela_24h_fim timestamptz, data_ultima_mensagem timestamptz, data_atualizacao timestamptz, data_criacao timestamptz DEFAULT NOW());
CREATE TABLE conversa_eventos (id uuid PRIMARY KEY, id_conversa uuid, id_conversa_grupo uuid, tipo text, origem text, ator_usuario_id int, ator_agente_id int,
                               ator_nome text, status_anterior text, status_novo text, motivo text, tipo_fechamento text, dados jsonb, data_criacao timestamptz);
CREATE TABLE pedido (id int PRIMARY KEY, id_estabelecimento uuid, cliente_id uuid, conversa_id uuid, status_pedido int, status_pagamento text,
                     tipo_pagamento text, value text);
CREATE TABLE cardapio_pedido_publico (id uuid PRIMARY KEY, id_estabelecimento uuid, status_pagamento text NOT NULL DEFAULT 'pendente', updated_at timestamptz,
                                      id_conversa uuid, id_pedido int, status text, total numeric, codigo text);
CREATE TABLE cardapio_web_config (id_estabelecimento uuid PRIMARY KEY, nome_publico text);
CREATE TABLE delivery_tracking_schema_versions (version text PRIMARY KEY);
CREATE TABLE delivery_realtime_outbox (event_id uuid PRIMARY KEY, event_name text, target_group text, estabelecimento_id uuid, motoboy_id int,
                                       aggregate_version bigint, payload jsonb, occurred_at_utc timestamptz);
INSERT INTO estabelecimentos VALUES (@L, 'Loja'), (@O, 'Outra');
INSERT INTO clientes VALUES (@Cli), (@Outro);
INSERT INTO usuario VALUES (7, 'Ana'), (8, 'Bia');
INSERT INTO conversas (id, id_estabelecimento, id_cliente, estado, janela_24h_fim, data_ultima_mensagem)
VALUES (@C, @L, @Cli, 'aberto', NOW() + INTERVAL '10 hours', NOW());
INSERT INTO pedido VALUES (101, @L, @Cli, @C, 6, 'Pendente', 'PIX', '80.00'), (102, @L, @Outro, NULL, 1, 'Pendente', 'PIX', '50.00'),
                          (103, @O, @Cli, NULL, 1, 'Pendente', 'PIX', '50.00');",
                new { L = db.Loja, O = db.OutraLoja, C = db.Conversa, Cli = db.Cliente, Outro = Guid.NewGuid() });
            var migracoes = Path.Combine(AppContext.BaseDirectory, "Migrations", "Delivery");
            await db.Execute(File.ReadAllText(Path.Combine(migracoes, "20261031_01_mercadopago_nucleo.sql")));
            await db.Execute(File.ReadAllText(Path.Combine(migracoes, "20261101_50_cobranca_chat_atendente.sql")));
            // Idempotente: aplicar de novo nao quebra.
            await db.Execute(File.ReadAllText(Path.Combine(migracoes, "20261101_50_cobranca_chat_atendente.sql")));

            var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Payments:ChaveMestra"] = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32)),
                ["Payments:MercadoPago:Ambiente"] = "sandbox",
                ["Payments:MercadoPago:NotificationUrl"] = "https://api.teste/api/webhooks/mercadopago",
                ["Payments:MercadoPago:LinkPixBaseUrl"] = "https://painel.teste",
                ["ConnectionStrings:DefaultConnection"] = options.ConnectionString
            }).Build();
            var services = new ServiceCollection();
            services.AddLogging(b => b.SetMinimumLevel(LogLevel.Warning));
            services.AddSingleton<IConfiguration>(config);
            services.AddSingleton(db._source);
            services.AddPagamentosMercadoPago(config);
            services.AddHttpClient(MercadoPagoClient.NomeHttpClient).ConfigurePrimaryHttpMessageHandler(() => db.Mp);
            services.AddScoped<IPagamentoEfeitoHandler, ChatCobrancaPedidoEfeito>();
            services.AddSingleton<ICobrancaChatMensageiro>(db.Mensageiro);
            services.AddSingleton<ICobrancaPedidoAvancador>(db.Avancador);
            services.AddScoped<CobrancaChatService>();
            services.AddScoped<CobrancaChatManutencao>();
            db._sp = services.BuildServiceProvider();

            var protector = db._sp.GetRequiredService<IPagamentoSegredoProtector>();
            var app = Guid.NewGuid();
            var conta = Guid.NewGuid();
            await db.Execute(@"
INSERT INTO mp_aplicacao (id, ambiente, client_id, client_secret_cifrado, webhook_secret_cifrado) VALUES (@App, 'sandbox', '123', @Cs, @Ws);
INSERT INTO mp_conta_loja (id, id_estabelecimento, ambiente, mp_user_id, mp_user_id_mascarado, access_token_cifrado, refresh_token_cifrado, public_key, token_expira_em)
VALUES (@Conta, @L, 'sandbox', @U, '**7001', @At, @Rt, 'TEST-pk', NOW() + INTERVAL '150 days');
INSERT INTO pagamento_preferencia_loja (id_estabelecimento, pix_ativo) VALUES (@L, TRUE), (@O, TRUE);", new
            {
                App = app, Conta = conta, L = db.Loja, O = db.OutraLoja, U = MpUserLoja,
                Cs = protector.Proteger("client-secret", SegredoContexto.AplicacaoClientSecret(app)),
                Ws = protector.Proteger(WebhookSecret, SegredoContexto.AplicacaoWebhookSecret(app)),
                At = protector.Proteger("TOKEN-DA-LOJA", SegredoContexto.LojaAccessToken(conta)),
                Rt = protector.Proteger("REFRESH-DA-LOJA", SegredoContexto.LojaRefreshToken(conta))
            });
            db.Mp.Collector = MpUserLoja;
            return db;
        }

        public MpWebhookEntrada Aviso(long mpId, string notificacao)
        {
            var ts = DateTimeOffset.UtcNow.ToUnixTimeSeconds().ToString();
            var req = Guid.NewGuid().ToString();
            var v1 = MercadoPagoAssinatura.Assinar(WebhookSecret, MercadoPagoAssinatura.Manifesto(mpId.ToString(), req, ts));
            return new MpWebhookEntrada
            {
                Assinatura = $"ts={ts},v1={v1}", RequestId = req, QueryDataId = mpId.ToString(), QueryTipo = "payment",
                CorpoBruto = JsonSerializer.Serialize(new { id = notificacao, type = "payment", action = "payment.updated", live_mode = false, user_id = MpUserLoja, data = new { id = mpId.ToString() } })
            };
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

    /// <summary>MP falso (mesmo contrato da API v1): guarda pagamentos, respeita X-Idempotency-Key, cancela (PUT) e busca por referencia.</summary>
    private sealed class MpFalso : HttpMessageHandler
    {
        private long _proximo = 8_000_000_000;
        private readonly ConcurrentDictionary<string, long> _porChave = new();
        public ConcurrentDictionary<long, MpPagamento> Pagamentos { get; } = new();
        public List<JsonObject> Criacoes { get; } = new();
        public long Collector { get; set; }

        public void Mudar(long id, string status, string detalhe, DateTimeOffset quando)
        {
            var p = Pagamentos[id];
            p.Status = status;
            p.StatusDetail = detalhe;
            p.DateLastUpdated = quando;
            if (status == "approved") p.DateApproved = quando;
        }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            var caminho = request.RequestUri!.AbsolutePath.TrimStart('/');
            var corpo = request.Content == null ? null : JsonNode.Parse(await request.Content.ReadAsStringAsync(ct))?.AsObject();
            var chave = request.Headers.TryGetValues("X-Idempotency-Key", out var v) ? v.First() : null;

            if (request.Method == HttpMethod.Post && caminho == "v1/payments")
            {
                var id = _porChave.GetOrAdd(chave!, _ =>
                {
                    lock (Criacoes) Criacoes.Add(corpo!);
                    var novo = Interlocked.Increment(ref _proximo);
                    Pagamentos[novo] = new MpPagamento
                    {
                        Id = novo, Status = "pending", StatusDetail = "pending_waiting_transfer", CollectorId = Collector,
                        TransactionAmount = corpo!["transaction_amount"]!.GetValue<decimal>(), CurrencyId = "BRL", LiveMode = false,
                        ExternalReference = corpo["external_reference"]!.GetValue<string>(), PaymentMethodId = "pix", PaymentTypeId = "bank_transfer",
                        DateCreated = DateTimeOffset.UtcNow, DateLastUpdated = DateTimeOffset.UtcNow, DateOfExpiration = DateTimeOffset.UtcNow.AddMinutes(30),
                        PointOfInteraction = new MpPointOfInteraction { TransactionData = new MpTransactionData { QrCode = "00020126pix" + novo, QrCodeBase64 = "iVBOR", TicketUrl = "https://mp/t" } }
                    };
                    return novo;
                });
                return Json(Pagamentos[id]);
            }

            if (request.Method == HttpMethod.Get && caminho == "v1/payments/search")
            {
                var referencia = Uri.UnescapeDataString(request.RequestUri.Query.Split('&')[0].Split('=')[1]);
                return Json(new MpBuscaPagamentos { Results = Pagamentos.Values.Where(p => p.ExternalReference == referencia).ToList() });
            }

            var partes = caminho.Split('/');
            if (partes.Length >= 3 && partes[1] == "payments" && long.TryParse(partes[2], out var pid) && Pagamentos.TryGetValue(pid, out var pag))
            {
                if (request.Method == HttpMethod.Get) return Json(pag);
                if (request.Method == HttpMethod.Put) { Mudar(pid, "cancelled", "by_collector", DateTimeOffset.UtcNow); return Json(pag); }
            }
            return new HttpResponseMessage(HttpStatusCode.NotFound) { Content = new StringContent("{\"error\":\"not_found\"}") };
        }

        private static HttpResponseMessage Json(object o) =>
            new(HttpStatusCode.OK) { Content = new StringContent(JsonSerializer.Serialize(o), Encoding.UTF8, "application/json") };
    }
}
