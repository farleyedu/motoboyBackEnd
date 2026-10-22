using System;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using APIBack.Controllers;
using APIBack.DTOs.Common;
using APIBack.DTOs.Configuracoes;
using APIBack.Hubs;
using APIBack.Middleware;
using APIBack.Repository.Interface;
using APIBack.Service;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.Caching.Memory;
using Moq;
using Npgsql;
using Xunit;

namespace APIBack.Tests.Unit
{
    /// <summary>Comecar a operar (autoatendimento), abrir/fechar a loja e as travas por situacao comercial.</summary>
    public class OperacaoComercialTests
    {
        private static readonly Guid Store = Guid.NewGuid();
        private static readonly DateTimeOffset Agora = new(2026, 10, 25, 12, 0, 0, TimeSpan.Zero);

        private static ProntidaoDto Prontidao(string situacao, Action<ProntidaoDados>? ajustar = null) =>
            ProntidaoRegras.Avaliar(ProntidaoRegrasTests.Pronta(d => { d.Situacao = situacao; ajustar?.Invoke(d); }), false, Agora);

        private static SituacaoComercialDto Situacao(string situacao, DateTimeOffset? fim = null) =>
            new(Store, situacao, null, fim, null, 3, 3, 3, 10, null, null);

        // ---- iniciar-operacao -------------------------------------------------------------------------------------------

        private sealed class Controle
        {
            public Mock<ISituacaoComercialService> Service { get; } = new();
            public Mock<IProntidaoService> Prontidao { get; } = new();
            public Mock<IClientProxy> Proxy { get; } = new();
            public DefaultHttpContext Context { get; } = new();
            public SituacaoComercialController Controller { get; }

            public Controle(bool superAdmin = false)
            {
                var hub = new Mock<IHubContext<DeliveryHub>>();
                var clients = new Mock<IHubClients>();
                hub.SetupGet(h => h.Clients).Returns(clients.Object);
                clients.Setup(c => c.Group(It.IsAny<string>())).Returns(Proxy.Object);
                Proxy.Setup(p => p.SendCoreAsync(It.IsAny<string>(), It.IsAny<object?[]>(), It.IsAny<CancellationToken>())).Returns(Task.CompletedTask);
                Context.Items["UserId"] = 7;
                Context.Items["EstabelecimentoId"] = Store;
                if (superAdmin) Context.Items["IsSuperAdmin"] = true;
                Controller = new SituacaoComercialController(Service.Object, Prontidao.Object, hub.Object, new DeliveryHubConnections())
                { ControllerContext = new() { HttpContext = Context } };
            }
        }

        [Fact]
        public async Task Iniciar_operacao_recusado_devolve_422_com_a_lista_de_pendencias_e_nao_notifica()
        {
            var c = new Controle();
            var prontidao = Prontidao("implantacao", d => { d.RaioEntregaKm = null; d.DiasAbertos = 0; });
            var erro = ErrosOperacionais.Obter(ErrosOperacionais.ProntidaoIncompleta);
            c.Service.Setup(s => s.IniciarOperacaoAsync(Store, 7, OrigensSituacao.Loja, It.IsAny<CancellationToken>()))
                .ThrowsAsync(new SituacaoComercialException(erro.Status, erro.Code, "Faltam itens.",
                    ErrosOperacionais.Detalhes(erro.Code, ProntidaoRegras.Pendencias(prontidao))));

            var resultado = Assert.IsType<ObjectResult>(await c.Controller.IniciarOperacao());

            Assert.Equal(422, resultado.StatusCode);
            var corpo = Assert.IsType<ApiResponse<object>>(resultado.Value);
            Assert.Equal("PRONTIDAO_INCOMPLETA", corpo.Code);
            var detalhes = Assert.IsType<DetalhesErroOperacional>(corpo.Details);
            Assert.Equal(new[] { "horario_semanal", "raio_entrega" }, detalhes.Pendencias!.Select(p => p.Codigo).OrderBy(x => x));
            Assert.All(detalhes.Pendencias!, p => Assert.False(string.IsNullOrWhiteSpace(p.ComoResolver)));
            c.Proxy.Verify(p => p.SendCoreAsync(It.IsAny<string>(), It.IsAny<object?[]>(), It.IsAny<CancellationToken>()), Times.Never);
        }

