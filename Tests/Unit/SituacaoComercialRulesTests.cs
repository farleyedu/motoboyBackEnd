using APIBack.Service;
using Xunit;

namespace APIBack.Tests.Unit
{
    public class SituacaoComercialRulesTests
    {
        [Theory]
        [InlineData("/api/auth/login", "POST")]
        [InlineData("/api/auth/me", "GET")]
        [InlineData("/api/me/estabelecimentos", "GET")]
        [InlineData("/api/v2/comercial/situacao", "GET")]
        [InlineData("/api/v2/motoboys/me/session", "GET")]
        [InlineData("/api/v2/motoboys/me/session", "DELETE")]
        [InlineData("/api/v2/motoboys/me/session/heartbeat", "POST")]
        [InlineData("/api/v2/motoboys/me/session/location", "POST")]
        [InlineData("/api/v2/motoboys/me/session/stops/current/deliver", "POST")]
        [InlineData("/api/v2/motoboys/me/session/completion/123/code", "POST")]
        [InlineData("/api/v2/delivery/motoboys/42/fila/concluir-atual", "POST")]
        [InlineData("/api/v2/motoboys/me/session/queue/arrived-at-store", "POST")]
        public void Loja_bloqueada_ainda_permite_login_e_terminar_entrega(string path, string method)
        {
            Assert.True(SituacaoComercialRules.PermitidoQuandoBloqueado(path, method));
        }

        [Theory]
        [InlineData("/api/v2/delivery/pedidos/10/atribuir", "POST")]
        [InlineData("/api/v2/delivery/pedidos", "GET")]
        [InlineData("/api/configuracoes/estabelecimento/negocio/operacao", "GET")]
        [InlineData("/hubs/delivery/negotiate", "POST")]
        [InlineData("/api/v2/motoboys/me/session/start", "POST")]
        [InlineData("/api/v2/motoboys/me/session/switch", "POST")]
        [InlineData("/api/v2/motoboys/me/session/queue/offer/accept", "POST")]
        [InlineData("/api/v2/motoboys/me/session/queue/offer/reject", "POST")]
        [InlineData("/api/v2/motoboys/me/session/queue/resume", "POST")]
        [InlineData("/api/v2/motoboys/me/session/stops/current/pickup", "POST")]
        [InlineData("/api/v2/motoboys/me/session/stops/123/transfer", "POST")]
        [InlineData("/api/motoboys/me/perfil", "PATCH")]
        [InlineData("/api/me/estabelecimentos", "POST")]
        [InlineData("/api/authx/qualquer", "GET")]
        public void Loja_bloqueada_barra_o_resto(string path, string method)
        {
            // Inclui "/api/authx": o prefixo de autenticacao precisa casar com o segmento inteiro.
            Assert.False(SituacaoComercialRules.PermitidoQuandoBloqueado(path, method));
        }

        [Theory]
        [InlineData("/api/v2/delivery/pedidos/10/atribuir", "POST")]
        [InlineData("/api/v2/delivery/pedidos", "GET")]
        [InlineData("/api/v2/motoboys/me/session/start", "POST")]
        [InlineData("/api/v2/ifood/store", "POST")]
        [InlineData("/api/Pedido", "POST")]
        [InlineData("/api/checkout/pix", "POST")]
        public void Implantacao_bloqueia_operacao(string path, string method) =>
            Assert.True(SituacaoComercialRules.OperacaoExigeInicio(path, method));

        [Theory]
        [InlineData("/api/configuracoes/estabelecimento/negocio/operacao", "PUT")]
        [InlineData("/api/v2/delivery/configuracoes", "PUT")]
        [InlineData("/api/v2/delivery/zonas", "POST")]
        [InlineData("/api/v2/delivery/impressao/configuracao", "PUT")]
        [InlineData("/api/v2/ifood/config", "GET")]
        [InlineData("/api/Pedido", "GET")]
        public void Implantacao_permite_configuracao(string path, string method) =>
            Assert.False(SituacaoComercialRules.OperacaoExigeInicio(path, method));

        [Theory]
        [InlineData(1, 3, 10)]
        [InlineData(2, 6, 15)]
        [InlineData(3, 9, 20)]
        [InlineData(4, 12, 25)]
        [InlineData(5, 15, 30)]
        [InlineData(8, 24, 30)]
        [InlineData(0, 3, 10)]
        public void Limites_de_motoboy_seguem_a_tabela(int pacotes, int vagas, int vinculados)
        {
            Assert.Equal(vagas, MotoboyLimites.VagasPorDia(pacotes));
            Assert.Equal(vinculados, MotoboyLimites.Vinculados(pacotes));
        }
    }
}
