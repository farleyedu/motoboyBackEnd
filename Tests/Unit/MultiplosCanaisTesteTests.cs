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
    public class MultiplosCanaisTesteTests
    {
        private static readonly Guid LojaA = Guid.NewGuid();
        private static readonly Guid LojaB = Guid.NewGuid();
        private static readonly AtendimentoAtor Gestao = new(1, true, null, (_, _) => true);
        private static readonly AtendimentoAtor Lojista = new(2, false, LojaA, (_, _) => true);

        [Fact]
        public async Task Dois_numeros_de_teste_atendem_destinos_diferentes()
        {
            var f = new Fixture();
            var criados = new List<CanalWhatsapp>();
            f.Canais.Setup(c => c.CriarAsync(It.IsAny<CanalWhatsapp>(), It.IsAny<IReadOnlyCollection<string>>()))
                .Callback<CanalWhatsapp, IReadOnlyCollection<string>>((c, _) => criados.Add(c)).Returns(Task.CompletedTask);

            await f.Build().CriarTesteAsync(Gestao, new CanalTesteRequest("821317791056700", "5534998218242", null, "A", LojaA, null, null));
            await f.Build().CriarTesteAsync(Gestao, new CanalTesteRequest("821317791056701", "5534998218243", null, "B", LojaB, null, null));

            Assert.Equal(2, criados.Count);
            Assert.All(criados, c => Assert.True(c.Teste));
            Assert.Equal(LojaA, criados[0].IdEstabelecimento);
            Assert.Equal(LojaB, criados[1].IdEstabelecimento);
        }

        [Fact]
        public async Task Primeiro_numero_vira_principal_se_nao_ha_principal()
        {
            var f = new Fixture();
            CanalWhatsapp? criado = null;
            f.Canais.Setup(c => c.CriarAsync(It.IsAny<CanalWhatsapp>(), It.IsAny<IReadOnlyCollection<string>>()))
                .Callback<CanalWhatsapp, IReadOnlyCollection<string>>((c, _) => criado = c).Returns(Task.CompletedTask);

            await f.Build().CriarTesteAsync(Gestao, new CanalTesteRequest("821317791056700", "5534998218242", null, null, LojaA, null, null));

            Assert.True(criado!.Central);
        }

        [Fact]
        public async Task Segundo_numero_nao_vira_principal_sozinho_mas_pode_pedir()
        {
            var f = new Fixture();
            f.Canais.Setup(c => c.ObterCentralAsync()).ReturnsAsync(Canal(LojaA, "1", "+5534990000001", true));
            CanalWhatsapp? criado = null;
            f.Canais.Setup(c => c.CriarAsync(It.IsAny<CanalWhatsapp>(), It.IsAny<IReadOnlyCollection<string>>()))
                .Callback<CanalWhatsapp, IReadOnlyCollection<string>>((c, _) => criado = c).Returns(Task.CompletedTask);

            await f.Build().CriarTesteAsync(Gestao, new CanalTesteRequest("821317791056700", "5534998218242", null, null, LojaB, null, true));

            Assert.False(criado!.Central);
            f.Canais.Verify(c => c.AplicarTesteAsync(It.Is<AlteracaoTeste>(a => a.CanalId == criado.Id && a.Principal == true)), Times.Once);
        }

        [Fact]
        public async Task Numero_ou_id_duplicado_da_conflito()
        {
            var f = new Fixture();
            f.Canais.Setup(c => c.ObterPorPhoneNumberIdAsync("821317791056700")).ReturnsAsync(Canal(LojaA, "821317791056700", "+5534998218242", false, teste: true));

            var erro = await Assert.ThrowsAsync<CanalConflitoException>(() =>
                f.Build().CriarTesteAsync(Gestao, new CanalTesteRequest("821317791056700", "5534998218299", null, null, LojaB, null, null)));

            Assert.Equal("phoneNumberId", erro.Campo);
            f.Canais.Verify(c => c.CriarAsync(It.IsAny<CanalWhatsapp>(), It.IsAny<IReadOnlyCollection<string>>()), Times.Never);
        }

        [Fact]
        public async Task Nao_adota_numero_real_de_cliente()
        {
            var f = new Fixture();
            var cliente = Canal(LojaA, "821317791056700", "+5534998218242", false);
            cliente.Origem = OrigemCanal.Proprio;
            f.Canais.Setup(c => c.ObterPorNumeroAsync("5534998218242")).ReturnsAsync(cliente);

            var erro = await Assert.ThrowsAsync<CanalConflitoException>(() =>
                f.Build().CriarTesteAsync(Gestao, new CanalTesteRequest("821317791056799", "5534998218242", null, null, LojaB, null, null)));

            Assert.Contains("pertence a uma loja", erro.Message);
        }

        [Fact]
        public async Task Quem_nao_e_da_gestao_leva_403()
        {
            var f = new Fixture();
            await Assert.ThrowsAsync<UnauthorizedAccessException>(() => f.Build().ListarTesteAsync(Lojista));
            await Assert.ThrowsAsync<UnauthorizedAccessException>(() => f.Build().VerificarTesteAsync(Lojista, Guid.NewGuid()));
        }

        [Fact]
        public async Task Destino_inexistente_e_404_e_sem_destino_e_400()
        {
            var f = new Fixture();
            f.Catalogo.Setup(c => c.LojaExisteAsync(LojaB)).ReturnsAsync(false);

            await Assert.ThrowsAsync<KeyNotFoundException>(() =>
                f.Build().CriarTesteAsync(Gestao, new CanalTesteRequest("821317791056700", "5534998218242", null, null, LojaB, null, null)));
            await Assert.ThrowsAsync<ArgumentException>(() =>
                f.Build().CriarTesteAsync(Gestao, new CanalTesteRequest("821317791056700", "5534998218242", null, null, Guid.Empty, null, null)));
        }

        [Fact]
        public async Task Dados_invalidos_do_numero_dao_422()
        {
            var f = new Fixture();
            var erro = await Assert.ThrowsAsync<RequestValidationException>(() =>
                f.Build().CriarTesteAsync(Gestao, new CanalTesteRequest(null, "abc", null, null, LojaA, null, null)));

            Assert.Contains("phoneNumberId", erro.Errors.Keys);
        }

        [Fact]
        public async Task Trocar_destino_e_uma_unica_operacao_atomica_que_zera_a_verificacao()
        {
            var f = new Fixture();
            var canal = Canal(LojaA, "821317791056700", "+5534998218242", false, teste: true);
            f.Canais.Setup(c => c.ObterAsync(canal.Id)).ReturnsAsync(canal);

            await f.Build().AtualizarTesteAsync(Gestao, canal.Id, new CanalTesteAtualizacao(null, null, LojaB, null, null, null));

            f.Canais.Verify(c => c.AplicarTesteAsync(It.Is<AlteracaoTeste>(a =>
                a.CanalId == canal.Id && a.Destino == LojaB && a.Servicos != null && a.Acao == "teste_destino_alterado")), Times.Once);
            f.Canais.Verify(c => c.MoverAsync(It.IsAny<Guid>(), It.IsAny<Guid>()), Times.Never);
        }

        [Fact]
        public async Task Finalidade_gestao_exige_o_id_da_loja_escolhido_explicitamente()
        {
            var f = new Fixture();
            var canal = Canal(LojaA, "821317791056700", "+5534998218242", false, teste: true);
            f.Canais.Setup(c => c.ObterAsync(canal.Id)).ReturnsAsync(canal);

            var erro = await Assert.ThrowsAsync<RequestValidationException>(() =>
                f.Build().AtualizarTesteAsync(Gestao, canal.Id, new CanalTesteAtualizacao(null, null, null, FinalidadeTeste.Gestao, null, null)));
            Assert.Contains("estabelecimentoId", erro.Errors.Keys);

            await f.Build().AtualizarTesteAsync(Gestao, canal.Id, new CanalTesteAtualizacao(null, null, LojaB, FinalidadeTeste.Gestao, null, null));
            f.Canais.Verify(c => c.AplicarTesteAsync(It.Is<AlteracaoTeste>(a => a.Finalidade == FinalidadeTeste.Gestao && a.Destino == LojaB)), Times.Once);
        }

        [Fact]
        public async Task Principal_nao_pode_ser_desligado_nem_inativado_sem_outro_principal()
        {
            var f = new Fixture();
            var principal = Canal(LojaA, "821317791056700", "+5534998218242", true);
            f.Canais.Setup(c => c.ObterAsync(principal.Id)).ReturnsAsync(principal);

            await Assert.ThrowsAsync<RequestValidationException>(() =>
                f.Build().AtualizarTesteAsync(Gestao, principal.Id, new CanalTesteAtualizacao(null, StatusCanal.Inativo, null, null, null, null)));
            await Assert.ThrowsAsync<RequestValidationException>(() =>
                f.Build().AtualizarTesteAsync(Gestao, principal.Id, new CanalTesteAtualizacao(null, null, null, null, false, null)));
            f.Canais.Verify(c => c.AplicarTesteAsync(It.IsAny<AlteracaoTeste>()), Times.Never);
        }

        [Fact]
        public async Task Ativar_sem_verificar_na_meta_da_422_e_status_invalido_400()
        {
            var f = new Fixture();
            var canal = Canal(LojaA, "821317791056700", "+5534998218242", false, teste: true);
            f.Canais.Setup(c => c.ObterAsync(canal.Id)).ReturnsAsync(canal);

            await Assert.ThrowsAsync<RequestValidationException>(() =>
                f.Build().AtualizarTesteAsync(Gestao, canal.Id, new CanalTesteAtualizacao(null, StatusCanal.Ativo, null, null, null, null)));
            await Assert.ThrowsAsync<ArgumentException>(() =>
                f.Build().AtualizarTesteAsync(Gestao, canal.Id, new CanalTesteAtualizacao(null, "erro", null, null, null, null)));
        }

        [Fact]
        public async Task Alterar_canal_de_cliente_ou_inexistente_e_404()
        {
            var f = new Fixture();
            var cliente = Canal(LojaA, "821317791056700", "+5534998218242", false);
            f.Canais.Setup(c => c.ObterAsync(cliente.Id)).ReturnsAsync(cliente);

            await Assert.ThrowsAsync<KeyNotFoundException>(() =>
                f.Build().AtualizarTesteAsync(Gestao, cliente.Id, new CanalTesteAtualizacao("x", null, null, null, null, null)));
            await Assert.ThrowsAsync<KeyNotFoundException>(() =>
                f.Build().AtualizarTesteAsync(Gestao, Guid.NewGuid(), new CanalTesteAtualizacao("x", null, null, null, null, null)));
        }

        [Fact]
        public async Task Verificar_consulta_a_meta_e_informa_credenciais_e_webhook()
        {
            var f = new Fixture();
            var canal = Canal(LojaA, "821317791056700", "+5534998218242", false, teste: true);
            canal.TokenCifrado = "x";
            f.Canais.Setup(c => c.ObterAsync(canal.Id)).ReturnsAsync(canal);
            f.Verificador.Setup(v => v.VerificarAsync(canal)).ReturnsAsync(new VerificacaoCanal(true, "ok", "+5534998218242", "Zippy"));

            var r = await f.Build().VerificarTesteAsync(Gestao, canal.Id);

            Assert.True(r.Ok);
            Assert.True(r.Credenciais);
            Assert.Equal(WebhookEstado.NaoAplicavel, r.Webhook);
            f.Verificador.Verify(v => v.VerificarAsync(canal), Times.Once);
        }

        [Fact]
        public async Task Contrato_antigo_aponta_para_o_principal()
        {
            var f = new Fixture();
            var principal = Canal(LojaA, "821317791056700", "+5534998218242", true);
            f.Canais.Setup(c => c.ObterCentralAsync()).ReturnsAsync(principal);

            var view = await f.Build().ObterAsync(Gestao);

            Assert.Equal(principal.Id, view!.Id);
        }

        [Fact]
        public async Task Contrato_antigo_ao_adotar_outro_principal_nao_rebaixa_o_anterior_para_numero_de_loja()
        {
            var f = new Fixture();
            var existente = Canal(LojaA, "821317791056700", "+5534998218242", false, teste: true);
            f.Canais.Setup(c => c.ObterPorPhoneNumberIdAsync("821317791056700")).ReturnsAsync(existente);
            f.Canais.Setup(c => c.ObterPorNumeroAsync("5534998218242")).ReturnsAsync(existente);
            f.Canais.Setup(c => c.ObterAsync(existente.Id)).ReturnsAsync(existente);

            await f.Build().SalvarAsync(Gestao, new CanalCentralRequest("821317791056700", "5534998218242", null, LojaB));

            f.Canais.Verify(c => c.DefinirCentralAsync(existente.Id), Times.Once);
        }

        [Fact]
        public void Canal_legado_so_com_marca_de_central_conta_como_numero_de_teste()
        {
            Assert.True(new CanalWhatsapp { Central = true }.DeTeste);
            Assert.True(new CanalWhatsapp { Teste = true }.DeTeste);
            Assert.False(new CanalWhatsapp().DeTeste);
        }

        private static CanalWhatsapp Canal(Guid loja, string phoneNumberId, string numero, bool central, bool teste = false) => new()
        {
            Id = Guid.NewGuid(), IdEstabelecimento = loja, PhoneNumberId = phoneNumberId, NumeroE164 = numero, Central = central, Teste = teste || central,
            Origem = central || teste ? OrigemCanal.Central : OrigemCanal.Zippy
        };

        private sealed class Fixture
        {
            public Mock<ICanalRepository> Canais { get; } = new();
            public Mock<ICatalogoRepository> Catalogo { get; } = new();
            public Mock<ITokenProtector> Token { get; } = new();
            public Mock<ICanalVerificador> Verificador { get; } = new();

            public Fixture()
            {
                Catalogo.Setup(c => c.LojaExisteAsync(It.IsAny<Guid>())).ReturnsAsync(true);
                Catalogo.Setup(c => c.ListarServicosDaLojaAsync(It.IsAny<Guid>())).ReturnsAsync(new[] { new ServicoDaLoja { Codigo = "delivery", Ativo = true } });
                Canais.Setup(c => c.ListarTesteAsync()).ReturnsAsync(new List<CanalWhatsapp>());
            }

            public CanalCentralService Build() =>
                new(Canais.Object, Catalogo.Object, Token.Object, NullLogger<CanalCentralService>.Instance, Verificador.Object);
        }
    }
}
