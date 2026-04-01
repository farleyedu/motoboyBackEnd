using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Mail;
using System.Text.Json;
using System.Text;
using System.Threading.Tasks;
using APIBack.Automation.Interfaces;
using APIBack.Automation.Models;
using APIBack.DTOs.Gestao;
using APIBack.Model.Gestao;
using APIBack.Repository.Interface;
using APIBack.Security;
using APIBack.Service.Interface;
using Microsoft.Extensions.Logging;

namespace APIBack.Service
{
    public class GestaoService : IGestaoService
    {
        private readonly IGestaoRepository _repository;
        private readonly ILogger<GestaoService> _logger;
        private readonly IWabaPhoneRepository? _wabaPhoneRepository;
        private readonly IAgenteRepository? _agenteRepository;

        public GestaoService(IGestaoRepository repository, ILogger<GestaoService> logger, IWabaPhoneRepository? wabaPhoneRepository = null, IAgenteRepository? agenteRepository = null)
        {
            _repository = repository;
            _logger = logger;
            _wabaPhoneRepository = wabaPhoneRepository;
            _agenteRepository = agenteRepository;
        }

        public async Task<IReadOnlyCollection<GestaoEmpresaDto>> ListarEmpresasAsync(
            int userId,
            Guid? empresaId,
            Guid? estabelecimentoId,
            string? companyRole,
            string? establishmentRole,
            bool isSuperAdmin)
        {
            var scope = BuildScope(empresaId, estabelecimentoId, companyRole, establishmentRole, isSuperAdmin);
            EnsureCanViewManagement(scope);

            var rows = await _repository.ListarEmpresasAsync(scope.IsSuperAdmin ? null : scope.EmpresaId);
            return rows.Select(MapEmpresa).ToArray();
        }

        public async Task<GestaoEmpresaDto> CriarEmpresaAsync(
            int userId,
            Guid? empresaId,
            Guid? estabelecimentoId,
            string? companyRole,
            string? establishmentRole,
            bool isSuperAdmin,
            SalvarEmpresaRequest request)
        {
            var scope = BuildScope(empresaId, estabelecimentoId, companyRole, establishmentRole, isSuperAdmin);
            EnsureCanCreateCompany(scope);
            ValidateSalvarEmpresaRequest(request);

            var createdId = request.InitialEstablishment != null
                ? await _repository.CriarEmpresaComEstabelecimentoAsync(request, request.InitialEstablishment)
                : await _repository.CriarEmpresaAsync(request);

            var created = await _repository.ObterEmpresaAsync(createdId)
                ?? throw new InvalidOperationException("Empresa criada mas nao encontrada na consulta de retorno.");

            return MapEmpresa(created);
        }

        public async Task<GestaoEmpresaDto> AtualizarEmpresaAsync(
            int userId,
            Guid? empresaId,
            Guid? estabelecimentoId,
            string? companyRole,
            string? establishmentRole,
            bool isSuperAdmin,
            Guid targetEmpresaId,
            SalvarEmpresaRequest request)
        {
            var scope = BuildScope(empresaId, estabelecimentoId, companyRole, establishmentRole, isSuperAdmin);
            EnsureCanManageCompany(scope, targetEmpresaId);
            ValidateSalvarEmpresaRequest(request);

            await _repository.AtualizarEmpresaAsync(targetEmpresaId, request);

            var updated = await _repository.ObterEmpresaAsync(targetEmpresaId)
                ?? throw new KeyNotFoundException("Empresa nao encontrada.");

            return MapEmpresa(updated);
        }

        public async Task AtualizarStatusEmpresaAsync(
            int userId,
            Guid? empresaId,
            Guid? estabelecimentoId,
            string? companyRole,
            string? establishmentRole,
            bool isSuperAdmin,
            Guid targetEmpresaId,
            bool ativa)
        {
            var scope = BuildScope(empresaId, estabelecimentoId, companyRole, establishmentRole, isSuperAdmin);
            EnsureCanManageCompany(scope, targetEmpresaId);
            await _repository.AtualizarStatusEmpresaAsync(targetEmpresaId, ativa);
        }

        public async Task<IReadOnlyCollection<GestaoEstabelecimentoDto>> ListarEstabelecimentosAsync(
            int userId,
            Guid? empresaId,
            Guid? estabelecimentoId,
            string? companyRole,
            string? establishmentRole,
            bool isSuperAdmin)
        {
            var scope = BuildScope(empresaId, estabelecimentoId, companyRole, establishmentRole, isSuperAdmin);
            EnsureCanViewManagement(scope);

            var rows = await _repository.ListarEstabelecimentosAsync(
                scope.IsSuperAdmin ? null : scope.EmpresaId,
                scope.IsSuperAdmin || IsCompanyManager(scope) ? null : scope.EstabelecimentoId);

            var dtos = new List<GestaoEstabelecimentoDto>();
            foreach (var row in rows)
            {
                var wabaPhoneNumberId = _wabaPhoneRepository != null
                    ? await _wabaPhoneRepository.ObterPhoneNumberIdPorEstabelecimentoAsync(row.Id)
                    : null;
                dtos.Add(MapEstabelecimento(row, wabaPhoneNumberId));
            }
            return dtos;
        }

        public async Task<GestaoEstabelecimentoDto> CriarEstabelecimentoAsync(
            int userId,
            Guid? empresaId,
            Guid? estabelecimentoId,
            string? companyRole,
            string? establishmentRole,
            bool isSuperAdmin,
            SalvarEstabelecimentoRequest request)
        {
            var scope = BuildScope(empresaId, estabelecimentoId, companyRole, establishmentRole, isSuperAdmin);
            EnsureCanCreateEstablishment(scope);
            if (!scope.IsSuperAdmin)
            {
                request.EmpresaId = scope.EmpresaId;
            }
            ValidateSalvarEstabelecimentoRequest(request, requireEmpresaId: true);

            var createdId = await _repository.CriarEstabelecimentoAsync(request);

            if (_wabaPhoneRepository != null && !string.IsNullOrWhiteSpace(request.WabaPhoneNumberId))
            {
                await _wabaPhoneRepository.InserirOuAtualizarAsync(new WabaPhone
                {
                    PhoneNumberId = request.WabaPhoneNumberId,
                    IdEstabelecimento = createdId,
                    Ativo = true,
                    Descricao = request.NomeFantasia
                });
            }

            var created = await _repository.ObterEstabelecimentoAsync(createdId)
                ?? throw new InvalidOperationException("Estabelecimento criado mas nao encontrado.");

            var createdWabaId = _wabaPhoneRepository != null
                ? await _wabaPhoneRepository.ObterPhoneNumberIdPorEstabelecimentoAsync(createdId)
                : null;

            return MapEstabelecimento(created, createdWabaId);
        }

