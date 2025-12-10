using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using APIBack.Automation.Dtos.Estabelecimentos;
using APIBack.Automation.Models;
using APIBack.Automation.Repository.Interface;
using APIBack.Automation.Services.Interface;
using APIBack.Automation.Validators;
using APIBack.Model.Auth;
using APIBack.Service.Interface;
using Microsoft.Extensions.Logging;

namespace APIBack.Automation.Services
{
    public class EstabelecimentoSelectionService : IEstabelecimentoSelectionService
    {
        private readonly IEstabelecimentoSelectionRepository _repository;
        private readonly EstabelecimentoSelectionValidator _validator;
        private readonly IJwtService _jwtService;
        private readonly ILogger<EstabelecimentoSelectionService> _logger;

        public EstabelecimentoSelectionService(
            IEstabelecimentoSelectionRepository repository,
            EstabelecimentoSelectionValidator validator,
            IJwtService jwtService,
            ILogger<EstabelecimentoSelectionService> logger)
        {
            _repository = repository ?? throw new ArgumentNullException(nameof(repository));
            _validator = validator ?? throw new ArgumentNullException(nameof(validator));
            _jwtService = jwtService ?? throw new ArgumentNullException(nameof(jwtService));
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        }

        public async Task<IReadOnlyCollection<UsuarioEstabelecimentoDto>> ListarEstabelecimentosAsync(int userId)
        {
            var usuario = _validator.EnsureUsuarioValido(await _repository.ObterUsuarioAsync(userId));

            if (usuario.IsSuperAdmin)
            {
                var estabelecimentos = await _repository.ListarEstabelecimentosAtivosAsync();
                return estabelecimentos
                    .Select(est => MapSuperAdminEstabelecimento(usuario, est))
                    .ToArray();
            }

            var vinculos = await _repository.ListarEstabelecimentosUsuarioAsync(userId);
            return vinculos
                .Select(v => MapUsuarioEstabelecimento(usuario, v))
                .ToArray();
        }

        public async Task<DefinirEstabelecimentoAtivoResponse> DefinirEstabelecimentoAtivoAsync(int userId, Guid estabelecimentoId)
        {
            var usuario = _validator.EnsureUsuarioValido(await _repository.ObterUsuarioAsync(userId));
            var estabelecimento = _validator.EnsureEstabelecimentoSelecionavel(
                await _repository.ObterEstabelecimentoDetalheAsync(estabelecimentoId));

            var vinculo = await _repository.ObterVinculoAsync(userId, estabelecimentoId);
            _validator.EnsureVinculoPermitido(vinculo, usuario.IsSuperAdmin);

            await _repository.AtualizarUltimoEstabelecimentoAsync(userId, estabelecimentoId);

            var tipoAcesso = usuario.IsSuperAdmin ? "super_admin" : vinculo?.TipoAcesso;
            var permissoes = await _repository.ObterPermissoesPorTipoAsync(tipoAcesso);

            var payload = new JwtPayload
            {
                UserId = usuario.Id,
                Nome = usuario.Nome,
                Email = usuario.Email,
                IsSuperAdmin = usuario.IsSuperAdmin,
                EstabelecimentoId = estabelecimento.Id,
                EstabelecimentoNome = estabelecimento.Nome,
                TipoEstabelecimento = estabelecimento.TipoEstabelecimento,
                TipoAcesso = tipoAcesso,
                VinculoId = vinculo?.Id,
                Permissoes = permissoes
            };

            var token = _jwtService.GenerateToken(payload);
            var refreshToken = _jwtService.GenerateRefreshToken();

            _logger.LogInformation(
                "Estabelecimento definido para usuário {UserId}: {EstabelecimentoId}",
                usuario.Id, estabelecimento.Id);

            return new DefinirEstabelecimentoAtivoResponse
            {
                Token = token,
                RefreshToken = refreshToken,
                EstabelecimentoSelecionado = new EstabelecimentoSelecionadoDto
                {
                    Id = estabelecimento.Id,
                    Nome = estabelecimento.Nome,
                    TipoEstabelecimento = estabelecimento.TipoEstabelecimento,
                    Plano = estabelecimento.Plano ?? string.Empty,
                    Status = _validator.NormalizarStatusEstabelecimento(estabelecimento.Status, estabelecimento.Ativo)
                }
            };
        }

        private UsuarioEstabelecimentoDto MapUsuarioEstabelecimento(UsuarioTenant usuario, UsuarioEstabelecimentoVinculo vinculo)
        {
            return new UsuarioEstabelecimentoDto
            {
                EstabelecimentoId = vinculo.EstabelecimentoId,
                Nome = vinculo.Nome,
                TipoEstabelecimento = vinculo.TipoEstabelecimento,
                StatusVinculo = vinculo.StatusVinculo,
                StatusEstabelecimento = _validator.NormalizarStatusEstabelecimento(vinculo.StatusEstabelecimento, vinculo.EstabelecimentoAtivo),
                IsAtual = usuario.UltimoEstabelecimentoAcessado.HasValue &&
                          usuario.UltimoEstabelecimentoAcessado.Value == vinculo.EstabelecimentoId,
                IsSuperAdminAccess = false,
                TipoAcesso = vinculo.TipoAcesso
            };
        }

        private UsuarioEstabelecimentoDto MapSuperAdminEstabelecimento(UsuarioTenant usuario, EstabelecimentoAtivoResumo estabelecimento)
        {
            return new UsuarioEstabelecimentoDto
            {
                EstabelecimentoId = estabelecimento.EstabelecimentoId,
                Nome = estabelecimento.Nome,
                TipoEstabelecimento = estabelecimento.TipoEstabelecimento,
                StatusEstabelecimento = _validator.NormalizarStatusEstabelecimento(
                    estabelecimento.StatusEstabelecimento,
                    estabelecimento.EstabelecimentoAtivo),
                StatusVinculo = "super_admin",
                IsAtual = usuario.UltimoEstabelecimentoAcessado.HasValue &&
                          usuario.UltimoEstabelecimentoAcessado.Value == estabelecimento.EstabelecimentoId,
                IsSuperAdminAccess = true,
                TipoAcesso = "super_admin"
            };
        }
    }
}
