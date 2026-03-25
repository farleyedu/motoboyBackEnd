using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using APIBack.Attributes;
using APIBack.Automation.Interfaces;
using APIBack.Automation.Models;
using APIBack.Automation.Repository.Interface;
using APIBack.DTOs.Permissoes;
using APIBack.Extensions;
using APIBack.Security;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;

namespace APIBack.Controllers
{
    [ApiController]
    [Route("api/estabelecimentos/permissoes")]
    public class VinculoPermissoesController : ControllerBase
    {
        private readonly IEstabelecimentoSelectionRepository _repository;
        private readonly IAgenteRepository _agenteRepository;
        private readonly ILogger<VinculoPermissoesController> _logger;

        public VinculoPermissoesController(
            IEstabelecimentoSelectionRepository repository,
            IAgenteRepository agenteRepository,
            ILogger<VinculoPermissoesController> logger)
        {
            _repository = repository;
            _agenteRepository = agenteRepository;
            _logger = logger;
        }

        [HttpGet("{vinculoId:guid}")]
        [RequirePermission("Configuracoes", "configurar")]
        public async Task<IActionResult> ObterPermissoes(Guid vinculoId)
        {
            var vinculo = await _repository.ObterVinculoPorIdAsync(vinculoId);
            if (vinculo == null)
            {
                return NotFound(new { success = false, error = "Vinculo nao encontrado." });
            }

            if (!PodeGerenciarVinculo(vinculo, out var erroAcesso))
            {
                return erroAcesso!;
            }

            var permissoes = ParsePermissoesStored(vinculo.PermissoesCustomizadas);
            return Ok(new
            {
                success = true,
                data = new
                {
                    vinculoId = vinculo.Id,
                    vinculo.UsuarioId,
                    vinculo.EstabelecimentoId,
                    tipoAcesso = RoleCatalog.Normalize(vinculo.TipoAcesso),
                    permissoes
                }
            });
        }

        [HttpPatch("{vinculoId:guid}")]
        [RequirePermission("Configuracoes", "configurar")]
        public async Task<IActionResult> AtualizarPermissoes(
            Guid vinculoId,
            [FromBody] AtualizarPermissoesVinculoRequest request)
        {
            if (request?.Permissoes == null || request.Permissoes.Count == 0)
            {
                return BadRequest(new { success = false, error = "Informe permissoes com pelo menos um modulo." });
            }

            var vinculo = await _repository.ObterVinculoPorIdAsync(vinculoId);
            if (vinculo == null)
            {
                return NotFound(new { success = false, error = "Vinculo nao encontrado." });
            }

            if (!PodeGerenciarVinculo(vinculo, out var erroAcesso))
            {
                return erroAcesso!;
            }

            var targetUser = await _repository.ObterUsuarioAsync(vinculo.UsuarioId);
            if (targetUser == null)
            {
                return NotFound(new { success = false, error = "Usuario do vinculo nao encontrado." });
            }

            if (!PodeAlterarPapelAlvo(vinculo.TipoAcesso, targetUser.IsSuperAdmin, out var erroHierarquia))
            {
                return erroHierarquia!;
            }

            var payload = JsonSerializer.Serialize(request.Permissoes);
            await _repository.AtualizarPermissoesCustomizadasAsync(vinculoId, payload);

            if (request.Permissoes.TryGetValue("WhatsApp", out var whatsAppAcoes)
                && whatsAppAcoes != null
                && whatsAppAcoes.Contains("assumir_conversa", StringComparer.OrdinalIgnoreCase))
            {
                try { await _agenteRepository.EnsureAgenteAsync(vinculo.UsuarioId); }
                catch (Exception ex) { _logger.LogWarning(ex, "Falha ao garantir agente para usuario {Id}", vinculo.UsuarioId); }
            }

            _logger.LogInformation("Permissoes customizadas atualizadas. Vinculo={VinculoId} UsuarioAlvo={UsuarioAlvo} UsuarioSolicitante={UsuarioSolicitante}",
                vinculoId, vinculo.UsuarioId, HttpContext.GetUserId());

            return Ok(new
            {
                success = true,
                data = new
                {
                    vinculoId = vinculo.Id,
                    permissoes = request.Permissoes
                }
            });
        }

        private bool PodeGerenciarVinculo(UsuarioEstabelecimentoAcesso vinculo, out IActionResult? erro)
        {
            erro = null;

            var actorRole = RoleCatalog.Normalize(HttpContext.GetTipoAcesso());
            var isSuperAdmin = HttpContext.IsSuperAdmin();
            var estabelecimentoToken = HttpContext.GetEstabelecimentoId();

            if (!RoleCatalog.CanManagePermissions(actorRole, isSuperAdmin))
            {
                erro = StatusCode(403, new { success = false, error = "Seu perfil nao pode gerenciar permissoes." });
                return false;
            }

            if (isSuperAdmin)
            {
                return true;
            }

            if (!estabelecimentoToken.HasValue)
            {
                erro = BadRequest(new { success = false, error = "Selecione um estabelecimento para gerenciar permissoes." });
                return false;
            }

            if (vinculo.EstabelecimentoId != estabelecimentoToken.Value)
            {
                erro = StatusCode(403, new { success = false, error = "Acesso negado ao vinculo informado." });
                return false;
            }

            return true;
        }

        private bool PodeAlterarPapelAlvo(string? targetRoleRaw, bool targetIsSuperAdmin, out IActionResult? erro)
        {
            erro = null;

            var actorRole = RoleCatalog.Normalize(HttpContext.GetTipoAcesso());
            var actorIsSuperAdmin = HttpContext.IsSuperAdmin();
            var actorRank = RoleCatalog.Rank(actorRole, actorIsSuperAdmin);
            var targetRank = RoleCatalog.Rank(targetRoleRaw, targetIsSuperAdmin);

            if (!actorIsSuperAdmin && actorRank <= targetRank)
            {
                erro = StatusCode(403, new { success = false, error = "Voce so pode alterar permissoes de papeis abaixo do seu nivel." });
                return false;
            }

            return true;
        }

        private static Dictionary<string, List<string>> ParsePermissoesStored(string? raw)
        {
            if (string.IsNullOrWhiteSpace(raw))
            {
                return new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
            }

            try
            {
                var result = JsonSerializer.Deserialize<Dictionary<string, List<string>>>(raw,
                    new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
                return result ?? new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
            }
            catch
            {
                return new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
            }
        }
    }
}
