using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using APIBack.Atendimento;
using APIBack.Atendimento.Templates;
using APIBack.Automation.Interfaces;
using APIBack.DTOs.Common;
using APIBack.Service;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Xunit;

namespace APIBack.Tests.Unit
{
    // ---- regras (sem rede) ------------------------------------------------------------------------------------

    public class TemplateRegrasTests
    {
        internal static CriarTemplateRequest Pedido(
            string nome = "pedido_confirmado", string categoria = "UTILITY", string? corpo = "Ola {{1}}, seu pedido {{2}} foi confirmado.",
            IReadOnlyList<string>? exemplos = null, string? cabecalho = null, IReadOnlyList<string>? cabecalhoEx = null, string? rodape = null,
            IReadOnlyList<TemplateBotao>? botoes = null, string? idioma = "pt_BR") =>
            new(nome, categoria, idioma, new TemplateConteudo(cabecalho, cabecalhoEx, corpo, exemplos ?? new[] { "Maria", "#123" }, rodape, botoes));

        private static Dictionary<string, List<string>> Erros(CriarTemplateRequest p) =>
            Assert.Throws<TemplateIncompativelException>(() => TemplateRegras.Validar(p)).Errors;

        [Fact]
        public void Modelo_valido_normaliza_e_monta_os_componentes_da_meta()
        {
            var novo = TemplateRegras.Validar(Pedido(categoria: "utilidade", cabecalho: " Pedido {{1}} ", cabecalhoEx: new[] { "#123" }, rodape: "Zippy",
                botoes: new[] { new TemplateBotao("quick_reply", "Ok"), new TemplateBotao("URL", "Acompanhar", "https://zippy.app/p/{{1}}", "https://zippy.app/p/123") }));

            Assert.Equal(CategoriaTemplate.Utilidade, novo.Categoria);
            Assert.Equal("Pedido {{1}}", novo.Conteudo.Cabecalho);
            var json = JsonSerializer.Serialize(TemplateRegras.PayloadMeta(novo));
            using var doc = JsonDocument.Parse(json);
            var raiz = doc.RootElement;
            Assert.Equal("pedido_confirmado", raiz.GetProperty("name").GetString());
            Assert.Equal("pt_BR", raiz.GetProperty("language").GetString());
            var comps = raiz.GetProperty("components").EnumerateArray().ToList();
            Assert.Equal(new[] { "HEADER", "BODY", "FOOTER", "BUTTONS" }, comps.Select(c => c.GetProperty("type").GetString()));
            Assert.Equal("#123", comps[0].GetProperty("example").GetProperty("header_text")[0].GetString());
            Assert.Equal("Maria", comps[1].GetProperty("example").GetProperty("body_text")[0][0].GetString());
            var botoes = comps[3].GetProperty("buttons");
            Assert.Equal("QUICK_REPLY", botoes[0].GetProperty("type").GetString());
            Assert.Equal("https://zippy.app/p/123", botoes[1].GetProperty("example")[0].GetString());

            // Ida e volta: o que a Meta devolve volta para o formato do Zippy.
            var volta = TemplateRegras.DeComponentesMeta(raiz.GetProperty("components"));
            Assert.Equal(novo.Conteudo.Corpo, volta!.Corpo);
            Assert.Equal(new[] { "Maria", "#123" }, volta.CorpoExemplos);
            Assert.Equal("https://zippy.app/p/123", volta.Botoes![1].UrlExemplo);
        }

        [Theory]
        [InlineData("Pedido_Confirmado")]
        [InlineData("pedido confirmado")]
        [InlineData("pedido-confirmado")]
        [InlineData("pedidoçao")]
        [InlineData("")]
        public void Nome_tecnico_segue_a_regra_da_meta(string nome) => Assert.True(Erros(Pedido(nome: nome)).ContainsKey("nome"));

        [Theory]
        [InlineData("Ola {{ 1 }}, tudo bem?")]
        [InlineData("Ola {{nome}}, tudo bem?")]
        [InlineData("Ola {1}, tudo bem?")]
        [InlineData("Ola {{1}, tudo bem?")]
        public void Variavel_malformada_e_recusada(string corpo) =>
            Assert.Contains(Erros(Pedido(corpo: corpo, exemplos: new[] { "x" }))["corpo"], m => m.Contains("malformada"));

        [Fact]
        public void Variaveis_em_sequencia_com_texto_entre_elas_e_sem_ficar_nas_pontas()
        {
            Assert.Contains(Erros(Pedido(corpo: "Ola {{1}} e {{3}} ok", exemplos: new[] { "a", "b" }))["corpo"], m => m.Contains("sequencia"));
            Assert.Contains(Erros(Pedido(corpo: "{{1}}, seu pedido saiu", exemplos: new[] { "a" }))["corpo"], m => m.Contains("comecar"));
            Assert.Contains(Erros(Pedido(corpo: "Seu pedido saiu {{1}}", exemplos: new[] { "a" }))["corpo"], m => m.Contains("terminar"));
            Assert.Contains(Erros(Pedido(corpo: "Ola {{1}} {{2}} ok", exemplos: new[] { "a", "b" }))["corpo"], m => m.Contains("entre duas"));
            // Repetir a mesma variavel e permitido; o exemplo e um so.
            TemplateRegras.Validar(Pedido(corpo: "Ola {{1}}, confirme {{1}} por favor", exemplos: new[] { "Maria" }));
        }

        [Fact]
        public void Exemplos_obrigatorios_um_por_variavel()
        {
            Assert.True(Erros(Pedido(exemplos: new[] { "Maria" })).ContainsKey("corpoExemplos"));
            Assert.True(Erros(Pedido(exemplos: new[] { "Maria", " " })).ContainsKey("corpoExemplos[1]"));
            Assert.True(Erros(Pedido(corpo: "Sem variavel aqui.", exemplos: new[] { "sobrando" })).ContainsKey("corpoExemplos"));
        }

        [Fact]
        public void Limites_de_tamanho_e_componentes()
        {
            Assert.True(Erros(Pedido(corpo: "a" + new string('b', TemplateRegras.CorpoMax), exemplos: Array.Empty<string>())).ContainsKey("corpo"));
            Assert.True(Erros(Pedido(cabecalho: new string('c', 61))).ContainsKey("cabecalho"));
            Assert.True(Erros(Pedido(cabecalho: "{{1}} e {{2}}", cabecalhoEx: new[] { "a", "b" })).ContainsKey("cabecalho"));
            Assert.True(Erros(Pedido(cabecalho: "Pedido {{1}}")).ContainsKey("cabecalhoExemplos"));
            Assert.True(Erros(Pedido(rodape: "Ate {{1}}")).ContainsKey("rodape"));
            Assert.True(Erros(Pedido(corpo: "", exemplos: Array.Empty<string>())).ContainsKey("corpo"));
        }