        public async Task<GestaoEstabelecimentoDto> AtualizarEstabelecimentoAsync(
            int userId,
            Guid? empresaId,
            Guid? estabelecimentoId,
            string? companyRole,
            string? establishmentRole,
            bool isSuperAdmin,
            Guid targetEstabelecimentoId,
            SalvarEstabelecimentoRequest request)
        {
            var scope = BuildScope(empresaId, estabelecimentoId, companyRole, establishmentRole, isSuperAdmin);
            var current = await _repository.ObterEstabelecimentoAsync(targetEstabelecimentoId)
                ?? throw new KeyNotFoundException("Estabelecimento nao encontrado.");

            EnsureCanManageEstablishment(scope, current);

            if (!scope.IsSuperAdmin)
            {
                request.EmpresaId = current.EmpresaId;
            }
            ValidateSalvarEstabelecimentoRequest(request, requireEmpresaId: true);

            await _repository.AtualizarEstabelecimentoAsync(targetEstabelecimentoId, request);

            if (_wabaPhoneRepository != null && !string.IsNullOrWhiteSpace(request.WabaPhoneNumberId))
            {
                await _wabaPhoneRepository.InserirOuAtualizarAsync(new WabaPhone
                {
                    PhoneNumberId = request.WabaPhoneNumberId,
                    IdEstabelecimento = targetEstabelecimentoId,
                    Ativo = true,
                    Descricao = request.NomeFantasia
                });
            }

            var updated = await _repository.ObterEstabelecimentoAsync(targetEstabelecimentoId)
                ?? throw new KeyNotFoundException("Estabelecimento nao encontrado.");

            var updatedWabaId = _wabaPhoneRepository != null
                ? await _wabaPhoneRepository.ObterPhoneNumberIdPorEstabelecimentoAsync(targetEstabelecimentoId)
                : null;

            return MapEstabelecimento(updated, updatedWabaId);
        }

        public async Task AtualizarStatusEstabelecimentoAsync(
            int userId,
            Guid? empresaId,
            Guid? estabelecimentoId,
            string? companyRole,
            string? establishmentRole,
            bool isSuperAdmin,
            Guid targetEstabelecimentoId,
            bool ativa)
        {
            var scope = BuildScope(empresaId, estabelecimentoId, companyRole, establishmentRole, isSuperAdmin);
            var current = await _repository.ObterEstabelecimentoAsync(targetEstabelecimentoId)
                ?? throw new KeyNotFoundException("Estabelecimento nao encontrado.");

            EnsureCanManageEstablishment(scope, current);
            await _repository.AtualizarStatusEstabelecimentoAsync(targetEstabelecimentoId, ativa);
        }

        public Task<IReadOnlyCollection<GestaoUsuarioDto>> ListarUsuariosAsync(
            int userId,
            Guid? empresaId,
            Guid? estabelecimentoId,
            string? companyRole,
            string? establishmentRole,
            bool isSuperAdmin)
        {
            return ListarUsuariosInternalAsync(BuildScope(empresaId, estabelecimentoId, companyRole, establishmentRole, isSuperAdmin), null);
        }

        public async Task<GestaoUsuarioDto> CriarUsuarioAsync(
            int userId,
            Guid? empresaId,
            Guid? estabelecimentoId,
            string? companyRole,
            string? establishmentRole,
            bool isSuperAdmin,
            SalvarUsuarioRequest request)
        {
            var scope = BuildScope(empresaId, estabelecimentoId, companyRole, establishmentRole, isSuperAdmin);
            EnsureCanManageUsers(scope);

            var command = await BuildSaveUsuarioCommandAsync(scope, userId, request, null);
            var createdUserId = await _repository.SalvarUsuarioAsync(command);
            await TryEnsureAgenteAsync(command, createdUserId);
            var created = (await ListarUsuariosInternalAsync(scope, createdUserId)).FirstOrDefault();
            return created ?? throw new InvalidOperationException("Usuario criado mas nao encontrado no retorno.");
        }

        public async Task<GestaoUsuarioDto> AtualizarUsuarioAsync(
            int userId,
            Guid? empresaId,
            Guid? estabelecimentoId,
            string? companyRole,
            string? establishmentRole,
            bool isSuperAdmin,
            int targetUserId,
            SalvarUsuarioRequest request)
        {
            var scope = BuildScope(empresaId, estabelecimentoId, companyRole, establishmentRole, isSuperAdmin);
            EnsureCanManageUsers(scope);
            await EnsureTargetUserAccessibleAsync(scope, targetUserId, userId, blockSelfMutation: false);

            var command = await BuildSaveUsuarioCommandAsync(scope, userId, request, targetUserId);
            await _repository.SalvarUsuarioAsync(command);
            await TryEnsureAgenteAsync(command, targetUserId);

            var updated = (await ListarUsuariosInternalAsync(scope, targetUserId)).FirstOrDefault();
            return updated ?? throw new InvalidOperationException("Usuario atualizado mas nao encontrado no retorno.");
        }

        public async Task AtualizarStatusUsuarioAsync(
            int userId,
            Guid? empresaId,
            Guid? estabelecimentoId,
            string? companyRole,
            string? establishmentRole,
            bool isSuperAdmin,
            int targetUserId,
            string status)
        {
            var scope = BuildScope(empresaId, estabelecimentoId, companyRole, establishmentRole, isSuperAdmin);
            EnsureCanManageUsers(scope);
            await EnsureTargetUserAccessibleAsync(scope, targetUserId, userId, blockSelfMutation: true);

            var ativo = NormalizeToken(status) != "inativo";
            await _repository.AtualizarStatusUsuarioAsync(targetUserId, ativo);
        }

        public async Task RemoverUsuarioAsync(
            int userId,
            Guid? empresaId,
            Guid? estabelecimentoId,
            string? companyRole,
            string? establishmentRole,
            bool isSuperAdmin,
            int targetUserId)
        {
            var scope = BuildScope(empresaId, estabelecimentoId, companyRole, establishmentRole, isSuperAdmin);
            EnsureCanManageUsers(scope);
            await EnsureTargetUserAccessibleAsync(scope, targetUserId, userId, blockSelfMutation: true);
            await _repository.RemoverUsuarioAsync(targetUserId);
        }

