using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using APIBack.Atendimento;
using APIBack.Service;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Xunit;

namespace APIBack.Tests.Unit
{
    public class CanalCentralTests
    {
        private static readonly Guid LojaA = Guid.NewGuid();
        private static readonly Guid LojaB = Guid.NewGuid();
        private static readonly AtendimentoAtor Gestao = new(1, true, null, (_, _) => true);

        [Fact]
        public async Task Trocar_o_dono_nao_exige_digitar_numero_nem_id_de_novo()
        {
            var f = new Fixture();
            var central = Canal(LojaA, "821317791056700", "+5534998218242", central: true);
            f.Canais.Setup(c => c.ObterCentralAsync()).ReturnsAsync(central);

            await f.Build().SalvarAsync(Gestao, new CanalCentralRequest(null, null, null, LojaB));

            f.Canais.Verify(c => c.MoverAsync(central.Id, LojaB), Times.Once);
            f.Canais.Verify(c => c.CriarAsync(It.IsAny<CanalWhatsapp>(), It.IsAny<IReadOnlyCollection<string>>()), Times.Never);
            f.Canais.Verify(c => c.AtualizarDadosAsync(It.IsAny<CanalWhatsapp>()), Times.Never);
            f.Canais.Verify(c => c.DefinirCentralAsync(central.Id), Times.Once);
        }

        [Fact]
        public async Task Numero_que_ja_esta_cadastrado_e_adotado_em_vez_de_dar_conflito()
        {
            var f = new Fixture();
            var existente = Canal(LojaA, "821317791056700", "+5534998218242", central: false);
            f.Canais.Setup(c => c.ObterPorPhoneNumberIdAsync("821317791056700")).ReturnsAsync(existente);
            f.Canais.Setup(c => c.ObterPorNumeroAsync("5534998218242")).ReturnsAsync(existente);

            await f.Build().SalvarAsync(Gestao, new CanalCentralRequest("821317791056700", "5534998218242", null, LojaB));

            f.Canais.Verify(c => c.CriarAsync(It.IsAny<CanalWhatsapp>(), It.IsAny<IReadOnlyCollection<string>>()), Times.Never);
            f.Canais.Verify(c => c.MoverAsync(existente.Id, LojaB), Times.Once);
            f.Canais.Verify(c => c.DefinirCentralAsync(existente.Id), Times.Once);
        }

        [Fact]
        public async Task Numero_achado_so_pelo_id_da_meta_e_adotado_mesmo_com_o_telefone_gravado_diferente()
        {
            var f = new Fixture();
            var existente = Canal(LojaA, "821317791056700", "+553498218242", central: false);
            f.Canais.Setup(c => c.ObterPorPhoneNumberIdAsync("821317791056700")).ReturnsAsync(existente);

            await f.Build().SalvarAsync(Gestao, new CanalCentralRequest("821317791056700", "5534998218242", null, LojaB));

            f.Canais.Verify(c => c.CriarAsync(It.IsAny<CanalWhatsapp>(), It.IsAny<IReadOnlyCollection<string>>()), Times.Never);
            f.Canais.Verify(c => c.AtualizarDadosAsync(It.Is<CanalWhatsapp>(x => x.NumeroE164 == "+5534998218242")), Times.Once);
        }

        [Fact]
        public async Task Numero_e_id_de_canais_diferentes_sao_recusados_com_a_explicacao()
        {
            var f = new Fixture();
            f.Canais.Setup(c => c.ObterPorPhoneNumberIdAsync("821317791056700")).ReturnsAsync(Canal(LojaA, "821317791056700", "+5534990000001", false));
            f.Canais.Setup(c => c.ObterPorNumeroAsync("5534998218242")).ReturnsAsync(Canal(LojaA, "999999999999999", "+5534998218242", false));

            var erro = await Assert.ThrowsAsync<RequestValidationException>(() =>
                f.Build().SalvarAsync(Gestao, new CanalCentralRequest("821317791056700", "5534998218242", null, LojaB)));

            Assert.Contains("numero", erro.Errors.Keys);
            f.Canais.Verify(c => c.MoverAsync(It.IsAny<Guid>(), It.IsAny<Guid>()), Times.Never);
        }

        [Fact]
        public async Task Sem_central_e_sem_numero_informado_pede_o_numero_uma_vez()
        {
            var f = new Fixture();

            var erro = await Assert.ThrowsAsync<RequestValidationException>(() =>
                f.Build().SalvarAsync(Gestao, new CanalCentralRequest(null, null, null, LojaB)));

            Assert.Contains("phoneNumberId", erro.Errors.Keys);
        }

        [Fact]
        public async Task Numero_novo_de_verdade_cria_o_canal_central()
        {
            var f = new Fixture();
            CanalWhatsapp? criado = null;
            f.Canais.Setup(c => c.CriarAsync(It.IsAny<CanalWhatsapp>(), It.IsAny<IReadOnlyCollection<string>>()))
                .Callback<CanalWhatsapp, IReadOnlyCollection<string>>((c, _) => criado = c).Returns(Task.CompletedTask);

            await f.Build().SalvarAsync(Gestao, new CanalCentralRequest("821317791056700", "5534998218242", null, LojaB));

            Assert.NotNull(criado);
            Assert.Equal(LojaB, criado!.IdEstabelecimento);
            Assert.True(criado.Central);
        }

        [Fact]
        public async Task Quem_nao_e_da_gestao_nao_troca_o_dono()
        {
            var f = new Fixture();

            await Assert.ThrowsAsync<UnauthorizedAccessException>(() =>
                f.Build().SalvarAsync(new AtendimentoAtor(2, false, LojaA, (_, _) => true), new CanalCentralRequest(null, null, null, LojaB)));
        }

        private static CanalWhatsapp Canal(Guid loja, string phoneNumberId, string numero, bool central) => new()
        {
            Id = Guid.NewGuid(), IdEstabelecimento = loja, PhoneNumberId = phoneNumberId, NumeroE164 = numero, Central = central
        };

        private sealed class Fixture
        {
            public Mock<ICanalRepository> Canais { get; } = new();
            public Mock<ICatalogoRepository> Catalogo { get; } = new();
            public Mock<ITokenProtector> Token { get; } = new();

            public Fixture()
            {
                Catalogo.Setup(c => c.LojaExisteAsync(It.IsAny<Guid>())).ReturnsAsync(true);
                Catalogo.Setup(c => c.ListarServicosDaLojaAsync(It.IsAny<Guid>())).ReturnsAsync(new[] { new ServicoDaLoja { Codigo = "delivery", Ativo = true } });
                Canais.Setup(c => c.ObterAsync(It.IsAny<Guid>())).ReturnsAsync((Guid id) => new CanalWhatsapp { Id = id, IdEstabelecimento = LojaB });
            }

            public CanalCentralService Build() => new(Canais.Object, Catalogo.Object, Token.Object, NullLogger<CanalCentralService>.Instance);
        }
    }
}