        [Fact]
        public void Botoes_compativeis_com_a_categoria_e_as_regras()
        {
            var cupom = new TemplateBotao("COPY_CODE", Codigo: "ZIPPY10");
            Assert.True(Erros(Pedido(botoes: new[] { cupom })).ContainsKey("botoes[0].tipo"));
            TemplateRegras.Validar(Pedido(categoria: "MARKETING", botoes: new[] { cupom }));

            var onze = Enumerable.Range(1, 11).Select(i => new TemplateBotao("QUICK_REPLY", $"Op {i}")).ToList();
            Assert.True(Erros(Pedido(botoes: onze)).ContainsKey("botoes"));
            Assert.True(Erros(Pedido(botoes: new[] { new TemplateBotao("QUICK_REPLY", new string('x', 26)) })).ContainsKey("botoes[0].texto"));
            Assert.True(Erros(Pedido(botoes: new[] { new TemplateBotao("QUICK_REPLY", "Sim"), new TemplateBotao("QUICK_REPLY", "sim") })).ContainsKey("botoes"));

            var misturados = new[]
            {
                new TemplateBotao("QUICK_REPLY", "A"), new TemplateBotao("URL", "Site", "https://zippy.app"), new TemplateBotao("QUICK_REPLY", "B")
            };
            Assert.Contains(Erros(Pedido(botoes: misturados))["botoes"], m => m.Contains("Agrupe"));

            Assert.True(Erros(Pedido(botoes: new[] { new TemplateBotao("URL", "Site", "ftp://zippy.app") })).ContainsKey("botoes[0].url"));
            Assert.True(Erros(Pedido(botoes: new[] { new TemplateBotao("URL", "Site", "https://zippy.app/{{1}}/x") })).ContainsKey("botoes[0].url"));
            Assert.True(Erros(Pedido(botoes: new[] { new TemplateBotao("URL", "Site", "https://zippy.app/{{1}}", "https://outro.com/1") })).ContainsKey("botoes[0].urlExemplo"));
            Assert.True(Erros(Pedido(botoes: new[] { new TemplateBotao("PHONE_NUMBER", "Ligar", Telefone: "3499") })).ContainsKey("botoes[0].telefone"));
            Assert.True(Erros(Pedido(botoes: new[] { new TemplateBotao("PHONE_NUMBER", "A", Telefone: "+5534999990000"), new TemplateBotao("PHONE_NUMBER", "B", Telefone: "+5534999990001") })).ContainsKey("botoes"));
        }

        [Fact]
        public void Pedido_malformado_e_400_e_nao_422()
        {
            Assert.Throws<TemplateRequisicaoInvalidaException>(() => TemplateRegras.Validar(null));
            Assert.True(Assert.Throws<TemplateRequisicaoInvalidaException>(() => TemplateRegras.Validar(Pedido(categoria: "AUTHENTICATION"))).Errors.ContainsKey("categoria"));
            Assert.True(Assert.Throws<TemplateRequisicaoInvalidaException>(() => TemplateRegras.Validar(Pedido(idioma: "xx_YY"))).Errors.ContainsKey("idioma"));
            Assert.True(Assert.Throws<TemplateRequisicaoInvalidaException>(() => TemplateRegras.Validar(Pedido(botoes: new[] { new TemplateBotao("FLOW", "x") }))).Errors.ContainsKey("botoes[0].tipo"));
            Assert.Throws<TemplateRequisicaoInvalidaException>(() => TemplateRegras.Validar(new CriarTemplateRequest("x", "UTILITY", "pt_BR", null)));
        }

        [Fact]
        public void Status_resumido_nunca_mostra_aprovado_sem_a_meta_aprovar()
        {
            Assert.Equal(StatusTemplate.Aprovado, StatusTemplate.De(SituacaoTemplate.Enviado, "APPROVED"));
            Assert.Equal(StatusTemplate.EmAnalise, StatusTemplate.De(SituacaoTemplate.Enviado, "PENDING"));
            Assert.Equal(StatusTemplate.Rejeitado, StatusTemplate.De(SituacaoTemplate.Enviado, "REJECTED"));
            Assert.Equal(StatusTemplate.Pausado, StatusTemplate.De(SituacaoTemplate.Enviado, "PAUSED"));
            Assert.Equal(StatusTemplate.Desativado, StatusTemplate.De(SituacaoTemplate.Enviado, "DISABLED"));
            Assert.Equal(StatusTemplate.Indisponivel, StatusTemplate.De(SituacaoTemplate.Enviado, "DELETED"));
            Assert.Equal(StatusTemplate.Verificando, StatusTemplate.De(SituacaoTemplate.Verificando, "APPROVED"));
            Assert.Equal(StatusTemplate.Falha, StatusTemplate.De(SituacaoTemplate.Falha, null));
        }

        [Fact]
        public void Erros_da_meta_viram_codigos_e_http_da_tabela()
        {
            WhatsappException E(int? http, int? codigo, int? sub = null, bool semResposta = false) =>
                TemplateErros.DaMeta(new MetaFalha(semResposta, http, codigo, sub, "x", null));

            Assert.Equal((409, TemplateCodigos.Duplicado), (E(400, 100, 2388024).Http, E(400, 100, 2388024).Erro.Codigo));
            Assert.Equal((409, TemplateCodigos.NomeEmExclusao), (E(400, 100, 2388023).Http, E(400, 100, 2388023).Erro.Codigo));
            Assert.Equal((422, WhatsappCodigos.TokenInvalido), (E(401, 190).Http, E(401, 190).Erro.Codigo));
            Assert.Equal((422, TemplateCodigos.SemPermissaoGestao), (E(403, 200).Http, E(403, 200).Erro.Codigo));
            Assert.Equal((429, TemplateCodigos.Limite), (E(400, 80008).Http, E(400, 80008).Erro.Codigo));
            Assert.Equal((429, TemplateCodigos.Limite), (E(429, null).Http, E(429, null).Erro.Codigo));
            Assert.Equal((503, WhatsappCodigos.MetaIndisponivel), (E(null, null, semResposta: true).Http, E(null, null, semResposta: true).Erro.Codigo));
            Assert.Equal(503, E(500, 2).Http);
            Assert.Equal((422, TemplateCodigos.ConteudoRecusado), (E(400, 100, 2388040).Http, E(400, 100, 2388040).Erro.Codigo));
            Assert.Equal(502, E(403, 12345).Http);
        }
    }

    // ---- cliente da Graph API (HTTP falso) ---------------------------------------------------------------------

    public class MetaTemplatesApiTests
    {
        private sealed class Handler : HttpMessageHandler
        {
            public List<HttpRequestMessage> Pedidos { get; } = new();
            public List<string?> Corpos { get; } = new();
            public Queue<Func<HttpRequestMessage, HttpResponseMessage>> Respostas { get; } = new();

            protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            {
                Pedidos.Add(request);
                Corpos.Add(request.Content == null ? null : await request.Content.ReadAsStringAsync(cancellationToken));
                return Respostas.Dequeue()(request);
            }
        }

        private static HttpResponseMessage Json(HttpStatusCode status, string corpo) => new(status) { Content = new StringContent(corpo, Encoding.UTF8, "application/json") };

