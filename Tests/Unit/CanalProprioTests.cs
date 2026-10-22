using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using APIBack.Atendimento;
using APIBack.Automation.Controllers;
using APIBack.Automation.Interfaces;
using APIBack.Automation.Services;
using APIBack.DTOs.Common;
using APIBack.Service;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Xunit;

namespace APIBack.Tests.Unit
{
    /// <summary>Dados e ajudas comuns dos testes de WhatsApp com origem (zippy, proprio, central). Sem rede: o Graph e sempre falso.</summary>
    internal static class WaTeste
    {
        public const string IdMeta = "821317791056700";
        public const string Waba = "109876543210987";
        public const string AppId = "123456789012345";
        public const string AppSecret = "0123456789abcdef0123456789abcdef";
        public const string Token = "EAAG-token-secreto-do-cliente";
        public static readonly Guid Loja = Guid.NewGuid();
        public static readonly Guid OutraLoja = Guid.NewGuid();

        public static AtendimentoAtor Dono(Guid? loja = null) =>
            new(2, false, loja ?? Loja, (modulo, acao) => modulo == "WhatsApp" && acao == "configurar");
        public static AtendimentoAtor Leitor() => new(3, false, Loja, (_, _) => false);
        public static AtendimentoAtor Gestao => new(1, true, null, (_, _) => true);

