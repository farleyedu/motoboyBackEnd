using System;
using System.Collections.Generic;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using APIBack.Atendimento;
using APIBack.Automation.Controllers;
using APIBack.Automation.Dtos;
using APIBack.Automation.Infra;
using APIBack.Automation.Interfaces;
using APIBack.Automation.Services;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Xunit;

namespace APIBack.Tests.Unit
{
    public class WebhookAssinaturaTests
    {
        private const string Segredo = "segredo-do-app";

        private static WebhookSignatureValidator Build(string? segredo, bool strict = false) =>
            new(Microsoft.Extensions.Options.Options.Create(new AutomationOptions { StrictSignatureValidation = strict, Meta = new MetaOptions { AppSecret = segredo! } }));

        private static string Assinar(string segredo, string corpo) =>
            "sha256=" + Convert.ToHexString(HMACSHA256.HashData(Encoding.UTF8.GetBytes(segredo), Encoding.UTF8.GetBytes(corpo))).ToLowerInvariant();

        [Fact]
        public void Com_segredo_a_assinatura_e_sempre_exigida_mesmo_sem_o_modo_estrito()
        {
            var validador = Build(Segredo, strict: false);
            const string corpo = "{\"a\":1}";

            Assert.True(validador.ValidarXHubSignature256(Assinar(Segredo, corpo), corpo));
            Assert.False(validador.ValidarXHubSignature256(Assinar("outro", corpo), corpo));
            Assert.False(validador.ValidarXHubSignature256(Assinar(Segredo, corpo), corpo + " "));
            Assert.False(validador.ValidarXHubSignature256(null, corpo));
            Assert.False(validador.ValidarXHubSignature256("", corpo));
        }

        [Theory]
        [InlineData(null)]
        [InlineData("")]
        [InlineData("__SET_IN_ENV__")]
        [InlineData("<TODO>")]
        public void Segredo_ausente_ou_de_modelo_conta_como_nao_configurado(string? valor)
        {
            Assert.False(WebhookSignatureValidator.SegredoConfigurado(valor));
            Assert.True(Build(valor).ValidarXHubSignature256(null, "{}"));             // aceita (e grita no log)
            Assert.False(Build(valor, strict: true).ValidarXHubSignature256(null, "{}")); // modo estrito sem segredo recusa tudo
        }
    }

    public class WaWebhookControllerTests
    {
        private sealed class Fixture
        {
            public Mock<IWebhookSignatureValidator> Assinatura { get; } = new();
            public Mock<IWaEventoRepository> Eventos { get; } = new();
            public List<WaEvento> Gravados { get; } = new();
            public string VerifyToken { get; set; } = "token-de-verificacao";

            public Fixture()
            {
                Assinatura.Setup(a => a.ValidarXHubSignature256(It.IsAny<string?>(), It.IsAny<string>())).Returns(true);
                Eventos.Setup(e => e.RegistrarAsync(It.IsAny<WaEvento>()))
                    .Callback<WaEvento>(e => Gravados.Add(e))
                    .ReturnsAsync((WaEvento e) => Gravados.Count(g => g.Tipo == e.Tipo && g.Chave == e.Chave) == 1);
            }

            public WaWebhookController Build(string corpo)
            {
                var controller = new WaWebhookController(
                    NullLogger<WaWebhookController>.Instance,
                    new WebhookValidatorService(Assinatura.Object, NullLogger<WebhookValidatorService>.Instance),
                    Eventos.Object,
                    Microsoft.Extensions.Options.Options.Create(new AutomationOptions { VerifyToken = VerifyToken }));
                var contexto = new DefaultHttpContext();
                contexto.Request.Body = new System.IO.MemoryStream(Encoding.UTF8.GetBytes(corpo));
                controller.ControllerContext = new ControllerContext { HttpContext = contexto };
                return controller;
            }
        }