        private static (MetaTemplatesApi Api, Handler Handler) Criar()
        {
            var handler = new Handler();
            var fabrica = new Mock<IHttpClientFactory>();
            fabrica.Setup(f => f.CreateClient(It.IsAny<string>())).Returns(() => new HttpClient(handler, disposeHandler: false));
            var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?> { ["WhatsApp:GraphApiVersion"] = "v23.0" }).Build();
            var api = new MetaTemplatesApi(fabrica.Object, config, NullLogger<MetaTemplatesApi>.Instance) { Esperar = _ => Task.CompletedTask };
            return (api, handler);
        }

        [Fact]
        public async Task Lista_todas_as_paginas_pelo_cursor_sem_seguir_url_de_outro_host()
        {
            var (api, h) = Criar();
            h.Respostas.Enqueue(_ => Json(HttpStatusCode.OK,
                "{\"data\":[{\"id\":\"1\",\"name\":\"a\",\"language\":\"pt_BR\",\"status\":\"APPROVED\",\"category\":\"UTILITY\",\"quality_score\":{\"score\":\"GREEN\"}}]," +
                "\"paging\":{\"cursors\":{\"after\":\"CUR2\"},\"next\":\"https://evil.example.com/steal\"}}"));
            h.Respostas.Enqueue(_ => Json(HttpStatusCode.OK, "{\"data\":[{\"id\":\"2\",\"name\":\"b\",\"language\":\"pt_BR\",\"status\":\"REJECTED\",\"rejected_reason\":\"INVALID_FORMAT\"}],\"paging\":{\"cursors\":{\"after\":\"CUR3\"}}}"));

            var r = await api.ListarAsync("109876543210987", "tok");

            Assert.True(r.Ok);
            Assert.True(r.Valor!.Completa);
            Assert.Equal(new[] { "1", "2" }, r.Valor.Itens.Select(i => i.Id));
            Assert.Equal("GREEN", r.Valor.Itens[0].Qualidade);
            Assert.Equal("INVALID_FORMAT", r.Valor.Itens[1].MotivoRejeicao);
            Assert.All(h.Pedidos, p => Assert.Equal("graph.facebook.com", p.RequestUri!.Host));
            Assert.All(h.Pedidos, p => Assert.Equal("Bearer tok", p.Headers.Authorization!.ToString()));
            Assert.All(h.Pedidos, p => Assert.DoesNotContain("tok", p.RequestUri!.Query));
            Assert.Contains("/109876543210987/message_templates", h.Pedidos[0].RequestUri!.AbsolutePath);
            Assert.Contains("after=CUR2", h.Pedidos[1].RequestUri!.Query);
        }

        [Fact]
        public async Task Leitura_repete_em_5xx_e_rede_mas_nao_em_4xx_nem_429()
        {
            var (api, h) = Criar();
            h.Respostas.Enqueue(_ => Json(HttpStatusCode.InternalServerError, "{\"error\":{\"code\":2}}"));
            h.Respostas.Enqueue(_ => throw new HttpRequestException("rede"));
            h.Respostas.Enqueue(_ => Json(HttpStatusCode.OK, "{\"data\":[]}"));
            Assert.True((await api.ListarAsync("1", "tok")).Ok);
            Assert.Equal(3, h.Pedidos.Count);

            h.Respostas.Enqueue(_ => Json((HttpStatusCode)429, "{\"error\":{\"code\":80008,\"message\":\"rate\"}}"));
            var limite = await api.ListarAsync("1", "tok");
            Assert.Equal(80008, limite.Falha!.Codigo);
            Assert.Equal(4, h.Pedidos.Count);
        }

        [Fact]
        public async Task Criacao_nunca_repete_e_timeout_volta_como_sem_resposta()
        {
            var (api, h) = Criar();
            h.Respostas.Enqueue(_ => throw new TaskCanceledException("timeout"));
            var r = await api.CriarAsync("1", "tok", new { name = "x" });
            Assert.True(r.Falha!.SemResposta);
            Assert.Single(h.Pedidos);

            h.Respostas.Enqueue(_ => Json(HttpStatusCode.OK, "{\"id\":\"999\",\"status\":\"PENDING\",\"category\":\"UTILITY\"}"));
            var ok = await api.CriarAsync("1", "tok", new { name = "x" });
            Assert.Equal(("999", "PENDING"), (ok.Valor!.Id, ok.Valor.Status));
            Assert.Equal(HttpMethod.Post, h.Pedidos[1].Method);

            h.Respostas.Enqueue(_ => Json(HttpStatusCode.BadRequest,
                "{\"error\":{\"code\":100,\"error_subcode\":2388024,\"message\":\"dup\",\"error_user_msg\":\"Ja existe conteudo neste idioma\"}}"));
            var dup = await api.CriarAsync("1", "tok", new { name = "x" });
            Assert.Equal((100, 2388024, "Ja existe conteudo neste idioma"), (dup.Falha!.Codigo, dup.Falha.Subcodigo, dup.Falha.MensagemUsuario));
        }