        public static IConfiguration Config(string? chaveToken = "chave-de-teste", string? baseUrl = "https://api.zippy.test/") =>
            new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Atendimento:ChaveToken"] = chaveToken, ["App:BaseUrl"] = baseUrl
            }).Build();

        public static ITokenProtector Protetor(string? chave = "chave-de-teste") => new TokenProtector(Config(chave));

        public static string Assinar(string segredo, string corpo) => AssinaturaMeta.Calcular(segredo, corpo);

        /// <summary>Canal proprio ja conectado, com segredos cifrados de verdade.</summary>
        public static CanalWhatsapp CanalProprio(ITokenProtector p, string chave = "k-abcdefghijklmnopqrstuvwxyz0123456789ABCDEF", Guid? loja = null) => new()
        {
            Id = Guid.NewGuid(), IdEstabelecimento = loja ?? Loja, PhoneNumberId = IdMeta, NumeroE164 = "+5534998218242",
            Origem = OrigemCanal.Proprio, WabaId = Waba, AppId = AppId, TokenCifrado = p.Proteger(Token),
            AppSecretCifrado = p.Proteger(AppSecret), VerifyTokenCifrado = p.Proteger("verify-do-canal"), WebhookChave = chave,
            WebhookEstado = WebhookEstado.Pendente, Status = StatusCanal.Configurando, VerificadoEm = DateTime.UtcNow
        };
    }

    public class CanalProprioConexaoTests
    {
        private sealed class Fixture
        {
            public Mock<ICanalRepository> Canais { get; } = new();
            public Mock<ICatalogoRepository> Catalogo { get; } = new();
            public Mock<ICanalVerificador> Verificador { get; } = new();
            public Mock<IWebhookRegistrador> Registrador { get; } = new();
            public ITokenProtector Protetor { get; }
            public CanalWhatsapp? Gravado { get; private set; }
            public List<string> Estados { get; } = new();

            public Fixture(string? chaveToken = "chave-de-teste")
            {
                Protetor = WaTeste.Protetor(chaveToken);
                Catalogo.Setup(c => c.LojaExisteAsync(It.IsAny<Guid>())).ReturnsAsync(true);
                Catalogo.Setup(c => c.ListarServicosDaLojaAsync(WaTeste.Loja)).ReturnsAsync(new[] { new ServicoDaLoja { Codigo = "delivery", Ativo = true } });
                Canais.Setup(c => c.ListarPorLojaAsync(It.IsAny<Guid>())).ReturnsAsync(Array.Empty<CanalWhatsapp>());
                Canais.Setup(c => c.CriarAsync(It.IsAny<CanalWhatsapp>(), It.IsAny<IReadOnlyCollection<string>>()))
                    .Callback<CanalWhatsapp, IReadOnlyCollection<string>>((c, _) => Gravado = c).Returns(Task.CompletedTask);
                Canais.Setup(c => c.ObterAsync(It.IsAny<Guid>())).ReturnsAsync(() => Gravado);
                Canais.Setup(c => c.DefinirWebhookAsync(It.IsAny<Guid>(), It.IsAny<string>(), It.IsAny<string?>(), It.IsAny<DateTime?>()))
                    .Callback<Guid, string, string?, DateTime?>((_, estado, erro, em) =>
                    {
                        Estados.Add(estado);
                        Gravado!.WebhookEstado = estado;
                        Gravado.WebhookErro = erro;
                        if (em.HasValue) Gravado.WebhookRegistradoEm = em;
                    }).Returns(Task.CompletedTask);
                Verificador.Setup(v => v.ConsultarCredenciaisAsync(It.IsAny<string>(), It.IsAny<string>()))
                    .ReturnsAsync(new VerificacaoCanal(true, "Numero verificado na Meta.", "+55 34 99821-8242", "Pizzaria Teste"));
                Registrador.Setup(r => r.RegistrarAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>()))
                    .ReturnsAsync(new ResultadoRegistro(true, null));
            }

            public CanaisWhatsappService Build()
            {
                var webhook = new CanalWebhookService(Canais.Object, Protetor, Registrador.Object, WaTeste.Config(), NullLogger<CanalWebhookService>.Instance);
                return new CanaisWhatsappService(Canais.Object, Catalogo.Object, Protetor, Verificador.Object, webhook, NullLogger<CanaisWhatsappService>.Instance);
            }
        }

        private static ConectarProprioRequest Pedido() => new(WaTeste.IdMeta, WaTeste.Waba, WaTeste.Token, WaTeste.AppId, WaTeste.AppSecret);

        [Fact]
        public async Task Conecta_cifra_os_segredos_gera_o_webhook_e_so_fica_ativo_quando_chegar_mensagem()
        {
            var f = new Fixture();
            string? callback = null, verify = null;
            f.Registrador.Setup(r => r.RegistrarAsync(WaTeste.AppId, WaTeste.AppSecret, WaTeste.Waba, WaTeste.Token, It.IsAny<string>(), It.IsAny<string>()))
                .Callback<string, string, string, string, string, string>((_, _, _, _, url, vt) => { callback = url; verify = vt; })
                .ReturnsAsync(new ResultadoRegistro(true, null));

            var view = await f.Build().ConectarProprioAsync(WaTeste.Dono(), WaTeste.Loja, Pedido());

            var canal = f.Gravado!;
            Assert.Equal(OrigemCanal.Proprio, canal.Origem);
            Assert.NotEqual(WaTeste.Token, canal.TokenCifrado);
            Assert.Equal(WaTeste.Token, f.Protetor.Revelar(canal.TokenCifrado));
            Assert.Equal(WaTeste.AppSecret, f.Protetor.Revelar(canal.AppSecretCifrado));
            Assert.DoesNotContain(WaTeste.AppSecret, canal.AppSecretCifrado);
            Assert.True(canal.WebhookChave!.Length >= 32);
            Assert.Matches("^[A-Za-z0-9_-]+$", canal.WebhookChave);
            Assert.Equal($"https://api.zippy.test/wa/webhook/{canal.WebhookChave}", callback);
            Assert.Equal(f.Protetor.Revelar(canal.VerifyTokenCifrado), verify);
            // O GET na Meta so prova que ID e token servem: o canal fica aguardando, nunca "ativo".
            Assert.Equal(StatusCanal.Configurando, canal.Status);
            f.Canais.Verify(c => c.MarcarVerificadoAsync(canal.Id, true, null), Times.Once);

            Assert.Equal("+5534998218242", canal.NumeroE164);
            Assert.Equal(WebhookEstado.Registrado, view.Webhook!.Estado);
            Assert.Equal(callback, view.Webhook.Url);
            Assert.Equal(verify, view.Webhook.VerifyToken);
            Assert.Equal(WaTeste.AppId, view.AppId);
            Assert.Equal(WaTeste.Waba, view.WabaId);
            Assert.Equal(OrigemCanal.Proprio, view.Origem);
        }

        [Fact]
        public async Task A_resposta_nunca_traz_token_nem_app_secret()
        {
            var f = new Fixture();

            var view = await f.Build().ConectarProprioAsync(WaTeste.Dono(), WaTeste.Loja, Pedido());
            var json = JsonSerializer.Serialize(ApiResponse<CanalView>.Ok(view));

            Assert.DoesNotContain(WaTeste.Token, json);
            Assert.DoesNotContain(WaTeste.AppSecret, json);
            Assert.DoesNotContain(f.Gravado!.TokenCifrado!, json);
            Assert.DoesNotContain(f.Gravado.AppSecretCifrado!, json);
            Assert.Contains("\"temToken\":true", json, StringComparison.OrdinalIgnoreCase);
        }

        [Fact]
        public async Task Registro_automatico_que_falha_nao_derruba_a_conexao_e_devolve_o_registro_manual()
        {
            var f = new Fixture();
            f.Registrador.Setup(r => r.RegistrarAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>()))
                .ReturnsAsync(new ResultadoRegistro(false, WhatsappErros.AppSecretInvalido()));

            var view = await f.Build().ConectarProprioAsync(WaTeste.Dono(), WaTeste.Loja, Pedido());

            f.Canais.Verify(c => c.CriarAsync(It.IsAny<CanalWhatsapp>(), It.IsAny<IReadOnlyCollection<string>>()), Times.Once);
            Assert.Equal(WebhookEstado.Pendente, view.Webhook!.Estado);
            Assert.Equal(WhatsappCodigos.AppSecretInvalido, view.Webhook.Codigo);
            Assert.False(string.IsNullOrWhiteSpace(view.Webhook.ComoResolver));
            Assert.Equal("appSecret", view.Webhook.Campo);
            // Dados do registro manual.
            Assert.StartsWith("https://api.zippy.test/wa/webhook/", view.Webhook.Url);
            Assert.False(string.IsNullOrWhiteSpace(view.Webhook.VerifyToken));
            Assert.Equal(StatusCanal.Configurando, f.Gravado!.Status);
        }

        [Fact]
        public async Task Registrar_de_novo_que_falha_vai_para_erro_e_dar_certo_vai_para_registrado()
        {
            var f = new Fixture();
            f.Registrador.SetupSequence(r => r.RegistrarAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>()))
                .ReturnsAsync(new ResultadoRegistro(false, WhatsappErros.WabaNaoEncontrada()))
                .ReturnsAsync(new ResultadoRegistro(false, WhatsappErros.WabaNaoEncontrada()))
                .ReturnsAsync(new ResultadoRegistro(true, null));
            var servico = f.Build();

            var conectado = await servico.ConectarProprioAsync(WaTeste.Dono(), WaTeste.Loja, Pedido());
            Assert.Equal(WebhookEstado.Pendente, conectado.Webhook!.Estado);

            var falhou = await servico.RegistrarWebhookAsync(WaTeste.Dono(), f.Gravado!.Id);
            Assert.Equal(WebhookEstado.Erro, falhou.Webhook!.Estado);
            Assert.Equal(WhatsappCodigos.WabaInvalida, falhou.Webhook.Codigo);

            var ok = await servico.RegistrarWebhookAsync(WaTeste.Dono(), f.Gravado.Id);
            Assert.Equal(WebhookEstado.Registrado, ok.Webhook!.Estado);
            Assert.Null(ok.Webhook.Erro);
            Assert.NotNull(ok.Webhook.RegistradoEm);
        }

        [Fact]
        public async Task Sem_a_url_publica_https_o_registro_fica_pendente_dizendo_qual_variavel_configurar()
        {
            var f = new Fixture();
            var webhook = new CanalWebhookService(f.Canais.Object, f.Protetor, f.Registrador.Object, WaTeste.Config(baseUrl: "http://localhost:5000"),
                NullLogger<CanalWebhookService>.Instance);
            var servico = new CanaisWhatsappService(f.Canais.Object, f.Catalogo.Object, f.Protetor, f.Verificador.Object, webhook, NullLogger<CanaisWhatsappService>.Instance);

            var view = await servico.ConectarProprioAsync(WaTeste.Dono(), WaTeste.Loja, Pedido());

            Assert.Equal(WebhookEstado.Pendente, view.Webhook!.Estado);
            Assert.Equal(WhatsappCodigos.WebhookNaoRegistrado, view.Webhook.Codigo);
            Assert.Contains("App__BaseUrl", view.Webhook.ComoResolver);
            f.Registrador.Verify(r => r.RegistrarAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>()), Times.Never);
        }

        [Fact]
        public async Task Numero_que_ja_e_de_outra_loja_e_recusado_sem_chamar_a_Meta()
        {
            var f = new Fixture();
            f.Canais.Setup(c => c.ObterPorPhoneNumberIdAsync(WaTeste.IdMeta)).ReturnsAsync(WaTeste.CanalProprio(f.Protetor, loja: WaTeste.OutraLoja));

            var erro = await Assert.ThrowsAsync<WhatsappException>(() => f.Build().ConectarProprioAsync(WaTeste.Dono(), WaTeste.Loja, Pedido()));

            Assert.Equal(WhatsappCodigos.NumeroOutraLoja, erro.Erro.Codigo);
            Assert.Equal(409, erro.Http);
            Assert.Equal("phoneNumberId", erro.Erro.Campo);
            f.Verificador.Verify(v => v.ConsultarCredenciaisAsync(It.IsAny<string>(), It.IsAny<string>()), Times.Never);
            f.Canais.Verify(c => c.CriarAsync(It.IsAny<CanalWhatsapp>(), It.IsAny<IReadOnlyCollection<string>>()), Times.Never);
        }

        [Fact]
        public async Task Telefone_que_a_Meta_devolve_e_ja_esta_em_outro_canal_tambem_e_recusado()
        {
            var f = new Fixture();
            f.Canais.Setup(c => c.ObterPorNumeroAsync("5534998218242")).ReturnsAsync(new CanalWhatsapp { Id = Guid.NewGuid(), IdEstabelecimento = WaTeste.OutraLoja, PhoneNumberId = "999999999999999" });

            var erro = await Assert.ThrowsAsync<WhatsappException>(() => f.Build().ConectarProprioAsync(WaTeste.Dono(), WaTeste.Loja, Pedido()));

            Assert.Equal(WhatsappCodigos.NumeroOutraLoja, erro.Erro.Codigo);
            f.Canais.Verify(c => c.CriarAsync(It.IsAny<CanalWhatsapp>(), It.IsAny<IReadOnlyCollection<string>>()), Times.Never);
        }

        [Fact]
        public async Task Reconectar_mantem_a_url_e_o_verify_token_e_reinicia_o_webhook()
        {
            var f = new Fixture();
            var existente = WaTeste.CanalProprio(f.Protetor);
            existente.WebhookEstado = WebhookEstado.Recebendo;
            var chave = existente.WebhookChave;
            var verify = existente.VerifyTokenCifrado;
            f.Canais.Setup(c => c.ObterPorPhoneNumberIdAsync(WaTeste.IdMeta)).ReturnsAsync(existente);
            f.Canais.Setup(c => c.ObterPorNumeroAsync("5534998218242")).ReturnsAsync(existente);
            f.Canais.Setup(c => c.ObterAsync(existente.Id)).ReturnsAsync(existente);
            f.Canais.Setup(c => c.DefinirWebhookAsync(existente.Id, It.IsAny<string>(), It.IsAny<string?>(), It.IsAny<DateTime?>()))
                .Callback<Guid, string, string?, DateTime?>((_, estado, _, _) => existente.WebhookEstado = estado).Returns(Task.CompletedTask);

            await f.Build().ConectarProprioAsync(WaTeste.Dono(), WaTeste.Loja, Pedido());

            f.Canais.Verify(c => c.CriarAsync(It.IsAny<CanalWhatsapp>(), It.IsAny<IReadOnlyCollection<string>>()), Times.Never);
            f.Canais.Verify(c => c.GravarProprioAsync(It.Is<CanalWhatsapp>(x => x.WebhookChave == chave && x.VerifyTokenCifrado == verify)), Times.Once);
            Assert.Equal(WebhookEstado.Registrado, existente.WebhookEstado);
        }

        [Fact]
        public async Task Sem_a_chave_de_cifragem_nada_e_guardado()
        {
            var f = new Fixture(chaveToken: null);

            var erro = await Assert.ThrowsAsync<WhatsappException>(() => f.Build().ConectarProprioAsync(WaTeste.Dono(), WaTeste.Loja, Pedido()));

            Assert.Equal(WhatsappCodigos.ChaveCifragemAusente, erro.Erro.Codigo);
            Assert.Equal(503, erro.Http);
            Assert.Contains("Atendimento__ChaveToken", erro.Erro.ComoResolver);
            f.Canais.Verify(c => c.CriarAsync(It.IsAny<CanalWhatsapp>(), It.IsAny<IReadOnlyCollection<string>>()), Times.Never);
        }

        [Theory]
        [InlineData("5534998218242", WaTeste.Waba, WaTeste.Token, WaTeste.AppId, WaTeste.AppSecret, WhatsappCodigos.IdParecePhone, "phoneNumberId")]
        [InlineData(WaTeste.IdMeta, "", WaTeste.Token, WaTeste.AppId, WaTeste.AppSecret, WhatsappCodigos.WabaInvalida, "wabaId")]
        [InlineData(WaTeste.IdMeta, WaTeste.IdMeta, WaTeste.Token, WaTeste.AppId, WaTeste.AppSecret, WhatsappCodigos.WabaInvalida, "wabaId")]
        [InlineData(WaTeste.IdMeta, WaTeste.Waba, "", WaTeste.AppId, WaTeste.AppSecret, WhatsappCodigos.TokenInvalido, "token")]
        [InlineData(WaTeste.IdMeta, WaTeste.Waba, WaTeste.Token, "abc", WaTeste.AppSecret, WhatsappCodigos.AppSecretInvalido, "appId")]
        [InlineData(WaTeste.IdMeta, WaTeste.Waba, WaTeste.Token, WaTeste.AppId, "curto", WhatsappCodigos.AppSecretInvalido, "appSecret")]
        public async Task Formato_errado_diz_o_codigo_o_campo_e_como_resolver(
            string id, string waba, string token, string appId, string secret, string codigo, string campo)
        {
            var f = new Fixture();

            var erro = await Assert.ThrowsAsync<WhatsappException>(() =>
                f.Build().ConectarProprioAsync(WaTeste.Dono(), WaTeste.Loja, new ConectarProprioRequest(id, waba, token, appId, secret)));

            Assert.Equal(codigo, erro.Erro.Codigo);
            Assert.Equal(campo, erro.Erro.Campo);
            Assert.False(string.IsNullOrWhiteSpace(erro.Erro.ComoResolver));
            Assert.Equal(422, erro.Http);
            f.Verificador.Verify(v => v.ConsultarCredenciaisAsync(It.IsAny<string>(), It.IsAny<string>()), Times.Never);
        }

        [Fact]
        public async Task Token_recusado_pela_Meta_nao_grava_nada()
        {
            var f = new Fixture();
            f.Verificador.Setup(v => v.ConsultarCredenciaisAsync(It.IsAny<string>(), It.IsAny<string>()))
                .ReturnsAsync(new VerificacaoCanal(false, "O token da Meta e invalido ou expirou.", null, null, WhatsappCodigos.TokenInvalido, "token"));

            var erro = await Assert.ThrowsAsync<WhatsappException>(() => f.Build().ConectarProprioAsync(WaTeste.Dono(), WaTeste.Loja, Pedido()));

            Assert.Equal(WhatsappCodigos.TokenInvalido, erro.Erro.Codigo);
            Assert.Equal("token", erro.Erro.Campo);
            f.Canais.Verify(c => c.CriarAsync(It.IsAny<CanalWhatsapp>(), It.IsAny<IReadOnlyCollection<string>>()), Times.Never);
            f.Registrador.Verify(r => r.RegistrarAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>()), Times.Never);
        }

        [Fact]
        public async Task Quem_nao_configura_o_whatsapp_da_loja_nao_conecta_nem_ve_o_webhook()
        {
            var f = new Fixture();
            var canal = WaTeste.CanalProprio(f.Protetor);
            f.Canais.Setup(c => c.ObterAsync(canal.Id)).ReturnsAsync(canal);

            await Assert.ThrowsAsync<UnauthorizedAccessException>(() => f.Build().ConectarProprioAsync(WaTeste.Leitor(), WaTeste.Loja, Pedido()));
            await Assert.ThrowsAsync<UnauthorizedAccessException>(() => f.Build().ConectarProprioAsync(WaTeste.Dono(WaTeste.OutraLoja), WaTeste.Loja, Pedido()));
            await Assert.ThrowsAsync<UnauthorizedAccessException>(() => f.Build().ObterWebhookAsync(WaTeste.Leitor(), canal.Id));
            await Assert.ThrowsAsync<UnauthorizedAccessException>(() => f.Build().RegistrarWebhookAsync(WaTeste.Leitor(), canal.Id));
        }

        [Fact]
        public async Task Webhook_mostra_a_url_e_o_verify_token_mas_nunca_o_app_secret()
        {
            var f = new Fixture();
            var canal = WaTeste.CanalProprio(f.Protetor);
            f.Canais.Setup(c => c.ObterAsync(canal.Id)).ReturnsAsync(canal);

            var webhook = await f.Build().ObterWebhookAsync(WaTeste.Dono(), canal.Id);
            var json = JsonSerializer.Serialize(webhook);

            Assert.Equal("verify-do-canal", webhook.VerifyToken);
            Assert.Equal($"https://api.zippy.test/wa/webhook/{canal.WebhookChave}", webhook.Url);
            Assert.DoesNotContain(WaTeste.AppSecret, json);
            Assert.DoesNotContain(WaTeste.Token, json);
        }

        [Fact]
        public async Task Quem_so_le_a_lista_ve_o_estado_mas_nao_a_url_nem_o_verify_token()
        {
            var f = new Fixture();
            var canal = WaTeste.CanalProprio(f.Protetor);
            f.Canais.Setup(c => c.ListarPorLojaAsync(WaTeste.Loja)).ReturnsAsync(new[] { canal });

            var leitor = (await f.Build().ListarAsync(WaTeste.Leitor() with { TemPermissao = (m, a) => m == "WhatsApp" && a == "ler" }, WaTeste.Loja)).Single();
            var dono = (await f.Build().ListarAsync(WaTeste.Dono(), WaTeste.Loja)).Single();

            Assert.Equal(WebhookEstado.Pendente, leitor.Webhook!.Estado);
            Assert.Null(leitor.Webhook.Url);
            Assert.Null(leitor.Webhook.VerifyToken);
            Assert.NotNull(dono.Webhook!.Url);
            Assert.NotNull(dono.Webhook.VerifyToken);
        }

        [Fact]
        public async Task Registrar_webhook_de_numero_que_nao_e_proprio_e_recusado()
        {
            var f = new Fixture();
            var zippy = new CanalWhatsapp { Id = Guid.NewGuid(), IdEstabelecimento = WaTeste.Loja, PhoneNumberId = "111111111111111", Origem = OrigemCanal.Zippy };
            f.Canais.Setup(c => c.ObterAsync(zippy.Id)).ReturnsAsync(zippy);

            await Assert.ThrowsAsync<WhatsappException>(() => f.Build().RegistrarWebhookAsync(WaTeste.Gestao, zippy.Id));
        }

        [Fact]
        public async Task Numero_proprio_nao_tem_o_token_trocado_pela_tela_antiga_da_Gestao()
        {
            var f = new Fixture();
            var canal = WaTeste.CanalProprio(f.Protetor);
            f.Canais.Setup(c => c.ObterAsync(canal.Id)).ReturnsAsync(canal);

            var erro = await Assert.ThrowsAsync<WhatsappException>(() => f.Build().AtualizarAsync(WaTeste.Gestao, canal.Id,
                new CanalRequest(WaTeste.IdMeta, "5534998218242", null, "EAAG-outro", null, null)));

            Assert.Equal(WhatsappCodigos.CampoInvalido, erro.Erro.Codigo);
            f.Canais.Verify(c => c.AtualizarDadosAsync(It.IsAny<CanalWhatsapp>()), Times.Never);
        }
    }

    public class CanalZippyTests
    {
        private static CanalRequest Zippy(string? id = WaTeste.IdMeta) => new(id, null, "Pizzaria", null, null, null, OrigemCanal.Zippy);

        private sealed class Fixture
        {
            public Mock<ICanalRepository> Canais { get; } = new();
            public Mock<ICatalogoRepository> Catalogo { get; } = new();
            public Mock<ICanalVerificador> Verificador { get; } = new();
            public Mock<ITokenProtector> Token { get; } = new();
            public Mock<ICanalWebhookService> Webhook { get; } = new();
            public CanalWhatsapp? Gravado { get; private set; }

            public Fixture()
            {
                Catalogo.Setup(c => c.LojaExisteAsync(It.IsAny<Guid>())).ReturnsAsync(true);
                Catalogo.Setup(c => c.ListarServicosDaLojaAsync(WaTeste.Loja)).ReturnsAsync(new[] { new ServicoDaLoja { Codigo = "delivery", Ativo = true } });
                Canais.Setup(c => c.CriarAsync(It.IsAny<CanalWhatsapp>(), It.IsAny<IReadOnlyCollection<string>>()))
                    .Callback<CanalWhatsapp, IReadOnlyCollection<string>>((c, _) => Gravado = c).Returns(Task.CompletedTask);
                Canais.Setup(c => c.ObterAsync(It.IsAny<Guid>())).ReturnsAsync(() => Gravado);
                Token.Setup(t => t.Configurado).Returns(true);
                Verificador.Setup(v => v.ConsultarComTokenGlobalAsync(It.IsAny<string>()))
                    .ReturnsAsync(new VerificacaoCanal(true, "Numero verificado na Meta.", "+55 34 99821-8242", "Pizzaria Teste"));
            }

            public CanaisWhatsappService Build() =>
                new(Canais.Object, Catalogo.Object, Token.Object, Verificador.Object, Webhook.Object, NullLogger<CanaisWhatsappService>.Instance);
        }

        [Fact]
        public async Task Gestao_cria_o_numero_so_com_o_phone_number_id_o_telefone_vem_da_Meta_e_fica_ativo()
        {
            var f = new Fixture();

            await f.Build().CriarAsync(WaTeste.Gestao, WaTeste.Loja, Zippy());

            var canal = f.Gravado!;
            Assert.Equal(OrigemCanal.Zippy, canal.Origem);
            Assert.Equal("+5534998218242", canal.NumeroE164);
            Assert.Null(canal.TokenCifrado);
            Assert.Equal(WebhookEstado.NaoAplicavel, canal.WebhookEstado);
            Assert.Contains("delivery", canal.Servicos);
            f.Canais.Verify(c => c.MarcarVerificadoAsync(canal.Id, true, null), Times.Once);
            f.Catalogo.Verify(c => c.GarantirModulosAsync(WaTeste.Loja, It.Is<IReadOnlyCollection<string>>(m => m.Contains("WHATSAPP"))), Times.Once);
            f.Verificador.Verify(v => v.ConsultarCredenciaisAsync(It.IsAny<string>(), It.IsAny<string>()), Times.Never);
        }

        [Fact]
        public async Task Token_enviado_junto_e_ignorado_no_numero_zippy()
        {
            var f = new Fixture();

            await f.Build().CriarAsync(WaTeste.Gestao, WaTeste.Loja, Zippy() with { Token = "EAAG-do-cliente" });

            Assert.Null(f.Gravado!.TokenCifrado);
            f.Token.Verify(t => t.Proteger(It.IsAny<string>()), Times.Never);
        }

        [Fact]
        public async Task Sem_o_token_global_recusa_dizendo_qual_variavel_configurar()
        {
            var f = new Fixture();
            f.Verificador.Setup(v => v.ConsultarComTokenGlobalAsync(It.IsAny<string>()))
                .ReturnsAsync(new VerificacaoCanal(false, "sem token global", null, null, WhatsappCodigos.TokenGlobalAusente));

            var erro = await Assert.ThrowsAsync<WhatsappException>(() => f.Build().CriarAsync(WaTeste.Gestao, WaTeste.Loja, Zippy()));

            Assert.Equal(WhatsappCodigos.TokenGlobalAusente, erro.Erro.Codigo);
            Assert.Equal(503, erro.Http);
            Assert.Contains("WhatsApp__AccessToken", erro.Erro.ComoResolver);
            f.Canais.Verify(c => c.CriarAsync(It.IsAny<CanalWhatsapp>(), It.IsAny<IReadOnlyCollection<string>>()), Times.Never);
        }

        [Fact]
        public async Task Phone_number_id_de_outra_loja_ou_telefone_repetido_e_recusado()
        {
            var f = new Fixture();
            f.Canais.Setup(c => c.ObterPorPhoneNumberIdAsync(WaTeste.IdMeta)).ReturnsAsync(new CanalWhatsapp { Id = Guid.NewGuid(), IdEstabelecimento = WaTeste.OutraLoja });
            var porId = await Assert.ThrowsAsync<WhatsappException>(() => f.Build().CriarAsync(WaTeste.Gestao, WaTeste.Loja, Zippy()));

            var g = new Fixture();
            g.Canais.Setup(c => c.ObterPorNumeroAsync("5534998218242")).ReturnsAsync(new CanalWhatsapp { Id = Guid.NewGuid(), IdEstabelecimento = WaTeste.OutraLoja });
            var porTelefone = await Assert.ThrowsAsync<WhatsappException>(() => g.Build().CriarAsync(WaTeste.Gestao, WaTeste.Loja, Zippy()));

            Assert.Equal(WhatsappCodigos.NumeroOutraLoja, porId.Erro.Codigo);
            Assert.Equal(WhatsappCodigos.NumeroOutraLoja, porTelefone.Erro.Codigo);
            f.Canais.Verify(c => c.CriarAsync(It.IsAny<CanalWhatsapp>(), It.IsAny<IReadOnlyCollection<string>>()), Times.Never);
            g.Canais.Verify(c => c.CriarAsync(It.IsAny<CanalWhatsapp>(), It.IsAny<IReadOnlyCollection<string>>()), Times.Never);
        }

        [Fact]
        public async Task Telefone_no_lugar_do_id_e_so_a_Gestao_cria()
        {
            var f = new Fixture();

            var erro = await Assert.ThrowsAsync<WhatsappException>(() => f.Build().CriarAsync(WaTeste.Gestao, WaTeste.Loja, Zippy("5534998218242")));
            await Assert.ThrowsAsync<UnauthorizedAccessException>(() => f.Build().CriarAsync(WaTeste.Dono(), WaTeste.Loja, Zippy()));
            await Assert.ThrowsAsync<UnauthorizedAccessException>(() => f.Build().PreverNumeroZippyAsync(WaTeste.Dono(), WaTeste.IdMeta));

            Assert.Equal(WhatsappCodigos.IdParecePhone, erro.Erro.Codigo);
        }

        [Fact]
        public async Task Prever_confere_na_Meta_sem_gravar_nada()
        {
            var f = new Fixture();

            await f.Build().PreverNumeroZippyAsync(WaTeste.Gestao, WaTeste.IdMeta);

            f.Verificador.Verify(v => v.ConsultarComTokenGlobalAsync(WaTeste.IdMeta), Times.Once);
            f.Canais.Verify(c => c.CriarAsync(It.IsAny<CanalWhatsapp>(), It.IsAny<IReadOnlyCollection<string>>()), Times.Never);
        }

        [Fact]
        public async Task Origem_diferente_de_zippy_nao_cria_por_aqui()
        {
            var f = new Fixture();

            var erro = await Assert.ThrowsAsync<WhatsappException>(() =>
                f.Build().CriarAsync(WaTeste.Gestao, WaTeste.Loja, Zippy() with { Origem = OrigemCanal.Proprio }));

            Assert.Equal("origem", erro.Erro.Campo);
        }
    }

    public class CanalVerificadorGlobalTests
    {
        private sealed class Handler : HttpMessageHandler
        {
            public List<HttpRequestMessage> Chamadas { get; } = new();
            public string Corpo { get; set; } = @"{""display_phone_number"":""+55 34 99821-8242"",""verified_name"":""Pizzaria""}";
            public HttpStatusCode Status { get; set; } = HttpStatusCode.OK;

            protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            {
                Chamadas.Add(request);
                return Task.FromResult(new HttpResponseMessage(Status) { Content = new StringContent(Corpo, Encoding.UTF8, "application/json") });
            }
        }

        private sealed class Fabrica : IHttpClientFactory
        {
            private readonly HttpMessageHandler _handler;
            public Fabrica(HttpMessageHandler handler) => _handler = handler;
            public HttpClient CreateClient(string name) => new(_handler, disposeHandler: false);
        }

        private static CanalVerificador Build(Handler handler, string? tokenGlobal)
        {
            var provider = new Mock<IWhatsAppTokenProvider>();
            provider.Setup(p => p.GetAccessToken()).Returns(tokenGlobal);
            return new CanalVerificador(new Fabrica(handler), new Mock<ICanalRepository>().Object, WaTeste.Protetor(), provider.Object,
                WaTeste.Config(), NullLogger<CanalVerificador>.Instance);
        }

        [Theory]
        [InlineData(null)]
        [InlineData("")]
        [InlineData("__SET_IN_ENV__")]
        public async Task Sem_token_global_configurado_nem_chama_a_Meta(string? global)
        {
            var handler = new Handler();

            var r = await Build(handler, global).ConsultarComTokenGlobalAsync(WaTeste.IdMeta);

            Assert.False(r.Ok);
            Assert.Equal(WhatsappCodigos.TokenGlobalAusente, r.Codigo);
            Assert.Empty(handler.Chamadas);
        }

        [Fact]
        public async Task Com_token_global_consulta_o_numero_com_ele_e_devolve_o_telefone_da_Meta()
        {
            var handler = new Handler();

            var r = await Build(handler, "EAAG-global").ConsultarComTokenGlobalAsync(WaTeste.IdMeta);

            Assert.True(r.Ok);
            Assert.Equal("+55 34 99821-8242", r.NumeroNaMeta);
            var chamada = Assert.Single(handler.Chamadas);
            Assert.Equal("Bearer", chamada.Headers.Authorization!.Scheme);
            Assert.Equal("EAAG-global", chamada.Headers.Authorization.Parameter);
            Assert.Contains($"/{WaTeste.IdMeta}?", chamada.RequestUri!.ToString());
        }

        [Theory]
        [InlineData(@"{""error"":{""message"":""x"",""code"":190}}", WhatsappCodigos.TokenInvalido, "token")]
        [InlineData(@"{""error"":{""message"":""x"",""code"":200}}", WhatsappCodigos.SemPermissao, "token")]
        [InlineData(@"{""error"":{""message"":""x"",""code"":10}}", WhatsappCodigos.SemPermissao, "token")]
        [InlineData(@"{""error"":{""message"":""x"",""code"":100,""error_subcode"":33}}", WhatsappCodigos.IdNaoEncontrado, "phoneNumberId")]
        public async Task Erros_da_Meta_viram_codigo_estavel_e_campo(string corpo, string codigo, string campo)
        {
            var handler = new Handler { Status = HttpStatusCode.BadRequest, Corpo = corpo };

            var r = await Build(handler, "EAAG-global").ConsultarComTokenGlobalAsync(WaTeste.IdMeta);

            Assert.False(r.Ok);
            Assert.Equal(codigo, r.Codigo);
            Assert.Equal(campo, r.Campo);
            var erro = WhatsappErros.DaVerificacao(r);
            Assert.Equal(codigo, erro.Codigo);
            Assert.False(string.IsNullOrWhiteSpace(erro.ComoResolver));
        }
    }

    public class WhatsappErrosTests
    {
        [Theory]
        [InlineData(ChamadaMeta.AssinaturaDoApp, 190, null, WhatsappCodigos.AppSecretInvalido)]
        [InlineData(ChamadaMeta.AssinaturaDoApp, 100, null, WhatsappCodigos.AppSecretInvalido)]
        [InlineData(ChamadaMeta.AssinaturaDoApp, 2200, null, WhatsappCodigos.WebhookNaoRegistrado)]
        [InlineData(ChamadaMeta.AppDaConta, 190, null, WhatsappCodigos.TokenInvalido)]
        [InlineData(ChamadaMeta.AppDaConta, 200, null, WhatsappCodigos.SemPermissao)]
        [InlineData(ChamadaMeta.AppDaConta, 100, null, WhatsappCodigos.WabaInvalida)]
        [InlineData(ChamadaMeta.Numero, 100, 33, WhatsappCodigos.IdNaoEncontrado)]
        [InlineData(ChamadaMeta.Numero, 190, null, WhatsappCodigos.TokenInvalido)]
        [InlineData(ChamadaMeta.Numero, 10, null, WhatsappCodigos.SemPermissao)]
        [InlineData(ChamadaMeta.Numero, 999, null, WhatsappCodigos.MetaErro)]
        public void O_mesmo_codigo_da_Meta_significa_coisas_diferentes_em_cada_chamada(ChamadaMeta chamada, int codigo, int? sub, string esperado)
        {
            var erro = WhatsappErros.DaMeta(chamada, 400, codigo, sub, "detalhe da meta");

            Assert.Equal(esperado, erro.Codigo);
            Assert.False(string.IsNullOrWhiteSpace(erro.Mensagem));
            Assert.False(string.IsNullOrWhiteSpace(erro.ComoResolver));
        }

        [Fact]
        public void A_resposta_de_erro_tem_o_formato_success_error_code_details()
        {
            var erro = WhatsappErros.AppSecretInvalido();

            var json = JsonSerializer.Serialize(ApiResponse<object>.Fail(erro.Mensagem, erro.Codigo, erro.Detalhes));
            using var doc = JsonDocument.Parse(json);
            var raiz = doc.RootElement;

            Assert.False(raiz.GetProperty("success").GetBoolean());
            Assert.Equal(erro.Mensagem, raiz.GetProperty("error").GetString());
            Assert.Equal("WA_APP_SECRET_INVALIDO", raiz.GetProperty("code").GetString());
            Assert.Equal(erro.ComoResolver, raiz.GetProperty("details").GetProperty("comoResolver").GetString());
            Assert.Equal("appSecret", raiz.GetProperty("details").GetProperty("campo").GetString());
        }

        [Fact]
        public void Todos_os_codigos_minimos_existem_com_passo_para_corrigir()
        {
            var erros = new[]
            {
                WhatsappErros.TokenInvalido(), WhatsappErros.SemPermissao(), WhatsappErros.IdNaoEncontrado(), WhatsappErros.IdParecePhone(),
                WhatsappErros.AppSecretInvalido(), WhatsappErros.WabaNaoEncontrada(), WhatsappErros.NumeroOutraLoja(),
                WhatsappErros.TokenGlobalAusente(), WhatsappErros.ChaveCifragemAusente(), WhatsappErros.WebhookNaoRegistrado(),
                WhatsappErros.SemRecebimento(10)
            };

            Assert.Equal(
                new[]
                {
                    "WA_TOKEN_INVALIDO", "WA_SEM_PERMISSAO", "WA_ID_NAO_ENCONTRADO", "WA_ID_PARECE_TELEFONE", "WA_APP_SECRET_INVALIDO",
                    "WA_WABA_INVALIDA", "WA_NUMERO_OUTRA_LOJA", "WA_TOKEN_GLOBAL_AUSENTE", "WA_CHAVE_CIFRAGEM_AUSENTE",
                    "WA_WEBHOOK_NAO_REGISTRADO", "WA_SEM_RECEBIMENTO"
                },
                erros.Select(e => e.Codigo));
            Assert.All(erros, e => Assert.True(e.ComoResolver.Length > 30, e.Codigo));
        }
    }

    public class TokenProtectorOrigemTests
    {
        [Fact]
        public void Cifragem_ida_e_volta_nao_expoe_o_texto_e_muda_a_cada_vez()
        {
            var p = WaTeste.Protetor();

            var a = p.Proteger(WaTeste.AppSecret);
            var b = p.Proteger(WaTeste.AppSecret);

            Assert.NotEqual(a, b);
            Assert.DoesNotContain(WaTeste.AppSecret, a);
            Assert.StartsWith("v1.", a);
            Assert.Equal(WaTeste.AppSecret, p.Revelar(a));
            Assert.Equal(WaTeste.AppSecret, p.Revelar(b));
        }

        [Fact]
        public void Outra_chave_ou_texto_adulterado_nao_abre()
        {
            var cifrado = WaTeste.Protetor("chave-um").Proteger(WaTeste.Token);

            Assert.Null(WaTeste.Protetor("chave-dois").Revelar(cifrado));
            Assert.Null(WaTeste.Protetor("chave-um").Revelar(cifrado[..^4] + "AAAA"));
            Assert.Null(WaTeste.Protetor("chave-um").Revelar("texto-puro"));
        }

        [Fact]
        public void Sem_a_chave_nao_cifra_e_nao_revela()
        {
            var p = WaTeste.Protetor(null);

            Assert.False(p.Configurado);
            Assert.Throws<InvalidOperationException>(() => p.Proteger("x"));
            Assert.Null(p.Revelar(WaTeste.Protetor().Proteger("x")));
        }

        [Fact]
        public void Token_do_cliente_nunca_cai_no_token_global_mas_zippy_e_central_sim()
        {
            var p = WaTeste.Protetor();
            var provider = new Mock<IWhatsAppTokenProvider>();
            provider.Setup(x => x.GetAccessToken()).Returns("EAAG-global");

            var proprioSemToken = new CanalWhatsapp { Origem = OrigemCanal.Proprio };
            var zippy = new CanalWhatsapp { Origem = OrigemCanal.Zippy };
            var central = new CanalWhatsapp { Origem = OrigemCanal.Central };
            var proprioComToken = WaTeste.CanalProprio(p);

            Assert.Null(CanalToken.Resolver(proprioSemToken, p, provider.Object).Token);
            Assert.Equal(("EAAG-global", CanalToken.Global), CanalToken.Resolver(zippy, p, provider.Object));
            Assert.Equal(("EAAG-global", CanalToken.Global), CanalToken.Resolver(central, p, provider.Object));
            Assert.Equal((WaTeste.Token, CanalToken.DoCanal), CanalToken.Resolver(proprioComToken, p, provider.Object));
        }
    }

    public class WebhookPorCanalTests
    {
        private const string Corpo = @"{""object"":""whatsapp_business_account"",""entry"":[{""changes"":[{""field"":""messages"",""value"":{
            ""metadata"":{""display_phone_number"":""5534998218242"",""phone_number_id"":""821317791056700""},
            ""contacts"":[{""wa_id"":""5534999990000""}],
            ""messages"":[{""id"":""wamid.AAA"",""from"":""5534999990000"",""timestamp"":""1790980926"",""type"":""text"",""text"":{""body"":""oi""}}]}}]}]}";

        private sealed class Fixture
        {
            public Mock<ICanalRepository> Canais { get; } = new();
            public Mock<IWebhookRegistrador> Registrador { get; } = new();
            public Mock<IWaEventoRepository> Eventos { get; } = new();
            public ITokenProtector Protetor { get; } = WaTeste.Protetor();
            public CanalWhatsapp Canal { get; }
            public List<WaEvento> Gravados { get; } = new();
            public List<(string Estado, string? Erro)> Webhooks { get; } = new();

            public Fixture()
            {
                Canal = WaTeste.CanalProprio(Protetor);
                Canais.Setup(c => c.ObterPorWebhookChaveAsync(Canal.WebhookChave!)).ReturnsAsync(Canal);
                Canais.Setup(c => c.DefinirWebhookAsync(Canal.Id, It.IsAny<string>(), It.IsAny<string?>(), It.IsAny<DateTime?>()))
                    .Callback<Guid, string, string?, DateTime?>((_, estado, erro, _) => { Webhooks.Add((estado, erro)); Canal.WebhookEstado = estado; })
                    .Returns(Task.CompletedTask);
                Eventos.Setup(e => e.RegistrarAsync(It.IsAny<WaEvento>()))
                    .Callback<WaEvento>(e => Gravados.Add(e))
                    .ReturnsAsync((WaEvento e) => Gravados.Count(g => g.Tipo == e.Tipo && g.Chave == e.Chave) == 1);
            }

            public CanalWebhookService Servico() =>
                new(Canais.Object, Protetor, Registrador.Object, WaTeste.Config(), NullLogger<CanalWebhookService>.Instance);

            public WaWebhookIngestor Ingestor() => new(Eventos.Object, NullLogger<WaWebhookIngestor>.Instance);

            public WaCanalWebhookController Controller(string corpo, string? assinatura)
            {
                var controller = new WaCanalWebhookController(
                    Servico(), new WebhookValidatorService(new Mock<IWebhookSignatureValidator>().Object, NullLogger<WebhookValidatorService>.Instance),
                    Ingestor(), NullLogger<WaCanalWebhookController>.Instance);
                var contexto = new DefaultHttpContext();
                contexto.Request.Body = new System.IO.MemoryStream(Encoding.UTF8.GetBytes(corpo));
                if (assinatura != null) contexto.Request.Headers["X-Hub-Signature-256"] = assinatura;
                controller.ControllerContext = new ControllerContext { HttpContext = contexto };
                return controller;
            }
        }

        // ---- assinatura -------------------------------------------------------------------------------------

        [Fact]
        public void Assinatura_certa_vale_e_errada_ou_ausente_nao()
        {
            var certa = WaTeste.Assinar(WaTeste.AppSecret, Corpo);

            Assert.True(AssinaturaMeta.Valida(WaTeste.AppSecret, certa, Corpo));
            Assert.True(AssinaturaMeta.Valida(WaTeste.AppSecret, certa.ToUpperInvariant().Replace("SHA256=", "sha256="), Corpo));
            Assert.False(AssinaturaMeta.Valida(WaTeste.AppSecret, WaTeste.Assinar("outro-segredo-qualquer-0000000000", Corpo), Corpo));
            Assert.False(AssinaturaMeta.Valida(WaTeste.AppSecret, certa, Corpo + " "));
            Assert.False(AssinaturaMeta.Valida(WaTeste.AppSecret, null, Corpo));
            Assert.False(AssinaturaMeta.Valida(WaTeste.AppSecret, "", Corpo));
            Assert.False(AssinaturaMeta.Valida(null, certa, Corpo));
            Assert.False(AssinaturaMeta.Valida("", certa, Corpo));
        }

        [Fact]
        public void A_assinatura_e_conferida_com_o_app_secret_do_canal_e_nao_com_o_de_outro_canal()
        {
            var f = new Fixture();
            var outro = WaTeste.CanalProprio(f.Protetor);
            outro.AppSecretCifrado = f.Protetor.Proteger("segredo-de-outro-cliente-0123456789");

            var assinadaPeloCanal = WaTeste.Assinar(WaTeste.AppSecret, Corpo);

            Assert.True(f.Servico().AssinaturaValida(f.Canal, assinadaPeloCanal, Corpo));
            Assert.False(f.Servico().AssinaturaValida(outro, assinadaPeloCanal, Corpo));
            Assert.False(f.Servico().AssinaturaValida(new CanalWhatsapp { Origem = OrigemCanal.Proprio }, assinadaPeloCanal, Corpo));
        }

        // ---- GET (handshake) --------------------------------------------------------------------------------

        [Fact]
        public async Task Verificacao_devolve_o_desafio_so_com_o_verify_token_do_canal_e_marca_registrado()
        {
            var f = new Fixture();

            var desafio = await f.Servico().ResponderVerificacaoAsync(f.Canal.WebhookChave!, "subscribe", "verify-do-canal", "12345");

            Assert.Equal("12345", desafio);
            Assert.Equal(WebhookEstado.Registrado, f.Canal.WebhookEstado);
        }

        [Theory]
        [InlineData("subscribe", "token-errado")]
        [InlineData("subscribe", "")]
        [InlineData("subscribe", null)]
        [InlineData("unsubscribe", "verify-do-canal")]
        [InlineData(null, "verify-do-canal")]
        public async Task Verificacao_recusa_token_errado_ou_modo_errado(string? modo, string? token)
        {
            var f = new Fixture();

            Assert.Null(await f.Servico().ResponderVerificacaoAsync(f.Canal.WebhookChave!, modo, token, "12345"));
            Assert.Equal(WebhookEstado.Pendente, f.Canal.WebhookEstado);
        }

        [Fact]
        public async Task Verificacao_com_chave_desconhecida_recusa_e_o_token_do_canal_nao_vale_em_outro()
        {
            var f = new Fixture();
            f.Canais.Setup(c => c.ObterPorWebhookChaveAsync("chave-que-nao-existe")).ReturnsAsync((CanalWhatsapp?)null);

            Assert.Null(await f.Servico().ResponderVerificacaoAsync("chave-que-nao-existe", "subscribe", "verify-do-canal", "1"));
        }

        [Fact]
        public async Task Verificacao_nao_desfaz_recebendo()
        {
            var f = new Fixture();
            f.Canal.WebhookEstado = WebhookEstado.Recebendo;

            await f.Servico().ResponderVerificacaoAsync(f.Canal.WebhookChave!, "subscribe", "verify-do-canal", "1");

            Assert.Empty(f.Webhooks);
            Assert.Equal(WebhookEstado.Recebendo, f.Canal.WebhookEstado);
        }

        [Fact]
        public async Task Controller_GET_responde_o_desafio_em_texto_ou_403()
        {
            var f = new Fixture();
            var controller = f.Controller("", null);

            var ok = Assert.IsType<ContentResult>(await controller.Verificar(f.Canal.WebhookChave!, "subscribe", "verify-do-canal", "desafio-123"));
            var recusado = Assert.IsType<ObjectResult>(await controller.Verificar(f.Canal.WebhookChave!, "subscribe", "errado", "desafio-123"));

            Assert.Equal("desafio-123", ok.Content);
            Assert.Equal(403, recusado.StatusCode);
        }

        // ---- POST: assinatura e spoof ------------------------------------------------------------------------

        [Fact]
        public async Task Controller_POST_com_assinatura_do_app_do_canal_grava_o_evento_ligado_ao_canal()
        {
            var f = new Fixture();

            var resultado = await f.Controller(Corpo, WaTeste.Assinar(WaTeste.AppSecret, Corpo)).Receber(f.Canal.WebhookChave!);

            Assert.IsType<OkResult>(resultado);
            var evento = Assert.Single(f.Gravados);
            Assert.Equal("wamid.AAA", evento.Chave);
            Assert.Equal(f.Canal.Id, evento.IdCanal);
        }

        [Fact]
        public async Task Controller_POST_com_assinatura_errada_chave_desconhecida_ou_sem_assinatura_da_403_e_nao_grava()
        {
            var f = new Fixture();

            var errada = await f.Controller(Corpo, WaTeste.Assinar("segredo-de-outro-cliente-0123456789", Corpo)).Receber(f.Canal.WebhookChave!);
            var semAssinatura = await f.Controller(Corpo, null).Receber(f.Canal.WebhookChave!);
            f.Canais.Setup(c => c.ObterPorWebhookChaveAsync("desconhecida")).ReturnsAsync((CanalWhatsapp?)null);
            var desconhecida = await f.Controller(Corpo, WaTeste.Assinar(WaTeste.AppSecret, Corpo)).Receber("desconhecida");

            Assert.Equal(403, Assert.IsType<StatusCodeResult>(errada).StatusCode);
            Assert.Equal(403, Assert.IsType<StatusCodeResult>(semAssinatura).StatusCode);
            Assert.Equal(403, Assert.IsType<StatusCodeResult>(desconhecida).StatusCode);
            Assert.Empty(f.Gravados);
        }

        [Fact]
        public async Task Spoof_evento_com_phone_number_id_de_outra_loja_e_ignorado_mesmo_com_assinatura_valida()
        {
            var f = new Fixture();
            var forjado = Corpo.Replace("821317791056700", "555555555555555");

            // Assinatura perfeita (o cliente controla o proprio app), mas o numero e de outra loja.
            var resultado = await f.Controller(forjado, WaTeste.Assinar(WaTeste.AppSecret, forjado)).Receber(f.Canal.WebhookChave!);

            Assert.IsType<OkResult>(resultado);
            Assert.Empty(f.Gravados);
        }

        [Fact]
        public async Task Spoof_vale_tambem_para_recibos_e_so_o_evento_do_proprio_numero_passa_num_lote_misto()
        {
            var f = new Fixture();
            var lote = @"{""entry"":[{""changes"":[
                {""value"":{""metadata"":{""phone_number_id"":""555555555555555""},""statuses"":[{""id"":""wamid.X"",""status"":""read""}],
                           ""messages"":[{""id"":""wamid.FORJADA"",""from"":""5534999990000"",""type"":""text"",""text"":{""body"":""x""}}]}},
                {""value"":{""metadata"":{""phone_number_id"":""821317791056700""},""statuses"":[{""id"":""wamid.OK"",""status"":""delivered""}]}}]}]}";
            using var doc = JsonDocument.Parse(lote);

            var r = await f.Ingestor().IngerirAsync(doc.RootElement, f.Canal);

            Assert.Equal(1, r.Novos);
            Assert.Equal(2, r.Ignorados);
            Assert.Equal("wamid.OK:delivered", Assert.Single(f.Gravados).Chave);
        }

        [Fact]
        public async Task O_webhook_global_continua_gravando_sem_canal_e_sem_filtro_de_numero()
        {
            var f = new Fixture();
            using var doc = JsonDocument.Parse(Corpo);

            var r = await f.Ingestor().IngerirAsync(doc.RootElement, canal: null);

            Assert.Equal(1, r.Novos);
            Assert.Null(Assert.Single(f.Gravados).IdCanal);
        }

        [Fact]
        public async Task Segunda_trava_o_processador_ignora_evento_de_um_canal_para_o_numero_de_outro()
        {
            var dono = new CanalWhatsapp { Id = Guid.NewGuid(), IdEstabelecimento = WaTeste.OutraLoja, PhoneNumberId = "555555555555555" };
            var canais = new Mock<ICanalRepository>();
            canais.Setup(c => c.ObterAtivoPorPhoneNumberIdAsync("555555555555555")).ReturnsAsync(dono);
            var pipeline = new Mock<IPipelineDeMensagem>();
            var processador = new WaEventoProcessor(canais.Object, new Mock<IMessageService>().Object, pipeline.Object, NullLogger<WaEventoProcessor>.Instance);
            var evento = new WaEvento
            {
                Id = Guid.NewGuid(), Tipo = TipoEvento.Mensagem, Chave = "wamid.FORJADA", PhoneNumberId = "555555555555555", IdCanal = Guid.NewGuid(),
                PayloadJson = @"{""metadata"":{""phone_number_id"":""555555555555555""},""message"":{""id"":""wamid.FORJADA"",""from"":""5534999990000"",""type"":""text"",""text"":{""body"":""oi""}}}"
            };

            var resultado = await processador.ProcessarAsync(evento);

            Assert.Equal("ignorado", resultado.Resultado);
            canais.Verify(c => c.MarcarRecebimentoAsync(It.IsAny<Guid>()), Times.Never);
            pipeline.Verify(p => p.ExecutarAsync(It.IsAny<ConversationProcessingInput>(), It.IsAny<CanalWhatsapp>()), Times.Never);
        }

        [Fact]
        public async Task Mensagem_do_proprio_canal_chega_ao_atendimento_e_marca_o_recebimento()
        {
            var f = new Fixture();
            f.Canais.Setup(c => c.ObterAtivoPorPhoneNumberIdAsync(WaTeste.IdMeta)).ReturnsAsync(f.Canal);
            var pipeline = new Mock<IPipelineDeMensagem>();
            var processador = new WaEventoProcessor(f.Canais.Object, new Mock<IMessageService>().Object, pipeline.Object, NullLogger<WaEventoProcessor>.Instance);
            var evento = new WaEvento
            {
                Id = Guid.NewGuid(), Tipo = TipoEvento.Mensagem, Chave = "wamid.AAA", PhoneNumberId = WaTeste.IdMeta, IdCanal = f.Canal.Id,
                PayloadJson = @"{""metadata"":{""phone_number_id"":""821317791056700""},""message"":{""id"":""wamid.AAA"",""from"":""5534999990000"",""type"":""text"",""text"":{""body"":""oi""}}}"
            };

            var resultado = await processador.ProcessarAsync(evento);

            Assert.Equal("processado", resultado.Resultado);
            f.Canais.Verify(c => c.MarcarRecebimentoAsync(f.Canal.Id), Times.Once);
        }
    }

    public class WebhookMaquinaDeEstadosTests
    {
        [Fact]
        public void Fluxo_normal_pendente_registrado_recebendo()
        {
            var estado = WebhookEstado.Pendente;

            estado = WebhookEstado.AoRegistrar(estado);
            Assert.Equal(WebhookEstado.Registrado, estado);

            estado = WebhookEstado.AoReceber(OrigemCanal.Proprio, estado);
            Assert.Equal(WebhookEstado.Recebendo, estado);
        }

        [Theory]
        [InlineData(WebhookEstado.Pendente)]
        [InlineData(WebhookEstado.Registrado)]
        [InlineData(WebhookEstado.Erro)]
        public void Mensagem_de_verdade_leva_a_recebendo_de_qualquer_estado_do_numero_proprio(string antes) =>
            Assert.Equal(WebhookEstado.Recebendo, WebhookEstado.AoReceber(OrigemCanal.Proprio, antes));

        [Theory]
        [InlineData(OrigemCanal.Zippy)]
        [InlineData(OrigemCanal.Central)]
        public void Zippy_e_central_continuam_nao_aplicavel(string origem) =>
            Assert.Equal(WebhookEstado.NaoAplicavel, WebhookEstado.AoReceber(origem, WebhookEstado.NaoAplicavel));

        [Fact]
        public void Falha_na_conexao_fica_pendente_ao_tentar_de_novo_vira_erro_e_recebendo_nunca_regride()
        {
            Assert.Equal(WebhookEstado.Pendente, WebhookEstado.AoFalharRegistro(WebhookEstado.Pendente, primeiraTentativa: true));
            Assert.Equal(WebhookEstado.Erro, WebhookEstado.AoFalharRegistro(WebhookEstado.Pendente, primeiraTentativa: false));
            Assert.Equal(WebhookEstado.Recebendo, WebhookEstado.AoFalharRegistro(WebhookEstado.Recebendo, primeiraTentativa: false));
            Assert.Equal(WebhookEstado.Recebendo, WebhookEstado.AoRegistrar(WebhookEstado.Recebendo));
        }

        private static CanalWhatsapp Canal(string estado, DateTime verificadoEm, DateTime? recebeuEm = null, string? erro = null) => new()
        {
            Origem = OrigemCanal.Proprio, WebhookEstado = estado, VerificadoEm = verificadoEm, UltimoRecebimentoEm = recebeuEm, WebhookErro = erro
        };

        [Fact]
        public void Depois_de_10_minutos_sem_nenhuma_mensagem_a_tela_avisa_WA_SEM_RECEBIMENTO()
        {
            var agora = new DateTime(2026, 10, 25, 12, 0, 0, DateTimeKind.Utc);

            var cedo = CanalWebhookService.MontarVisao(Canal(WebhookEstado.Registrado, agora.AddMinutes(-9)), null, null, agora);
            var tarde = CanalWebhookService.MontarVisao(Canal(WebhookEstado.Registrado, agora.AddMinutes(-11)), null, null, agora);
            var recebendo = CanalWebhookService.MontarVisao(Canal(WebhookEstado.Recebendo, agora.AddHours(-3), agora.AddMinutes(-1)), null, null, agora);
            var jaRecebeu = CanalWebhookService.MontarVisao(Canal(WebhookEstado.Registrado, agora.AddHours(-3), agora.AddHours(-2)), null, null, agora);

            Assert.Null(cedo.Codigo);
            Assert.Equal(WhatsappCodigos.SemRecebimento, tarde.Codigo);
            Assert.Contains("oi", tarde.ComoResolver, StringComparison.OrdinalIgnoreCase);
            Assert.Null(recebendo.Codigo);
            Assert.Null(jaRecebeu.Codigo);
        }

        [Fact]
        public void Erro_de_registro_gravado_prevalece_e_volta_com_codigo_e_como_resolver()
        {
            var agora = DateTime.UtcNow;
            var guardado = CanalWebhookService.GuardarErro(WhatsappErros.WabaNaoEncontrada());

            var visao = CanalWebhookService.MontarVisao(Canal(WebhookEstado.Pendente, agora.AddHours(-1), erro: guardado), "https://x/wa/webhook/k", "vt", agora);

            Assert.Equal(WhatsappCodigos.WabaInvalida, visao.Codigo);
            Assert.Equal("wabaId", visao.Campo);
            Assert.False(string.IsNullOrWhiteSpace(visao.ComoResolver));
            Assert.Equal("https://x/wa/webhook/k", visao.Url);
            Assert.Equal("vt", visao.VerifyToken);
        }

        [Fact]
        public void Canal_que_nao_e_proprio_nao_tem_webhook()
        {
            var visao = CanalWebhookService.MontarVisao(new CanalWhatsapp { Origem = OrigemCanal.Zippy }, "u", "v", DateTime.UtcNow);

            Assert.Equal(WebhookEstado.NaoAplicavel, visao.Estado);
            Assert.Null(visao.Url);
            Assert.Null(visao.VerifyToken);
        }
    }

    public class WebhookRegistradorTests
    {
        private sealed class Handler : HttpMessageHandler
        {
            public List<(HttpRequestMessage Pedido, string Corpo)> Chamadas { get; } = new();
            public Func<HttpRequestMessage, (HttpStatusCode, string)> Resposta { get; set; } = _ => (HttpStatusCode.OK, @"{""success"":true}");

            protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            {
                var corpo = request.Content == null ? string.Empty : await request.Content.ReadAsStringAsync(cancellationToken);
                Chamadas.Add((request, corpo));
                var (status, texto) = Resposta(request);
                return new HttpResponseMessage(status) { Content = new StringContent(texto, Encoding.UTF8, "application/json") };
            }
        }

        private sealed class Fabrica : IHttpClientFactory
        {
            private readonly HttpMessageHandler _handler;
            public Fabrica(HttpMessageHandler handler) => _handler = handler;
            public HttpClient CreateClient(string name) => new(_handler, disposeHandler: false);
        }

        private static MetaWebhookRegistrador Build(Handler h) =>
            new(new Fabrica(h), WaTeste.Config(), NullLogger<MetaWebhookRegistrador>.Instance);

        [Fact]
        public async Task Registra_no_app_com_token_de_app_no_corpo_e_liga_a_WABA_com_o_token_do_cliente()
        {
            var h = new Handler();

            var r = await Build(h).RegistrarAsync(WaTeste.AppId, WaTeste.AppSecret, WaTeste.Waba, WaTeste.Token, "https://api.zippy.test/wa/webhook/k", "verify-x");

            Assert.True(r.Ok);
            Assert.Equal(2, h.Chamadas.Count);

            var (assinatura, corpoA) = h.Chamadas[0];
            Assert.Equal(HttpMethod.Post, assinatura.Method);
            Assert.EndsWith($"/{WaTeste.AppId}/subscriptions", assinatura.RequestUri!.AbsolutePath);
            Assert.DoesNotContain(WaTeste.AppSecret, assinatura.RequestUri.ToString());   // segredo nunca na URL
            var campos = System.Web.HttpUtility.ParseQueryString(corpoA);
            Assert.Equal("whatsapp_business_account", campos["object"]);
            Assert.Equal("messages", campos["fields"]);
            Assert.Equal("https://api.zippy.test/wa/webhook/k", campos["callback_url"]);
            Assert.Equal("verify-x", campos["verify_token"]);
            Assert.Equal($"{WaTeste.AppId}|{WaTeste.AppSecret}", campos["access_token"]);
            Assert.Null(assinatura.Headers.Authorization);

            var (conta, _) = h.Chamadas[1];
            Assert.Equal(HttpMethod.Post, conta.Method);
            Assert.EndsWith($"/{WaTeste.Waba}/subscribed_apps", conta.RequestUri!.AbsolutePath);
            Assert.Equal(WaTeste.Token, conta.Headers.Authorization!.Parameter);
        }

        [Fact]
        public async Task Segredo_errado_no_primeiro_passo_e_explicado_e_o_segundo_ainda_e_tentado()
        {
            var h = new Handler
            {
                Resposta = req => req.RequestUri!.AbsolutePath.EndsWith("/subscriptions")
                    ? (HttpStatusCode.BadRequest, @"{""error"":{""message"":""Invalid OAuth access token - Signature mismatch"",""code"":190}}")
                    : (HttpStatusCode.OK, @"{""success"":true}")
            };

            var r = await Build(h).RegistrarAsync(WaTeste.AppId, "segredo-errado-0000000000", WaTeste.Waba, WaTeste.Token, "https://x/wa/webhook/k", "v");

            Assert.False(r.Ok);
            Assert.Equal(WhatsappCodigos.AppSecretInvalido, r.Erro!.Codigo);
            Assert.Equal(2, h.Chamadas.Count);
        }

        [Fact]
        public async Task WABA_inexistente_e_token_sem_permissao_sao_explicados()
        {
            var waba = new Handler { Resposta = req => req.RequestUri!.AbsolutePath.EndsWith("/subscribed_apps")
                ? (HttpStatusCode.BadRequest, @"{""error"":{""message"":""Unsupported post request"",""code"":100,""error_subcode"":33}}")
                : (HttpStatusCode.OK, "{}") };
            var permissao = new Handler { Resposta = req => req.RequestUri!.AbsolutePath.EndsWith("/subscribed_apps")
                ? (HttpStatusCode.Forbidden, @"{""error"":{""message"":""(#200) permissions"",""code"":200}}")
                : (HttpStatusCode.OK, "{}") };

            var r1 = await Build(waba).RegistrarAsync(WaTeste.AppId, WaTeste.AppSecret, WaTeste.Waba, WaTeste.Token, "https://x/wa/webhook/k", "v");
            var r2 = await Build(permissao).RegistrarAsync(WaTeste.AppId, WaTeste.AppSecret, WaTeste.Waba, WaTeste.Token, "https://x/wa/webhook/k", "v");

            Assert.Equal(WhatsappCodigos.WabaInvalida, r1.Erro!.Codigo);
            Assert.Equal("wabaId", r1.Erro.Campo);
            Assert.Equal(WhatsappCodigos.SemPermissao, r2.Erro!.Codigo);
        }

        [Fact]
        public async Task Meta_fora_do_ar_nao_estoura_excecao()
        {
            var h = new Handler { Resposta = _ => throw new HttpRequestException("sem rede") };

            var r = await Build(h).RegistrarAsync(WaTeste.AppId, WaTeste.AppSecret, WaTeste.Waba, WaTeste.Token, "https://x/wa/webhook/k", "v");

            Assert.False(r.Ok);
            Assert.Equal(WhatsappCodigos.MetaIndisponivel, r.Erro!.Codigo);
        }
    }

    public class CanalOrigemMigrationContractTests
    {
        private static string Ler(params string[] caminho)
        {
            var path = System.IO.Path.Combine(new[] { AppContext.BaseDirectory, "Migrations", "Delivery" }.Concat(caminho).ToArray());
            Assert.True(System.IO.File.Exists(path), $"Migration ausente no output de teste: {path}");
            return System.IO.File.ReadAllText(path);
        }

        [Fact]
        public void Migration_e_idempotente_aditiva_e_registra_no_ledger()
        {
            var sql = Ler("20261025_01_canal_origem_proprio.sql");

            foreach (var coluna in new[]
            {
                "origem TEXT NOT NULL DEFAULT 'zippy'", "waba_id TEXT NULL", "app_id TEXT NULL", "app_secret_cifrado TEXT NULL", "webhook_chave TEXT NULL",
                "verify_token_cifrado TEXT NULL", "webhook_estado TEXT NOT NULL DEFAULT 'nao_aplicavel'", "webhook_erro TEXT NULL"
            })
            {
                Assert.Contains($"ADD COLUMN IF NOT EXISTS {coluna}", sql, StringComparison.Ordinal);
            }

            Assert.Contains("CHECK (origem IN ('zippy', 'proprio', 'central'))", sql, StringComparison.Ordinal);
            Assert.Contains("'nao_aplicavel', 'pendente', 'registrado', 'recebendo', 'erro'", sql, StringComparison.Ordinal);
            Assert.Contains("length(webhook_chave) >= 32", sql, StringComparison.Ordinal);
            Assert.Contains("CREATE UNIQUE INDEX IF NOT EXISTS ux_canal_whatsapp_webhook_chave", sql, StringComparison.Ordinal);
            Assert.Contains("UPDATE canal_whatsapp SET origem = 'central' WHERE central", sql, StringComparison.Ordinal);

            // Cada constraint so e criada se ainda nao existe (rodar duas vezes nao quebra).
            var adds = System.Text.RegularExpressions.Regex.Matches(sql, "ADD CONSTRAINT (\\w+)").Select(m => m.Groups[1].Value).ToList();
            Assert.Equal(4, adds.Count);
            Assert.All(adds, nome => Assert.Contains($"conname = '{nome}'", sql, StringComparison.Ordinal));

            foreach (var destrutivo in new[] { "DROP TABLE", "DROP COLUMN", "DROP INDEX", "TRUNCATE", "DELETE FROM" })
            {
                Assert.DoesNotContain(destrutivo, sql, StringComparison.OrdinalIgnoreCase);
            }

            Assert.Contains("INSERT INTO delivery_tracking_schema_versions (version)", sql, StringComparison.Ordinal);
            Assert.Contains("'20261025_01_canal_origem_proprio'", sql, StringComparison.Ordinal);
            Assert.Contains("ON CONFLICT (version) DO NOTHING", sql, StringComparison.Ordinal);
        }

        [Fact]
        public void Verify_confere_colunas_constraints_indice_e_backfill()
        {
            var sql = Ler("20261025_02_canal_origem_proprio_verify.sql");

            foreach (var coluna in new[] { "origem", "waba_id", "app_id", "app_secret_cifrado", "webhook_chave", "verify_token_cifrado", "webhook_estado", "webhook_erro" })
            {
                Assert.Contains($"'{coluna}'", sql, StringComparison.Ordinal);
            }

            Assert.Contains("ck_canal_whatsapp_origem", sql, StringComparison.Ordinal);
            Assert.Contains("ux_canal_whatsapp_webhook_chave", sql, StringComparison.Ordinal);
            Assert.Contains("WHERE central AND origem <> 'central'", sql, StringComparison.Ordinal);
        }

        [Fact]
        public void Rollback_so_roda_vazio_e_limpa_o_ledger()
        {
            var sql = Ler("rollback", "20261025_01_canal_origem_proprio.down.sql");

            var recusa = sql.IndexOf("WHERE origem = 'proprio'", StringComparison.Ordinal);
            var primeiroDrop = sql.IndexOf("DROP ", StringComparison.Ordinal);
            Assert.True(recusa >= 0 && recusa < primeiroDrop, "A checagem de numero proprio em uso precisa vir antes de qualquer DROP.");
            Assert.Contains("RAISE EXCEPTION", sql, StringComparison.Ordinal);
            Assert.Contains("DROP COLUMN IF EXISTS origem", sql, StringComparison.Ordinal);
            Assert.Contains("DROP INDEX IF EXISTS ux_canal_whatsapp_webhook_chave", sql, StringComparison.Ordinal);
            Assert.Contains("DELETE FROM delivery_tracking_schema_versions WHERE version = '20261025_01_canal_origem_proprio'", sql, StringComparison.Ordinal);
            Assert.DoesNotContain("DROP TABLE", sql, StringComparison.OrdinalIgnoreCase);
        }
    }
}
