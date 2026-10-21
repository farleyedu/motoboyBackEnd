using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using APIBack.DTOs.Gestao;
using APIBack.Model.Gestao;
using APIBack.Repository.Interface;
using APIBack.Service;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Xunit;

namespace APIBack.Tests.Unit
{
    public class GestaoProvisionamentoTests
    {
        private static readonly Guid Empresa = Guid.NewGuid();
        private static readonly Guid Loja = Guid.NewGuid();

        [Theory]
        [InlineData("empresa01", 1, "gerente.empresa01@zippy.com")]
        [InlineData("empresa01", 2, "gerente.empresa012@zippy.com")]
        [InlineData("Pizzaria Do Zé!", 1, "gerente.pizzariadoz@zippy.com")]
        [InlineData("--", 1, "gerente.loja@zippy.com")]
        [InlineData(null, 1, "gerente.loja@zippy.com")]
        public void Email_do_gerente_padrao_sai_do_slug_da_loja(string? slug, int tentativa, string esperado)
        {
            Assert.Equal(esperado, GerentePadraoRules.Email(slug, tentativa));
        }

        [Fact]
        public void Email_do_gerente_padrao_nao_passa_do_limite_do_login()
        {
            var email = GerentePadraoRules.Email(new string('a', 120));

            Assert.True(email.Split('@')[0].Length <= 64);
            Assert.EndsWith("@zippy.com", email);
        }

        [Fact]
        public void Nome_do_gerente_padrao_leva_o_nome_da_empresa()
        {
            Assert.Equal("Gerente Empresa01", GerentePadraoRules.Nome("  Empresa01 "));
            Assert.Equal("Gerente", GerentePadraoRules.Nome(null));
        }

        [Fact]
        public async Task Empresa_sem_responsavel_nasce_com_o_gerente_padrao_e_o_link_de_primeiro_acesso()
        {
            var (service, repo, acesso) = Montar();
            GestaoGerentePadraoCommand? recebido = null;
            repo.Setup(r => r.CriarEmpresaComEstabelecimentoAsync(It.IsAny<SalvarEmpresaRequest>(), It.IsAny<SalvarEstabelecimentoRequest>(), It.IsAny<GestaoGerentePadraoCommand?>()))
                .Callback<SalvarEmpresaRequest, SalvarEstabelecimentoRequest, GestaoGerentePadraoCommand?>((_, _, g) => recebido = g)
                .ReturnsAsync(new GestaoProvisionamentoResultado(Empresa, Loja, 77, "gerente.empresa01@zippy.com", 1));

            var dto = await service.CriarEmpresaAsync(1, null, null, null, null, true, NovaEmpresa(responsavel: null));

            Assert.NotNull(recebido);
            Assert.Equal("Gerente Empresa01", recebido!.Nome);
            Assert.Null(recebido.Email);
            Assert.True(recebido.CriarSePreciso);
            Assert.False(string.IsNullOrWhiteSpace(recebido.SenhaHash));
            Assert.NotNull(recebido.PermissoesJson);
            Assert.NotNull(dto.AcessoResponsavel);
            Assert.Null(dto.AcessoResponsavelErro);
            acesso.Verify(a => a.GerarLinkAsync(Loja, 77, 1, It.IsAny<CancellationToken>()), Times.Once);
        }

        [Fact]
        public async Task Empresa_com_responsavel_usa_o_nome_e_o_email_informados()
        {
            var (service, repo, _) = Montar();
            GestaoGerentePadraoCommand? recebido = null;
            repo.Setup(r => r.CriarEmpresaComEstabelecimentoAsync(It.IsAny<SalvarEmpresaRequest>(), It.IsAny<SalvarEstabelecimentoRequest>(), It.IsAny<GestaoGerentePadraoCommand?>()))
                .Callback<SalvarEmpresaRequest, SalvarEstabelecimentoRequest, GestaoGerentePadraoCommand?>((_, _, g) => recebido = g)
                .ReturnsAsync(new GestaoProvisionamentoResultado(Empresa, Loja, 78, "ana@loja.com", 1));

            await service.CriarEmpresaAsync(1, null, null, null, null, true,
                NovaEmpresa(new ResponsavelRequest { Nome = " Ana ", Email = " ana@loja.com " }));

            Assert.Equal("Ana", recebido!.Nome);
            Assert.Equal("ana@loja.com", recebido.Email);
        }

        [Fact]
        public async Task Falha_ao_gerar_o_link_nao_derruba_a_criacao_e_avisa()
        {
            var (service, repo, acesso) = Montar();
            repo.Setup(r => r.CriarEmpresaComEstabelecimentoAsync(It.IsAny<SalvarEmpresaRequest>(), It.IsAny<SalvarEstabelecimentoRequest>(), It.IsAny<GestaoGerentePadraoCommand?>()))
                .ReturnsAsync(new GestaoProvisionamentoResultado(Empresa, Loja, 79, "gerente.empresa01@zippy.com", 1));
            acesso.Setup(a => a.GerarLinkAsync(It.IsAny<Guid>(), It.IsAny<int>(), It.IsAny<int?>(), It.IsAny<CancellationToken>()))
                .ThrowsAsync(new InvalidOperationException("falha"));

            var dto = await service.CriarEmpresaAsync(1, null, null, null, null, true, NovaEmpresa(null));

            Assert.Null(dto.AcessoResponsavel);
            Assert.Contains("Acesso ao painel", dto.AcessoResponsavelErro);
        }

