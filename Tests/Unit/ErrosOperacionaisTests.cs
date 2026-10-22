using System;
using System.Linq;
using System.Reflection;
using System.Text.Json;
using APIBack.Service;
using Xunit;

namespace APIBack.Tests.Unit
{
    public class ErrosOperacionaisTests
    {
        private static readonly string[] CodigosExigidos =
        {
            "PRONTIDAO_INCOMPLETA", "OPERACAO_NAO_INICIADA", "ESTABELECIMENTO_BLOQUEADO", "STORE_CLOSED", "STORE_NOT_ACCEPTING",
            "MINIMUM_NOT_REACHED", "OUT_OF_DELIVERY_RANGE", "RAIO_ENTREGA_NAO_CONFIGURADO", "LOJA_SEM_COORDENADA", "CARDAPIO_SEM_WHATSAPP",
            "VAGAS_MOTOBOY_ESGOTADAS", "DELIVERY_TRACKING_DISABLED", "MODULO_INATIVO", "RIDER_PAY_PLAN_REQUIRED", "loja_fechada"
        };

        [Fact]
        public void Todo_erro_do_catalogo_tem_code_mensagem_como_resolver_e_quem_resolve()
        {
            foreach (var erro in ErrosOperacionais.Todos)
            {
                Assert.False(string.IsNullOrWhiteSpace(erro.Code));
                Assert.False(string.IsNullOrWhiteSpace(erro.Mensagem), erro.Code);
                Assert.False(string.IsNullOrWhiteSpace(erro.ComoResolver), erro.Code);
                Assert.Contains(erro.QuemResolve, new[] { QuemResolve.Loja, QuemResolve.Zippy, QuemResolve.Infra });
                Assert.InRange(erro.Status, 400, 599);
            }
        }

        [Fact]
        public void Os_erros_pedidos_pelo_produto_estao_todos_no_catalogo()
        {
            foreach (var code in CodigosExigidos) Assert.True(ErrosOperacionais.Existe(code), code);
        }

        [Fact]
        public void Toda_constante_publica_de_codigo_tem_cadastro()
        {
            var constantes = typeof(ErrosOperacionais).GetFields(BindingFlags.Public | BindingFlags.Static)
                .Where(f => f.IsLiteral && f.FieldType == typeof(string)).Select(f => (string)f.GetRawConstantValue()!).ToList();

            Assert.NotEmpty(constantes);
            Assert.All(constantes, c => Assert.True(ErrosOperacionais.Existe(c), c));
        }

        [Theory]
        [InlineData("RAIO_ENTREGA_NAO_CONFIGURADO", "delivery", null)]
        [InlineData("LOJA_SEM_COORDENADA", "negocio", "endereco")]
        [InlineData("CARDAPIO_SEM_WHATSAPP", "atendimento", "numeros")]
        [InlineData("STORE_CLOSED", "negocio", "horarios")]
        [InlineData("RIDER_PAY_PLAN_REQUIRED", "remuneracao", null)]
        public void Erros_da_loja_apontam_para_a_tela_certa(string code, string area, string? foco)
        {
            var erro = ErrosOperacionais.Obter(code);

            Assert.Equal(QuemResolve.Loja, erro.QuemResolve);
            Assert.Equal(area, erro.Correcao!.Area);
            Assert.Equal(foco, erro.Correcao.Foco);
        }

        [Fact]
        public void Erros_da_zippy_e_da_infra_dizem_quem_resolve()
        {
            Assert.Equal(QuemResolve.Zippy, ErrosOperacionais.Obter("ESTABELECIMENTO_BLOQUEADO").QuemResolve);
            Assert.Equal(QuemResolve.Zippy, ErrosOperacionais.Obter("VAGAS_MOTOBOY_ESGOTADAS").QuemResolve);
            Assert.Equal(QuemResolve.Zippy, ErrosOperacionais.Obter("MODULO_INATIVO").QuemResolve);
            Assert.Equal(QuemResolve.Infra, ErrosOperacionais.Obter("DELIVERY_TRACKING_DISABLED").QuemResolve);
        }