        private async Task TryEnsureAgenteAsync(GestaoPersistenciaUsuarioCommand command, int userId)
        {
            if (_agenteRepository == null) return;

            bool customHas = false;
            if (!string.IsNullOrWhiteSpace(command.PermissoesCustomizadasJson))
            {
                try
                {
                    var parsed = JsonSerializer.Deserialize<Dictionary<string, List<string>>>(command.PermissoesCustomizadasJson);
                    customHas = parsed != null
                        && parsed.TryGetValue("WhatsApp", out var actions)
                        && actions.Any(a => string.Equals(a, "assumir_conversa", StringComparison.OrdinalIgnoreCase));
                }
                catch { /* JSON inválido — ignora */ }
            }

            bool roleHas = false;
            if (!string.IsNullOrWhiteSpace(command.EstablishmentRole))
            {
                var role = RoleCatalog.Normalize(command.EstablishmentRole);
                var defaults = await _repository.ListarPermissoesPadraoPorTipoAsync(new[] { role });
                roleHas = defaults.TryGetValue(role, out var rolePerms)
                    && rolePerms.TryGetValue("WhatsApp", out var roleActions)
                    && roleActions.Any(a => string.Equals(a, "assumir_conversa", StringComparison.OrdinalIgnoreCase));
            }

            if (!customHas && !roleHas) return;

            try { await _agenteRepository.EnsureAgenteAsync(userId); }
            catch (Exception ex) { _logger.LogWarning(ex, "Falha ao garantir agente para usuario {Id}", userId); }
        }

        private async Task<IReadOnlyCollection<GestaoUsuarioDto>> ListarUsuariosInternalAsync(GestaoEmpresaScope scope, int? specificUserId)
        {
            EnsureCanViewManagement(scope);

            var summaries = await _repository.ListarUsuariosResumoAsync(
                scope.IsSuperAdmin ? null : scope.EmpresaId,
                scope.IsSuperAdmin || IsCompanyManager(scope) ? null : scope.EstabelecimentoId,
                scope.IsSuperAdmin);
            var summaryRows = specificUserId.HasValue
                ? summaries.Where(item => item.Id == specificUserId.Value).ToArray()
                : summaries.ToArray();

            if (summaryRows.Length == 0)
            {
                return Array.Empty<GestaoUsuarioDto>();
            }

            var companies = await _repository.ListarUsuarioEmpresasAsync(
                scope.IsSuperAdmin ? null : scope.EmpresaId,
                scope.IsSuperAdmin || IsCompanyManager(scope) ? null : scope.EstabelecimentoId,
                scope.IsSuperAdmin);
            var establishments = await _repository.ListarUsuarioEstabelecimentosAsync(
                scope.IsSuperAdmin ? null : scope.EmpresaId,
                scope.IsSuperAdmin || IsCompanyManager(scope) ? null : scope.EstabelecimentoId);
            var empresasDisponiveis = (await _repository.ListarEmpresasAsync(scope.IsSuperAdmin ? null : scope.EmpresaId))
                .ToDictionary(item => item.Id, item => item.NomeFantasia, EqualityComparer<Guid>.Default);

            var companyRows = specificUserId.HasValue
                ? companies.Where(item => item.UserId == specificUserId.Value).ToArray()
                : companies.ToArray();
            var establishmentRows = specificUserId.HasValue
                ? establishments.Where(item => item.UserId == specificUserId.Value).ToArray()
                : establishments.ToArray();

            var roles = companyRows.Select(item => RoleCatalog.Normalize(item.TipoAcesso))
                .Concat(establishmentRows.Select(item => RoleCatalog.Normalize(item.TipoAcesso)))
                .Where(item => !string.IsNullOrWhiteSpace(item))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToArray();
            var defaultPermissionsByRole = await _repository.ListarPermissoesPadraoPorTipoAsync(roles);

            var result = new List<GestaoUsuarioDto>(summaryRows.Length);
            foreach (var summary in summaryRows)
            {
                var userCompanies = companyRows.Where(item => item.UserId == summary.Id).ToList();
                var userEstablishments = establishmentRows.Where(item => item.UserId == summary.Id).ToList();

                var vinculos = new List<GestaoUsuarioVinculoDto>();
                foreach (var row in userEstablishments)
                {
                    vinculos.Add(new GestaoUsuarioVinculoDto
                    {
                        VinculoId = row.VinculoId,
                        EmpresaId = row.EmpresaId,
                        EstabelecimentoId = row.EstabelecimentoId,
                        EstabelecimentoNome = row.EstabelecimentoNome,
                        TipoEstabelecimento = row.TipoEstabelecimentoSlug,
                        TipoAcesso = RoleCatalog.Normalize(row.TipoAcesso),
                        Status = summary.DeletedAt.HasValue ? "inativo" : NormalizeStatus(row.Status),
                        OrigemEscopo = "estabelecimento",
                        Permissoes = BuildEffectivePermissions(defaultPermissionsByRole, row.TipoAcesso, row.PermissoesCustomizadas)
                    });
                }

                foreach (var row in userCompanies.OrderByDescending(item => RoleCatalog.Rank(item.TipoAcesso, summary.IsSuperAdmin)))
                {
                    vinculos.Add(new GestaoUsuarioVinculoDto
                    {
                        VinculoId = row.VinculoId,
                        EmpresaId = row.EmpresaId,
                        EstabelecimentoId = null,
                        EstabelecimentoNome = null,
                        TipoEstabelecimento = null,
                        TipoAcesso = summary.IsSuperAdmin ? "super_admin" : RoleCatalog.Normalize(row.TipoAcesso),
                        Status = summary.DeletedAt.HasValue ? "inativo" : NormalizeStatus(row.Status),
                        OrigemEscopo = "empresa",
                        Permissoes = BuildEffectivePermissions(defaultPermissionsByRole, row.TipoAcesso, null)
                    });
                }

                if (summary.IsSuperAdmin && vinculos.Count == 0)
                {
                    vinculos.Add(new GestaoUsuarioVinculoDto
                    {
                        VinculoId = Guid.NewGuid(),
                        EmpresaId = scope.EmpresaId,
                        EstabelecimentoId = null,
                        EstabelecimentoNome = null,
                        TipoEstabelecimento = null,
                        TipoAcesso = "super_admin",
                        Status = summary.DeletedAt.HasValue ? "inativo" : "ativo",
                        OrigemEscopo = "empresa",
                        Permissoes = null
                    });
                }

                vinculos = vinculos
                    .OrderByDescending(item => RoleCatalog.Rank(item.TipoAcesso, string.Equals(item.TipoAcesso, "super_admin", StringComparison.OrdinalIgnoreCase)))
                    .ThenBy(item => item.EstabelecimentoNome)
                    .ToList();

                var empresaVinculo = userCompanies.FirstOrDefault();
                var estabelecimentoVinculo = userEstablishments.FirstOrDefault();
                var empresaAtualId = empresaVinculo?.EmpresaId ?? estabelecimentoVinculo?.EmpresaId ?? scope.EmpresaId ?? Guid.Empty;
                var empresaAtualNome = empresaVinculo?.EmpresaNome
                    ?? (empresaAtualId != Guid.Empty && empresasDisponiveis.TryGetValue(empresaAtualId, out var nome)
                        ? nome
                        : string.Empty);

                result.Add(new GestaoUsuarioDto
                {
                    Id = summary.Id,
                    Nome = summary.Nome,
                    Email = summary.Email,
                    Telefone = null,
                    Status = summary.DeletedAt.HasValue ? "inativo" : "ativo",
                    IsSuperAdmin = summary.IsSuperAdmin,
                    CreatedAt = summary.CreatedAt,
                    UltimoAcesso = null,
                    Veiculo = null,
                    RaioMaxKm = null,
                    TaxaBase = null,
                    Empresa = empresaAtualId == Guid.Empty
                        ? null
                        : new GestaoUsuarioEmpresaDto
                        {
                            Id = empresaAtualId,
                            Nome = empresaAtualNome
                        },
                    Vinculos = vinculos
                });
            }

            return result;
        }

