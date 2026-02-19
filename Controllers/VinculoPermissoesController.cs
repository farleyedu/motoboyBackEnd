using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using APIBack.Attributes;
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
        private readonly ILogger<VinculoPermissoesController> _logger;

        public VinculoPermissoesController(
            IEstabelecimentoSelectionRepository repository,
            ILogger<VinculoPermissoesController> logger)
        {
            _repository = repository;
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

            ParseExistingCustomPermissions(vinculo.PermissoesCustomizadas, out var grants, out var revokes);
            return Ok(new
            {
                success = true,
                data = new
                {
                    vinculoId = vinculo.Id,
                    vinculo.UsuarioId,
                    vinculo.EstabelecimentoId,
                    tipoAcesso = RoleCatalog.Normalize(vinculo.TipoAcesso),
                    grants = ToResponseMap(grants),
                    revokes = ToResponseMap(revokes)
                }
            });
        }

        [HttpPatch("{vinculoId:guid}")]
        [RequirePermission("Configuracoes", "configurar")]
        public async Task<IActionResult> AtualizarPermissoes(
            Guid vinculoId,
            [FromBody] AtualizarPermissoesVinculoRequest request)
        {
            if (request == null || !HasAnyAction(request))
            {
                return BadRequest(new { success = false, error = "Informe grants ou revokes com pelo menos uma acao." });
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

            ParseExistingCustomPermissions(vinculo.PermissoesCustomizadas, out var grants, out var revokes);

            MergePermissions(grants, revokes, request.Grants, isGrant: true);
            MergePermissions(grants, revokes, request.Revokes, isGrant: false);

            var payload = SerializeCustomPermissions(grants, revokes);
            await _repository.AtualizarPermissoesCustomizadasAsync(vinculoId, payload);

            _logger.LogInformation("Permissoes customizadas atualizadas. Vinculo={VinculoId} UsuarioAlvo={UsuarioAlvo} UsuarioSolicitante={UsuarioSolicitante}",
                vinculoId, vinculo.UsuarioId, HttpContext.GetUserId());

            return Ok(new
            {
                success = true,
                data = new
                {
                    vinculoId = vinculo.Id,
                    grants = ToResponseMap(grants),
                    revokes = ToResponseMap(revokes)
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

        private static bool HasAnyAction(AtualizarPermissoesVinculoRequest request)
        {
            return HasAnyAction(request.Grants) || HasAnyAction(request.Revokes);
        }

        private static bool HasAnyAction(Dictionary<string, List<string>>? source)
        {
            if (source == null || source.Count == 0)
            {
                return false;
            }

            foreach (var kvp in source)
            {
                if (string.IsNullOrWhiteSpace(kvp.Key) || kvp.Value == null)
                {
                    continue;
                }

                if (kvp.Value.Any(v => !string.IsNullOrWhiteSpace(v)))
                {
                    return true;
                }
            }

            return false;
        }

        private static void ParseExistingCustomPermissions(
            string? raw,
            out Dictionary<string, HashSet<string>> grants,
            out Dictionary<string, HashSet<string>> revokes)
        {
            grants = new Dictionary<string, HashSet<string>>(StringComparer.OrdinalIgnoreCase);
            revokes = new Dictionary<string, HashSet<string>>(StringComparer.OrdinalIgnoreCase);

            if (string.IsNullOrWhiteSpace(raw))
            {
                return;
            }

            try
            {
                using var doc = JsonDocument.Parse(raw);
                var root = doc.RootElement;

                if (root.ValueKind != JsonValueKind.Object)
                {
                    return;
                }

                var parsedGrants = ReadMap(root, "grants")
                    ?? ReadMap(root, "allow")
                    ?? ReadMap(root, "permissions")
                    ?? ReadMap(root, "permissoes");
                var parsedRevokes = ReadMap(root, "revokes") ?? ReadMap(root, "deny");

                if (parsedGrants == null)
                {
                    parsedGrants = ReadDirectMap(root);
                }

                if (parsedGrants != null)
                {
                    grants = parsedGrants;
                }

                if (parsedRevokes != null)
                {
                    revokes = parsedRevokes;
                }
            }
            catch
            {
                // Ignora legado invalido sem impedir atualizacao.
            }
        }

        private static Dictionary<string, HashSet<string>>? ReadMap(JsonElement root, string propertyName)
        {
            if (!root.TryGetProperty(propertyName, out var node) || node.ValueKind != JsonValueKind.Object)
            {
                return null;
            }

            var result = new Dictionary<string, HashSet<string>>(StringComparer.OrdinalIgnoreCase);
            foreach (var moduloNode in node.EnumerateObject())
            {
                var modulo = NormalizeName(moduloNode.Name);
                if (string.IsNullOrWhiteSpace(modulo))
                {
                    continue;
                }

                var actions = ReadActions(moduloNode.Value);
                if (actions.Count > 0)
                {
                    result[modulo] = actions;
                }
            }

            return result;
        }

        private static Dictionary<string, HashSet<string>>? ReadDirectMap(JsonElement root)
        {
            var result = new Dictionary<string, HashSet<string>>(StringComparer.OrdinalIgnoreCase);
            foreach (var prop in root.EnumerateObject())
            {
                var key = NormalizeName(prop.Name);
                if (string.IsNullOrWhiteSpace(key))
                {
                    continue;
                }

                if (string.Equals(key, "grants", StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(key, "allow", StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(key, "permissions", StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(key, "permissoes", StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(key, "revokes", StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(key, "deny", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                var actions = ReadActions(prop.Value);
                if (actions.Count > 0)
                {
                    result[key] = actions;
                }
            }

            return result.Count > 0 ? result : null;
        }

        private static HashSet<string> ReadActions(JsonElement node)
        {
            var result = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            if (node.ValueKind == JsonValueKind.String)
            {
                var value = NormalizeName(node.GetString());
                if (!string.IsNullOrWhiteSpace(value))
                {
                    result.Add(value);
                }

                return result;
            }

            if (node.ValueKind != JsonValueKind.Array)
            {
                return result;
            }

            foreach (var item in node.EnumerateArray())
            {
                if (item.ValueKind != JsonValueKind.String)
                {
                    continue;
                }

                var action = NormalizeName(item.GetString());
                if (!string.IsNullOrWhiteSpace(action))
                {
                    result.Add(action);
                }
            }

            return result;
        }

        private static void MergePermissions(
            Dictionary<string, HashSet<string>> grants,
            Dictionary<string, HashSet<string>> revokes,
            Dictionary<string, List<string>>? source,
            bool isGrant)
        {
            if (source == null || source.Count == 0)
            {
                return;
            }

            foreach (var kvp in source)
            {
                var modulo = NormalizeName(kvp.Key);
                if (string.IsNullOrWhiteSpace(modulo))
                {
                    continue;
                }

                var actions = kvp.Value?
                    .Where(a => !string.IsNullOrWhiteSpace(a))
                    .Select(NormalizeName)
                    .Where(a => !string.IsNullOrWhiteSpace(a))
                    .ToList() ?? new List<string>();

                if (actions.Count == 0)
                {
                    continue;
                }

                if (isGrant)
                {
                    if (!grants.TryGetValue(modulo, out var grantSet))
                    {
                        grantSet = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                        grants[modulo] = grantSet;
                    }

                    if (!revokes.TryGetValue(modulo, out var revokeSet))
                    {
                        revokeSet = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                        revokes[modulo] = revokeSet;
                    }

                    foreach (var action in actions)
                    {
                        grantSet.Add(action);
                        revokeSet.Remove(action);
                    }
                }
                else
                {
                    if (!revokes.TryGetValue(modulo, out var revokeSet))
                    {
                        revokeSet = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                        revokes[modulo] = revokeSet;
                    }

                    if (!grants.TryGetValue(modulo, out var grantSet))
                    {
                        grantSet = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                        grants[modulo] = grantSet;
                    }

                    foreach (var action in actions)
                    {
                        revokeSet.Add(action);
                        grantSet.Remove(action);
                    }
                }
            }

            CleanupMap(grants);
            CleanupMap(revokes);
        }

        private static void CleanupMap(Dictionary<string, HashSet<string>> map)
        {
            foreach (var key in map.Keys.ToList())
            {
                if (map[key] == null || map[key].Count == 0)
                {
                    map.Remove(key);
                }
            }
        }

        private static string? SerializeCustomPermissions(
            Dictionary<string, HashSet<string>> grants,
            Dictionary<string, HashSet<string>> revokes)
        {
            CleanupMap(grants);
            CleanupMap(revokes);

            if (grants.Count == 0 && revokes.Count == 0)
            {
                return null;
            }

            var payload = new Dictionary<string, object>(StringComparer.OrdinalIgnoreCase);
            if (grants.Count > 0)
            {
                payload["grants"] = ToResponseMap(grants);
            }

            if (revokes.Count > 0)
            {
                payload["revokes"] = ToResponseMap(revokes);
            }

            return JsonSerializer.Serialize(payload);
        }

        private static Dictionary<string, List<string>> ToResponseMap(Dictionary<string, HashSet<string>> map)
        {
            return map
                .Where(kvp => kvp.Value != null && kvp.Value.Count > 0)
                .ToDictionary(
                    kvp => kvp.Key,
                    kvp => kvp.Value
                        .OrderBy(v => v, StringComparer.OrdinalIgnoreCase)
                        .ToList(),
                    StringComparer.OrdinalIgnoreCase);
        }

        private static string NormalizeName(string? value)
        {
            return string.IsNullOrWhiteSpace(value) ? string.Empty : value.Trim();
        }
    }
}