        [Fact]
        public async Task Iniciar_operacao_aceito_responde_com_15_dias_e_notifica_o_painel_da_loja()
        {
            var c = new Controle();
            var fim = Agora.AddDays(SituacaoComercialRules.DiasTeste);
            var teste = Situacao("teste", fim) with { DiasTesteRestantes = 15 };
            c.Service.Setup(s => s.IniciarOperacaoAsync(Store, 7, OrigensSituacao.Loja, It.IsAny<CancellationToken>()))
                .ReturnsAsync(new IniciarOperacaoDto(teste, fim, SituacaoComercialRules.DiasTeste, 15));

            var resultado = Assert.IsType<OkObjectResult>(await c.Controller.IniciarOperacao());

            var corpo = Assert.IsType<ApiResponse<IniciarOperacaoDto>>(resultado.Value);
            Assert.Equal("teste", corpo.Data!.Situacao.Situacao);
            Assert.Equal(15, corpo.Data.DiasTeste);
            Assert.Equal(fim, corpo.Data.TesteTerminaEm);
            c.Proxy.Verify(p => p.SendCoreAsync(DeliveryRealtimeEvents.EstabelecimentoSituacaoAlterada, It.IsAny<object?[]>(), It.IsAny<CancellationToken>()), Times.Once);
        }