        private async Task<GestaoPersistenciaUsuarioCommand> BuildSaveUsuarioCommandAsync(
            GestaoEmpresaScope scope,
            int actorUserId,
            SalvarUsuarioRequest request,
            int? targetUserId)
        {
            var errors = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);

            ValidateRequired(request.Nome, "nome", "Nome obrigatorio.", errors);
            ValidateRequired(request.Email, "email", "E-mail obrigatorio.", errors);
            ValidateOptionalEmail(request.Email, "email", "E-mail invalido.", errors);
            ValidateRequired(request.TipoUsuario, "tipoUsuario", "Tipo de usuario obrigatorio.", errors);
            if (!targetUserId.HasValue)
            {
                ValidateRequired(request.SenhaInicial, "senhaInicial", "Senha inicial obrigatoria.", errors);
            }
            ValidatePassword(request.SenhaInicial, "senhaInicial", !targetUserId.HasValue, errors);

            var tipoUsuario = RoleCatalog.Normalize(request.TipoUsuario);
            if (string.IsNullOrWhiteSpace(tipoUsuario))
            {
                AddError(errors, "tipoUsuario", "Tipo de usuario obrigatorio.");
            }

            var targetEmpresaId = scope.IsSuperAdmin ? request.EmpresaId : scope.EmpresaId;
            if (!targetEmpresaId.HasValue || targetEmpresaId.Value == Guid.Empty)
            {
                AddError(errors, "empresaId", "Empresa obrigatoria.");
            }

            if (!scope.IsSuperAdmin && request.EmpresaId.HasValue && scope.EmpresaId.HasValue && request.EmpresaId.Value != scope.EmpresaId.Value)
            {
                AddError(errors, "empresaId", "Voce so pode usar a empresa atual.");
            }

            var establishmentIds = request.EstabelecimentoIds?
                .Where(id => id != Guid.Empty)
                .Distinct()
                .ToArray() ?? Array.Empty<Guid>();

            if (tipoUsuario != "super_admin" && establishmentIds.Length == 0)
            {
                AddError(errors, "estabelecimentoIds", "Informe ao menos um estabelecimento.");
            }

            var selectedEstablishments = establishmentIds.Length > 0
                ? (await _repository.ListarEstabelecimentosPorIdsAsync(establishmentIds)).ToList()
                : new List<GestaoTargetEstabelecimento>();

            if (establishmentIds.Length > 0 && selectedEstablishments.Count != establishmentIds.Length)
            {
                AddError(errors, "estabelecimentoIds", "Um ou mais estabelecimentos informados nao existem.");
            }

            if (targetEmpresaId.HasValue)
            {
                foreach (var establishment in selectedEstablishments)
                {
                    if (establishment.EmpresaId != targetEmpresaId.Value)
                    {
                        AddError(errors, "estabelecimentoIds", "Todos os estabelecimentos devem pertencer a mesma empresa.");
                        break;
                    }
                }
            }

            if (selectedEstablishments.Any(item => !item.Ativo || !string.Equals(item.Status, "ativo", StringComparison.OrdinalIgnoreCase)))
            {
                AddError(errors, "estabelecimentoIds", "Selecione apenas estabelecimentos ativos.");
            }

            if (!scope.IsSuperAdmin && !IsCompanyManager(scope) && IsEstablishmentManager(scope))
            {
                if (!scope.EstabelecimentoId.HasValue)
                {
                    AddError(errors, "estabelecimentoIds", "Selecione um estabelecimento valido.");
                }
                else if (establishmentIds.Length == 0 || establishmentIds.Any(item => item != scope.EstabelecimentoId.Value))
                {
                    AddError(errors, "estabelecimentoIds", "Gerente de estabelecimento so pode atuar no estabelecimento atual.");
                }
            }

            if (!scope.IsSuperAdmin && RoleCatalog.IsManagerRole(tipoUsuario))
            {
                AddError(errors, "tipoUsuario", "Somente o super admin pode criar ou editar gerentes.");
            }

            string companyRole;
            string? establishmentRole;
            bool targetIsSuperAdmin = false;

            switch (tipoUsuario)
            {
                case "super_admin":
                    if (!scope.IsSuperAdmin)
                    {
                        AddError(errors, "tipoUsuario", "Somente o super admin pode criar outro super admin.");
                    }

                    companyRole = "dono";
                    establishmentRole = null;
                    targetIsSuperAdmin = true;
                    break;
                case "gerente_empresa":
                    if (establishmentIds.Length == 0)
                    {
                        AddError(errors, "estabelecimentoIds", "Gerente da empresa precisa de ao menos um estabelecimento.");
                    }

                    companyRole = "gerente_empresa";
                    establishmentRole = "gerente_estabelecimento";
                    break;
                case "gerente_estabelecimento":
                    if (establishmentIds.Length != 1)
                    {
                        AddError(errors, "estabelecimentoIds", "Gerente de estabelecimento exige exatamente um estabelecimento.");
                    }

                    companyRole = "colaborador";
                    establishmentRole = "gerente_estabelecimento";
                    break;
                default:
                    companyRole = "colaborador";
                    establishmentRole = tipoUsuario;
                    break;
            }

            if (!RoleCatalog.IsManagerRole(tipoUsuario))
            {
                foreach (var establishment in selectedEstablishments)
                {
                    if (!RoleCatalog.IsAllowedForEstablishmentType(tipoUsuario, establishment.TipoEstabelecimentoNome))
                    {
                        AddError(errors, "tipoUsuario", "Tipo de usuario nao permitido para um dos estabelecimentos selecionados.");
                        break;
                    }
                }
            }

            if (errors.Count == 0 && await _repository.EmailExisteAsync(request.Email.Trim(), targetUserId))
            {
                AddError(errors, "email", "E-mail ja cadastrado.");
            }

