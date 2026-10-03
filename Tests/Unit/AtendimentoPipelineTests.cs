using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using APIBack.Atendimento;
using APIBack.Atendimento.Motor;
using APIBack.Automation.Dtos;
using APIBack.Automation.Interfaces;
using APIBack.Automation.Models;
using APIBack.Automation.Services;
using APIBack.DTOs.Atendimento;
using APIBack.Repository.Interface;
using APIBack.Service;
using APIBack.Service.Interface;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Xunit;

namespace APIBack.Tests.Unit
{
    public class PipelineDeMensagemTests
    {
        private static readonly Guid Loja = Guid.NewGuid();
        private static readonly Guid Conversa = Guid.NewGuid();

        private sealed class Fixture
        {
            public Mock<IIngressoDeConversa> Ingresso { get; } = new();
            public Mock<IConversationRepository> Conversas { get; } = new();
            public Mock<IExecutorDeAtendimento> Executor { get; } = new();
            public Mock<IEnviadorDeRespostas> Enviador { get; } = new();
            public Mock<ICardapioPedidoWebService> PedidosWeb { get; } = new();
            public CanalWhatsapp Canal { get; } = new()
            {
                Id = Guid.NewGuid(), IdEstabelecimento = Loja, PhoneNumberId = "821317791056700", NumeroE164 = "+5534991480112",
                ModoAtendimento = ModoAtendimento.Hibrido, Servicos = new List<string> { "cardapio_web" }
            };
            public ConversationIngressResult Ingressou { get; set; } = new(new Message { IdConversa = Conversa }, false, null, false, true);

            public Fixture()
            {
                Ingresso.Setup(i => i.AcrescentarEntradaAsync(
                        It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string?>(), It.IsAny<DateTime?>(),
                        It.IsAny<string?>(), It.IsAny<string?>(), It.IsAny<Guid?>(), It.IsAny<Guid?>()))
                    .ReturnsAsync(() => Ingressou);
                Conversas.Setup(c => c.ObterControleConversaAsync(Conversa, null)).ReturnsAsync(new ConversationControlDto { ConversationId = Conversa, CanBotReply = true });
                PedidosWeb.Setup(p => p.TentarConfirmarPorMensagemAsync(It.IsAny<Guid>(), It.IsAny<string?>())).ReturnsAsync(false);
            }

            public PipelineDeMensagem Build() =>
                new(Ingresso.Object, Conversas.Object, Executor.Object, Enviador.Object, NullLogger<PipelineDeMensagem>.Instance, PedidosWeb.Object);
        }

        private static ConversationProcessingInput Input(string texto = "oi", string de = "5534999990000") => new(
            new WebhookMessageDto { Id = "wamid.AAA", De = de, Tipo = "text" }, texto, "5534991480112", "821317791056700", DateTime.UtcNow,
            new WebhookChangeValueDto(), texto);

        [Fact]
        public async Task Mensagem_normal_chega_ao_motor_com_o_canal_e_marca_a_primeira_mensagem_da_conversa()
        {
            var f = new Fixture();

            await f.Build().ExecutarAsync(Input("oi"), f.Canal);

            f.Executor.Verify(e => e.ExecutarAsync(It.Is<MensagemRecebida>(m =>
                m.ConversaId == Conversa && m.Canal == f.Canal && m.Texto == "oi" && m.PrimeiraMensagem && m.Telefone == "+5534999990000")), Times.Once);
        }

        [Fact]
        public async Task Conversa_que_ja_existia_nao_e_primeira_mensagem()
        {
            var f = new Fixture { Ingressou = new ConversationIngressResult(new Message { IdConversa = Conversa }, false, null, false, false) };

            await f.Build().ExecutarAsync(Input(), f.Canal);

            f.Executor.Verify(e => e.ExecutarAsync(It.Is<MensagemRecebida>(m => !m.PrimeiraMensagem)), Times.Once);
        }

        [Fact]
        public async Task Conversa_reiniciada_por_expiracao_volta_a_ser_tratada_como_primeira_mensagem()
        {
            var f = new Fixture { Ingressou = new ConversationIngressResult(new Message { IdConversa = Conversa }, true, null, false, false) };

            await f.Build().ExecutarAsync(Input(), f.Canal);

            f.Executor.Verify(e => e.ExecutarAsync(It.Is<MensagemRecebida>(m => m.PrimeiraMensagem)), Times.Once);
        }

        [Fact]
        public async Task Mensagem_duplicada_ou_de_empresa_desativada_nao_vai_a_lugar_nenhum()
        {
            var f = new Fixture { Ingressou = null! };

            await f.Build().ExecutarAsync(Input(), f.Canal);

            f.Executor.VerifyNoOtherCalls();
            f.Enviador.VerifyNoOtherCalls();
        }

