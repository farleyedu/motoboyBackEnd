using System.Linq;
using APIBack.Model.Gestao;
using APIBack.Security;
using APIBack.Controllers;
using APIBack.Attributes;
using Xunit;

namespace APIBack.Tests.Unit
{
    public class ModulosNovosTests
    {
        [Theory]
        [InlineData("iFood", "IFOOD")]
        [InlineData("Impressao", "IMPRESSAO")]
        [InlineData("Remuneracao", "REMUNERACAO")]
        [InlineData("Simulador", "SIMULADOR")]
        [InlineData("Experience", "EXPERIENCE")]
        [InlineData("Financeiro", "FINANCEIRO")]
        [InlineData("Atendimento", "WHATSAPP")]
        public void Modulo_novo_vai_e_volta_do_banco(string ui, string banco)
        {
            Assert.Contains(banco, EstabelecimentoModuleMapper.ToDatabaseModules(new[] { ui }, "restaurante"));
        }

        [Fact]
        public void Nomes_do_banco_viram_os_nomes_da_tela()
        {
            var ui = EstabelecimentoModuleMapper.ToUiModules("Loja", new[] { "IFOOD", "IMPRESSAO", "REMUNERACAO", "SIMULADOR", "EXPERIENCE", "FINANCEIRO" });
            Assert.Equal(new[] { "Experience", "Financeiro", "Impressao", "Remuneracao", "Simulador", "iFood" }, ui.OrderBy(x => x, System.StringComparer.Ordinal).ToArray());
        }

        [Theory]
        [InlineData("Leads")]
        [InlineData("Estoque")]
        [InlineData("Vitrine")]
        [InlineData("Agendamentos")]
        [InlineData("Relatorios")]
        public void Modulos_removidos_sao_ignorados(string ui)
        {
            var banco = EstabelecimentoModuleMapper.ToDatabaseModules(new[] { ui }, null);
            Assert.Equal(new[] { "GERAL" }, banco.ToArray());
        }

        [Fact]
        public void Dependencias_dos_modulos_novos()
        {
            Assert.Contains("PEDIDOS", ModuleDependencies.Close(new[] { "IFOOD" }));
            Assert.DoesNotContain("DELIVERY", ModuleDependencies.Close(new[] { "IFOOD" }));
            Assert.Contains("PEDIDOS", ModuleDependencies.Close(new[] { "IMPRESSAO" }));
            var remuneracao = ModuleDependencies.Close(new[] { "REMUNERACAO" });
            Assert.Contains("DELIVERY", remuneracao);
            Assert.Contains("PEDIDOS", remuneracao);
            Assert.Contains("DELIVERY", ModuleDependencies.Close(new[] { "SIMULADOR" }));
        }

        [Fact]
        public void Sem_o_modulo_iFood_a_acao_de_integracao_some_da_permissao_Delivery()
        {
            const string json = "{\"Delivery\":[\"visualizar\",\"integracao_ifood\",\"configurar_impressao\"]}";

            var semModulos = EstablishmentPermissions.Resolve(json, new[] { "DELIVERY", "PEDIDOS" });
            Assert.Equal(new[] { "visualizar" }, semModulos["Delivery"]);

            var comModulos = EstablishmentPermissions.Resolve(json, new[] { "DELIVERY", "PEDIDOS", "IFOOD", "IMPRESSAO" });
            Assert.Equal(new[] { "visualizar", "integracao_ifood", "configurar_impressao" }, comModulos["Delivery"]);
        }

        [Fact]
        public void Rotas_de_impressao_exigem_o_modulo_proprio()
        {
            Assert.Contains(typeof(PrintController).GetCustomAttributes(typeof(RequireModuleAttribute), true),
                attribute => ((RequireModuleAttribute)attribute).Modulo == "Impressao");
        }
    }
}