            if (errors.Count > 0)
            {
                throw new AdminUsuarioValidationException("Dados invalidos.", errors);
            }

            var modulosAtivosSelecionados = selectedEstablishments
                .SelectMany(e => EstabelecimentoModuleMapper.ToUiModules(e.Nome, e.ModulosAtivosRaw))
                .ToHashSet(StringComparer.OrdinalIgnoreCase);

            string? permissionsJson;
            if (request.Permissoes != null && request.Permissoes.Count > 0)
            {
                permissionsJson = JsonSerializer.Serialize(CardapioPermissionBridge.Apply(request.Permissoes, modulosAtivosSelecionados.ToArray()));
            }
            else if (!targetUserId.HasValue && !string.IsNullOrWhiteSpace(establishmentRole))
            {
                // Nova criação sem permissões explícitas: usa template do cargo filtrado pelos módulos do estabelecimento
                var roleNorm = RoleCatalog.Normalize(establishmentRole);
                var padrao = await _repository.ListarPermissoesPadraoPorTipoAsync(new[] { roleNorm });
                if (padrao.TryGetValue(roleNorm, out var templatePorModulo) && templatePorModulo.Count > 0)
                {
                    var permissoesFiltradas = modulosAtivosSelecionados.Count > 0
                        ? templatePorModulo
                            .Where(kvp => modulosAtivosSelecionados.Contains(kvp.Key))
                            .ToDictionary(kvp => kvp.Key, kvp => kvp.Value, StringComparer.OrdinalIgnoreCase)
                        : templatePorModulo;
                    var permissoesAjustadas = CardapioPermissionBridge.Apply(permissoesFiltradas, modulosAtivosSelecionados.ToArray());
                    permissionsJson = permissoesAjustadas.Count > 0 ? JsonSerializer.Serialize(permissoesAjustadas) : null;
                }
                else
                {
                    permissionsJson = null;
                }
            }
            else
            {
                permissionsJson = null;
            }

            return new GestaoPersistenciaUsuarioCommand
            {
                UsuarioId = targetUserId,
                Nome = request.Nome.Trim(),
                Email = request.Email.Trim(),
                SenhaHash = string.IsNullOrWhiteSpace(request.SenhaInicial) ? null : PasswordSecurity.Hash(request.SenhaInicial.Trim()),
                IsSuperAdmin = targetIsSuperAdmin,
                EmpresaId = targetEmpresaId!.Value,
                CompanyRole = companyRole,
                EstablishmentRole = establishmentRole,
                EstabelecimentoIds = establishmentIds,
                PermissoesCustomizadasJson = permissionsJson,
                ActorUserId = actorUserId
            };
        }

        private async Task EnsureTargetUserAccessibleAsync(GestaoEmpresaScope scope, int targetUserId, int actorUserId, bool blockSelfMutation)
        {
            if (blockSelfMutation && targetUserId == actorUserId)
            {
                throw new UnauthorizedAccessException("Voce nao pode alterar ou remover o proprio usuario por este fluxo.");
            }

            var target = await _repository.ObterUsuarioResumoAsync(targetUserId)
                ?? throw new KeyNotFoundException("Usuario nao encontrado.");
            var companies = await _repository.ObterUsuarioEmpresasAsync(targetUserId);
            var establishments = await _repository.ObterUsuarioEstabelecimentosAsync(targetUserId);

            var accessible = scope.IsSuperAdmin
                || (scope.EmpresaId.HasValue && (companies.Any(item => item.EmpresaId == scope.EmpresaId.Value) || establishments.Any(item => item.EmpresaId == scope.EmpresaId.Value)))
                || (scope.EstabelecimentoId.HasValue && establishments.Any(item => item.EstabelecimentoId == scope.EstabelecimentoId.Value));

            if (!accessible)
            {
                throw new UnauthorizedAccessException("Usuario fora do seu escopo.");
            }

            var targetMaxRank = companies.Select(item => RoleCatalog.Rank(item.TipoAcesso, target.IsSuperAdmin))
                .Concat(establishments.Select(item => RoleCatalog.Rank(item.TipoAcesso, target.IsSuperAdmin)))
                .DefaultIfEmpty(RoleCatalog.Rank(target.IsSuperAdmin ? "super_admin" : string.Empty, target.IsSuperAdmin))
                .Max();
            var actorRank = Math.Max(
                RoleCatalog.Rank(scope.CompanyRole, scope.IsSuperAdmin),
                RoleCatalog.Rank(scope.EstablishmentRole, scope.IsSuperAdmin));

            if (!scope.IsSuperAdmin && actorRank <= targetMaxRank)
            {
                throw new UnauthorizedAccessException("Voce so pode gerenciar usuarios abaixo do seu nivel.");
            }
        }

        private static void ValidateSalvarEmpresaRequest(SalvarEmpresaRequest request)
        {
            var errors = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);

            ValidateRequired(request.NomeFantasia, "nomeFantasia", "Nome fantasia obrigatorio.", errors);
            ValidateOptionalCnpj(request.Cnpj, "cnpj", "CNPJ deve conter 14 digitos.", errors);
            ValidateOptionalEmail(request.Email, "email", "E-mail invalido.", errors);

            if (request.InitialEstablishment != null)
            {
                ValidateSalvarEstabelecimentoRequest(
                    request.InitialEstablishment,
                    requireEmpresaId: false,
                    fieldPrefix: "initialEstablishment.",
                    errors);
            }

