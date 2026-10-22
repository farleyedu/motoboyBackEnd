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
    public class CanalConexaoTests
    {
        private const string IdMeta = "821317791056700";
        private static readonly Guid Loja = Guid.NewGuid();
        private static readonly Guid OutraLoja = Guid.NewGuid();
        private static AtendimentoAtor Dono(bool podeConfigurar = true, Guid? loja = null) =>
            new(2, false, loja ?? Loja, (modulo, acao) => podeConfigurar && modulo == "WhatsApp" && acao == "configurar");
        private static AtendimentoAtor Gestao => new(1, true, null, (_, _) => true);

        [Fact]
        public async Task Dono_conecta_com_id_e_token_e_o_telefone_vem_da_Meta()
        {
            var f = new Fixture();
            CanalWhatsapp? criado = null;
            f.Canais.Setup(c => c.CriarAsync(It.IsAny<CanalWhatsapp>(), It.IsAny<IReadOnlyCollection<string>>()))
                .Callback<CanalWhatsapp, IReadOnlyCollection<string>>((c, _) => criado = c).Returns(Task.CompletedTask);

            await f.Build().ConectarAsync(Dono(), Loja, new ConectarCanalRequest(IdMeta, "EAAG-token"));

            Assert.Equal("+5534998218242", criado!.NumeroE164);
            Assert.Equal(IdMeta, criado.PhoneNumberId);
            Assert.Equal("v1.cifrado", criado.TokenCifrado);
            Assert.Equal(Loja, criado.IdEstabelecimento);
            Assert.Contains("delivery", criado.Servicos);
            f.Canais.Verify(c => c.MarcarVerificadoAsync(criado.Id, true, null), Times.Once);
        }

        [Fact]
        public async Task Id_que_ja_e_de_outra_loja_e_recusado_sem_consultar_a_Meta()
        {
            var f = new Fixture();
            f.Canais.Setup(c => c.ObterPorPhoneNumberIdAsync(IdMeta)).ReturnsAsync(new CanalWhatsapp { Id = Guid.NewGuid(), IdEstabelecimento = OutraLoja, PhoneNumberId = IdMeta });

            var erro = await Assert.ThrowsAnyAsync<RequestValidationException>(() => f.Build().ConectarAsync(Dono(), Loja, new ConectarCanalRequest(IdMeta, "EAAG-token")));

            Assert.Contains("phoneNumberId", erro.Errors.Keys);
            f.Verificador.Verify(v => v.ConsultarCredenciaisAsync(It.IsAny<string>(), It.IsAny<string>()), Times.Never);
            f.Canais.Verify(c => c.CriarAsync(It.IsAny<CanalWhatsapp>(), It.IsAny<IReadOnlyCollection<string>>()), Times.Never);
        }

        [Fact]
        public async Task Token_recusado_pela_Meta_nao_grava_nada_e_mostra_o_motivo()
        {
            var f = new Fixture();
            f.Verificador.Setup(v => v.ConsultarCredenciaisAsync(IdMeta, "ruim"))
                .ReturnsAsync(new VerificacaoCanal(false, "O token da Meta e invalido ou expirou.", null, null));

            var erro = await Assert.ThrowsAnyAsync<RequestValidationException>(() => f.Build().ConectarAsync(Dono(), Loja, new ConectarCanalRequest(IdMeta, "ruim")));

            Assert.Contains("token", erro.Errors.Keys);
            f.Canais.Verify(c => c.CriarAsync(It.IsAny<CanalWhatsapp>(), It.IsAny<IReadOnlyCollection<string>>()), Times.Never);
        }

        [Fact]
        public async Task Loja_que_ja_tem_numero_proprio_precisa_desconectar_antes()
        {
            var f = new Fixture();
            f.Canais.Setup(c => c.ListarPorLojaAsync(Loja)).ReturnsAsync(new[]
            {
                new CanalWhatsapp { Id = Guid.NewGuid(), IdEstabelecimento = Loja, PhoneNumberId = "111111111111111", Status = StatusCanal.Ativo }
            });

            var erro = await Assert.ThrowsAnyAsync<RequestValidationException>(() => f.Build().ConectarAsync(Dono(), Loja, new ConectarCanalRequest(IdMeta, "EAAG-token")));

            Assert.Contains("desconecte", erro.Errors["phoneNumberId"][0], StringComparison.OrdinalIgnoreCase);
        }

        [Fact]
        public async Task Reconectar_o_mesmo_numero_so_troca_o_token()
        {
            var f = new Fixture();
            var existente = new CanalWhatsapp { Id = Guid.NewGuid(), IdEstabelecimento = Loja, PhoneNumberId = IdMeta, NumeroE164 = "+5534998218242", Status = StatusCanal.Erro };
            f.Canais.Setup(c => c.ObterPorPhoneNumberIdAsync(IdMeta)).ReturnsAsync(existente);
            f.Canais.Setup(c => c.ObterPorNumeroAsync("5534998218242")).ReturnsAsync(existente);
            f.Canais.Setup(c => c.ObterAsync(existente.Id)).ReturnsAsync(existente);

            await f.Build().ConectarAsync(Dono(), Loja, new ConectarCanalRequest(IdMeta, "EAAG-novo"));

            f.Canais.Verify(c => c.CriarAsync(It.IsAny<CanalWhatsapp>(), It.IsAny<IReadOnlyCollection<string>>()), Times.Never);
            f.Canais.Verify(c => c.AtualizarDadosAsync(It.Is<CanalWhatsapp>(x => x.TokenCifrado == "v1.cifrado")), Times.Once);
            f.Canais.Verify(c => c.MarcarVerificadoAsync(existente.Id, true, null), Times.Once);
        }

        [Fact]
        public async Task Quem_nao_configura_o_whatsapp_da_loja_nao_conecta()
        {
            var f = new Fixture();

            await Assert.ThrowsAsync<UnauthorizedAccessException>(() => f.Build().ConectarAsync(Dono(podeConfigurar: false), Loja, new ConectarCanalRequest(IdMeta, "t")));
            await Assert.ThrowsAsync<UnauthorizedAccessException>(() => f.Build().ConectarAsync(Dono(loja: OutraLoja), Loja, new ConectarCanalRequest(IdMeta, "t")));
        }

        [Fact]
        public async Task Sem_a_chave_de_cifragem_o_token_nao_e_aceito()
        {
            var f = new Fixture();
            f.Token.Setup(t => t.Configurado).Returns(false);

            var erro = await Assert.ThrowsAnyAsync<RequestValidationException>(() => f.Build().ConectarAsync(Dono(), Loja, new ConectarCanalRequest(IdMeta, "EAAG-token")));

            Assert.Equal(WhatsappCodigos.ChaveCifragemAusente, Assert.IsType<WhatsappException>(erro).Erro.Codigo);
        }

        [Fact]
        public async Task Dono_desconecta_o_numero_da_propria_loja_mas_nao_o_central()
        {
            var f = new Fixture();
            var proprio = new CanalWhatsapp { Id = Guid.NewGuid(), IdEstabelecimento = Loja, PhoneNumberId = IdMeta };
            var central = new CanalWhatsapp { Id = Guid.NewGuid(), IdEstabelecimento = Loja, PhoneNumberId = "222222222222222", Central = true };
            f.Canais.Setup(c => c.ObterAsync(proprio.Id)).ReturnsAsync(proprio);
            f.Canais.Setup(c => c.ObterAsync(central.Id)).ReturnsAsync(central);

            await f.Build().DesconectarAsync(Dono(), proprio.Id);
            await Assert.ThrowsAsync<UnauthorizedAccessException>(() => f.Build().DesconectarAsync(Dono(), central.Id));
            await f.Build().DesconectarAsync(Gestao, central.Id);

            f.Canais.Verify(c => c.RemoverAsync(proprio.Id), Times.Once);
            f.Canais.Verify(c => c.RemoverAsync(central.Id), Times.Once);
        }

        private sealed class Fixture
        {
            public Mock<ICanalRepository> Canais { get; } = new();
            public Mock<ICatalogoRepository> Catalogo { get; } = new();
            public Mock<ITokenProtector> Token { get; } = new();
            public Mock<ICanalVerificador> Verificador { get; } = new();
            public Mock<ICanalWebhookService> Webhook { get; } = new();

            public Fixture()
            {
                Catalogo.Setup(c => c.LojaExisteAsync(It.IsAny<Guid>())).ReturnsAsync(true);
                Catalogo.Setup(c => c.ListarServicosDaLojaAsync(Loja)).ReturnsAsync(new[] { new ServicoDaLoja { Codigo = "delivery", Ativo = true } });
                Token.Setup(t => t.Configurado).Returns(true);
                Token.Setup(t => t.Proteger(It.IsAny<string>())).Returns("v1.cifrado");
                Canais.Setup(c => c.ListarPorLojaAsync(It.IsAny<Guid>())).ReturnsAsync(Array.Empty<CanalWhatsapp>());
                Canais.Setup(c => c.ObterAsync(It.IsAny<Guid>())).ReturnsAsync((Guid id) => new CanalWhatsapp { Id = id, IdEstabelecimento = Loja, PhoneNumberId = IdMeta });
                Verificador.Setup(v => v.ConsultarCredenciaisAsync(It.IsAny<string>(), It.Is<string>(t => t != "ruim")))
                    .ReturnsAsync(new VerificacaoCanal(true, "Numero verificado na Meta.", "+55 34 99821-8242", "Pizzaria Teste"));
            }

            public CanaisWhatsappService Build() =>
                new(Canais.Object, Catalogo.Object, Token.Object, Verificador.Object, Webhook.Object, NullLogger<CanaisWhatsappService>.Instance);
        }
    }
}