        private const string Mensagem = @"{""object"":""whatsapp_business_account"",""entry"":[{""changes"":[{""field"":""messages"",""value"":{
            ""metadata"":{""display_phone_number"":""5534991480112"",""phone_number_id"":""821317791056700""},
            ""contacts"":[{""wa_id"":""5534999990000"",""profile"":{""name"":""Maria""}}],
            ""messages"":[{""id"":""wamid.AAA"",""from"":""5534999990000"",""timestamp"":""1790980926"",""type"":""text"",""text"":{""body"":""oi""}}]}}]}]}";

        private const string Recibo = @"{""entry"":[{""changes"":[{""value"":{
            ""metadata"":{""phone_number_id"":""821317791056700""},
            ""statuses"":[{""id"":""wamid.BBB"",""status"":""delivered"",""recipient_id"":""5534999990000""},
                          {""id"":""wamid.BBB"",""status"":""read"",""recipient_id"":""5534999990000""}]}}]}]}";

        [Fact]
        public async Task A_mensagem_e_gravada_na_fila_antes_de_responder_com_o_numero_que_a_recebeu()
        {
            var f = new Fixture();

            var resposta = await f.Build(Mensagem).Webhook();

            Assert.IsType<OkResult>(resposta);
            var evento = Assert.Single(f.Gravados);
            Assert.Equal("mensagem", evento.Tipo);
            Assert.Equal("wamid.AAA", evento.Chave);
            Assert.Equal("821317791056700", evento.PhoneNumberId);
            Assert.Contains("\"oi\"", evento.PayloadJson);
        }

        [Fact]
        public async Task Cada_recibo_vira_um_evento_proprio_e_o_reenvio_da_Meta_nao_duplica()
        {
            var f = new Fixture();

            await f.Build(Recibo).Webhook();
            await f.Build(Recibo).Webhook();

            Assert.Equal(new[] { "wamid.BBB:delivered", "wamid.BBB:read" }, f.Gravados.Select(g => g.Chave).Distinct().OrderBy(c => c).ToArray());
            Assert.All(f.Gravados, g => Assert.Equal("status", g.Tipo));
        }

        [Fact]
        public async Task Assinatura_invalida_e_recusada_com_403_e_nada_e_gravado()
        {
            var f = new Fixture();
            f.Assinatura.Setup(a => a.ValidarXHubSignature256(It.IsAny<string?>(), It.IsAny<string>())).Returns(false);

            var resposta = await f.Build(Mensagem).Webhook();

            Assert.Equal(403, ((StatusCodeResult)resposta).StatusCode);
            Assert.Empty(f.Gravados);
        }

        [Fact]
        public async Task Se_nao_deu_para_gravar_responde_500_para_a_Meta_reenviar()
        {
            var f = new Fixture();
            f.Eventos.Setup(e => e.RegistrarAsync(It.IsAny<WaEvento>())).ThrowsAsync(new InvalidOperationException("banco fora"));

            var resposta = await f.Build(Mensagem).Webhook();

            Assert.Equal(500, ((StatusCodeResult)resposta).StatusCode);
        }

        [Fact]
        public async Task Corpo_que_nao_e_json_nao_derruba_nada()
        {
            var f = new Fixture();

            Assert.IsType<OkResult>(await f.Build("isto nao e json").Webhook());
            Assert.Empty(f.Gravados);
        }

        [Fact]
        public void A_verificacao_so_vale_com_o_token_configurado_e_nunca_com_valor_padrao()
        {
            var f = new Fixture();
            var controller = f.Build("{}");

            Assert.Equal("desafio", ((OkObjectResult)controller.VerifyWebhook("subscribe", "token-de-verificacao", "desafio")).Value);
            Assert.Equal(403, ((ObjectResult)controller.VerifyWebhook("subscribe", "errado", "desafio")).StatusCode);

            f.VerifyToken = "<TODO>";
            Assert.Equal(403, ((ObjectResult)f.Build("{}").VerifyWebhook("subscribe", "<TODO>", "desafio")).StatusCode);
            f.VerifyToken = "__SET_IN_ENV__";
            Assert.Equal(403, ((ObjectResult)f.Build("{}").VerifyWebhook("subscribe", "zippygo123", "desafio")).StatusCode);
        }
    }

    public class WaEventoProcessorTests
    {
        private static readonly Guid Loja = Guid.NewGuid();

        private sealed class Fixture
        {
            public Mock<ICanalRepository> Canais { get; } = new();
            public Mock<IMessageService> Mensagens { get; } = new();
            public Mock<IPipelineDeMensagem> Pipeline { get; } = new();
            public CanalWhatsapp Canal { get; } = new() { Id = Guid.NewGuid(), IdEstabelecimento = Loja, PhoneNumberId = "821317791056700", ModoAtendimento = "hibrido" };

            public WaEventoProcessor Build() =>
                new(Canais.Object, Mensagens.Object, Pipeline.Object, NullLogger<WaEventoProcessor>.Instance);
        }

        private static WaEvento Mensagem(string phoneNumberId = "821317791056700") => new()
        {
            Id = Guid.NewGuid(), Tipo = "mensagem", Chave = "wamid.AAA", PhoneNumberId = phoneNumberId,
            PayloadJson = $@"{{""metadata"":{{""display_phone_number"":""5534991480112"",""phone_number_id"":""{phoneNumberId}""}},
                ""contacts"":[{{""wa_id"":""5534999990000""}}],
                ""message"":{{""id"":""wamid.AAA"",""from"":""5534999990000"",""timestamp"":""1790980926"",""type"":""text"",""text"":{{""body"":""quero o cardapio""}}}}}}"
        };

        private static WaEvento Recibo(string status, int tentativas = 1, string extra = "") => new()
        {
            Id = Guid.NewGuid(), Tipo = "status", Chave = $"wamid.BBB:{status}", Tentativas = tentativas,
            PayloadJson = $@"{{""metadata"":{{}},""status"":{{""id"":""wamid.BBB"",""status"":""{status}""{extra}}}}}"
        };

        [Fact]
        public async Task Mensagem_de_numero_desconhecido_e_ignorada_com_o_motivo_e_nunca_chega_ao_atendimento()
        {
            var f = new Fixture();

            var resultado = await f.Build().ProcessarAsync(Mensagem("111111111111111"));

            Assert.Equal("ignorado", resultado.Resultado);
            Assert.Contains("canal desconhecido", resultado.Motivo);
            Assert.Contains("111111111111111", resultado.Motivo);
            f.Pipeline.Verify(p => p.ExecutarAsync(It.IsAny<ConversationProcessingInput>(), It.IsAny<CanalWhatsapp>()), Times.Never);
        }

        [Fact]
        public async Task Mensagem_de_numero_conhecido_vai_ao_atendimento_com_a_loja_e_o_canal_ja_resolvidos()
        {
            var f = new Fixture();
            f.Canais.Setup(c => c.ObterAtivoPorPhoneNumberIdAsync("821317791056700")).ReturnsAsync(f.Canal);
            ConversationProcessingInput? recebido = null;
            f.Pipeline.Setup(p => p.ExecutarAsync(It.IsAny<ConversationProcessingInput>(), f.Canal))
                .Callback<ConversationProcessingInput, CanalWhatsapp>((i, _) => recebido = i).Returns(Task.CompletedTask);

            var resultado = await f.Build().ProcessarAsync(Mensagem());

            Assert.Equal("processado", resultado.Resultado);
            Assert.Equal(Loja, recebido!.IdEstabelecimento);
            Assert.Equal(f.Canal.Id, recebido.IdCanal);
            Assert.Equal("quero o cardapio", recebido.Texto);
            Assert.Equal("5534999990000", recebido.Mensagem.De);
            f.Canais.Verify(c => c.MarcarRecebimentoAsync(f.Canal.Id), Times.Once);
        }

        [Fact]
        public async Task Tipo_que_nao_e_texto_chega_ao_atendente_como_aviso_e_nao_some()
        {
            var texto = WaMensagemTexto.Exibicao(new WebhookMessageDto { Tipo = "image" });

            Assert.Contains("image", texto);
            Assert.Null(WaMensagemTexto.Interpretado(new WebhookMessageDto { Tipo = "image" }));
            Assert.Equal("btn_menu", WaMensagemTexto.Interpretado(new WebhookMessageDto
            {
                Interactive = new WebhookInteractiveDto { ButtonReply = new WebhookButtonReplyDto { Id = "btn_menu", Title = "Menu" } }
            }));
            await Task.CompletedTask;
        }

        [Theory]
        [InlineData("delivered", "entregue")]
        [InlineData("read", "lida")]
        public async Task Recibo_atualiza_a_mensagem_enviada_pelo_id_da_Meta(string status, string _)
        {
            var f = new Fixture();
            f.Mensagens.Setup(m => m.AtualizarStatusPorProvedorAsync("wamid.BBB", status, null, null)).ReturnsAsync(true);

            var resultado = await f.Build().ProcessarAsync(Recibo(status));

            Assert.Equal("processado", resultado.Resultado);
        }

        [Fact]
        public async Task Recibo_de_falha_leva_o_codigo_e_o_motivo_da_Meta()
        {
            var f = new Fixture();
            f.Mensagens.Setup(m => m.AtualizarStatusPorProvedorAsync("wamid.BBB", "failed", "131047", "Re-engagement message: janela fechada")).ReturnsAsync(true);

            var resultado = await f.Build().ProcessarAsync(Recibo("failed",
                extra: @",""errors"":[{""code"":131047,""title"":""Re-engagement message"",""error_data"":{""details"":""janela fechada""}}]"));

            Assert.Equal("processado", resultado.Resultado);
            f.Mensagens.VerifyAll();
        }

        [Fact]
        public async Task Recibo_que_chega_antes_de_a_mensagem_ser_vinculada_tenta_de_novo_e_depois_desiste_sem_erro()
        {
            var f = new Fixture();
            f.Mensagens.Setup(m => m.AtualizarStatusPorProvedorAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string?>(), It.IsAny<string?>())).ReturnsAsync(false);
            var processador = f.Build();

            Assert.Equal("tentar_de_novo", (await processador.ProcessarAsync(Recibo("sent", tentativas: 1))).Resultado);
            Assert.Equal("ignorado", (await processador.ProcessarAsync(Recibo("sent", tentativas: 4))).Resultado);
        }
    }

    public class WaEventoWorkerTests
    {
        private static async Task<WaEventoWorker> RodarAsync(Mock<IWaEventoRepository> fila, Mock<IWaEventoProcessor> processador, Func<bool> terminou)
        {
            var services = new ServiceCollection();
            services.AddScoped(_ => fila.Object);
            services.AddScoped(_ => processador.Object);
            var worker = new WaEventoWorker(services.BuildServiceProvider().GetRequiredService<IServiceScopeFactory>(), NullLogger<WaEventoWorker>.Instance);

            await worker.StartAsync(CancellationToken.None);
            for (var i = 0; i < 100 && !terminou(); i++) await Task.Delay(30);
            await worker.StopAsync(CancellationToken.None);
            return worker;
        }

        private static WaEvento Evento(int tentativas) => new() { Id = Guid.NewGuid(), Tipo = "mensagem", Chave = "wamid.X", Tentativas = tentativas };

        [Fact]
        public async Task Evento_processado_e_marcado_e_o_ignorado_guarda_o_motivo()
        {
            var ok = Evento(1);
            var ignorado = Evento(1);
            var fila = new Mock<IWaEventoRepository>();
            var entregue = false;
            fila.Setup(f => f.ReivindicarAsync(It.IsAny<int>())).ReturnsAsync(() =>
            {
                if (entregue) return Array.Empty<WaEvento>();
                entregue = true;
                return new[] { ok, ignorado };
            });
            var processador = new Mock<IWaEventoProcessor>();
            processador.Setup(p => p.ProcessarAsync(ok)).ReturnsAsync(ResultadoEvento.Processado());
            processador.Setup(p => p.ProcessarAsync(ignorado)).ReturnsAsync(ResultadoEvento.Ignorado("canal desconhecido"));

            await RodarAsync(fila, processador, () => fila.Invocations.Any(i => i.Method.Name == "MarcarIgnoradoAsync"));

            fila.Verify(f => f.MarcarProcessadoAsync(ok.Id), Times.Once);
            fila.Verify(f => f.MarcarIgnoradoAsync(ignorado.Id, "canal desconhecido"), Times.Once);
        }

        [Fact]
        public async Task Falha_volta_para_a_fila_com_espera_crescente_e_na_quinta_tentativa_vira_erro()
        {
            var primeira = Evento(1);
            var ultima = Evento(5);
            var fila = new Mock<IWaEventoRepository>();
            var entregue = false;
            fila.Setup(f => f.ReivindicarAsync(It.IsAny<int>())).ReturnsAsync(() =>
            {
                if (entregue) return Array.Empty<WaEvento>();
                entregue = true;
                return new[] { primeira, ultima };
            });
            var processador = new Mock<IWaEventoProcessor>();
            processador.Setup(p => p.ProcessarAsync(It.IsAny<WaEvento>())).ThrowsAsync(new InvalidOperationException("banco"));

            await RodarAsync(fila, processador, () => fila.Invocations.Count(i => i.Method.Name == "ReagendarAsync") == 2);

            fila.Verify(f => f.ReagendarAsync(primeira.Id, "banco", TimeSpan.FromSeconds(15), false), Times.Once);
            fila.Verify(f => f.ReagendarAsync(ultima.Id, "banco", It.IsAny<TimeSpan>(), true), Times.Once);
        }
    }

    public class WhatsAppEnvioTests
    {
        [Fact]
        public void O_id_da_mensagem_na_Meta_e_lido_da_resposta_de_envio()
        {
            Assert.Equal("wamid.XYZ", WhatsAppSender.ExtrairWamid(@"{""messaging_product"":""whatsapp"",""messages"":[{""id"":""wamid.XYZ""}]}"));
            Assert.Null(WhatsAppSender.ExtrairWamid("{}"));
            Assert.Null(WhatsAppSender.ExtrairWamid("nao e json"));
        }

        [Fact]
        public void O_erro_da_Meta_traz_codigo_subcodigo_e_motivo()
        {
            var (codigo, sub, mensagem) = WhatsAppSender.ExtrairErro(
                @"{""error"":{""message"":""Object with ID '34991480112' does not exist"",""code"":100,""error_subcode"":33}}");

            Assert.Equal(100, codigo);
            Assert.Equal(33, sub);
            Assert.Contains("does not exist", mensagem);
            Assert.Equal((null, null, null), WhatsAppSender.ExtrairErro("lixo"));
        }

        [Theory]
        [InlineData(100, 33, true)]    // ID do numero inexistente: o que aconteceu com o cadastro errado
        [InlineData(190, null, true)]  // token invalido
        [InlineData(10, null, true)]
        [InlineData(131047, null, false)] // janela de 24 h fechada: o canal esta bom
        [InlineData(null, null, false)]
        public void So_erro_do_proprio_canal_marca_o_numero_como_com_erro(int? codigo, int? sub, bool esperado) =>
            Assert.Equal(esperado, WhatsAppSender.ErroPermanenteDoCanal(codigo, sub));
    }

    public class CanalVerificadorTests
    {
        private static readonly CanalWhatsapp Canal = new() { Id = Guid.NewGuid(), PhoneNumberId = "821317791056700", NumeroE164 = "+5534991480112" };

        [Fact]
        public void O_id_da_Meta_do_mesmo_telefone_verifica_o_numero()
        {
            var r = CanalVerificador.Avaliar(Canal, 200, @"{""display_phone_number"":""+55 34 99148-0112"",""verified_name"":""Zippy""}");

            Assert.True(r.Ok);
            Assert.Equal("Zippy", r.NomeVerificado);
        }

        [Fact]
        public void Id_de_outro_telefone_e_recusado_dizendo_os_dois_numeros()
        {
            var r = CanalVerificador.Avaliar(Canal, 200, @"{""display_phone_number"":""+55 34 99821-8242""}");

            Assert.False(r.Ok);
            Assert.Contains("+55 34 99821-8242", r.Mensagem);
            Assert.Contains("+5534991480112", r.Mensagem);
        }

        [Theory]
        [InlineData(400, @"{""error"":{""code"":100,""error_subcode"":33,""message"":""x""}}", "nao conhece este Phone Number ID")]
        [InlineData(401, @"{""error"":{""code"":190,""message"":""x""}}", "token")]
        [InlineData(500, @"{""error"":{""code"":1,""message"":""instavel""}}", "instavel")]
        [InlineData(502, "<html>", "Resposta inesperada")]
        public void Erros_da_Meta_viram_mensagem_clara(int http, string corpo, string trecho)
        {
            var r = CanalVerificador.Avaliar(Canal, http, corpo);

            Assert.False(r.Ok);
            Assert.Contains(trecho, r.Mensagem);
        }
    }
}