        [Theory]
        [InlineData("5534991480112")]  // o proprio numero de exibicao
        [InlineData("821317791056700")] // o phone_number_id
        public async Task Eco_do_proprio_numero_e_ignorado_antes_de_gravar(string de)
        {
            var f = new Fixture();

            await f.Build().ExecutarAsync(Input(de: de), f.Canal);

            f.Ingresso.VerifyNoOtherCalls();
            f.Executor.VerifyNoOtherCalls();
        }

        [Fact]
        public async Task Codigo_do_cardapio_confirma_o_pedido_e_o_bot_nao_responde()
        {
            var f = new Fixture();
            f.PedidosWeb.Setup(p => p.TentarConfirmarPorMensagemAsync(Conversa, "4821")).ReturnsAsync(true);

            await f.Build().ExecutarAsync(Input("4821"), f.Canal);

            f.Executor.VerifyNoOtherCalls();
        }

        [Fact]
        public async Task Falha_na_confirmacao_do_cardapio_nao_perde_a_mensagem_e_ela_segue_para_o_atendimento()
        {
            var f = new Fixture();
            f.PedidosWeb.Setup(p => p.TentarConfirmarPorMensagemAsync(It.IsAny<Guid>(), It.IsAny<string?>())).ThrowsAsync(new InvalidOperationException("banco"));

            await f.Build().ExecutarAsync(Input("4821"), f.Canal);

            f.Executor.Verify(e => e.ExecutarAsync(It.IsAny<MensagemRecebida>()), Times.Once);
        }

        [Fact]
        public async Task Numero_em_modo_humano_grava_a_mensagem_mas_o_bot_fica_quieto()
        {
            var f = new Fixture();
            f.Canal.ModoAtendimento = ModoAtendimento.Humano;

            await f.Build().ExecutarAsync(Input(), f.Canal);

            f.Ingresso.Verify(i => i.AcrescentarEntradaAsync(
                It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string?>(), It.IsAny<DateTime?>(),
                It.IsAny<string?>(), It.IsAny<string?>(), Loja, f.Canal.Id), Times.Once);
            f.Executor.VerifyNoOtherCalls();
        }

        [Fact]
        public async Task Conversa_que_um_atendente_assumiu_nao_recebe_resposta_do_bot()
        {
            var f = new Fixture();
            f.Conversas.Setup(c => c.ObterControleConversaAsync(Conversa, null))
                .ReturnsAsync(new ConversationControlDto { ConversationId = Conversa, CanBotReply = false, Status = "em_andamento", AssignedAgentId = 7 });

            await f.Build().ExecutarAsync(Input(), f.Canal);

            f.Executor.VerifyNoOtherCalls();
        }

        [Fact]
        public async Task Empresa_pausada_recebe_so_o_aviso_de_instabilidade()
        {
            var f = new Fixture { Ingressou = new ConversationIngressResult(new Message { IdConversa = Conversa }, false, null, true, false) };

            await f.Build().ExecutarAsync(Input(), f.Canal);

            f.Enviador.Verify(e => e.EnviarAsync(Conversa, f.Canal, "+5534999990000", It.Is<AcaoResponder>(a => a.Mensagem.Contains("instabilidade"))), Times.Once);
            f.Executor.VerifyNoOtherCalls();
        }
    }

    public class ExecutorDeAtendimentoTests
    {
        private static readonly Guid Loja = Guid.NewGuid();
        private static readonly Guid Conversa = Guid.NewGuid();

        private sealed class Fixture
        {
            public Mock<IFluxoEstadoRepository> Estados { get; } = new();
            public Mock<IEnviadorDeRespostas> Enviador { get; } = new();
            public Mock<IEstabelecimentoRepository> Lojas { get; } = new();
            public Mock<IAtendimentoRepository> Config { get; } = new();
            public Mock<IConversationRepository> Conversas { get; } = new();
            public Mock<IChatRealtimePublisher> TempoReal { get; } = new();
            public List<AcaoMotor> Enviadas { get; } = new();
            public string? BaseUrl { get; set; } = "https://app.zippygo.com.br/";
            public CanalWhatsapp Canal { get; } = new()
            {
                Id = Guid.NewGuid(), IdEstabelecimento = Loja, PhoneNumberId = "821317791056700", NumeroE164 = "+5534991480112",
                ModoAtendimento = ModoAtendimento.Hibrido, Servicos = new List<string> { "cardapio_web" }
            };

