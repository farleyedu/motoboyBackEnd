using System;
using System.Threading;
using System.Threading.Tasks;
using APIBack.Controllers;
using APIBack.DTOs.Delivery;
using APIBack.Integrations.Ifood;
using APIBack.Integrations.Ifood.Config;
using APIBack.Integrations.Ifood.Merchants;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Moq;
using Xunit;

namespace APIBack.Tests.Unit
{
    public class IfoodStoreConnectionTests
    {
        private static readonly Guid Loja = Guid.NewGuid();
        private static readonly Guid Merchant = Guid.NewGuid();

        [Fact]
        public async Task Loja_informa_o_id_e_a_conexao_fica_pendente_para_a_Gestao_autorizar()
        {
            var (controller, repo, _) = Montar();

            var resultado = await controller.Connect(new IfoodStoreConnectionRequest { MerchantId = $" {Merchant} " }, CancellationToken.None);

            Assert.IsType<OkObjectResult>(resultado);
            repo.Verify(r => r.RequestStoreConnectionAsync(Merchant, Loja, It.IsAny<CancellationToken>()), Times.Once);
            repo.Verify(r => r.AuthorizeConnectionAsync(It.IsAny<Guid>(), It.IsAny<Guid>(), It.IsAny<CancellationToken>()), Times.Never);
        }

        [Theory]
        [InlineData("")]
        [InlineData("123")]
        [InlineData("00000000-0000-0000-0000-000000000000")]
        public async Task Id_fora_do_formato_e_recusado_com_explicacao(string id)
        {
            var (controller, repo, _) = Montar();

            var resultado = await controller.Connect(new IfoodStoreConnectionRequest { MerchantId = id }, CancellationToken.None);

            Assert.IsType<BadRequestObjectResult>(resultado);
            repo.Verify(r => r.RequestStoreConnectionAsync(It.IsAny<Guid>(), It.IsAny<Guid>(), It.IsAny<CancellationToken>()), Times.Never);
        }

        [Theory]
        [InlineData("IFOOD_CONNECTION_MERCHANT_LINKED_ELSEWHERE")]
        [InlineData("IFOOD_CONNECTION_STORE_HAS_ACTIVE")]
        public async Task Merchant_de_outra_loja_ou_loja_ja_conectada_vira_conflito(string codigo)
        {
            var (controller, repo, _) = Montar();
            repo.Setup(r => r.RequestStoreConnectionAsync(Merchant, Loja, It.IsAny<CancellationToken>())).ThrowsAsync(new IfoodIntegrationException(codigo));

            var resultado = await controller.Connect(new IfoodStoreConnectionRequest { MerchantId = Merchant.ToString() }, CancellationToken.None);

            Assert.Equal(409, ((ObjectResult)resultado).StatusCode);
        }

        [Fact]
        public async Task Desconectar_sem_conexao_ativa_responde_nao_encontrado()
        {
            var (controller, repo, _) = Montar();
            repo.Setup(r => r.DisconnectStoreAsync(Loja, It.IsAny<CancellationToken>())).ReturnsAsync(false);

            Assert.IsType<NotFoundObjectResult>(await controller.Disconnect(CancellationToken.None));

            repo.Setup(r => r.DisconnectStoreAsync(Loja, It.IsAny<CancellationToken>())).ReturnsAsync(true);
            Assert.IsType<OkObjectResult>(await controller.Disconnect(CancellationToken.None));
        }

        [Fact]
        public async Task Verificar_sem_conexao_nao_chama_o_iFood()
        {
            var (controller, repo, probe) = Montar();
            repo.Setup(r => r.GetStoreConnectionAsync(Loja, It.IsAny<CancellationToken>())).ReturnsAsync((IfoodConnectionRow?)null);

            var resultado = await controller.Verify(CancellationToken.None);

            Assert.IsType<OkObjectResult>(resultado);
            probe.Verify(p => p.CheckAsync(It.IsAny<Guid>(), It.IsAny<CancellationToken>()), Times.Never);
        }

        [Fact]
        public async Task Verificar_pergunta_ao_iFood_pelo_merchant_da_propria_loja()
        {
            var (controller, repo, probe) = Montar();
            repo.Setup(r => r.GetStoreConnectionAsync(Loja, It.IsAny<CancellationToken>()))
                .ReturnsAsync(new IfoodConnectionRow { MerchantId = Merchant, EstabelecimentoId = Loja, Status = "pending" });
            probe.Setup(p => p.CheckAsync(Merchant, It.IsAny<CancellationToken>())).ReturnsAsync(new IfoodMerchantCheck(true, "AUTHORIZED", "ok"));

            var resultado = (OkObjectResult)await controller.Verify(CancellationToken.None);

            probe.Verify(p => p.CheckAsync(Merchant, It.IsAny<CancellationToken>()), Times.Once);
            Assert.NotNull(resultado.Value);
        }

        [Fact]
        public async Task Gestao_le_o_ifood_de_outra_loja_sem_depender_da_loja_da_sessao()
        {
            var (controller, repo, _) = Montar();
            controller.HttpContext.Items["IsSuperAdmin"] = true;
            var outra = Guid.NewGuid();
            repo.Setup(r => r.GetSettingsAsync(It.IsAny<CancellationToken>())).ReturnsAsync(new IfoodRuntimeSettings());

            var resultado = await controller.GetByStore(outra, CancellationToken.None);

            Assert.IsType<OkObjectResult>(resultado);
            repo.Verify(r => r.GetStoreConnectionAsync(outra, It.IsAny<CancellationToken>()), Times.Once);
            repo.Verify(r => r.GetStoreConnectionAsync(Loja, It.IsAny<CancellationToken>()), Times.Never);
        }

        [Fact]
        public async Task Loja_comum_nao_le_o_ifood_de_outra_loja()
        {
            var (controller, repo, _) = Montar();

            var resultado = await controller.GetByStore(Guid.NewGuid(), CancellationToken.None);

            Assert.Equal(403, Assert.IsType<ObjectResult>(resultado).StatusCode);
            repo.Verify(r => r.GetStoreConnectionAsync(It.IsAny<Guid>(), It.IsAny<CancellationToken>()), Times.Never);
        }

        private static (IfoodStoreController Controller, Mock<IIfoodRuntimeSettingsRepository> Repo, Mock<IIfoodMerchantProbe> Probe) Montar()
        {
            var repo = new Mock<IIfoodRuntimeSettingsRepository>();
            var probe = new Mock<IIfoodMerchantProbe>();
            var context = new DefaultHttpContext();
            context.Items["EstabelecimentoId"] = Loja;
            return (new IfoodStoreController(repo.Object, probe.Object, new Mock<APIBack.Integrations.Ifood.Merchants.IIfoodMerchantDirectory>().Object, new Mock<APIBack.Integrations.Ifood.Connections.IIfoodConnectionHealth>().Object, new Mock<APIBack.Integrations.Ifood.Connections.IIfoodConnectionLifecycle>().Object, TimeProvider.System) { ControllerContext = new ControllerContext { HttpContext = context } }, repo, probe);
        }
    }
}
