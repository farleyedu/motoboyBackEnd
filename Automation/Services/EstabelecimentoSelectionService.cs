using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using APIBack.Automation.Dtos.Estabelecimentos;
using APIBack.Automation.Interfaces;
using APIBack.Automation.Models;
using APIBack.Automation.Repository.Interface;
using APIBack.Automation.Services.Interface;
using APIBack.Automation.Validators;
using APIBack.Model.Auth;
using APIBack.Model.Gestao;
using APIBack.Security;
using APIBack.Service.Interface;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

namespace APIBack.Automation.Services
{
    public class EstabelecimentoSelectionService : IEstabelecimentoSelectionService
    {
        private readonly IEstabelecimentoSelectionRepository _repository;
        private readonly EstabelecimentoSelectionValidator _validator;
        private readonly IJwtService _jwtService;
        private readonly ILogger<EstabelecimentoSelectionService> _logger;
        private readonly int _jwtExpirationSeconds;
        private readonly IAgenteRepository? _agenteRepository;

        public EstabelecimentoSelectionService(
            IEstabelecimentoSelectionRepository repository,
            EstabelecimentoSelectionValidator validator,
            IJwtService jwtService,
            ILogger<EstabelecimentoSelectionService> logger)
            : this(repository, validator, jwtService, null, logger, null)
        {
        }

        public EstabelecimentoSelectionService(
            IEstabelecimentoSelectionRepository repository,
            EstabelecimentoSelectionValidator validator,
            IJwtService jwtService,
            IConfiguration? configuration,
            ILogger<EstabelecimentoSelectionService> logger,
            IAgenteRepository? agenteRepository = null)
        {
            _repository = repository ?? throw new ArgumentNullException(nameof(repository));
            _validator = validator ?? throw new ArgumentNullException(nameof(validator));
            _jwtService = jwtService ?? throw new ArgumentNullException(nameof(jwtService));
            var expirationMinutes = int.TryParse(configuration?["Jwt:ExpirationMinutes"], out var minutes)
                ? minutes
                : 60;
            _jwtExpirationSeconds = expirationMinutes * 60;
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));
            _agenteRepository = agenteRepository;
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
            var vinculoEmpresa = estabelecimento != null
                ? await _repository.ObterVinculoEmpresaAsync(userId, estabelecimento.EmpresaId)
                : null;
            _validator.EnsureVinculoPermitido(vinculo, usuario.IsSuperAdmin);

            await _repository.AtualizarUltimoEstabelecimentoAsync(userId, estabelecimentoId);

            var tipoAcesso = usuario.IsSuperAdmin
                ? "super_admin"
                : RoleCatalog.Normalize(vinculo?.TipoAcesso);
            var tipoAcessoEmpresa = usuario.IsSuperAdmin
                ? "super_admin"
                : RoleCatalog.Normalize(vinculoEmpresa?.TipoAcesso);

            var permissoes = ParsePermissoesCustomizadas(vinculo?.PermissoesCustomizadas);
            var modulosUi = ResolveUiModules(estabelecimento.Nome, estabelecimento.ModulosAtivosRaw)
                .ToHashSet(StringComparer.OrdinalIgnoreCase);
            if (modulosUi.Count > 0 && permissoes.Keys.Any(k => modulosUi.Contains(k)))
            {
                permissoes = permissoes
                    .Where(kvp => modulosUi.Contains(kvp.Key))
                    .ToDictionary(kvp => kvp.Key, kvp => kvp.Value, StringComparer.OrdinalIgnoreCase);
            }
            await TryEnsureAgenteSeNecessarioAsync(usuario.Id, permissoes);