            public Fixture()
            {
                Lojas.Setup(l => l.ObterNomeFantasiaAsync(Loja)).ReturnsAsync("Pizza Bom Centro");
                Config.Setup(c => c.GetConfigAsync(Loja)).ReturnsAsync(new AtendimentoConfigDto { EstabelecimentoId = Loja });
                Enviador.Setup(e => e.EnviarAsync(It.IsAny<Guid>(), It.IsAny<CanalWhatsapp>(), It.IsAny<string>(), It.IsAny<AcaoMotor>()))
                    .Callback<Guid, CanalWhatsapp, string, AcaoMotor>((_, _, _, a) => Enviadas.Add(a)).ReturnsAsync(true);
                Conversas.Setup(c => c.AtualizarStatusAtendimentoAsync(It.IsAny<Guid>(), It.IsAny<string>(), It.IsAny<int?>(), It.IsAny<string?>(), It.IsAny<Guid?>())).ReturnsAsync(true);
            }

            public ExecutorDeAtendimento Build() => new(
                new AtendimentoMotor(new IFluxoDeServico[] { new FluxoCardapioWeb(), new FluxoDelivery(), new FluxoAgendamento() }),
                Estados.Object, Enviador.Object, Lojas.Object, Config.Object, Conversas.Object, TempoReal.Object,
                new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?> { ["Atendimento:CardapioBaseUrl"] = BaseUrl }).Build(),
                NullLogger<ExecutorDeAtendimento>.Instance);

            public MensagemRecebida Mensagem(string texto = "oi", bool primeira = true) =>
                new(Conversa, Canal, "+5534999990000", texto, texto, primeira, DateTime.UtcNow);
        }

        [Fact]
        public async Task Cliente_pede_o_cardapio_e_recebe_o_link_da_loja_com_a_base_configurada()
        {
            var f = new Fixture();

            await f.Build().ExecutarAsync(f.Mensagem());

            var resposta = Assert.IsType<AcaoResponder>(Assert.Single(f.Enviadas));
            Assert.Contains($"https://app.zippygo.com.br/cardapio/{Loja}", resposta.Mensagem);
            Assert.Contains("Pizza Bom Centro", resposta.Mensagem);
            f.Estados.Verify(e => e.SalvarAsync(Conversa, It.IsAny<EstadoFluxo>(), "cardapio_web"), Times.Once);
        }

        [Theory]
        [InlineData(null)]
        [InlineData("")]
        [InlineData("__SET_IN_ENV__")]
        public async Task Sem_a_url_base_configurada_nao_manda_link_quebrado_e_chama_a_equipe(string? baseUrl)
        {
            var f = new Fixture { BaseUrl = baseUrl };

            await f.Build().ExecutarAsync(f.Mensagem());

            Assert.DoesNotContain(f.Enviadas.OfType<AcaoResponder>(), r => r.Mensagem.Contains("http"));
            f.Conversas.Verify(c => c.AtualizarStatusAtendimentoAsync(Conversa, "aguardando_interno", null, null, Loja), Times.Once);
        }

        [Fact]
        public async Task Chamar_a_equipe_coloca_a_conversa_na_fila_humana_registra_o_evento_e_avisa_as_telas()
        {
            var f = new Fixture();

            await f.Build().ExecutarAsync(f.Mensagem("quero falar com um atendente", primeira: false));

            f.Conversas.Verify(c => c.AtualizarStatusAtendimentoAsync(Conversa, "aguardando_interno", null, null, Loja), Times.Once);
            f.Conversas.Verify(c => c.RegistrarEventoAsync(Conversa, It.Is<ConversationEventDto>(e => e.Source == "bot" && e.ToStatus == "aguardando_interno"), Loja), Times.Once);
            f.TempoReal.Verify(t => t.ConversaAtualizadaAsync(Conversa, Loja, "chamar_atendente"), Times.Once);
            Assert.Contains("equipe", f.Enviadas.OfType<AcaoResponder>().Single().Mensagem);
        }

        [Fact]
        public async Task Falha_ao_chamar_a_equipe_nao_derruba_o_atendimento_e_o_estado_ainda_e_salvo()
        {
            var f = new Fixture();
            f.Conversas.Setup(c => c.AtualizarStatusAtendimentoAsync(It.IsAny<Guid>(), It.IsAny<string>(), It.IsAny<int?>(), It.IsAny<string?>(), It.IsAny<Guid?>()))
                .ThrowsAsync(new InvalidOperationException("banco"));

            await f.Build().ExecutarAsync(f.Mensagem("atendente", primeira: false));

            f.Estados.Verify(e => e.SalvarAsync(Conversa, It.IsAny<EstadoFluxo>(), It.IsAny<string>()), Times.Once);
        }

