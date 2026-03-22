using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using APIBack.Automation.Models;
using APIBack.Automation.Repository.Interface;
using APIBack.Automation.Services;
using APIBack.Automation.Validators;
using APIBack.Model.Auth;
using APIBack.Service.Interface;
using Microsoft.Extensions.Logging;
using Moq;
using Xunit;

namespace APIBack.Tests.Unit
{
    public class EstabelecimentoSelectionServiceTests
    {
        private readonly Mock<IEstabelecimentoSelectionRepository> _repositoryMock = new();
        private readonly Mock<IJwtService> _jwtServiceMock = new();
        private readonly EstabelecimentoSelectionValidator _validator = new();
        private readonly Mock<ILogger<EstabelecimentoSelectionService>> _loggerMock = new();
        private readonly EstabelecimentoSelectionService _service;

        public EstabelecimentoSelectionServiceTests()
        {
            _service = new EstabelecimentoSelectionService(
                _repositoryMock.Object,
                _validator,
                _jwtServiceMock.Object,
                _loggerMock.Object);
        }

        [Fact]
        public async Task DefinirEstabelecimentoAtivoAsync_UsuarioSemVinculo_DeveLancarForbidden()
        {
            var usuario = CreateUsuario();
            var estabelecimento = CreateEstabelecimentoDetalhe();

            _repositoryMock.Setup(r => r.ObterUsuarioAsync(usuario.Id)).ReturnsAsync(usuario);
            _repositoryMock.Setup(r => r.ObterEstabelecimentoDetalheAsync(estabelecimento.Id)).ReturnsAsync(estabelecimento);
            _repositoryMock.Setup(r => r.ObterVinculoAsync(usuario.Id, estabelecimento.Id))
                .ReturnsAsync((UsuarioEstabelecimentoAcesso?)null);

            await Assert.ThrowsAsync<UnauthorizedAccessException>(() =>
                _service.DefinirEstabelecimentoAtivoAsync(usuario.Id, estabelecimento.Id));
        }

        [Fact]
        public async Task DefinirEstabelecimentoAtivoAsync_UsuarioComVinculoAtivo_DeveRetornarToken()
        {
            var usuario = CreateUsuario();
            var estabelecimento = CreateEstabelecimentoDetalhe();
            JwtPayload? payloadCapturado = null;
            var vinculo = new UsuarioEstabelecimentoAcesso
            {
                Id = Guid.NewGuid(),
                EstabelecimentoId = estabelecimento.Id,
                UsuarioId = usuario.Id,
                Status = "ativo",
                VinculoAtivo = true,
                TipoAcesso = "manager"
            };

            _repositoryMock.Setup(r => r.ObterUsuarioAsync(usuario.Id)).ReturnsAsync(usuario);
            _repositoryMock.Setup(r => r.ObterEstabelecimentoDetalheAsync(estabelecimento.Id)).ReturnsAsync(estabelecimento);
            _repositoryMock.Setup(r => r.ObterVinculoAsync(usuario.Id, estabelecimento.Id)).ReturnsAsync(vinculo);
            _repositoryMock.Setup(r => r.ObterPermissoesPorTipoAsync(vinculo.TipoAcesso))
                .ReturnsAsync(new Dictionary<string, List<string>> { { "reservas", new List<string> { "read" } } });
            _jwtServiceMock
                .Setup(j => j.GenerateToken(It.IsAny<JwtPayload>()))
                .Callback<JwtPayload>(payload => payloadCapturado = payload)
                .Returns("token-value");
            _jwtServiceMock.Setup(j => j.GenerateRefreshToken()).Returns("refresh-value");

            var response = await _service.DefinirEstabelecimentoAtivoAsync(usuario.Id, estabelecimento.Id);

            Assert.Equal("token-value", response.Token);
            Assert.Equal("refresh-value", response.RefreshToken);
            Assert.Equal(estabelecimento.Id, response.EstabelecimentoSelecionado.Id);
            Assert.Contains("WhatsApp", response.EstabelecimentoSelecionado.ModulosAtivos);
            Assert.Contains("Nautica", response.EstabelecimentoSelecionado.ModulosAtivos);
            Assert.NotNull(payloadCapturado);
            Assert.Contains("WhatsApp", payloadCapturado!.EstabelecimentoModulosAtivos);
            Assert.Contains("Nautica", payloadCapturado.EstabelecimentoModulosAtivos);
            _repositoryMock.Verify(r => r.AtualizarUltimoEstabelecimentoAsync(usuario.Id, estabelecimento.Id), Times.Once);
        }

