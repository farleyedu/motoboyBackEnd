using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using APIBack.Atendimento;
using APIBack.Automation.Infra;
using APIBack.Automation.Models;
using APIBack.Service;
using Moq;
using Xunit;

namespace APIBack.Tests.Unit
{
    /// <summary>O contrato antigo (IWabaPhoneRepository) agora le e grava em canal_whatsapp.</summary>
    public class WabaPhoneAdaptadorTests
    {
        private static readonly Guid Loja = Guid.NewGuid();

        private sealed class Fixture
        {
            public Mock<ICanalRepository> Canais { get; } = new();
            public Mock<ICatalogoRepository> Catalogo { get; } = new();
            public Mock<ITokenProtector> Token { get; } = new();
            public SqlWabaPhoneRepository Build() => new(Canais.Object, Catalogo.Object, Token.Object);

            public Fixture()
            {
                Catalogo.Setup(c => c.ListarServicosDaLojaAsync(Loja)).ReturnsAsync(new[] { new ServicoDaLoja { Codigo = "delivery", Ativo = true } });
            }
        }

        private static CanalWhatsapp Canal() => new()
        {
            Id = Guid.NewGuid(), IdEstabelecimento = Loja, PhoneNumberId = "821317791056700", NumeroE164 = "+5534991480112",
            Status = StatusCanal.Ativo
        };

        [Theory]
        [InlineData(null)]
        [InlineData("")]
        [InlineData("   ")]
        public async Task Busca_com_valor_vazio_devolve_nulo_sem_consultar_o_banco(string? valor)
        {
            var f = new Fixture();
            var repo = f.Build();

            Assert.Null(await repo.ObterIdEstabelecimentoPorPhoneNumberIdAsync(valor!));
            Assert.Null(await repo.ObterIdEstabelecimentoPorDisplayPhoneAsync(valor!));
            Assert.Null(await repo.ObterAccessTokenPorPhoneNumberIdAsync(valor!));
            f.Canais.VerifyNoOtherCalls();
        }

        [Fact]
        public async Task A_loja_de_um_numero_vem_do_canal_e_o_telefone_da_loja_vem_normalizado()
        {
            var f = new Fixture();
            var canal = Canal();
            f.Canais.Setup(c => c.ObterAtivoPorPhoneNumberIdAsync("821317791056700")).ReturnsAsync(canal);
            f.Canais.Setup(c => c.ObterPorNumeroAsync("5534991480112")).ReturnsAsync(canal);
            f.Canais.Setup(c => c.ObterPrimeiroUsavelAsync(Loja)).ReturnsAsync(canal);
            var repo = f.Build();

            Assert.Equal(Loja, await repo.ObterIdEstabelecimentoPorPhoneNumberIdAsync("821317791056700"));
            Assert.Equal(Loja, await repo.ObterIdEstabelecimentoPorDisplayPhoneAsync("+55 (34) 99148-0112"));
            Assert.Equal("+5534991480112", await repo.ObterDisplayPhonePorEstabelecimentoAsync(Loja));
            Assert.Equal("821317791056700", await repo.ObterPhoneNumberIdPorEstabelecimentoAsync(Loja));
        }

        [Fact]
        public async Task O_numero_do_servico_vem_do_canal_que_atende_aquele_servico_e_so_dele()
        {
            var f = new Fixture();
            f.Canais.Setup(c => c.ObterParaServicoAsync(Loja, "cardapio_web")).ReturnsAsync(Canal());
            var repo = f.Build();

            Assert.Equal("+5534991480112", await repo.ObterDisplayPhoneParaServicoAsync(Loja, "cardapio_web"));
            Assert.Null(await repo.ObterDisplayPhoneParaServicoAsync(Loja, "agendamento"));
            Assert.Null(await repo.ObterDisplayPhoneParaServicoAsync(Guid.Empty, "cardapio_web"));
            Assert.Null(await repo.ObterDisplayPhoneParaServicoAsync(Loja, " "));
        }

        [Fact]
        public async Task Numero_inativo_nao_resolve_loja_pelo_telefone()
        {
            var f = new Fixture();
            var canal = Canal();
            canal.Status = StatusCanal.Inativo;
            f.Canais.Setup(c => c.ObterPorNumeroAsync("5534991480112")).ReturnsAsync(canal);

            Assert.Null(await f.Build().ObterIdEstabelecimentoPorDisplayPhoneAsync("5534991480112"));
        }