            ThrowIfValidationErrors(errors);
        }

        private static void ValidateSalvarEstabelecimentoRequest(
            SalvarEstabelecimentoRequest request,
            bool requireEmpresaId,
            string fieldPrefix = "",
            Dictionary<string, List<string>>? externalErrors = null)
        {
            var errors = externalErrors ?? new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);

            if (requireEmpresaId && (!request.EmpresaId.HasValue || request.EmpresaId.Value == Guid.Empty))
            {
                AddError(errors, BuildFieldPath(fieldPrefix, "empresaId"), "Empresa obrigatoria.");
            }

            ValidateRequired(
                request.NomeFantasia,
                BuildFieldPath(fieldPrefix, "nomeFantasia"),
                fieldPrefix.Length == 0 ? "Nome fantasia obrigatorio." : "Nome fantasia do estabelecimento obrigatorio.",
                errors);
            ValidateOptionalCnpj(
                request.CnpjLoja,
                BuildFieldPath(fieldPrefix, "cnpjLoja"),
                "CNPJ do estabelecimento deve conter 14 digitos.",
                errors);
            ValidateOptionalWhatsappE164(
                request.WhatsappE164,
                BuildFieldPath(fieldPrefix, "whatsappE164"),
                "WhatsApp deve estar em formato E.164 valido.",
                errors);
            ValidateOptionalEmail(
                request.Email,
                BuildFieldPath(fieldPrefix, "email"),
                "E-mail invalido.",
                errors);
            ValidateOptionalCep(
                request.Cep,
                BuildFieldPath(fieldPrefix, "cep"),
                "CEP deve conter 8 digitos.",
                errors);
            ValidateOptionalUf(
                request.Uf,
                BuildFieldPath(fieldPrefix, "uf"),
                "UF deve conter 2 letras.",
                errors);
            ValidateOptionalLatitude(
                request.Latitude,
                BuildFieldPath(fieldPrefix, "latitude"),
                errors);
            ValidateOptionalLongitude(
                request.Longitude,
                BuildFieldPath(fieldPrefix, "longitude"),
                errors);
            ValidateOptionalNonNegative(
                request.RaioEntregaKm,
                BuildFieldPath(fieldPrefix, "raioEntregaKm"),
                "Raio de entrega deve ser maior ou igual a zero.",
                errors);
            ValidateOptionalNonNegative(
                request.PedidoMinimo,
                BuildFieldPath(fieldPrefix, "pedidoMinimo"),
                "Pedido minimo deve ser maior ou igual a zero.",
                errors);
            ValidateOptionalNonNegative(
                request.TaxaEntregaFixa,
                BuildFieldPath(fieldPrefix, "taxaEntregaFixa"),
                "Taxa de entrega fixa deve ser maior ou igual a zero.",
                errors);
            ValidateOptionalNonNegative(
                request.TaxaEntregaPorKm,
                BuildFieldPath(fieldPrefix, "taxaEntregaPorKm"),
                "Taxa de entrega por km deve ser maior ou igual a zero.",
                errors);
            ValidateOptionalPositiveInt(
                request.TempoPreparoMin,
                BuildFieldPath(fieldPrefix, "tempoPreparoMin"),
                "Tempo de preparo deve ser maior que zero.",
                errors);

            if (externalErrors == null)
            {
                ThrowIfValidationErrors(errors);
            }
        }

        private static GestaoEmpresaDto MapEmpresa(GestaoEmpresaRow row)
        {
            return new GestaoEmpresaDto
            {
                Id = row.Id,
                IdInt = row.IdInt,
                NomeFantasia = row.NomeFantasia,
                RazaoSocial = row.RazaoSocial,
                Cnpj = row.Cnpj,
                Email = row.Email,
                Telefone = row.Telefone,
                Site = row.Site,
                ImagemCapa = null,
                CidadeBase = row.CidadeBase,
                TipoOrganizacao = string.IsNullOrWhiteSpace(row.TipoOrganizacao) ? "empresa" : row.TipoOrganizacao,
                Ativa = row.Ativa,
                CreatedAt = row.CreatedAt,
                UpdatedAt = row.UpdatedAt,
                Plano = null
            };
        }

        private static GestaoEstabelecimentoDto MapEstabelecimento(GestaoEstabelecimentoRow row, string? wabaPhoneNumberId = null)
        {
            var normalizedStatus = NormalizeStatus(row.Status);

            return new GestaoEstabelecimentoDto
            {
                Id = row.Id,
                EmpresaId = row.EmpresaId,
                TipoUnidade = row.TipoUnidade,
                NomeFantasia = row.NomeFantasia,
                CnpjLoja = row.CnpjLoja,
                InscricaoEstadual = row.InscricaoEstadual,
                InscricaoMunicipal = row.InscricaoMunicipal,
                Telefone = row.Telefone,
                WhatsappE164 = row.WhatsappE164,
                Email = row.Email,
                Logradouro = row.Logradouro,
                Numero = row.Numero,
                Complemento = row.Complemento,
                Bairro = row.Bairro,
                Cidade = row.Cidade,
                Uf = row.Uf,
                Cep = row.Cep,
                Latitude = row.Latitude,
                Longitude = row.Longitude,
                UrlLogo = row.UrlLogo,
                AceitaPedidos = row.AceitaPedidos,
                TimezoneIana = row.TimezoneIana,
                RaioEntregaKm = row.RaioEntregaKm,
                PedidoMinimo = row.PedidoMinimo,
                TaxaEntregaFixa = row.TaxaEntregaFixa,
                TaxaEntregaPorKm = row.TaxaEntregaPorKm,
                TempoPreparoMin = row.TempoPreparoMin,
                CreatedAt = row.CreatedAt,
                UpdatedAt = row.UpdatedAt,
                TipoEstabelecimentoId = row.TipoEstabelecimentoId,
                TipoEstabelecimentoSlug = row.TipoEstabelecimentoSlug,
                TipoEstabelecimentoNome = row.TipoEstabelecimentoNome,
                Slug = row.Slug,
                ModulosAtivos = EstabelecimentoModuleMapper.ToUiModules(row.NomeFantasia, row.ModulosAtivosRaw),
                Ativo = row.Ativo,
                Status = !row.Ativo && (normalizedStatus == "ativo" || normalizedStatus == "trial")
                    ? "inativo"
                    : normalizedStatus,
                Endereco = BuildAddress(row),
                WabaPhoneNumberId = wabaPhoneNumberId
            };
        }

        private static string? BuildAddress(GestaoEstabelecimentoRow row)
        {
            var main = string.Join(", ", new[] { row.Logradouro, row.Numero }.Where(item => !string.IsNullOrWhiteSpace(item)));
            var extras = string.Join(" - ", new[] { row.Complemento, row.Bairro }.Where(item => !string.IsNullOrWhiteSpace(item)));

            if (string.IsNullOrWhiteSpace(main))
            {
                return string.IsNullOrWhiteSpace(extras) ? null : extras;
            }

            if (string.IsNullOrWhiteSpace(extras))
            {
                return main;
            }

            return $"{main} - {extras}";
        }

        private static Dictionary<string, List<string>>? BuildEffectivePermissions(
            Dictionary<string, Dictionary<string, List<string>>> defaultsByRole,
            string? role,
            string? rawCustomPermissions)
        {
            var result = ClonePermissionMap(defaultsByRole.TryGetValue(RoleCatalog.Normalize(role), out var defaults)
                ? defaults
                : null);

            ParseCustomPermissionPayload(rawCustomPermissions, out var grants, out var revokes);

            foreach (var grant in grants)
            {
                if (!result.TryGetValue(grant.Key, out var actions))
                {
                    actions = new List<string>();
                    result[grant.Key] = actions;
                }

                foreach (var action in grant.Value)
                {
                    if (!actions.Any(item => string.Equals(item, action, StringComparison.OrdinalIgnoreCase)))
                    {
                        actions.Add(action);
                    }
                }
            }

            foreach (var revoke in revokes)
            {
                if (!result.TryGetValue(revoke.Key, out var actions))
                {
                    continue;
                }

                actions.RemoveAll(action => revoke.Value.Any(item => string.Equals(item, action, StringComparison.OrdinalIgnoreCase)));
                if (actions.Count == 0)
                {
                    result.Remove(revoke.Key);
                }
            }

            return result.Count == 0 ? null : result;
        }

        private static Dictionary<string, List<string>> ClonePermissionMap(Dictionary<string, List<string>>? source)
        {
            var result = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
            if (source == null)
            {
                return result;
            }

            foreach (var item in source)
            {
                result[item.Key] = item.Value
                    .Where(action => !string.IsNullOrWhiteSpace(action))
                    .Select(action => action.Trim())
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .ToList();
            }

            return result;
        }

        private static void ParseCustomPermissionPayload(
            string? raw,
            out Dictionary<string, List<string>> grants,
            out Dictionary<string, List<string>> revokes)
        {
            grants = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
            revokes = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);

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

                grants = ReadPermissionMap(root, "grants")
                    ?? ReadPermissionMap(root, "allow")
                    ?? ReadPermissionMap(root, "permissions")
                    ?? ReadPermissionMap(root, "permissoes")
                    ?? ReadDirectPermissionMap(root)
                    ?? grants;

                revokes = ReadPermissionMap(root, "revokes")
                    ?? ReadPermissionMap(root, "deny")
                    ?? revokes;
            }
            catch
            {
                // Ignora payload invalido para nao quebrar a listagem.
            }
        }

        private static Dictionary<string, List<string>>? ReadPermissionMap(JsonElement root, string propertyName)
        {
            if (!root.TryGetProperty(propertyName, out var node) || node.ValueKind != JsonValueKind.Object)
            {
                return null;
            }

            return ReadDirectPermissionMap(node);
        }

        private static Dictionary<string, List<string>>? ReadDirectPermissionMap(JsonElement node)
        {
            if (node.ValueKind != JsonValueKind.Object)
            {
                return null;
            }

            var result = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
            foreach (var property in node.EnumerateObject())
            {
                if (property.Value.ValueKind != JsonValueKind.Array)
                {
                    continue;
                }

                var actions = property.Value.EnumerateArray()
                    .Where(item => item.ValueKind == JsonValueKind.String)
                    .Select(item => item.GetString())
                    .Where(item => !string.IsNullOrWhiteSpace(item))
                    .Select(item => item!.Trim())
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .ToList();

                if (actions.Count > 0)
                {
                    result[property.Name] = actions;
                }
            }

            return result;
        }

        private static GestaoEmpresaScope BuildScope(
            Guid? empresaId,
            Guid? estabelecimentoId,
            string? companyRole,
            string? establishmentRole,
            bool isSuperAdmin)
        {
            return new GestaoEmpresaScope
            {
                IsSuperAdmin = isSuperAdmin,
                EmpresaId = empresaId,
                EstabelecimentoId = estabelecimentoId,
                CompanyRole = RoleCatalog.Normalize(companyRole),
                EstablishmentRole = RoleCatalog.Normalize(establishmentRole)
            };
        }

        private static bool IsCompanyManager(GestaoEmpresaScope scope)
        {
            return string.Equals(scope.CompanyRole, "dono", StringComparison.OrdinalIgnoreCase)
                || string.Equals(scope.CompanyRole, "gerente_empresa", StringComparison.OrdinalIgnoreCase);
        }

        private static bool IsEstablishmentManager(GestaoEmpresaScope scope)
        {
            return string.Equals(scope.EstablishmentRole, "gerente_estabelecimento", StringComparison.OrdinalIgnoreCase);
        }

        private static void EnsureCanViewManagement(GestaoEmpresaScope scope)
        {
            if (scope.IsSuperAdmin || IsCompanyManager(scope) || IsEstablishmentManager(scope))
            {
                return;
            }

            throw new UnauthorizedAccessException("Seu perfil nao pode acessar a area de gestao.");
        }

        private static void EnsureCanCreateCompany(GestaoEmpresaScope scope)
        {
            if (!scope.IsSuperAdmin)
            {
                throw new UnauthorizedAccessException("Somente super admin pode criar empresas.");
            }
        }

        private static void EnsureCanManageCompany(GestaoEmpresaScope scope, Guid targetEmpresaId)
        {
            if (scope.IsSuperAdmin)
            {
                return;
            }

            if (IsCompanyManager(scope) && scope.EmpresaId.HasValue && scope.EmpresaId.Value == targetEmpresaId)
            {
                return;
            }

            throw new UnauthorizedAccessException("Voce nao pode gerenciar esta empresa.");
        }

        private static void EnsureCanCreateEstablishment(GestaoEmpresaScope scope)
        {
            if (scope.IsSuperAdmin || IsCompanyManager(scope))
            {
                return;
            }

            throw new UnauthorizedAccessException("Voce nao pode criar estabelecimentos.");
        }

        private static void EnsureCanManageEstablishment(GestaoEmpresaScope scope, GestaoEstabelecimentoRow current)
        {
            if (scope.IsSuperAdmin)
            {
                return;
            }

            if (IsCompanyManager(scope) && scope.EmpresaId.HasValue && scope.EmpresaId.Value == current.EmpresaId)
            {
                return;
            }

            if (IsEstablishmentManager(scope) && scope.EstabelecimentoId.HasValue && scope.EstabelecimentoId.Value == current.Id)
            {
                return;
            }

            throw new UnauthorizedAccessException("Voce nao pode gerenciar este estabelecimento.");
        }

        private static void EnsureCanManageUsers(GestaoEmpresaScope scope)
        {
            if (scope.IsSuperAdmin || IsCompanyManager(scope) || IsEstablishmentManager(scope))
            {
                return;
            }

            throw new UnauthorizedAccessException("Seu perfil nao pode gerenciar usuarios.");
        }

        private static string NormalizeStatus(string? status)
        {
            return NormalizeToken(status) switch
            {
                "trial" => "trial",
                "suspenso" => "suspenso",
                "cancelado" => "cancelado",
                "pendente_aprovacao" => "pendente_aprovacao",
                "pausado" => "pausado",
                "recusado" => "recusado",
                "removido" => "removido",
                "inativo" => "inativo",
                "ativo" => "ativo",
                _ => "ativo"
            };
        }

        private static string NormalizeToken(string? value)
        {
            if (string.IsNullOrWhiteSpace(value))
            {
                return string.Empty;
            }

            var normalized = value.Trim().ToLowerInvariant().Normalize(NormalizationForm.FormD);
            var builder = new StringBuilder(normalized.Length);
            foreach (var ch in normalized)
            {
                if (System.Globalization.CharUnicodeInfo.GetUnicodeCategory(ch) != System.Globalization.UnicodeCategory.NonSpacingMark)
                {
                    builder.Append(ch);
                }
            }

            return builder.ToString().Normalize(NormalizationForm.FormC);
        }

        private static void ValidateRequired(string? value, string field, string message)
        {
            if (string.IsNullOrWhiteSpace(value))
            {
                throw ValidationException("Dados invalidos.", (field, message));
            }
        }

        private static void ValidateOptionalCnpj(string? value, string field, string message)
        {
            if (string.IsNullOrWhiteSpace(value))
            {
                return;
            }

            if (ExtractDigits(value).Length != 14)
            {
                throw ValidationException("Dados invalidos.", (field, message));
            }
        }

        private static void ValidateOptionalCnpj(string? value, string field, string message, Dictionary<string, List<string>> errors)
        {
            if (string.IsNullOrWhiteSpace(value))
            {
                return;
            }

            if (ExtractDigits(value).Length != 14)
            {
                AddError(errors, field, message);
            }
        }

        private static void ValidateOptionalWhatsappE164(string? value, string field, string message)
        {
            if (string.IsNullOrWhiteSpace(value))
            {
                return;
            }

            var normalized = NormalizeWhatsappE164(value);
            var digits = ExtractDigits(normalized);
            if (string.IsNullOrWhiteSpace(normalized) || digits.Length < 8 || digits.Length > 15 || digits[0] == '0')
            {
                throw ValidationException("Dados invalidos.", (field, message));
            }
        }

        private static void ValidateOptionalWhatsappE164(string? value, string field, string message, Dictionary<string, List<string>> errors)
        {
            if (string.IsNullOrWhiteSpace(value))
            {
                return;
            }

            var normalized = NormalizeWhatsappE164(value);
            var digits = ExtractDigits(normalized);
            if (string.IsNullOrWhiteSpace(normalized) || digits.Length < 8 || digits.Length > 15 || digits[0] == '0')
            {
                AddError(errors, field, message);
            }
        }

        private static void ValidateOptionalEmail(string? value, string field, string message, Dictionary<string, List<string>> errors)
        {
            if (string.IsNullOrWhiteSpace(value))
            {
                return;
            }

            try
            {
                var parsed = new MailAddress(value.Trim());
                if (!string.Equals(parsed.Address, value.Trim(), StringComparison.OrdinalIgnoreCase))
                {
                    AddError(errors, field, message);
                }
            }
            catch
            {
                AddError(errors, field, message);
            }
        }

        private static void ValidatePassword(string? value, string field, bool required, Dictionary<string, List<string>> errors)
        {
            if (string.IsNullOrWhiteSpace(value))
            {
                if (required)
                {
                    return;
                }

                return;
            }

            if (value.Trim().Length < 6)
            {
                AddError(errors, field, "Senha inicial deve conter ao menos 6 caracteres.");
            }
        }

        private static void ValidateOptionalCep(string? value, string field, string message, Dictionary<string, List<string>> errors)
        {
            if (string.IsNullOrWhiteSpace(value))
            {
                return;
            }

            if (ExtractDigits(value).Length != 8)
            {
                AddError(errors, field, message);
            }
        }

        private static void ValidateOptionalUf(string? value, string field, string message, Dictionary<string, List<string>> errors)
        {
            if (string.IsNullOrWhiteSpace(value))
            {
                return;
            }

            var normalized = value.Trim();
            if (normalized.Length != 2 || !normalized.All(char.IsLetter))
            {
                AddError(errors, field, message);
            }
        }

        private static void ValidateOptionalLatitude(double? value, string field, Dictionary<string, List<string>> errors)
        {
            if (!value.HasValue)
            {
                return;
            }

            if (value.Value < -90 || value.Value > 90)
            {
                AddError(errors, field, "Latitude deve estar entre -90 e 90.");
            }
        }

        private static void ValidateOptionalLongitude(double? value, string field, Dictionary<string, List<string>> errors)
        {
            if (!value.HasValue)
            {
                return;
            }

            if (value.Value < -180 || value.Value > 180)
            {
                AddError(errors, field, "Longitude deve estar entre -180 e 180.");
            }
        }

        private static void ValidateOptionalNonNegative(decimal? value, string field, string message, Dictionary<string, List<string>> errors)
        {
            if (!value.HasValue)
            {
                return;
            }

            if (value.Value < 0)
            {
                AddError(errors, field, message);
            }
        }

        private static void ValidateOptionalPositiveInt(int? value, string field, string message, Dictionary<string, List<string>> errors)
        {
            if (!value.HasValue)
            {
                return;
            }

            if (value.Value <= 0)
            {
                AddError(errors, field, message);
            }
        }

        private static void ValidateRequired(string? value, string field, string message, Dictionary<string, List<string>> errors)
        {
            if (string.IsNullOrWhiteSpace(value))
            {
                AddError(errors, field, message);
            }
        }

        private static RequestValidationException ValidationException(string message, params (string Field, string Message)[] errors)
        {
            var dict = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
            foreach (var error in errors)
            {
                AddError(dict, error.Field, error.Message);
            }

            return new RequestValidationException(message, dict);
        }

        private static void AddError(Dictionary<string, List<string>> errors, string field, string message)
        {
            if (!errors.TryGetValue(field, out var list))
            {
                list = new List<string>();
                errors[field] = list;
            }

            if (!list.Any(item => string.Equals(item, message, StringComparison.OrdinalIgnoreCase)))
            {
                list.Add(message);
            }
        }

        private static void ThrowIfValidationErrors(Dictionary<string, List<string>> errors)
        {
            if (errors.Count > 0)
            {
                throw new RequestValidationException("Dados invalidos.", errors);
            }
        }

        private static string BuildFieldPath(string prefix, string field)
        {
            return string.IsNullOrWhiteSpace(prefix)
                ? field
                : prefix + field;
        }

        private static string ExtractDigits(string? value)
        {
            if (string.IsNullOrWhiteSpace(value))
            {
                return string.Empty;
            }

            var builder = new StringBuilder(value.Length);
            foreach (var ch in value)
            {
                if (char.IsDigit(ch))
                {
                    builder.Append(ch);
                }
            }

            return builder.ToString();
        }

        private static string? NormalizeWhatsappE164(string? value)
        {
            if (string.IsNullOrWhiteSpace(value))
            {
                return null;
            }

            var trimmed = value.Trim();
            var digits = ExtractDigits(trimmed);
            if (string.IsNullOrWhiteSpace(digits))
            {
                return null;
            }

            if (trimmed.StartsWith("+", StringComparison.Ordinal))
            {
                return "+" + digits;
            }

            return digits.StartsWith("55", StringComparison.Ordinal)
                ? "+" + digits
                : "+55" + digits;
        }
    }
}