        [Fact]
        public async Task DefinirEstabelecimentoAtivoAsync_SuperAdminSemVinculo_DeveRetornarToken()
        {
            var usuario = CreateUsuario(isSuperAdmin: true);
            var estabelecimento = CreateEstabelecimentoDetalhe();
            JwtPayload? payloadCapturado = null;

            _repositoryMock.Setup(r => r.ObterUsuarioAsync(usuario.Id)).ReturnsAsync(usuario);
            _repositoryMock.Setup(r => r.ObterEstabelecimentoDetalheAsync(estabelecimento.Id)).ReturnsAsync(estabelecimento);
            _repositoryMock.Setup(r => r.ObterVinculoAsync(usuario.Id, estabelecimento.Id))
                .ReturnsAsync((UsuarioEstabelecimentoAcesso?)null);
            _repositoryMock.Setup(r => r.ObterPermissoesPorTipoAsync("super_admin"))
                .ReturnsAsync(new Dictionary<string, List<string>>());
            _jwtServiceMock
                .Setup(j => j.GenerateToken(It.IsAny<JwtPayload>()))
                .Callback<JwtPayload>(payload => payloadCapturado = payload)
                .Returns("admin-token");
            _jwtServiceMock.Setup(j => j.GenerateRefreshToken()).Returns("admin-refresh");

            var response = await _service.DefinirEstabelecimentoAtivoAsync(usuario.Id, estabelecimento.Id);

            Assert.Equal("admin-token", response.Token);
            Assert.Equal(estabelecimento.Id, response.EstabelecimentoSelecionado.Id);
            Assert.NotNull(payloadCapturado);
            Assert.Contains("Nautica", payloadCapturado!.EstabelecimentoModulosAtivos);
        }

        [Fact]
        public async Task ListarEstabelecimentosAsync_DeveRetornarModulosAtivosDoEstabelecimento()
        {
            var usuario = CreateUsuario();
            var estabelecimentoId = Guid.NewGuid();

            _repositoryMock.Setup(r => r.ObterUsuarioAsync(usuario.Id)).ReturnsAsync(usuario);
            _repositoryMock.Setup(r => r.ListarEstabelecimentosUsuarioAsync(usuario.Id))
                .ReturnsAsync(new[]
                {
                    new UsuarioEstabelecimentoVinculo
                    {
                        VinculoId = Guid.NewGuid(),
                        EstabelecimentoId = estabelecimentoId,
                        Nome = "Amazon Nautica",
                        TipoEstabelecimento = "nautica",
                        StatusVinculo = "ativo",
                        StatusEstabelecimento = "ativo",
                        EstabelecimentoAtivo = true,
                        VinculoAtivo = true,
                        TipoAcesso = "gerente_estabelecimento",
                        ModulosAtivosRaw = new[] { "GERAL", "WHATSAPP", "NAUTICA" }
                    }
                });

            var response = await _service.ListarEstabelecimentosAsync(usuario.Id);

            var estabelecimento = Assert.Single(response);
            Assert.Contains("WhatsApp", estabelecimento.ModulosAtivos);
            Assert.Contains("Nautica", estabelecimento.ModulosAtivos);
        }

        [Fact]
        public async Task DefinirEstabelecimentoAtivoAsync_EstabelecimentoInativo_DeveLancar()
        {
            var usuario = CreateUsuario();
            var estabelecimento = new EstabelecimentoDetalhe
            {
                Id = Guid.NewGuid(),
                Nome = "Barbearia XPTO",
                TipoEstabelecimento = "barbearia",
                Plano = "pro",
                Status = "suspenso",
                Ativo = false
            };

            _repositoryMock.Setup(r => r.ObterUsuarioAsync(usuario.Id)).ReturnsAsync(usuario);
            _repositoryMock.Setup(r => r.ObterEstabelecimentoDetalheAsync(estabelecimento.Id)).ReturnsAsync(estabelecimento);

            await Assert.ThrowsAsync<InvalidOperationException>(() =>
                _service.DefinirEstabelecimentoAtivoAsync(usuario.Id, estabelecimento.Id));
        }

        private static UsuarioTenant CreateUsuario(bool isSuperAdmin = false)
        {
            return new UsuarioTenant
            {
                Id = 42,
                Nome = "João",
                Email = "joao@example.com",
                IsSuperAdmin = isSuperAdmin
            };
        }

        private static EstabelecimentoDetalhe CreateEstabelecimentoDetalhe()
        {
            return new EstabelecimentoDetalhe
            {
                Id = Guid.NewGuid(),
                Nome = "Amazon Nautica",
                TipoEstabelecimento = "nautica",
                Plano = "premium",
                Status = "ativo",
                Ativo = true,
                ModulosAtivosRaw = new[] { "GERAL", "WHATSAPP", "NAUTICA" }
            };
        }
    }
}