        [Fact]
        public async Task O_token_do_numero_e_decifrado_e_sem_canal_nao_ha_token()
        {
            var f = new Fixture();
            var canal = Canal();
            canal.TokenCifrado = "v1.cifrado";
            f.Canais.Setup(c => c.ObterAtivoPorPhoneNumberIdAsync("821317791056700")).ReturnsAsync(canal);
            f.Token.Setup(t => t.Revelar("v1.cifrado")).Returns("EAAG-token");
            var repo = f.Build();

            Assert.Equal("EAAG-token", await repo.ObterAccessTokenPorPhoneNumberIdAsync("821317791056700"));
            Assert.Null(await repo.ObterAccessTokenPorPhoneNumberIdAsync("111111111111111"));
        }

        [Fact]
        public async Task A_tela_antiga_cria_o_primeiro_numero_da_loja_com_os_servicos_ativos_e_o_modulo_WhatsApp()
        {
            var f = new Fixture();
            f.Canais.Setup(c => c.ListarPorLojaAsync(Loja)).ReturnsAsync(Array.Empty<CanalWhatsapp>());
            CanalWhatsapp? criado = null;
            f.Canais.Setup(c => c.CriarAsync(It.IsAny<CanalWhatsapp>(), It.IsAny<IReadOnlyCollection<string>>()))
                .Callback<CanalWhatsapp, IReadOnlyCollection<string>>((c, _) => criado = c).Returns(Task.CompletedTask);

            var ok = await f.Build().InserirOuAtualizarAsync(new WabaPhone
            {
                IdEstabelecimento = Loja, PhoneNumberId = "821317791056700", DisplayPhoneNumber = "5534991480112", Descricao = "Loja"
            });

            Assert.True(ok);
            Assert.Equal("+5534991480112", criado!.NumeroE164);
            Assert.Equal(new[] { "delivery" }, criado.Servicos);
            f.Catalogo.Verify(c => c.GarantirModulosAsync(Loja, It.Is<IReadOnlyCollection<string>>(m => m.Contains("WHATSAPP"))), Times.Once);
        }

        [Fact]
        public async Task Sem_o_id_da_Meta_ou_com_telefone_no_lugar_dele_nada_e_criado()
        {
            var f = new Fixture();
            f.Canais.Setup(c => c.ListarPorLojaAsync(Loja)).ReturnsAsync(Array.Empty<CanalWhatsapp>());
            var repo = f.Build();

            Assert.False(await repo.InserirOuAtualizarAsync(new WabaPhone { IdEstabelecimento = Loja, DisplayPhoneNumber = "5534991480112" }));
            Assert.False(await repo.InserirOuAtualizarAsync(new WabaPhone { IdEstabelecimento = Loja, PhoneNumberId = "34991480112", DisplayPhoneNumber = "5534991480112" }));
            f.Canais.Verify(c => c.CriarAsync(It.IsAny<CanalWhatsapp>(), It.IsAny<IReadOnlyCollection<string>>()), Times.Never);
        }

        [Fact]
        public async Task Alterar_so_o_telefone_mantem_o_id_e_volta_o_canal_para_verificar()
        {
            var f = new Fixture();
            var canal = Canal();
            f.Canais.Setup(c => c.ListarPorLojaAsync(Loja)).ReturnsAsync(new[] { canal });
            CanalWhatsapp? salvo = null;
            f.Canais.Setup(c => c.AtualizarDadosAsync(It.IsAny<CanalWhatsapp>())).Callback<CanalWhatsapp>(c => salvo = c).Returns(Task.CompletedTask);

            await f.Build().InserirOuAtualizarAsync(new WabaPhone { IdEstabelecimento = Loja, DisplayPhoneNumber = "5534999990000" });

            Assert.Equal("+5534999990000", salvo!.NumeroE164);
            Assert.Equal("821317791056700", salvo.PhoneNumberId);
            Assert.Equal(StatusCanal.Configurando, salvo.Status);
        }

        [Fact]
        public async Task Numero_que_ja_e_de_outra_loja_vira_erro_no_campo_da_tela_de_estabelecimento()
        {
            var f = new Fixture();
            f.Canais.Setup(c => c.ListarPorLojaAsync(Loja)).ReturnsAsync(Array.Empty<CanalWhatsapp>());
            f.Canais.Setup(c => c.CriarAsync(It.IsAny<CanalWhatsapp>(), It.IsAny<IReadOnlyCollection<string>>()))
                .ThrowsAsync(new CanalConflitoException("numero", "Este numero ja esta cadastrado."));

            var erro = await Assert.ThrowsAsync<RequestValidationException>(() => f.Build().InserirOuAtualizarAsync(new WabaPhone
            {
                IdEstabelecimento = Loja, PhoneNumberId = "821317791056700", DisplayPhoneNumber = "5534991480112"
            }));

            Assert.Contains("wabaDisplayPhone", erro.Errors.Keys);
        }
    }
}
