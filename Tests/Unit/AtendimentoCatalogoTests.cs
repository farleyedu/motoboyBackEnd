using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using APIBack.Atendimento;
using APIBack.Service;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Xunit;

namespace APIBack.Tests.Unit
{
    public class AtendimentoRegrasTests
    {
        private static readonly ServicoCatalogoItem[] Catalogo =
        {
            new("delivery", "Delivery", null, new[] { "DELIVERY", "PEDIDOS" }, 10, true),
            new("cardapio_web", "Cardapio web", null, new[] { "CARDAPIO", "CARDAPIOWEB", "PEDIDOS" }, 20, true),
            new("agendamento", "Agendamento", null, new[] { "AGENDAMENTOS" }, 30, true),
            new("antigo", "Antigo", null, Array.Empty<string>(), 40, false),
        };

        [Theory]
        [InlineData("821317791056700", true)]
        [InlineData("34991480112", false)]      // telefone digitado no lugar do ID da Meta
        [InlineData("5534991480112", false)]    // 13 digitos: ainda e telefone
        [InlineData("82131779105670a", false)]
        [InlineData("", false)]
        [InlineData(null, false)]
        public void PhoneNumberId_so_vale_se_nao_parecer_telefone(string? valor, bool esperado) =>
            Assert.Equal(esperado, AtendimentoRegras.PhoneNumberIdValido(valor));

        [Fact]
        public void Canal_valido_nao_gera_erros()
        {
            var erros = AtendimentoRegras.ValidarCanal("821317791056700", "(34) 99148-0112", "bot");

            Assert.Empty(erros);
            Assert.Equal("+5534991480112", AtendimentoRegras.NormalizarNumero("(34) 99148-0112"));
        }

        [Fact]
        public void Canal_com_telefone_no_lugar_do_id_numero_ruim_e_modo_invalido_lista_cada_campo()
        {
            var erros = AtendimentoRegras.ValidarCanal("34991480112", "123", "central");

            Assert.Equal(new[] { "modoAtendimento", "numero", "phoneNumberId" }, erros.Keys.OrderBy(k => k).ToArray());
        }

        [Fact]
        public void Ligar_um_servico_traz_os_modulos_que_ele_exige_e_os_modulos_deles()
        {
            var modulos = AtendimentoRegras.ModulosParaServicos(Catalogo, new[] { "cardapio_web" });

            Assert.Equal(new[] { "CARDAPIO", "CARDAPIOWEB", "PEDIDOS" }, modulos.OrderBy(m => m).ToArray());
        }

        [Fact]
        public void Servicos_da_loja_voltam_sem_repeticao_e_na_ordem_do_catalogo()
        {
            var permitidos = new[] { new ServicoDoTipo("delivery", true), new ServicoDoTipo("cardapio_web", true) };

            var ativos = AtendimentoRegras.ValidarServicosDaLoja(new[] { "cardapio_web", "DELIVERY", "delivery" }, Catalogo, permitidos);

            Assert.Equal(new[] { "delivery", "cardapio_web" }, ativos);
        }

        [Fact]
        public void Servico_inexistente_inativo_ou_fora_do_tipo_e_recusado_com_o_motivo()
        {
            var permitidos = new[] { new ServicoDoTipo("delivery", true) };

            var erro = Assert.Throws<RequestValidationException>(() =>
                AtendimentoRegras.ValidarServicosDaLoja(new[] { "inexistente", "antigo", "agendamento" }, Catalogo, permitidos));

            Assert.Equal(3, erro.Errors["servicos"].Count);
            Assert.Contains(erro.Errors["servicos"], m => m.Contains("nao esta disponivel para este tipo"));
        }

        [Fact]
        public void Um_numero_so_atende_servicos_que_a_loja_tem_ligados()
        {
            var ok = AtendimentoRegras.ValidarServicosDoCanal(new[] { "Delivery" }, new[] { "delivery", "cardapio_web" });
            Assert.Equal(new[] { "delivery" }, ok);

            var erro = Assert.Throws<RequestValidationException>(() =>
                AtendimentoRegras.ValidarServicosDoCanal(new[] { "agendamento" }, new[] { "delivery" }));
            Assert.Contains("agendamento", erro.Errors["servicos"].Single());
        }
    }