        [Fact]
        public async Task Sem_a_configuracao_da_loja_usa_os_textos_padrao_e_responde_mesmo_assim()
        {
            var f = new Fixture();
            f.Config.Setup(c => c.GetConfigAsync(Loja)).ThrowsAsync(new InvalidOperationException("banco"));

            await f.Build().ExecutarAsync(f.Mensagem());

            Assert.NotEmpty(f.Enviadas);
        }

        [Fact]
        public async Task Fora_do_horario_da_loja_o_cliente_recebe_a_mensagem_configurada_antes_do_link()
        {
            var f = new Fixture();
            f.Config.Setup(c => c.GetConfigAsync(Loja)).ReturnsAsync(new AtendimentoConfigDto
            {
                EstabelecimentoId = Loja,
                MensagemForaHorario = "Estamos fechados, abrimos às 18h!",
                // Todos os dias fechado no horario da mensagem: abre e fecha no mesmo minuto distante de agora.
                HorarioAtendimento = new HorarioAtendimentoDto
                {
                    Dias = Enumerable.Range(0, 7).Select(d => new HorarioDiaDto { Dia = d, Abre = "03:00", Fecha = "03:01" }).ToList()
                }
            });
            var chamada = f.Mensagem();
            chamada = chamada with { QuandoUtc = new DateTime(2026, 10, 2, 15, 0, 0, DateTimeKind.Utc) }; // 12h em Sao Paulo

            await f.Build().ExecutarAsync(chamada);

            Assert.Equal("Estamos fechados, abrimos às 18h!", f.Enviadas.OfType<AcaoResponder>().First().Mensagem);
            Assert.Contains("cardapio", f.Enviadas.OfType<AcaoResponder>().Last().Mensagem);
        }

        [Fact]
        public async Task O_estado_salvo_da_conversa_e_entregue_ao_motor()
        {
            var f = new Fixture();
            f.Estados.Setup(e => e.ObterAsync(Conversa)).ReturnsAsync(new EstadoFluxo { ForaDoHorarioAvisadoEm = DateTime.UtcNow });

            await f.Build().ExecutarAsync(f.Mensagem(primeira: false));

            f.Estados.Verify(e => e.ObterAsync(Conversa), Times.Once);
        }

        [Fact]
        public async Task Texto_do_cardapio_personalizado_troca_loja_e_link()
        {
            var f = new Fixture();
            f.Config.Setup(c => c.GetConfigAsync(Loja)).ReturnsAsync(new AtendimentoConfigDto
            {
                EstabelecimentoId = Loja,
                Mensagens = new Dictionary<string, string> { ["cardapio"] = "Bem-vindo a {loja}! Peca aqui: {link}" }
            });

            await f.Build().ExecutarAsync(f.Mensagem("cardapio", primeira: false));

            Assert.Contains(f.Enviadas.OfType<AcaoResponder>(), r => r.Mensagem.StartsWith("Bem-vindo a Pizza Bom Centro! Peca aqui: https://app.zippygo.com.br/cardapio/"));
        }

        [Fact]
        public async Task Simulacao_decide_sem_enviar_nem_gravar_nada()
        {
            var f = new Fixture();

            var r = await f.Build().SimularAsync(Loja, ModoAtendimento.Hibrido, new[] { "cardapio_web" }, "cardapio", null, false);

            Assert.EndsWith("link_do_cardapio", r.Regra);
            f.Enviador.VerifyNoOtherCalls();
            f.Estados.Verify(e => e.SalvarAsync(It.IsAny<Guid>(), It.IsAny<EstadoFluxo>(), It.IsAny<string>()), Times.Never);
        }

        [Fact]
        public void Mensagens_do_atendimento_so_aceitam_chaves_e_variaveis_conhecidas()
        {
            Assert.Throws<DeliveryDomainException>(() => MensagensDoAtendimento.Normalizar(new() { ["outra"] = "x" }, 1000));
            Assert.Throws<DeliveryDomainException>(() => MensagensDoAtendimento.Normalizar(new() { ["menu"] = "Oi {nome}" }, 1000));
            Assert.Throws<DeliveryDomainException>(() => MensagensDoAtendimento.Normalizar(new() { ["cardapio"] = "Sem o endereco" }, 1000));
            Assert.Throws<DeliveryDomainException>(() => MensagensDoAtendimento.Normalizar(new() { ["menu"] = new string('a', 1001) }, 1000));

            var ok = MensagensDoAtendimento.Normalizar(new() { ["cardapio"] = " Veja {link} ", ["menu"] = "  " }, 1000);

            Assert.Equal("Veja {link}", ok["cardapio"]);
            Assert.False(ok.ContainsKey("menu"));
        }
    }
}