        [Fact]
        public void A_excecao_de_dominio_leva_status_code_e_details_do_catalogo()
        {
            var pendencia = new PendenciaErroDto("raio_entrega", "Raio de entrega", "Falta o raio.", "Informe o raio.", QuemResolve.Loja, CorrecaoDto.Config("delivery"));

            var ex = ErrosOperacionais.Excecao("PRONTIDAO_INCOMPLETA", pendencias: new[] { pendencia });

            Assert.Equal(422, ex.StatusCode);
            Assert.Equal("PRONTIDAO_INCOMPLETA", ex.Code);
            var details = Assert.IsType<DetalhesErroOperacional>(ex.Details);
            Assert.False(string.IsNullOrWhiteSpace(details.ComoResolver));
            Assert.Equal("raio_entrega", Assert.Single(details.Pendencias!).Codigo);
        }

        [Fact]
        public void O_corpo_segue_o_formato_success_error_code_details()
        {
            var corpo = ErrosOperacionais.Corpo("OUT_OF_DELIVERY_RANGE", "Fora do raio.",
                dados: new System.Collections.Generic.Dictionary<string, object?> { ["raioKm"] = 5m, ["distanciaKm"] = 7.2 });

            var json = JsonDocument.Parse(JsonSerializer.Serialize(corpo, new JsonSerializerOptions(JsonSerializerDefaults.Web))).RootElement;

            Assert.False(json.GetProperty("success").GetBoolean());
            Assert.Equal("Fora do raio.", json.GetProperty("error").GetString());
            Assert.Equal("OUT_OF_DELIVERY_RANGE", json.GetProperty("code").GetString());
            var details = json.GetProperty("details");
            Assert.False(string.IsNullOrWhiteSpace(details.GetProperty("comoResolver").GetString()));
            Assert.Equal("loja", details.GetProperty("quemResolve").GetString());
            Assert.Equal("delivery", details.GetProperty("correcao").GetProperty("area").GetString());
            Assert.Equal(7.2, details.GetProperty("dados").GetProperty("distanciaKm").GetDouble());
        }

        [Fact]
        public void Pedido_manual_de_entrega_sem_raio_ou_sem_coordenada_e_recusado_com_o_caminho()
        {
            var semRaio = Assert.Throws<DeliveryDomainException>(() =>
                PedidoCoreService.ExigirRaioECoordenada(new APIBack.DTOs.Delivery.RestaurantSettingsDto { RaioEntregaKm = 0m, Latitude = -18.9, Longitude = -48.2 }));
            Assert.Equal("RAIO_ENTREGA_NAO_CONFIGURADO", semRaio.Code);
            Assert.Equal(422, semRaio.StatusCode);
            Assert.Equal("delivery", ((DetalhesErroOperacional)semRaio.Details!).Correcao!.Area);

            var semPonto = Assert.Throws<DeliveryDomainException>(() =>
                PedidoCoreService.ExigirRaioECoordenada(new APIBack.DTOs.Delivery.RestaurantSettingsDto { RaioEntregaKm = 5m, Latitude = 0, Longitude = 0 }));
            Assert.Equal("LOJA_SEM_COORDENADA", semPonto.Code);
            var correcao = ((DetalhesErroOperacional)semPonto.Details!).Correcao!;
            Assert.Equal(("negocio", "endereco"), (correcao.Area, correcao.Foco));

            PedidoCoreService.ExigirRaioECoordenada(new APIBack.DTOs.Delivery.RestaurantSettingsDto { RaioEntregaKm = 5m, Latitude = -18.9, Longitude = -48.2 });
        }

        [Fact]
        public void Cliente_final_recebe_texto_neutro_sem_revelar_o_que_a_loja_precisa_configurar()
        {
            Assert.DoesNotContain("Configurações", CardapioPublicService.MensagemIndisponivel);
            Assert.Contains("tarde", CardapioPublicService.MensagemIndisponivel);
            Assert.Equal("Loja fechada ou indisponível no momento. Tente mais tarde.", ErrosOperacionais.Obter("CARDAPIO_INDISPONIVEL").Mensagem);
            var fechada = PedidosAbertosRules.Mensagem(new SituacaoPedidos(false, PedidosAbertosRules.SemHorario, null));
            Assert.DoesNotContain("horário de funcionamento", fechada);
            Assert.DoesNotContain("cadastr", fechada);
        }
    }
}