        [Fact]
        public async Task Iniciar_operacao_exige_loja_selecionada_e_nao_serve_ao_super_admin()
        {
            var semLoja = new Controle();
            semLoja.Context.Items.Remove("EstabelecimentoId");
            Assert.IsType<BadRequestObjectResult>(await semLoja.Controller.IniciarOperacao());

            var admin = new Controle(superAdmin: true);
            Assert.Equal(403, Assert.IsType<ObjectResult>(await admin.Controller.IniciarOperacao()).StatusCode);
            admin.Service.Verify(s => s.IniciarOperacaoAsync(It.IsAny<Guid>(), It.IsAny<int?>(), It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
        }

        [Fact]
        public void Iniciar_operacao_pede_permissao_de_configuracao_da_loja()
        {
            var metodo = typeof(SituacaoComercialController).GetMethod(nameof(SituacaoComercialController.IniciarOperacao))!;
            var permissao = metodo.GetCustomAttributes(typeof(APIBack.Attributes.RequirePermissionAttribute), true)
                .Cast<APIBack.Attributes.RequirePermissionAttribute>().Single();
            Assert.Equal(("Configuracoes", "editar"), (permissao.Modulo, permissao.Acao));
        }

        [Fact]
        public async Task O_servico_recusa_antes_de_tocar_o_banco_quando_a_prontidao_esta_incompleta_sem_opcao_de_forcar()
        {
            await using var fonte = NpgsqlDataSource.Create("Host=localhost;Database=inexistente;Timeout=1;Command Timeout=1");
            var prontidao = new Mock<IProntidaoService>();
            var incompleta = Prontidao("implantacao", d => d.ProdutosVendaveis = 0);
            prontidao.Setup(p => p.ObterAsync(Store, false, It.IsAny<CancellationToken>())).ReturnsAsync(incompleta);
            var servico = new SituacaoComercialService(fonte, new MemoryCache(new MemoryCacheOptions()), prontidao.Object);

            var ex = await Assert.ThrowsAsync<SituacaoComercialException>(() => servico.IniciarOperacaoAsync(Store, 7, OrigensSituacao.Loja));

            Assert.Equal(422, ex.Status);
            Assert.Equal("PRONTIDAO_INCOMPLETA", ex.Code);
            var detalhes = Assert.IsType<DetalhesErroOperacional>(ex.Details);
            Assert.Equal("produto_vendavel", Assert.Single(detalhes.Pendencias!).Codigo);
            // A Gestao tambem nao forca: a acao iniciar_operacao passa pela mesma reavaliacao (com o detalhe da infra).
            prontidao.Setup(p => p.ObterAsync(Store, true, It.IsAny<CancellationToken>())).ReturnsAsync(incompleta);
            var gestao = await Assert.ThrowsAsync<SituacaoComercialException>(() =>
                servico.AplicarAsync(Store, new AcaoSituacaoRequest("iniciar_operacao", null, null, null), 1));
            Assert.Equal("PRONTIDAO_INCOMPLETA", gestao.Code);
        }

        [Fact]
        public void A_origem_e_o_ator_do_inicio_vao_para_o_historico_e_o_prazo_e_de_15_dias()
        {
            // O historico (estabelecimento_situacao_log.detalhes) e o prazo ficam num so lugar: AplicarAsync. Garante que ninguem os tire.
            var fonte = File.ReadAllText(Path.Combine(ProjetoDir(), "Service", "SituacaoComercial.cs"));
            Assert.Contains("origem, ator = usuarioId", fonte);
            Assert.Contains("teste_termina_em = NOW() + make_interval(days => @Dias)", fonte);
            Assert.Contains("Dias = SituacaoComercialRules.DiasTeste", fonte);
            Assert.Contains("OperacaoJaIniciada", fonte);
            Assert.Equal(15, SituacaoComercialRules.DiasTeste);
        }

        private static string ProjetoDir()
        {
            var dir = new DirectoryInfo(AppContext.BaseDirectory);
            while (dir != null && !File.Exists(Path.Combine(dir.FullName, "APIBack.csproj"))) dir = dir.Parent;
            return dir?.FullName ?? throw new InvalidOperationException("APIBack.csproj nao encontrado.");
        }

        // ---- abrir e fechar ------------------------------------------------------------------------------------------------

        private static (OperacaoLojaService Servico, Mock<IHorarioOperacaoRepository> Horarios) Operacao(string situacao, ProntidaoDto prontidao, StoreOperationDto? estado = null)
        {
            var horarios = new Mock<IHorarioOperacaoRepository>();
            var estadoAtual = estado ?? new StoreOperationDto { EstabelecimentoId = Store, AbertoAgora = false, DentroDoHorario = false, MotivoFechada = "FORA_HORARIO", PodeAbrir = true };
            horarios.Setup(h => h.ObterEstadoAsync(Store, It.IsAny<CancellationToken>())).ReturnsAsync(estadoAtual);
            horarios.Setup(h => h.AbrirHojeAsync(Store, 7, It.IsAny<OpenStoreTodayRequest>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync(new StoreOperationDto { EstabelecimentoId = Store, AbertoAgora = true });
            horarios.Setup(h => h.FecharAgoraAsync(Store, 7, It.IsAny<CancellationToken>()))
                .ReturnsAsync(new StoreOperationDto { EstabelecimentoId = Store, AbertoAgora = false });
            var situacoes = new Mock<ISituacaoComercialService>();
            situacoes.Setup(s => s.ObterAsync(Store, It.IsAny<CancellationToken>())).ReturnsAsync(Situacao(situacao));
            var pronto = new Mock<IProntidaoService>();
            pronto.Setup(p => p.ObterAsync(Store, false, It.IsAny<CancellationToken>())).ReturnsAsync(prontidao);
            return (new OperacaoLojaService(horarios.Object, situacoes.Object, pronto.Object), horarios);
        }

        private static OpenStoreTodayRequest PedidoAbrir() => new() { EstabelecimentoId = Store, DataLocal = "2026-10-25", FechaAs = "23:00" };

        [Theory]
        [InlineData("teste")]
        [InlineData("ativo")]
        public async Task Abrir_e_fechar_funcionam_em_teste_e_ativo_com_a_prontidao_ok(string situacao)
        {
            var (servico, horarios) = Operacao(situacao, Prontidao(situacao));

            Assert.True((await servico.AbrirHojeAsync(Store, 7, PedidoAbrir())).AbertoAgora);
            Assert.False((await servico.FecharAsync(Store, 7)).AbertoAgora);
            horarios.Verify(h => h.AbrirHojeAsync(Store, 7, It.IsAny<OpenStoreTodayRequest>(), It.IsAny<CancellationToken>()), Times.Once);
            horarios.Verify(h => h.FecharAgoraAsync(Store, 7, It.IsAny<CancellationToken>()), Times.Once);
        }

        [Fact]
        public async Task Em_implantacao_nem_abrir_nem_fechar_e_a_recusa_traz_as_tres_principais_pendencias()
        {
            var prontidao = Prontidao("implantacao", d => { d.DiasAbertos = 0; d.RaioEntregaKm = 0; d.ProdutosVendaveis = 0; d.Cep = null; });
            var (servico, horarios) = Operacao("implantacao", prontidao);

            var abrir = await Assert.ThrowsAsync<DeliveryDomainException>(() => servico.AbrirHojeAsync(Store, 7, PedidoAbrir()));
            var fechar = await Assert.ThrowsAsync<DeliveryDomainException>(() => servico.FecharAsync(Store, 7));

            foreach (var ex in new[] { abrir, fechar })
            {
                Assert.Equal("OPERACAO_NAO_INICIADA", ex.Code);
                Assert.Equal(403, ex.StatusCode);
                Assert.Equal(3, ((DetalhesErroOperacional)ex.Details!).Pendencias!.Count);
            }
            horarios.Verify(h => h.AbrirHojeAsync(It.IsAny<Guid>(), It.IsAny<int>(), It.IsAny<OpenStoreTodayRequest>(), It.IsAny<CancellationToken>()), Times.Never);
            horarios.Verify(h => h.FecharAgoraAsync(It.IsAny<Guid>(), It.IsAny<int>(), It.IsAny<CancellationToken>()), Times.Never);
        }

        [Fact]
        public async Task Loja_bloqueada_nao_abre_nem_fecha()
        {
            var (servico, _) = Operacao("bloqueado", Prontidao("bloqueado"));

            var ex = await Assert.ThrowsAsync<DeliveryDomainException>(() => servico.AbrirHojeAsync(Store, 7, PedidoAbrir()));
            Assert.Equal("ESTABELECIMENTO_BLOQUEADO", ex.Code);
            await Assert.ThrowsAsync<DeliveryDomainException>(() => servico.FecharAsync(Store, 7));
        }

        [Fact]
        public async Task Abrir_manual_exige_a_prontidao_obrigatoria_ok_senao_422_com_a_lista_mas_fechar_nao()
        {
            var prontidao = Prontidao("teste", d => d.RaioEntregaKm = null);
            var (servico, horarios) = Operacao("teste", prontidao);

            var ex = await Assert.ThrowsAsync<DeliveryDomainException>(() => servico.AbrirHojeAsync(Store, 7, PedidoAbrir()));

            Assert.Equal("PRONTIDAO_INCOMPLETA", ex.Code);
            Assert.Equal(422, ex.StatusCode);
            Assert.Equal("raio_entrega", Assert.Single(((DetalhesErroOperacional)ex.Details!).Pendencias!).Codigo);
            horarios.Verify(h => h.AbrirHojeAsync(It.IsAny<Guid>(), It.IsAny<int>(), It.IsAny<OpenStoreTodayRequest>(), It.IsAny<CancellationToken>()), Times.Never);
            Assert.False((await servico.FecharAsync(Store, 7)).AbertoAgora);
        }

        [Fact]
        public async Task O_estado_diz_se_pode_abrir_e_para_onde_ir_quando_nao_pode()
        {
            var pode = Operacao("teste", Prontidao("teste"));
            Assert.True((await pode.Servico.StatusAsync(Store)).PodeAbrir);

            var pendente = Operacao("teste", Prontidao("teste", d => d.ProdutosVendaveis = 0));
            var estado = await pendente.Servico.StatusAsync(Store);
            Assert.False(estado.PodeAbrir);
            Assert.Equal("FORA_HORARIO", estado.MotivoFechada);
            Assert.Equal("/apps/cardapio", estado.Correcao!.Rota);
            Assert.False(string.IsNullOrWhiteSpace(estado.ComoResolver));

            var aberta = Operacao("teste", Prontidao("teste"), new StoreOperationDto { EstabelecimentoId = Store, AbertoAgora = true, PodeFechar = true });
            var aberto = await aberta.Servico.StatusAsync(Store);
            Assert.False(aberto.PodeAbrir);
            Assert.True(aberto.PodeFechar);
        }

        // ---- super admin sem privilegio: matriz de situacoes ----------------------------------------------------------------

        public enum Tipo { Operacional, Gestao, Configuracao }

        private static string Caminho(Tipo tipo) => tipo switch
        {
            Tipo.Operacional => "/api/v2/delivery/pedidos",
            Tipo.Gestao => "/api/gestao/estabelecimentos",
            _ => "/api/v2/delivery/configuracoes"
        };

        private static async Task<(int Status, string? Code, bool Passou)> Chamar(string situacao, Tipo tipo, bool superAdmin)
        {
            var context = new DefaultHttpContext();
            context.Items["EstabelecimentoId"] = Store;
            if (superAdmin) context.Items["IsSuperAdmin"] = true;
            context.Request.Path = Caminho(tipo);
            context.Request.Method = tipo == Tipo.Configuracao ? "PUT" : "GET";
            context.Response.Body = new MemoryStream();
            var passou = false;
            var middleware = new SituacaoComercialMiddleware(_ => { passou = true; return Task.CompletedTask; });
            var servico = new Mock<ISituacaoComercialService>();
            servico.Setup(s => s.ObterAsync(Store, context.RequestAborted)).ReturnsAsync(Situacao(situacao));
            var prontidao = new Mock<IProntidaoService>();
            prontidao.Setup(p => p.ObterAsync(Store, false, context.RequestAborted))
                .ReturnsAsync(Prontidao("implantacao", d => { d.DiasAbertos = 0; d.RaioEntregaKm = 0; }));
            await middleware.InvokeAsync(context, servico.Object, prontidao.Object);
            string? code = null;
            if (!passou)
            {
                context.Response.Body.Position = 0;
                using var json = await JsonDocument.ParseAsync(context.Response.Body);
                code = json.RootElement.GetProperty("code").GetString();
            }
            return (context.Response.StatusCode, code, passou);
        }

        public static TheoryData<bool, string, Tipo, string?> Matriz()
        {
            var dados = new TheoryData<bool, string, Tipo, string?>();
            foreach (var superAdmin in new[] { false, true })
            {
                // null = a chamada passa; senao o code da recusa. Igual para super admin e usuario.
                dados.Add(superAdmin, "implantacao", Tipo.Operacional, "OPERACAO_NAO_INICIADA");
                dados.Add(superAdmin, "implantacao", Tipo.Gestao, null);
                dados.Add(superAdmin, "implantacao", Tipo.Configuracao, null);
                dados.Add(superAdmin, "teste", Tipo.Operacional, null);
                dados.Add(superAdmin, "teste", Tipo.Gestao, null);
                dados.Add(superAdmin, "teste", Tipo.Configuracao, null);
                dados.Add(superAdmin, "ativo", Tipo.Operacional, null);
                dados.Add(superAdmin, "ativo", Tipo.Gestao, null);
                dados.Add(superAdmin, "ativo", Tipo.Configuracao, null);
                dados.Add(superAdmin, "bloqueado", Tipo.Operacional, "ESTABELECIMENTO_BLOQUEADO");
                dados.Add(superAdmin, "bloqueado", Tipo.Gestao, null);
                dados.Add(superAdmin, "bloqueado", Tipo.Configuracao, "ESTABELECIMENTO_BLOQUEADO");
            }
            return dados;
        }

        [Theory]
        [MemberData(nameof(Matriz))]
        public async Task Super_admin_e_usuario_recebem_as_mesmas_travas(bool superAdmin, string situacao, Tipo tipo, string? codeEsperado)
        {
            var (status, code, passou) = await Chamar(situacao, tipo, superAdmin);

            Assert.Equal(codeEsperado == null, passou);
            Assert.Equal(codeEsperado, code);
            if (codeEsperado != null) Assert.Equal(403, status);
        }

        [Fact]
        public async Task A_recusa_por_operacao_nao_iniciada_traz_as_3_principais_pendencias_e_o_caminho()
        {
            var context = new DefaultHttpContext();
            context.Items["EstabelecimentoId"] = Store;
            context.Request.Path = "/api/v2/delivery/pedidos";
            context.Request.Method = "POST";
            context.Response.Body = new MemoryStream();
            var servico = new Mock<ISituacaoComercialService>();
            servico.Setup(s => s.ObterAsync(Store, context.RequestAborted)).ReturnsAsync(Situacao("implantacao"));
            var prontidao = new Mock<IProntidaoService>();
            prontidao.Setup(p => p.ObterAsync(Store, false, context.RequestAborted))
                .ReturnsAsync(Prontidao("implantacao", d => { d.DiasAbertos = 0; d.RaioEntregaKm = 0; d.ProdutosVendaveis = 0; d.Cep = null; }));

            await new SituacaoComercialMiddleware(_ => Task.CompletedTask).InvokeAsync(context, servico.Object, prontidao.Object);

            context.Response.Body.Position = 0;
            using var json = await JsonDocument.ParseAsync(context.Response.Body);
            var raiz = json.RootElement;
            Assert.Equal("OPERACAO_NAO_INICIADA", raiz.GetProperty("code").GetString());
            var detalhes = raiz.GetProperty("details");
            Assert.False(string.IsNullOrWhiteSpace(detalhes.GetProperty("comoResolver").GetString()));
            var pendencias = detalhes.GetProperty("pendencias");
            Assert.Equal(3, pendencias.GetArrayLength());
            Assert.All(pendencias.EnumerateArray(), p =>
            {
                Assert.False(string.IsNullOrWhiteSpace(p.GetProperty("comoResolver").GetString()));
                Assert.True(p.GetProperty("correcao").EnumerateObject().Any());
            });
        }

        [Fact]
        public async Task A_recusa_por_bloqueio_continua_com_o_mesmo_code_e_traz_o_como_resolver()
        {
            var context = new DefaultHttpContext();
            context.Items["EstabelecimentoId"] = Store;
            context.Request.Path = "/api/v2/delivery/pedidos";
            context.Response.Body = new MemoryStream();
            var servico = new Mock<ISituacaoComercialService>();
            servico.Setup(s => s.ObterAsync(Store, context.RequestAborted)).ReturnsAsync(Situacao("bloqueado") with { BloqueadoMotivo = "Fatura em aberto" });

            await new SituacaoComercialMiddleware(_ => Task.CompletedTask).InvokeAsync(context, servico.Object);

            context.Response.Body.Position = 0;
            using var json = await JsonDocument.ParseAsync(context.Response.Body);
            Assert.Equal(403, context.Response.StatusCode);
            Assert.Equal("ESTABELECIMENTO_BLOQUEADO", json.RootElement.GetProperty("code").GetString());
            Assert.Equal("zippy", json.RootElement.GetProperty("details").GetProperty("quemResolve").GetString());
            Assert.Equal("Fatura em aberto", json.RootElement.GetProperty("details").GetProperty("dados").GetProperty("motivo").GetString());
        }

        [Theory]
        [InlineData("/api/gestao/comercial/painel", "GET")]
        [InlineData("/api/gestao/estabelecimentos/1/prontidao", "GET")]
        [InlineData("/api/admin/usuarios", "GET")]
        [InlineData("/api/auth/me", "GET")]
        [InlineData("/api/me/estabelecimentos", "GET")]
        [InlineData("/api/v2/comercial/prontidao", "GET")]
        [InlineData("/api/v2/comercial/iniciar-operacao", "POST")]
        public void Rotas_de_gestao_login_e_comercial_ficam_livres_em_qualquer_situacao(string path, string method)
        {
            Assert.True(SituacaoComercialRules.RotaSempreLiberada(path));
            Assert.True(SituacaoComercialRules.PermitidoQuandoBloqueado(path, method));
            Assert.False(SituacaoComercialRules.OperacaoExigeInicio(path, method));
        }

        [Theory]
        [InlineData("/api/gestaox/estabelecimentos")]
        [InlineData("/api/v2/comercialx/situacao")]
        [InlineData("/api/v2/delivery/pedidos")]
        [InlineData("/hubs/delivery")]
        public void O_prefixo_liberado_casa_o_segmento_inteiro(string path) =>
            Assert.False(SituacaoComercialRules.RotaSempreLiberada(path));

        // ---- painel da Gestao e prazo do teste ---------------------------------------------------------------------------

        [Fact]
        public void O_painel_separa_prontas_em_teste_e_vencidas_sem_bloquear_ninguem()
        {
            Assert.Equal(10, ProntidaoRegras.DiasRestantes(Agora.AddDays(10), Agora));
            var atraso = ProntidaoRegras.DiasRestantes(Agora.AddDays(-4), Agora);
            Assert.Equal(-4, atraso);
            var loja = new PainelLojaDto(Store, "Loja", null, "teste", null, Agora.AddDays(-4), null, -atraso, ProntidaoRegras.Resumir(Prontidao("teste")));
            var json = JsonSerializer.Serialize(loja, new JsonSerializerOptions(JsonSerializerDefaults.Web));
            Assert.Contains("\"diasAtraso\":4", json);
            Assert.DoesNotContain("diasRestantes", json);
            // Teste vencido nao muda a situacao sozinho: a regra de bloqueio so existe na acao manual da Gestao.
            Assert.DoesNotContain("teste_termina_em <", File.ReadAllText(Path.Combine(ProjetoDir(), "Middleware", "SituacaoComercialMiddleware.cs")));
        }
    }
}