        [Fact]
        public async Task Email_do_responsavel_ja_usado_barra_antes_de_criar_qualquer_coisa()
        {
            var (service, repo, _) = Montar();
            repo.Setup(r => r.EmailExisteAsync("ana@loja.com", null)).ReturnsAsync(true);

            await Assert.ThrowsAsync<RequestValidationException>(() => service.CriarEmpresaAsync(1, null, null, null, null, true,
                NovaEmpresa(new ResponsavelRequest { Nome = "Ana", Email = "ana@loja.com" })));

            repo.Verify(r => r.CriarEmpresaComEstabelecimentoAsync(It.IsAny<SalvarEmpresaRequest>(), It.IsAny<SalvarEstabelecimentoRequest>(), It.IsAny<GestaoGerentePadraoCommand?>()), Times.Never);
        }

        [Fact]
        public async Task Loja_em_empresa_que_ja_tem_gerente_so_vincula_e_nao_devolve_link()
        {
            var (service, repo, acesso) = Montar();
            repo.Setup(r => r.CriarEstabelecimentoAsync(It.IsAny<SalvarEstabelecimentoRequest>(), It.IsAny<GestaoGerentePadraoCommand?>()))
                .ReturnsAsync(new GestaoProvisionamentoResultado(Empresa, Loja, null, null, 2));
            repo.Setup(r => r.ObterEstabelecimentoAsync(Loja)).ReturnsAsync(new GestaoEstabelecimentoRow { Id = Loja, EmpresaId = Empresa, NomeFantasia = "Filial" });

            var dto = await service.CriarEstabelecimentoAsync(1, null, null, null, null, true, NovaLoja());

            Assert.Null(dto.AcessoGerente);
            acesso.Verify(a => a.GerarLinkAsync(It.IsAny<Guid>(), It.IsAny<int>(), It.IsAny<int?>(), It.IsAny<CancellationToken>()), Times.Never);
        }

        [Fact]
        public async Task Loja_em_empresa_sem_gerente_cria_o_padrao_com_o_nome_da_empresa_e_devolve_o_link()
        {
            var (service, repo, acesso) = Montar();
            GestaoGerentePadraoCommand? recebido = null;
            repo.Setup(r => r.CriarEstabelecimentoAsync(It.IsAny<SalvarEstabelecimentoRequest>(), It.IsAny<GestaoGerentePadraoCommand?>()))
                .Callback<SalvarEstabelecimentoRequest, GestaoGerentePadraoCommand?>((_, g) => recebido = g)
                .ReturnsAsync(new GestaoProvisionamentoResultado(Empresa, Loja, 90, "gerente.filial@zippy.com", 1));
            repo.Setup(r => r.ObterEstabelecimentoAsync(Loja)).ReturnsAsync(new GestaoEstabelecimentoRow { Id = Loja, EmpresaId = Empresa, NomeFantasia = "Filial" });

            var dto = await service.CriarEstabelecimentoAsync(1, null, null, null, null, true, NovaLoja());

            Assert.Equal("Gerente Empresa01", recebido!.Nome);
            Assert.NotNull(dto.AcessoGerente);
            acesso.Verify(a => a.GerarLinkAsync(Loja, 90, 1, It.IsAny<CancellationToken>()), Times.Once);
        }

        [Fact]
        public async Task Quem_nao_e_da_gestao_nao_cria_estabelecimento()
        {
            var (service, _, _) = Montar();

            await Assert.ThrowsAsync<UnauthorizedAccessException>(() =>
                service.CriarEstabelecimentoAsync(2, Empresa, null, "gerente_empresa", null, false, NovaLoja()));
        }

        // ---- apoio ---------------------------------------------------------------------------------------------

        private static (GestaoService Service, Mock<IGestaoRepository> Repo, Mock<IAcessoService> Acesso) Montar()
        {
            var repo = new Mock<IGestaoRepository>();
            repo.Setup(r => r.ListarPermissoesPadraoPorTipoAsync(It.IsAny<IReadOnlyCollection<string>>()))
                .ReturnsAsync(new Dictionary<string, Dictionary<string, List<string>>>(StringComparer.OrdinalIgnoreCase)
                {
                    ["gerente_estabelecimento"] = new(StringComparer.OrdinalIgnoreCase) { ["Delivery"] = new List<string> { "visualizar" } }
                });
            repo.Setup(r => r.ObterEmpresaAsync(Empresa)).ReturnsAsync(new GestaoEmpresaRow { Id = Empresa, NomeFantasia = "Empresa01" });
            repo.Setup(r => r.ObterEmpresaAsync(It.Is<Guid>(g => g == Guid.Empty))).ReturnsAsync((GestaoEmpresaRow?)null);
            repo.Setup(r => r.CriarEmpresaAsync(It.IsAny<SalvarEmpresaRequest>())).ReturnsAsync(Empresa);

            var acesso = new Mock<IAcessoService>();
            acesso.Setup(a => a.GerarLinkAsync(It.IsAny<Guid>(), It.IsAny<int>(), It.IsAny<int?>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync((Guid _, int usuario, int? _, CancellationToken _) =>
                    new AcessoLinkDto(usuario, "Gerente", "gerente@zippy.com", "primeiro_acesso", "token", DateTimeOffset.UtcNow.AddDays(7)));

            var service = new GestaoService(repo.Object, NullLogger<GestaoService>.Instance, null, null, acesso.Object);
            return (service, repo, acesso);
        }

        private static SalvarEmpresaRequest NovaEmpresa(ResponsavelRequest? responsavel) => new()
        {
            NomeFantasia = "Empresa01",
            InitialEstablishment = new SalvarEstabelecimentoRequest { NomeFantasia = "Empresa01", TipoEstabelecimentoSlug = "restaurante" },
            Responsavel = responsavel
        };

        private static SalvarEstabelecimentoRequest NovaLoja() => new()
        {
            EmpresaId = Empresa,
            NomeFantasia = "Filial",
            TipoEstabelecimentoSlug = "restaurante"
        };
    }
}