    public class TokenProtectorTests
    {
        private static TokenProtector Com(string? chave) =>
            new(new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?> { ["Atendimento:ChaveToken"] = chave }).Build());

        [Fact]
        public void O_token_vai_cifrado_e_volta_igual()
        {
            var protector = Com("uma-frase-qualquer-de-teste");

            var cifrado = protector.Proteger("EAAG-token-secreto");

            Assert.DoesNotContain("EAAG", cifrado);
            Assert.Equal("EAAG-token-secreto", protector.Revelar(cifrado));
        }

        [Fact]
        public void Cada_cifragem_e_diferente_e_chave_errada_ou_dado_adulterado_nao_revela_nada()
        {
            var a = Com("chave-a");
            var primeiro = a.Proteger("token");

            Assert.NotEqual(primeiro, a.Proteger("token"));
            Assert.Null(Com("chave-b").Revelar(primeiro));
            Assert.Null(a.Revelar(primeiro[..^4] + "AAAA"));
            Assert.Null(a.Revelar("lixo"));
        }

        [Fact]
        public void Sem_chave_configurada_nao_cifra_e_nao_revela()
        {
            var sem = Com(null);

            Assert.False(sem.Configurado);
            Assert.Throws<InvalidOperationException>(() => sem.Proteger("x"));
            Assert.Null(sem.Revelar("v1.abc"));
        }
    }

    public class ServicosDaLojaServiceTests
    {
        private static readonly Guid Loja = Guid.NewGuid();
        private static readonly Guid Tipo = Guid.NewGuid();
        private static AtendimentoAtor Gestao => new(1, true, null, (_, _) => true);
        private static AtendimentoAtor Dono(Guid? loja = null) => new(2, false, loja ?? Loja, (_, _) => true);

        private static (ServicosDaLojaService Service, Mock<ICatalogoRepository> Repo) Build()
        {
            var repo = new Mock<ICatalogoRepository>();
            repo.Setup(r => r.LojaExisteAsync(Loja)).ReturnsAsync(true);
            repo.Setup(r => r.ObterTipoDaLojaAsync(Loja)).ReturnsAsync(Tipo);
            repo.Setup(r => r.ListarCatalogoAsync()).ReturnsAsync(new[]
            {
                new ServicoCatalogoItem("delivery", "Delivery", null, new[] { "DELIVERY", "PEDIDOS" }, 10, true),
                new ServicoCatalogoItem("agendamento", "Agendamento", null, new[] { "AGENDAMENTOS" }, 30, true),
            });
            repo.Setup(r => r.ListarServicosDoTipoAsync(Tipo)).ReturnsAsync(new[] { new ServicoDoTipo("delivery", true) });
            repo.Setup(r => r.ListarServicosDaLojaAsync(Loja)).ReturnsAsync(new[] { new ServicoDaLoja { Codigo = "delivery", Ativo = true } });
            return (new ServicosDaLojaService(repo.Object, NullLogger<ServicosDaLojaService>.Instance), repo);
        }

        [Fact]
        public async Task A_gestao_liga_o_servico_e_os_modulos_exigidos_vao_junto()
        {
            var (service, repo) = Build();

            var view = await service.DefinirAsync(Gestao, Loja, new[] { "delivery" });

            repo.Verify(r => r.DefinirServicosDaLojaAsync(
                Loja,
                It.Is<IReadOnlyCollection<string>>(a => a.SequenceEqual(new[] { "delivery" })),
                It.Is<IReadOnlyCollection<string>>(m => m.OrderBy(x => x).SequenceEqual(new[] { "DELIVERY", "PEDIDOS" })),
                1), Times.Once);
            Assert.True(view.PodeEditar);
        }

        [Fact]
        public async Task O_dono_nao_liga_nem_desliga_servico_e_nada_e_gravado()
        {
            var (service, repo) = Build();

            await Assert.ThrowsAsync<UnauthorizedAccessException>(() => service.DefinirAsync(Dono(), Loja, new[] { "delivery" }));

            repo.Verify(r => r.DefinirServicosDaLojaAsync(
                It.IsAny<Guid>(), It.IsAny<IReadOnlyCollection<string>>(), It.IsAny<IReadOnlyCollection<string>>(), It.IsAny<int?>()), Times.Never);
        }

        [Fact]
        public async Task Servico_que_o_tipo_nao_permite_e_recusado_sem_gravar()
        {
            var (service, repo) = Build();

            await Assert.ThrowsAsync<RequestValidationException>(() => service.DefinirAsync(Gestao, Loja, new[] { "agendamento" }));

            repo.Verify(r => r.DefinirServicosDaLojaAsync(
                It.IsAny<Guid>(), It.IsAny<IReadOnlyCollection<string>>(), It.IsAny<IReadOnlyCollection<string>>(), It.IsAny<int?>()), Times.Never);
        }

        [Fact]
        public async Task O_dono_le_so_a_propria_loja_e_a_tela_mostra_o_que_e_permitido_e_o_que_esta_ativo()
        {
            var (service, _) = Build();

            var view = await service.ObterAsync(Dono(), Loja);

            Assert.False(view.PodeEditar);
            Assert.True(view.Servicos.Single(s => s.Codigo == "delivery").Ativo);
            Assert.False(view.Servicos.Single(s => s.Codigo == "agendamento").Permitido);
            await Assert.ThrowsAsync<UnauthorizedAccessException>(() => service.ObterAsync(Dono(Guid.NewGuid()), Loja));
        }

        [Fact]
        public async Task Loja_inexistente_e_nao_encontrada()
        {
            var (service, repo) = Build();
            repo.Setup(r => r.LojaExisteAsync(Loja)).ReturnsAsync(false);

            await Assert.ThrowsAsync<KeyNotFoundException>(() => service.DefinirAsync(Gestao, Loja, new[] { "delivery" }));
        }
    }

    public class CanaisWhatsappServiceTests
    {
        private static readonly Guid Loja = Guid.NewGuid();
        private static readonly Guid Outra = Guid.NewGuid();
        private static AtendimentoAtor Gestao => new(1, true, null, (_, _) => true);
        private static AtendimentoAtor Dono(bool permissao = true) => new(2, false, Loja, (m, a) => permissao && m == "WhatsApp" && a == "configurar");

        private static CanalRequest Pedido(string id = "821317791056700", string numero = "5534991480112") =>
            new(id, numero, "Loja", null, null, null);

        private sealed class Fixture
        {
            public Mock<ICanalRepository> Canais { get; } = new();
            public Mock<ICatalogoRepository> Catalogo { get; } = new();
            public Mock<ITokenProtector> Token { get; } = new();
            public Mock<ICanalVerificador> Verificador { get; } = new();
            public CanalWhatsapp? Existente { get; set; }

            public Fixture()
            {
                Catalogo.Setup(c => c.LojaExisteAsync(It.IsAny<Guid>())).ReturnsAsync(true);
                Catalogo.Setup(c => c.ListarServicosDaLojaAsync(Loja)).ReturnsAsync(new[]
                {
                    new ServicoDaLoja { Codigo = "delivery", Ativo = true }, new ServicoDaLoja { Codigo = "agendamento", Ativo = false }
                });
                Existente = new CanalWhatsapp
                {
                    Id = Guid.NewGuid(), IdEstabelecimento = Loja, PhoneNumberId = "821317791056700", NumeroE164 = "+5534991480112",
                    ModoAtendimento = "hibrido", Servicos = new List<string> { "delivery" }
                };
                Canais.Setup(c => c.ObterAsync(It.IsAny<Guid>())).ReturnsAsync(() => Existente);
            }

            public CanaisWhatsappService Build() =>
                new(Canais.Object, Catalogo.Object, Token.Object, Verificador.Object, NullLogger<CanaisWhatsappService>.Instance);
        }

        [Fact]
        public async Task Criar_um_numero_atende_por_padrao_os_servicos_ativos_da_loja_e_garante_o_modulo_WhatsApp()
        {
            var f = new Fixture();
            CanalWhatsapp? gravado = null;
            f.Canais.Setup(c => c.CriarAsync(It.IsAny<CanalWhatsapp>(), It.IsAny<IReadOnlyCollection<string>>()))
                .Callback<CanalWhatsapp, IReadOnlyCollection<string>>((c, _) => gravado = c).Returns(Task.CompletedTask);

            await f.Build().CriarAsync(Gestao, Loja, Pedido(numero: "(34) 99148-0112"));

            Assert.Equal("+5534991480112", gravado!.NumeroE164);
            Assert.Equal("configurando", gravado.Status);
            Assert.Equal(new[] { "delivery" }, gravado.Servicos);
            f.Catalogo.Verify(c => c.GarantirModulosAsync(Loja, It.Is<IReadOnlyCollection<string>>(m => m.Contains("WHATSAPP"))), Times.Once);
            f.Canais.Verify(c => c.RegistrarAuditoriaAsync(gravado.Id, "criado", null, Loja, 1, It.IsAny<string?>()), Times.Once);
        }

        [Fact]
        public async Task Somente_a_gestao_cria_altera_move_ou_remove_numero()
        {
            var f = new Fixture();
            var service = f.Build();

            await Assert.ThrowsAsync<UnauthorizedAccessException>(() => service.CriarAsync(Dono(), Loja, Pedido()));
            await Assert.ThrowsAsync<UnauthorizedAccessException>(() => service.AtualizarAsync(Dono(), f.Existente!.Id, Pedido()));
            await Assert.ThrowsAsync<UnauthorizedAccessException>(() => service.MoverAsync(Dono(), f.Existente!.Id, Outra));
            await Assert.ThrowsAsync<UnauthorizedAccessException>(() => service.RemoverAsync(Dono(), f.Existente!.Id));

            f.Canais.Verify(c => c.CriarAsync(It.IsAny<CanalWhatsapp>(), It.IsAny<IReadOnlyCollection<string>>()), Times.Never);
            f.Canais.Verify(c => c.RemoverAsync(It.IsAny<Guid>()), Times.Never);
        }

        [Fact]
        public async Task Telefone_no_lugar_do_id_da_Meta_e_recusado_sem_gravar()
        {
            var f = new Fixture();

            var erro = await Assert.ThrowsAsync<RequestValidationException>(() => f.Build().CriarAsync(Gestao, Loja, Pedido(id: "34991480112")));

            Assert.Contains("phoneNumberId", erro.Errors.Keys);
            f.Canais.Verify(c => c.CriarAsync(It.IsAny<CanalWhatsapp>(), It.IsAny<IReadOnlyCollection<string>>()), Times.Never);
        }

        [Fact]
        public async Task Numero_que_ja_pertence_a_outra_loja_vira_erro_de_campo_com_a_dica_de_mover()
        {
            var f = new Fixture();
            f.Canais.Setup(c => c.CriarAsync(It.IsAny<CanalWhatsapp>(), It.IsAny<IReadOnlyCollection<string>>()))
                .ThrowsAsync(new CanalConflitoException("numero", "Este numero ja esta cadastrado. Use mover numero."));

            var erro = await Assert.ThrowsAsync<RequestValidationException>(() => f.Build().CriarAsync(Gestao, Loja, Pedido()));

            Assert.Contains("mover", erro.Errors["numero"].Single());
        }

        [Fact]
        public async Task Token_so_e_aceito_com_a_chave_de_cifragem_configurada_e_nunca_vai_em_texto_puro()
        {
            var f = new Fixture();
            f.Token.Setup(t => t.Configurado).Returns(false);

            var erro = await Assert.ThrowsAsync<RequestValidationException>(() =>
                f.Build().CriarAsync(Gestao, Loja, Pedido() with { Token = "EAAG-secreto" }));
            Assert.Contains("token", erro.Errors.Keys);

            f.Token.Setup(t => t.Configurado).Returns(true);
            f.Token.Setup(t => t.Proteger("EAAG-secreto")).Returns("v1.cifrado");
            CanalWhatsapp? gravado = null;
            f.Canais.Setup(c => c.CriarAsync(It.IsAny<CanalWhatsapp>(), It.IsAny<IReadOnlyCollection<string>>()))
                .Callback<CanalWhatsapp, IReadOnlyCollection<string>>((c, _) => gravado = c).Returns(Task.CompletedTask);

            var view = await f.Build().CriarAsync(Gestao, Loja, Pedido() with { Token = "EAAG-secreto" });

            Assert.Equal("v1.cifrado", gravado!.TokenCifrado);
            Assert.DoesNotContain("EAAG", view.ToString());
        }

        [Fact]
        public async Task Mudar_o_id_ou_o_numero_exige_verificar_de_novo()
        {
            var f = new Fixture();
            CanalWhatsapp? salvo = null;
            f.Canais.Setup(c => c.AtualizarDadosAsync(It.IsAny<CanalWhatsapp>())).Callback<CanalWhatsapp>(c => salvo = c).Returns(Task.CompletedTask);
            f.Existente!.Status = "ativo";

            await f.Build().AtualizarAsync(Gestao, f.Existente.Id, Pedido(id: "999999999999999"));

            Assert.Equal("configurando", salvo!.Status);
        }

        [Fact]
        public async Task O_dono_com_permissao_escolhe_o_modo_do_numero_da_propria_loja_e_so_dele()
        {
            var f = new Fixture();
            var service = f.Build();

            await service.DefinirModoAsync(Dono(), f.Existente!.Id, "bot");
            f.Canais.Verify(c => c.AtualizarModoAsync(f.Existente.Id, "bot"), Times.Once);

            await Assert.ThrowsAsync<UnauthorizedAccessException>(() => service.DefinirModoAsync(Dono(permissao: false), f.Existente.Id, "humano"));
            f.Existente.IdEstabelecimento = Outra;
            await Assert.ThrowsAsync<UnauthorizedAccessException>(() => service.DefinirModoAsync(Dono(), f.Existente.Id, "humano"));
            await Assert.ThrowsAsync<RequestValidationException>(() => service.DefinirModoAsync(Gestao, f.Existente.Id, "central"));
        }

        [Fact]
        public async Task Mover_o_numero_troca_a_loja_zera_os_servicos_registra_e_nao_aceita_a_mesma_loja()
        {
            var f = new Fixture();
            var service = f.Build();

            await service.MoverAsync(Gestao, f.Existente!.Id, Outra);

            f.Canais.Verify(c => c.MoverAsync(f.Existente.Id, Outra), Times.Once);
            f.Catalogo.Verify(c => c.GarantirModulosAsync(Outra, It.Is<IReadOnlyCollection<string>>(m => m.Contains("WHATSAPP"))), Times.Once);
            f.Canais.Verify(c => c.RegistrarAuditoriaAsync(f.Existente.Id, "movido", Loja, Outra, 1, It.IsAny<string?>()), Times.Once);
            await Assert.ThrowsAsync<RequestValidationException>(() => service.MoverAsync(Gestao, f.Existente.Id, Loja));
        }

        [Fact]
        public async Task O_dono_lista_so_os_numeros_da_propria_loja()
        {
            var f = new Fixture();
            f.Canais.Setup(c => c.ListarPorLojaAsync(Loja)).ReturnsAsync(new[] { f.Existente! });

            var lista = await f.Build().ListarAsync(Dono(), Loja);

            Assert.Single(lista);
            await Assert.ThrowsAsync<UnauthorizedAccessException>(() => f.Build().ListarAsync(Dono(), Outra));
        }
    }
}