        [Fact]
        public async Task Exclusao_aponta_so_o_modelo_pelo_hsm_id()
        {
            var (api, h) = Criar();
            h.Respostas.Enqueue(_ => Json(HttpStatusCode.OK, "{\"success\":true}"));
            Assert.True((await api.ExcluirAsync("1", "tok", "pedido_ok", "777")).Ok);
            Assert.Equal(HttpMethod.Delete, h.Pedidos[0].Method);
            Assert.Contains("hsm_id=777", h.Pedidos[0].RequestUri!.Query);
            Assert.Contains("name=pedido_ok", h.Pedidos[0].RequestUri!.Query);
        }
    }

    // ---- servico (Meta falsa, banco em memoria) ----------------------------------------------------------------

    internal sealed class TemplatesEmMemoria : ITemplateRepository
    {
        private static readonly string[] Ativas = { SituacaoTemplate.Enviando, SituacaoTemplate.Verificando, SituacaoTemplate.Enviado };
        public List<TemplateWhatsapp> Linhas { get; } = new();
        public List<(Guid Template, string Acao, string? Detalhe)> Eventos { get; } = new();

        private static TemplateWhatsapp Copia(TemplateWhatsapp t) => (TemplateWhatsapp)typeof(object)
            .GetMethod("MemberwiseClone", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!.Invoke(t, null)!;

        public Task<IReadOnlyList<TemplateWhatsapp>> ListarAsync(Guid est, Guid canal, string? waba)
        {
            var wabasDoCanal = Linhas.Where(t => t.IdCanal == canal && t.IdEstabelecimento == est).Select(t => t.WabaId).ToHashSet();
            return Task.FromResult<IReadOnlyList<TemplateWhatsapp>>(Linhas
                .Where(t => t.IdEstabelecimento == est && (t.IdCanal == canal || t.WabaId == waba || (waba == null && wabasDoCanal.Contains(t.WabaId))))
                .Select(Copia).ToList());
        }

        public Task<TemplateWhatsapp?> ObterAsync(Guid id) => Task.FromResult(Linhas.Where(t => t.Id == id).Select(Copia).FirstOrDefault());

        public Task<IReadOnlyList<TemplateWhatsapp>> ListarDaWabaAsync(string waba) =>
            Task.FromResult<IReadOnlyList<TemplateWhatsapp>>(Linhas.Where(t => t.WabaId == waba && t.Situacao != SituacaoTemplate.Excluido).Select(Copia).ToList());

        public Task InserirAsync(TemplateWhatsapp t)
        {
            Conferir(t);
            Linhas.Add(Copia(t));
            return Task.CompletedTask;
        }

        public Task AtualizarAsync(TemplateWhatsapp t)
        {
            Conferir(t);
            var i = Linhas.FindIndex(x => x.Id == t.Id);
            Linhas[i] = Copia(t);
            return Task.CompletedTask;
        }

        // Os mesmos indices unicos da migracao.
        private void Conferir(TemplateWhatsapp t)
        {
            var outros = Linhas.Where(x => x.Id != t.Id).ToList();
            if (Ativas.Contains(t.Situacao) && outros.Any(x => Ativas.Contains(x.Situacao) && x.WabaId == t.WabaId && x.Nome == t.Nome && x.Idioma == t.Idioma))
                throw new TemplateConflitoException("nome");
            if (t.MetaTemplateId != null && outros.Any(x => x.WabaId == t.WabaId && x.MetaTemplateId == t.MetaTemplateId))
                throw new TemplateConflitoException("meta");
            if (t.Situacao == SituacaoTemplate.Enviado && t.MetaTemplateId == null) throw new InvalidOperationException("enviado sem id");
        }

        public Task RegistrarEventoAsync(Guid templateId, string acao, int? usuarioId, string? detalheJson)
        {
            Eventos.Add((templateId, acao, detalheJson));
            return Task.CompletedTask;
        }

        public Task<IReadOnlyList<TemplateEvento>> ListarEventosAsync(Guid templateId) =>
            Task.FromResult<IReadOnlyList<TemplateEvento>>(Eventos.Where(e => e.Template == templateId)
                .Select(e => new TemplateEvento { Acao = e.Acao, DetalheJson = e.Detalhe, CriadoEm = DateTime.UtcNow }).Reverse().ToList());
    }

    public class TemplatesWhatsappServiceTests
    {
        private const string WabaZippy = "200000000000001";
        private const string OutraWabaZippy = "200000000000002";
        private const string TokenGlobal = "EAAG-token-global-da-zippy";

        private sealed class Fixture
        {
            public Mock<ICanalRepository> Canais { get; } = new();
            public TemplatesEmMemoria Repo { get; } = new();
            public Mock<IMetaTemplatesApi> Meta { get; } = new(MockBehavior.Strict);
            public Mock<IWhatsAppTokenProvider> Global { get; } = new();
            public ITokenProtector Protetor { get; } = WaTeste.Protetor();
            public Dictionary<Guid, CanalWhatsapp> CanaisPorId { get; } = new();
            public List<MetaTemplate> Remotos { get; } = new();
            public List<object> Criados { get; } = new();
            public Dictionary<string, List<string>> NumerosPorWaba { get; } = new();
            public Func<MetaResultado<MetaCriacao>>? RespostaCriacao { get; set; }
            public bool ListaCompleta { get; set; } = true;
            public DateTime Agora { get; set; } = new(2026, 10, 25, 12, 0, 0, DateTimeKind.Utc);
            public string? WabaIds { get; set; } = WabaZippy;
            public IMemoryCache Cache { get; } = new MemoryCache(new MemoryCacheOptions());

            public Fixture()
            {
                Global.Setup(g => g.GetAccessToken()).Returns(TokenGlobal);
                Canais.Setup(c => c.ObterAsync(It.IsAny<Guid>())).ReturnsAsync((Guid id) => CanaisPorId.TryGetValue(id, out var c) ? c : null);
                Canais.Setup(c => c.RegistrarAuditoriaAsync(It.IsAny<Guid?>(), It.IsAny<string>(), It.IsAny<Guid?>(), It.IsAny<Guid?>(), It.IsAny<int?>(), It.IsAny<string?>()))
                    .Returns(Task.CompletedTask);

                Meta.Setup(m => m.ListarNumerosAsync(It.IsAny<string>(), It.IsAny<string>()))
                    .ReturnsAsync((string waba, string _) => NumerosPorWaba.TryGetValue(waba, out var n)
                        ? MetaResultado<IReadOnlyList<string>>.Sucesso(n)
                        : MetaResultado<IReadOnlyList<string>>.Erro(new MetaFalha(false, 400, 100, 33, "nao existe", null)));
                Meta.Setup(m => m.ListarAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string?>()))
                    .ReturnsAsync((string _, string _, string? nome) => MetaResultado<MetaListaTemplates>.Sucesso(
                        new MetaListaTemplates(Remotos.Where(r => nome == null || r.Nome.Contains(nome)).ToList(), ListaCompleta)));
                Meta.Setup(m => m.ObterAsync(It.IsAny<string>(), It.IsAny<string>()))
                    .ReturnsAsync((string id, string _) => Remotos.FirstOrDefault(r => r.Id == id) is { } r
                        ? MetaResultado<MetaTemplate>.Sucesso(r)
                        : MetaResultado<MetaTemplate>.Erro(new MetaFalha(false, 400, 100, 33, "nao existe", null)));
                Meta.Setup(m => m.CriarAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<object>()))
                    .ReturnsAsync((string _, string _, object payload) =>
                    {
                        Criados.Add(payload);
                        return RespostaCriacao?.Invoke() ?? MetaResultado<MetaCriacao>.Sucesso(new MetaCriacao((900 + Criados.Count).ToString(), "PENDING", "UTILITY"));
                    });
                Meta.Setup(m => m.ExcluirAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>()))
                    .ReturnsAsync(MetaResultado<bool>.Sucesso(true));
            }

            public CanalWhatsapp Canal(Guid loja, string origem = OrigemCanal.Zippy, string? waba = null, string phone = WaTeste.IdMeta, bool comToken = false)
            {
                var canal = new CanalWhatsapp
                {
                    Id = Guid.NewGuid(), IdEstabelecimento = loja, PhoneNumberId = phone, NumeroE164 = "+5534998218242", Origem = origem,
                    Central = origem == OrigemCanal.Central, WabaId = waba, Status = StatusCanal.Ativo,
                    TokenCifrado = origem == OrigemCanal.Proprio || comToken ? Protetor.Proteger(WaTeste.Token) : null
                };
                CanaisPorId[canal.Id] = canal;
                return canal;
            }

            public TemplatesWhatsappService Build()
            {
                var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?> { ["WhatsApp:WabaIds"] = WabaIds }).Build();
                return new TemplatesWhatsappService(Canais.Object, Repo, Meta.Object, Protetor, Global.Object, config, Cache,
                    NullLogger<TemplatesWhatsappService>.Instance) { Agora = () => Agora };
            }
        }

        private static readonly Guid LojaA = WaTeste.Loja;
        private static readonly Guid LojaB = WaTeste.OutraLoja;

        private static CriarTemplateRequest Pedido(string nome = "pedido_confirmado") => TemplateRegrasTests.Pedido(nome: nome);

        private static MetaTemplate Remoto(string id, string nome, string status = "PENDING", string? motivo = null) =>
            new(id, nome, "pt_BR", status, "UTILITY", motivo, null, JsonDocument.Parse("[{\"type\":\"BODY\",\"text\":\"Ola {{1}}, seu pedido {{2}} foi confirmado.\"}]").RootElement.Clone());

        [Fact]
        public async Task Cria_na_waba_confirmada_grava_o_id_e_o_status_real_da_meta()
        {
            var f = new Fixture();
            f.NumerosPorWaba[WabaZippy] = new() { WaTeste.IdMeta };
            var canal = f.Canal(LojaA);

            var r = await f.Build().CriarAsync(WaTeste.Dono(), LojaA, canal.Id, Pedido());

            Assert.True(r.Confirmado);
            Assert.Equal("901", r.Modelo.MetaTemplateId);
            Assert.Equal(StatusTemplate.EmAnalise, r.Modelo.Status);
            Assert.Equal("PENDING", r.Modelo.StatusMeta);
            Assert.False(r.Modelo.DisponivelParaEnvio);
            var linha = Assert.Single(f.Repo.Linhas);
            Assert.Equal((LojaA, WabaZippy, SituacaoTemplate.Enviado, (int?)2), (linha.IdEstabelecimento, linha.WabaId, linha.Situacao, linha.CriadoPor));
            Assert.Equal(new[] { "criado", "enviado" }, f.Repo.Eventos.Select(e => e.Acao));
            // Operou pela WABA (nunca pelo Phone Number ID) e com o token do servidor.
            f.Meta.Verify(m => m.CriarAsync(WabaZippy, TokenGlobal, It.IsAny<object>()), Times.Once);
            Assert.DoesNotContain(TokenGlobal, JsonSerializer.Serialize(r));
        }

        [Fact]
        public async Task Numero_proprio_usa_a_waba_e_o_token_da_conexao_e_confirma_o_vinculo()
        {
            var f = new Fixture();
            f.NumerosPorWaba[WaTeste.Waba] = new() { "111111111111111" }; // a WABA existe mas o numero nao e dela
            var canal = f.Canal(LojaA, OrigemCanal.Proprio, WaTeste.Waba);

            var ex = await Assert.ThrowsAsync<WhatsappException>(() => f.Build().CriarAsync(WaTeste.Dono(), LojaA, canal.Id, Pedido()));
            Assert.Equal(TemplateCodigos.WabaIncorreta, ex.Erro.Codigo);
            Assert.Empty(f.Criados);

            f.NumerosPorWaba[WaTeste.Waba] = new() { WaTeste.IdMeta };
            await f.Build().CriarAsync(WaTeste.Dono(), LojaA, canal.Id, Pedido());
            f.Meta.Verify(m => m.CriarAsync(WaTeste.Waba, WaTeste.Token, It.IsAny<object>()), Times.Once);
        }

        [Fact]
        public async Task Numero_da_zippy_acha_a_waba_autorizada_e_sem_configuracao_avisa_o_servidor()
        {
            var f = new Fixture { WabaIds = $"{OutraWabaZippy}, {WabaZippy}" };
            f.NumerosPorWaba[OutraWabaZippy] = new() { "333333333333333" };
            f.NumerosPorWaba[WabaZippy] = new() { WaTeste.IdMeta };
            var canal = f.Canal(LojaA);
            await f.Build().CriarAsync(WaTeste.Dono(), LojaA, canal.Id, Pedido());
            f.Meta.Verify(m => m.CriarAsync(WabaZippy, It.IsAny<string>(), It.IsAny<object>()), Times.Once);

            var semConfig = new Fixture { WabaIds = null };
            var c2 = semConfig.Canal(LojaA);
            var ex = await Assert.ThrowsAsync<WhatsappException>(() => semConfig.Build().CriarAsync(WaTeste.Dono(), LojaA, c2.Id, Pedido()));
            Assert.Equal((TemplateCodigos.WabaNaoConfigurada, 503), (ex.Erro.Codigo, ex.Http));

            // WABA gravada no canal da Zippy que nao esta entre as autorizadas: nao opera.
            var f3 = new Fixture();
            var c3 = f3.Canal(LojaA, waba: "999999999999999");
            Assert.Equal(TemplateCodigos.WabaIncorreta,
                (await Assert.ThrowsAsync<WhatsappException>(() => f3.Build().CriarAsync(WaTeste.Dono(), LojaA, c3.Id, Pedido()))).Erro.Codigo);
        }

        [Fact]
        public async Task Sem_token_ou_token_invalido_ou_sem_permissao_de_gestao_explica_o_motivo()
        {
            var f = new Fixture();
            f.Global.Setup(g => g.GetAccessToken()).Returns("__SET_IN_ENV__");
            var canal = f.Canal(LojaA);
            Assert.Equal(WhatsappCodigos.TokenGlobalAusente,
                (await Assert.ThrowsAsync<WhatsappException>(() => f.Build().CriarAsync(WaTeste.Dono(), LojaA, canal.Id, Pedido()))).Erro.Codigo);

            foreach (var (codigo, esperado) in new[] { (190, WhatsappCodigos.TokenInvalido), (200, TemplateCodigos.SemPermissaoGestao) })
            {
                var g = new Fixture();
                var c = g.Canal(LojaA);
                g.Meta.Setup(m => m.ListarNumerosAsync(It.IsAny<string>(), It.IsAny<string>()))
                    .ReturnsAsync(MetaResultado<IReadOnlyList<string>>.Erro(new MetaFalha(false, 400, codigo, null, "x", null)));
                var ex = await Assert.ThrowsAsync<WhatsappException>(() => g.Build().CriarAsync(WaTeste.Dono(), LojaA, c.Id, Pedido()));
                Assert.Equal(esperado, ex.Erro.Codigo);
                Assert.Empty(g.Repo.Linhas);
            }

            var d = new Fixture();
            d.NumerosPorWaba[WabaZippy] = new() { WaTeste.IdMeta };
            var cd = d.Canal(LojaA);
            var diag = await d.Build().DiagnosticarAsync(WaTeste.Leitor(), LojaA, cd.Id);
            Assert.True(diag.Ok);
        }

        [Fact]
        public async Task Duplicidade_na_meta_e_409_e_fica_no_historico_como_falha()
        {
            var f = new Fixture();
            f.NumerosPorWaba[WabaZippy] = new() { WaTeste.IdMeta };
            var canal = f.Canal(LojaA);
            f.RespostaCriacao = () => MetaResultado<MetaCriacao>.Erro(new MetaFalha(false, 400, 100, 2388024, "dup", null));

            var ex = await Assert.ThrowsAsync<WhatsappException>(() => f.Build().CriarAsync(WaTeste.Dono(), LojaA, canal.Id, Pedido()));
            Assert.Equal((409, TemplateCodigos.Duplicado), (ex.Http, ex.Erro.Codigo));
            Assert.Equal(SituacaoTemplate.Falha, Assert.Single(f.Repo.Linhas).Situacao);
            Assert.Contains("recusado_meta", f.Repo.Eventos.Select(e => e.Acao));

            // Ja enviado pela loja: 409 sem chamar a Meta de novo.
            f.RespostaCriacao = null;
            await f.Build().CriarAsync(WaTeste.Dono(), LojaA, canal.Id, Pedido());
            var de_novo = await Assert.ThrowsAsync<WhatsappException>(() => f.Build().CriarAsync(WaTeste.Dono(), LojaA, canal.Id, Pedido()));
            Assert.Equal(409, de_novo.Http);
            Assert.Equal(2, f.Criados.Count);
        }

        [Fact]
        public async Task Nome_ja_existente_na_meta_e_desconhecido_nao_e_adotado_e_bloqueia()
        {
            var f = new Fixture();
            f.NumerosPorWaba[WabaZippy] = new() { WaTeste.IdMeta };
            f.Remotos.Add(Remoto("555", "pedido_confirmado", "APPROVED"));
            var canal = f.Canal(LojaA);

            var ex = await Assert.ThrowsAsync<WhatsappException>(() => f.Build().CriarAsync(WaTeste.Dono(), LojaA, canal.Id, Pedido()));
            Assert.Equal(409, ex.Http);
            Assert.Empty(f.Criados);
            Assert.Empty(f.Repo.Linhas);
        }

        [Fact]
        public async Task Waba_compartilhada_isola_as_lojas()
        {
            var f = new Fixture();
            f.NumerosPorWaba[WabaZippy] = new() { WaTeste.IdMeta, "444444444444444" };
            var canalA = f.Canal(LojaA);
            var canalB = f.Canal(LojaB, phone: "444444444444444");
            var servico = f.Build();

            var criado = await servico.CriarAsync(WaTeste.Dono(), LojaA, canalA.Id, Pedido());
            f.Remotos.Add(Remoto(criado.Modelo.MetaTemplateId!, "pedido_confirmado", "APPROVED"));
            f.Remotos.Add(Remoto("777", "de_fora_do_zippy", "REJECTED"));

            // B nao usa o nome de A (em nenhum idioma).
            var dup = await Assert.ThrowsAsync<WhatsappException>(() => servico.CriarAsync(WaTeste.Dono(LojaB), LojaB, canalB.Id, Pedido()));
            Assert.Equal(409, dup.Http);

            // B sincroniza: nao enxerga nem altera o modelo de A nem adota o de fora; nao sabe quantos existem.
            var syncB = await servico.SincronizarAsync(WaTeste.Dono(LojaB), LojaB, canalB.Id);
            Assert.Empty(syncB.Lista.Itens);
            Assert.Null(syncB.Ignorados);
            Assert.Equal("PENDING", f.Repo.Linhas.Single().StatusMeta);

            // A sincroniza e recebe a aprovacao real; o modelo de fora continua sem dono.
            var syncA = await servico.SincronizarAsync(WaTeste.Dono(), LojaA, canalA.Id);
            var item = Assert.Single(syncA.Lista.Itens);
            Assert.Equal(StatusTemplate.Aprovado, item.Status);
            Assert.True(item.DisponivelParaEnvio);
            Assert.Single(f.Repo.Linhas);

            // B nao le o modelo de A nem pelo ID.
            await Assert.ThrowsAsync<KeyNotFoundException>(() => servico.ObterAsync(WaTeste.Dono(LojaB), LojaB, canalB.Id, item.Id));

            // A Gestao ve quantos modelos da WABA nao sao do Zippy.
            f.Cache.Remove($"wa.tpl.sync:{canalA.Id}");
            var syncGestao = await servico.SincronizarAsync(WaTeste.Gestao, LojaA, canalA.Id);
            Assert.Equal(1, syncGestao.Ignorados);
        }

        [Fact]
        public async Task Acesso_indevido_entre_lojas_e_permissao()
        {
            var f = new Fixture();
            f.NumerosPorWaba[WabaZippy] = new() { WaTeste.IdMeta };
            var canalA = f.Canal(LojaA);
            var servico = f.Build();

            // Usuario da loja B chamando a rota da loja A.
            await Assert.ThrowsAsync<UnauthorizedAccessException>(() => servico.ListarAsync(WaTeste.Dono(LojaB), LojaA, canalA.Id));
            await Assert.ThrowsAsync<UnauthorizedAccessException>(() => servico.CriarAsync(WaTeste.Dono(LojaB), LojaA, canalA.Id, Pedido()));
            // Canal da loja A pela rota da loja B: como se nao existisse.
            await Assert.ThrowsAsync<KeyNotFoundException>(() => servico.ListarAsync(WaTeste.Dono(LojaB), LojaB, canalA.Id));
            // Quem so le: lista, mas nao cria nem sincroniza.
            var lista = await servico.ListarAsync(WaTeste.Leitor(), LojaA, canalA.Id);
            Assert.False(lista.PodeGerenciar);
            await Assert.ThrowsAsync<UnauthorizedAccessException>(() => servico.CriarAsync(WaTeste.Leitor(), LojaA, canalA.Id, Pedido()));
            await Assert.ThrowsAsync<UnauthorizedAccessException>(() => servico.SincronizarAsync(WaTeste.Leitor(), LojaA, canalA.Id));
            Assert.Empty(f.Criados);

            // Numero central de testes: so a Gestao.
            f.NumerosPorWaba[WabaZippy].Add("555555555555555");
            var central = f.Canal(LojaA, OrigemCanal.Central, phone: "555555555555555");
            await Assert.ThrowsAsync<UnauthorizedAccessException>(() => servico.CriarAsync(WaTeste.Dono(), LojaA, central.Id, Pedido("teste_central")));
            await servico.CriarAsync(WaTeste.Gestao, LojaA, central.Id, Pedido("teste_central"));
        }

        [Fact]
        public async Task Timeout_apos_criacao_remota_reconcilia_sem_reenviar()
        {
            var f = new Fixture();
            f.NumerosPorWaba[WabaZippy] = new() { WaTeste.IdMeta };
            var canal = f.Canal(LojaA);
            // A Meta criou, mas a resposta se perdeu.
            f.RespostaCriacao = () =>
            {
                f.Remotos.Add(Remoto("4242", "pedido_confirmado"));
                return MetaResultado<MetaCriacao>.Erro(new MetaFalha(true, null, null, null, null, null));
            };

            var r = await f.Build().CriarAsync(WaTeste.Dono(), LojaA, canal.Id, Pedido());

            Assert.True(r.Confirmado);
            Assert.Equal("4242", r.Modelo.MetaTemplateId);
            Assert.Equal(SituacaoTemplate.Enviado, r.Modelo.Situacao);
            Assert.Single(f.Criados);
            Assert.Contains("envio_incerto", f.Repo.Eventos.Select(e => e.Acao));
            Assert.Contains("confirmado_na_meta", f.Repo.Eventos.Select(e => e.Acao));
        }

        [Fact]
        public async Task Timeout_sem_criacao_fica_verificando_bloqueia_reenvio_e_so_libera_depois_de_conferir()
        {
            var f = new Fixture();
            f.NumerosPorWaba[WabaZippy] = new() { WaTeste.IdMeta };
            var canal = f.Canal(LojaA);
            f.RespostaCriacao = () => MetaResultado<MetaCriacao>.Erro(new MetaFalha(true, null, null, null, null, null));
            var servico = f.Build();

            var r = await servico.CriarAsync(WaTeste.Dono(), LojaA, canal.Id, Pedido());
            Assert.False(r.Confirmado);
            Assert.Equal(StatusTemplate.Verificando, r.Modelo.Status);
            Assert.False(r.Modelo.DisponivelParaEnvio);

            // Logo em seguida: a Meta pode so estar lenta. Nao reenvia.
            var cedo = await Assert.ThrowsAsync<WhatsappException>(() => servico.CriarAsync(WaTeste.Dono(), LojaA, canal.Id, Pedido()));
            Assert.Equal(409, cedo.Http);
            Assert.Single(f.Criados);

            // Passado o prazo e conferido que a Meta nao tem: vira falha e o novo envio segue.
            f.Agora = f.Agora.AddMinutes(3);
            f.RespostaCriacao = null;
            var depois = await servico.CriarAsync(WaTeste.Dono(), LojaA, canal.Id, Pedido());
            Assert.True(depois.Confirmado);
            Assert.Equal(2, f.Criados.Count);
            Assert.Equal(new[] { SituacaoTemplate.Falha, SituacaoTemplate.Enviado }, f.Repo.Linhas.Select(l => l.Situacao));

            // Lista incompleta (paginas demais) nunca da um envio como nao criado.
            var g = new Fixture { ListaCompleta = false };
            g.NumerosPorWaba[WabaZippy] = new() { WaTeste.IdMeta };
            var cg = g.Canal(LojaA);
            g.RespostaCriacao = () => MetaResultado<MetaCriacao>.Erro(new MetaFalha(true, null, null, null, null, null));
            await g.Build().CriarAsync(WaTeste.Dono(), LojaA, cg.Id, Pedido());
            g.Agora = g.Agora.AddMinutes(10);
            await g.Build().SincronizarAsync(WaTeste.Dono(), LojaA, cg.Id);
            Assert.Equal(SituacaoTemplate.Verificando, g.Repo.Linhas.Single().Situacao);
        }

        [Fact]
        public async Task Sincronizacao_traz_aprovacao_rejeicao_com_orientacao_e_exclusao_feita_na_meta()
        {
            var f = new Fixture();
            f.NumerosPorWaba[WabaZippy] = new() { WaTeste.IdMeta };
            var canal = f.Canal(LojaA);
            var servico = f.Build();
            var aprovado = await servico.CriarAsync(WaTeste.Dono(), LojaA, canal.Id, Pedido("modelo_um"));
            var rejeitado = await servico.CriarAsync(WaTeste.Dono(), LojaA, canal.Id, Pedido("modelo_dois"));
            var sumido = await servico.CriarAsync(WaTeste.Dono(), LojaA, canal.Id, Pedido("modelo_tres"));
            f.Remotos.Add(Remoto(aprovado.Modelo.MetaTemplateId!, "modelo_um", "APPROVED"));
            f.Remotos.Add(Remoto(rejeitado.Modelo.MetaTemplateId!, "modelo_dois", "REJECTED", "TAG_CONTENT_MISMATCH"));

            var sync = await servico.SincronizarAsync(WaTeste.Dono(), LojaA, canal.Id);

            Assert.Equal((2, 2, 1), (sync.Atualizados, sync.Alterados, sync.NaoEncontrados));
            var porNome = sync.Lista.Itens.ToDictionary(i => i.Nome);
            Assert.Equal(StatusTemplate.Aprovado, porNome["modelo_um"].Status);
            Assert.Equal(StatusTemplate.Rejeitado, porNome["modelo_dois"].Status);
            Assert.Equal("TAG_CONTENT_MISMATCH", porNome["modelo_dois"].MotivoRejeicao);
            Assert.Contains("categoria", porNome["modelo_dois"].OrientacaoRejeicao);
            Assert.Equal(StatusTemplate.Indisponivel, porNome["modelo_tres"].Status);
            Assert.False(porNome["modelo_tres"].DisponivelParaEnvio);
            Assert.Equal((1, 0, 1), (sync.Lista.Resumo.Aprovados, sync.Lista.Resumo.EmAnalise, sync.Lista.Resumo.Rejeitados));
            Assert.Equal(f.Agora, sync.Lista.UltimaSincronizacao);

            // Duas sincronizacoes seguidas do mesmo numero: a segunda espera (protege o limite da Meta).
            var limite = await Assert.ThrowsAsync<WhatsappException>(() => servico.SincronizarAsync(WaTeste.Dono(), LojaA, canal.Id));
            Assert.Equal(429, limite.Http);

            // Sincronizar um so: a Meta aprovou o rejeitado em recurso.
            f.Remotos[1] = Remoto(rejeitado.Modelo.MetaTemplateId!, "modelo_dois", "APPROVED");
            var um = await servico.SincronizarUmAsync(WaTeste.Dono(), LojaA, canal.Id, rejeitado.Modelo.Id);
            Assert.Equal(StatusTemplate.Aprovado, um.Status);
            Assert.Null(um.MotivoRejeicao);
            Assert.Contains(um.Historico!, h => h.Acao == "status");
            Assert.NotNull(um.ComponentesMeta);
        }

        [Fact]
        public async Task Rate_limit_da_meta_e_429()
        {
            var f = new Fixture();
            f.NumerosPorWaba[WabaZippy] = new() { WaTeste.IdMeta };
            var canal = f.Canal(LojaA);
            f.RespostaCriacao = () => MetaResultado<MetaCriacao>.Erro(new MetaFalha(false, 400, 80008, null, "rate", null));
            var ex = await Assert.ThrowsAsync<WhatsappException>(() => f.Build().CriarAsync(WaTeste.Dono(), LojaA, canal.Id, Pedido()));
            Assert.Equal((429, TemplateCodigos.Limite), (ex.Http, ex.Erro.Codigo));
            Assert.Equal(SituacaoTemplate.Falha, f.Repo.Linhas.Single().Situacao);
        }

        [Fact]
        public async Task Exclusao_exige_confirmacao_e_apaga_so_este_modelo()
        {
            var f = new Fixture();
            f.NumerosPorWaba[WabaZippy] = new() { WaTeste.IdMeta };
            var canal = f.Canal(LojaA);
            var servico = f.Build();
            var criado = await servico.CriarAsync(WaTeste.Dono(), LojaA, canal.Id, Pedido());

            await Assert.ThrowsAsync<TemplateRequisicaoInvalidaException>(() => servico.ExcluirAsync(WaTeste.Dono(), LojaA, canal.Id, criado.Modelo.Id, "outro_nome"));
            await Assert.ThrowsAsync<UnauthorizedAccessException>(() => servico.ExcluirAsync(WaTeste.Leitor(), LojaA, canal.Id, criado.Modelo.Id, "pedido_confirmado"));
            f.Meta.Verify(m => m.ExcluirAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>()), Times.Never);

            var excluido = await servico.ExcluirAsync(WaTeste.Dono(), LojaA, canal.Id, criado.Modelo.Id, "pedido_confirmado");

            Assert.Equal(StatusTemplate.Excluido, excluido.Status);
            f.Meta.Verify(m => m.ExcluirAsync(WabaZippy, TokenGlobal, "pedido_confirmado", criado.Modelo.MetaTemplateId!), Times.Once);
            Assert.Contains(excluido.Historico!, h => h.Acao == "excluido");
            await Assert.ThrowsAsync<WhatsappException>(() => servico.ExcluirAsync(WaTeste.Dono(), LojaA, canal.Id, criado.Modelo.Id, "pedido_confirmado"));
        }

        [Fact]
        public async Task Conteudo_incompativel_nao_chama_a_meta()
        {
            var f = new Fixture();
            var canal = f.Canal(LojaA);
            await Assert.ThrowsAsync<TemplateIncompativelException>(() =>
                f.Build().CriarAsync(WaTeste.Dono(), LojaA, canal.Id, TemplateRegrasTests.Pedido(corpo: "{{1}} so", exemplos: new[] { "a" })));
            f.Meta.VerifyNoOtherCalls();
        }
    }

    // ---- controller --------------------------------------------------------------------------------------------

    public class TemplatesWhatsappControllerTests
    {
        private static TemplatesWhatsappController Controller(ITemplatesWhatsappService servico, bool autenticado = true)
        {
            var controller = new TemplatesWhatsappController(servico, NullLogger<TemplatesWhatsappController>.Instance);
            var contexto = new DefaultHttpContext();
            if (autenticado)
            {
                contexto.Items["UserId"] = 1;
                contexto.Items["IsSuperAdmin"] = true;
            }
            controller.ControllerContext = new ControllerContext { HttpContext = contexto };
            return controller;
        }

        private static TemplateView Modelo() => TemplatesWhatsappService.Ver(new TemplateWhatsapp
        {
            Id = Guid.NewGuid(), IdEstabelecimento = WaTeste.Loja, WabaId = "1", Nome = "x", Situacao = SituacaoTemplate.Verificando,
            ConteudoJson = "{\"corpo\":\"Ola\"}", CriadoEm = DateTime.UtcNow
        });

        private static int? Status(IActionResult r) => (r as ObjectResult)?.StatusCode;

        [Fact]
        public async Task Codigos_http_seguem_a_tabela()
        {
            var s = new Mock<ITemplatesWhatsappService>();
            var est = WaTeste.Loja;
            var canal = Guid.NewGuid();

            s.Setup(x => x.CriarAsync(It.IsAny<AtendimentoAtor>(), est, canal, It.IsAny<CriarTemplateRequest>())).ReturnsAsync(new CriacaoTemplateView(false, Modelo()));
            Assert.Equal(202, Status(await Controller(s.Object).Criar(est, canal, null)));
            s.Setup(x => x.CriarAsync(It.IsAny<AtendimentoAtor>(), est, canal, It.IsAny<CriarTemplateRequest>())).ReturnsAsync(new CriacaoTemplateView(true, Modelo()));
            Assert.Equal(201, Status(await Controller(s.Object).Criar(est, canal, null)));

            async Task<int?> Com(Exception ex)
            {
                s.Setup(x => x.SincronizarAsync(It.IsAny<AtendimentoAtor>(), est, canal)).ThrowsAsync(ex);
                return Status(await Controller(s.Object).Sincronizar(est, canal));
            }

            Assert.Equal(400, await Com(new TemplateRequisicaoInvalidaException(new() { ["x"] = new() { "y" } })));
            Assert.Equal(422, await Com(new TemplateIncompativelException(new() { ["x"] = new() { "y" } })));
            Assert.Equal(403, await Com(new UnauthorizedAccessException("nao")));
            Assert.Equal(404, await Com(new KeyNotFoundException("nao")));
            Assert.Equal(409, await Com(TemplateErros.Duplicado()));
            Assert.Equal(429, await Com(TemplateErros.Limite()));
            Assert.Equal(503, await Com(new WhatsappException(WhatsappErros.MetaIndisponivel(), 503)));
            Assert.Equal(502, await Com(TemplateErros.DaMeta(new MetaFalha(false, 403, 999, null, "x", null))));
            Assert.Equal(500, await Com(new InvalidOperationException("segredo interno")));

            var r = await Controller(s.Object).Sincronizar(est, canal);
            Assert.DoesNotContain("segredo interno", JsonSerializer.Serialize((r as ObjectResult)!.Value));
            Assert.Equal(401, Status(await Controller(s.Object, autenticado: false).Listar(est, canal)));
        }
    }

    // ---- migracao ----------------------------------------------------------------------------------------------

    public class WhatsappTemplatesMigrationContractTests
    {
        private static string Ler(string relativo) => File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Migrations", "Delivery", relativo));

        [Fact]
        public void Migracao_aditiva_com_vinculo_a_loja_e_unicidade()
        {
            var sql = Ler("20261029_01_whatsapp_templates.sql");
            Assert.Contains("CREATE TABLE IF NOT EXISTS whatsapp_template (", sql, StringComparison.Ordinal);
            Assert.Contains("id_estabelecimento UUID NOT NULL REFERENCES estabelecimentos (id) ON DELETE CASCADE", sql, StringComparison.Ordinal);
            Assert.Contains("id_canal UUID NULL REFERENCES canal_whatsapp (id) ON DELETE SET NULL", sql, StringComparison.Ordinal);
            Assert.Contains("ux_whatsapp_template_meta_id", sql, StringComparison.Ordinal);
            Assert.Contains("ON whatsapp_template (waba_id, nome, idioma) WHERE situacao IN ('enviando', 'verificando', 'enviado')", sql, StringComparison.Ordinal);
            Assert.Contains("CREATE TABLE IF NOT EXISTS whatsapp_template_evento", sql, StringComparison.Ordinal);
            Assert.Contains("'20261029_01_whatsapp_templates'", sql, StringComparison.Ordinal);
            Assert.DoesNotContain("DROP ", sql, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("ALTER TABLE", sql, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("token", sql, StringComparison.OrdinalIgnoreCase);
        }

        [Fact]
        public void Rollback_so_remove_as_tabelas_de_modelos()
        {
            var sql = Ler("rollback/20261029_01_whatsapp_templates.down.sql");
            Assert.Contains("DROP TABLE IF EXISTS whatsapp_template_evento;", sql, StringComparison.Ordinal);
            Assert.Contains("DROP TABLE IF EXISTS whatsapp_template;", sql, StringComparison.Ordinal);
            Assert.DoesNotContain("canal_whatsapp", sql, StringComparison.Ordinal);
        }
    }
}