            var payload = new JwtPayload
            {
                UserId = usuario.Id,
                Nome = usuario.Nome,
                Email = usuario.Email,
                IsSuperAdmin = usuario.IsSuperAdmin,
                EmpresaId = estabelecimento.EmpresaId,
                EmpresaNome = estabelecimento.EmpresaNome,
                TipoAcessoEmpresa = tipoAcessoEmpresa,
                EmpresaVinculoId = vinculoEmpresa?.Id,
                EstabelecimentoId = estabelecimento.Id,
                EstabelecimentoNome = estabelecimento.Nome,
                TipoEstabelecimento = estabelecimento.TipoEstabelecimento,
                EstabelecimentoModulosAtivos = ResolveUiModules(estabelecimento.Nome, estabelecimento.ModulosAtivosRaw),
                TipoAcesso = tipoAcesso,
                VinculoId = vinculo?.Id,
                Permissoes = permissoes
            };

            var token = _jwtService.GenerateToken(payload);
            var refreshToken = _jwtService.GenerateRefreshToken();

            _logger.LogInformation(
                "Estabelecimento definido para usuario {UserId}: {EstabelecimentoId}",
                usuario.Id, estabelecimento.Id);

            return new DefinirEstabelecimentoAtivoResponse
            {
                AccessToken = token,
                RefreshToken = refreshToken,
                TokenType = "Bearer",
                ExpiresIn = _jwtExpirationSeconds,
                EstabelecimentoSelecionado = new EstabelecimentoSelecionadoDto
                {
                    Id = estabelecimento.Id,
                    Nome = estabelecimento.Nome,
                    TipoEstabelecimento = estabelecimento.TipoEstabelecimento,
                    ModulosAtivos = ResolveUiModules(estabelecimento.Nome, estabelecimento.ModulosAtivosRaw),
                    Plano = estabelecimento.Plano ?? string.Empty,
                    Status = _validator.NormalizarStatusEstabelecimento(estabelecimento.Status, estabelecimento.Ativo)
                }
            };
        }

        private async Task TryEnsureAgenteSeNecessarioAsync(int userId, Dictionary<string, List<string>> permissoes)
        {
            if (_agenteRepository == null) return;
            if (!permissoes.TryGetValue("WhatsApp", out var actions)) return;
            if (!actions.Any(a => string.Equals(a, "assumir_conversa", StringComparison.OrdinalIgnoreCase))) return;
            try { await _agenteRepository.EnsureAgenteAsync(userId); }
            catch (Exception ex) { _logger.LogWarning(ex, "Falha ao garantir agente no login para usuario {Id}", userId); }
        }

        private static void MergePermissoes(
            Dictionary<string, List<string>> destino,
            Dictionary<string, List<string>> origem)
        {
            if (destino == null)
            {
                return;
            }

            if (origem == null)
            {
                return;
            }

            foreach (var kvp in origem)
            {
                if (!destino.TryGetValue(kvp.Key, out var existentes))
                {
                    existentes = new List<string>();
                    destino[kvp.Key] = existentes;
                }

                foreach (var permissao in kvp.Value)
                {
                    if (!existentes.Any(item => string.Equals(item, permissao, StringComparison.OrdinalIgnoreCase)))
                    {
                        existentes.Add(permissao);
                    }
                }
            }
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
                TipoAcesso = RoleCatalog.Normalize(vinculo.TipoAcesso),
                ModulosAtivos = ResolveUiModules(vinculo.Nome, vinculo.ModulosAtivosRaw)
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
                TipoAcesso = "super_admin",
                ModulosAtivos = ResolveUiModules(estabelecimento.Nome, estabelecimento.ModulosAtivosRaw)
            };
        }

        private static List<string> ResolveUiModules(string? establishmentName, string[]? rawModules)
        {
            return EstabelecimentoModuleMapper.ToUiModules(establishmentName ?? string.Empty, rawModules);
        }

        private static void ApplyCustomPermissions(
            Dictionary<string, List<string>> permissoesBase,
            string? permissoesCustomizadasRaw)
        {
            if (string.IsNullOrWhiteSpace(permissoesCustomizadasRaw))
            {
                return;
            }

            try
            {
                using var doc = JsonDocument.Parse(permissoesCustomizadasRaw);
                var root = doc.RootElement;

                if (root.ValueKind == JsonValueKind.Array)
                {
                    var acoes = ReadActions(root);
                    foreach (var modulo in permissoesBase.Keys.ToList())
                    {
                        foreach (var acao in acoes)
                        {
                            if (!permissoesBase[modulo].Any(x => string.Equals(x, acao, StringComparison.OrdinalIgnoreCase)))
                            {
                                permissoesBase[modulo].Add(acao);
                            }
                        }
                    }

                    return;
                }

                if (root.ValueKind != JsonValueKind.Object)
                {
                    return;
                }

                var grants = ReadModuleMap(root, "grants")
                             ?? ReadModuleMap(root, "allow")
                             ?? ReadModuleMap(root, "permissoes")
                             ?? ReadModuleMap(root, "permissions");

                if (grants == null)
                {
                    grants = ReadDirectModuleMap(root);
                }

                var revokes = ReadModuleMap(root, "revokes")
                              ?? ReadModuleMap(root, "deny");

                if (grants != null)
                {
                    foreach (var kvp in grants)
                    {
                        if (!permissoesBase.TryGetValue(kvp.Key, out var existentes))
                        {
                            existentes = new List<string>();
                            permissoesBase[kvp.Key] = existentes;
                        }

                        foreach (var acao in kvp.Value)
                        {
                            if (!existentes.Any(x => string.Equals(x, acao, StringComparison.OrdinalIgnoreCase)))
                            {
                                existentes.Add(acao);
                            }
                        }
                    }
                }

                if (revokes != null)
                {
                    foreach (var kvp in revokes)
                    {
                        if (!permissoesBase.TryGetValue(kvp.Key, out var existentes) || existentes.Count == 0)
                        {
                            continue;
                        }

                        existentes.RemoveAll(acao =>
                            kvp.Value.Any(rev => string.Equals(rev, acao, StringComparison.OrdinalIgnoreCase)));
                    }
                }
            }
            catch
            {
                // Ignora payload legado invalido sem interromper selecao de estabelecimento.
            }
        }

        private static Dictionary<string, List<string>>? ReadModuleMap(JsonElement root, string propertyName)
        {
            if (!root.TryGetProperty(propertyName, out var node) || node.ValueKind != JsonValueKind.Object)
            {
                return null;
            }

            var result = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
            foreach (var prop in node.EnumerateObject())
            {
                var modulo = prop.Name?.Trim();
                if (string.IsNullOrWhiteSpace(modulo))
                {
                    continue;
                }

                result[modulo] = ReadActions(prop.Value);
            }

            return result;
        }

        private static Dictionary<string, List<string>>? ReadDirectModuleMap(JsonElement root)
        {
            var result = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
            foreach (var prop in root.EnumerateObject())
            {
                var key = prop.Name?.Trim();
                if (string.IsNullOrWhiteSpace(key))
                {
                    continue;
                }

                if (string.Equals(key, "grants", StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(key, "revokes", StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(key, "allow", StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(key, "deny", StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(key, "permissions", StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(key, "permissoes", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                if (prop.Value.ValueKind != JsonValueKind.Array &&
                    prop.Value.ValueKind != JsonValueKind.String)
                {
                    continue;
                }

                result[key] = ReadActions(prop.Value);
            }

            return result.Count > 0 ? result : null;
        }

        private static List<string> ReadActions(JsonElement node)
        {
            if (node.ValueKind == JsonValueKind.String)
            {
                var single = node.GetString();
                return string.IsNullOrWhiteSpace(single)
                    ? new List<string>()
                    : new List<string> { single.Trim() };
            }

            var list = new List<string>();
            if (node.ValueKind != JsonValueKind.Array)
            {
                return list;
            }

            foreach (var item in node.EnumerateArray())
            {
                if (item.ValueKind != JsonValueKind.String)
                {
                    continue;
                }

                var action = item.GetString();
                if (!string.IsNullOrWhiteSpace(action))
                {
                    list.Add(action.Trim());
                }
            }

            return list
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();
        }

        private static Dictionary<string, List<string>> ParsePermissoesCustomizadas(string? raw)
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
