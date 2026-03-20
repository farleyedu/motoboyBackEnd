using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Mail;
using System.Threading.Tasks;
using APIBack.DTOs.AdminUsers;
using APIBack.Model.AdminUsers;
using APIBack.Repository.Interface;
using APIBack.Security;
using APIBack.Service.Interface;
using Microsoft.Extensions.Logging;

namespace APIBack.Service
{
    public class AdminUsuariosService : IAdminUsuariosService
    {
        private readonly IAdminUsuariosRepository _repository;
        private readonly ILogger<AdminUsuariosService> _logger;

        public AdminUsuariosService(
            IAdminUsuariosRepository repository,
            ILogger<AdminUsuariosService> logger)
        {
            _repository = repository;
            _logger = logger;
        }

        public async Task<AdminUsuariosContextoResponse> ObterContextoAsync(int userId, Guid? estabelecimentoId, bool isSuperAdmin)
        {
            var actor = await EnsureActorAsync(userId, estabelecimentoId, isSuperAdmin);
            EnsureCanView(actor);

            var gerenciaveis = (await _repository.ListarEstabelecimentosGerenciaveisAsync(userId, isSuperAdmin)).ToList();
            foreach (var item in gerenciaveis)
            {
                item.IsCurrent = actor.EstabelecimentoId.HasValue && item.Id == actor.EstabelecimentoId.Value;
            }

            var response = new AdminUsuariosContextoResponse
            {
                Actor = new AdminUsuariosActorDto
                {
                    UserId = actor.UserId,
                    IsSuperAdmin = actor.IsSuperAdmin,
                    Role = RoleCatalog.ResolveScreenRole(actor.CompanyRole, actor.EstablishmentRole, actor.IsSuperAdmin),
                    CanViewUsers = RoleCatalog.CanViewUsers(actor.CompanyRole, actor.EstablishmentRole, actor.IsSuperAdmin),
                    CanCreateUsers = RoleCatalog.CanCreateUsers(actor.CompanyRole, actor.EstablishmentRole, actor.IsSuperAdmin),
                    CanCreateManagers = RoleCatalog.CanCreateManagers(actor.IsSuperAdmin)
                },
                EmpresaAtual = actor.EmpresaId.HasValue
                    ? new AdminUsuariosEmpresaDto
                    {
                        Id = actor.EmpresaId.Value,
                        Nome = actor.EmpresaNome,
                        TipoAcesso = NormalizeCompanyRole(actor.CompanyRole)
                    }
                    : null,
                EstabelecimentoAtual = actor.EstabelecimentoId.HasValue
                    ? new AdminUsuariosEstabelecimentoDto
                    {
                        Id = actor.EstabelecimentoId.Value,
                        EmpresaId = actor.EmpresaId ?? Guid.Empty,
                        EmpresaNome = actor.EmpresaNome,
                        Nome = actor.EstabelecimentoNome,
                        TipoEstabelecimento = actor.TipoEstabelecimento,
                        IsCurrent = true
                    }
                    : null,
                EstabelecimentosGerenciaveis = gerenciaveis.Select(MapEstabelecimento).ToList(),
                AllowedTargetUserTypes = BuildAllowedTargetTypes(actor.TipoEstabelecimento),
                AllowedManagerTypes = RoleCatalog.GetAllowedManagerTypes(actor.IsSuperAdmin)
                    .Select(CreateRoleDto)
                    .ToList(),
                FormRules = new AdminUsuariosFormRulesDto
                {
                    EmpresaLocked = !actor.IsSuperAdmin,
                    EstabelecimentoLocked = !actor.IsSuperAdmin,
                    AllowMultiEstablishment = actor.IsSuperAdmin
                }
            };

            if (actor.IsSuperAdmin)
            {
                response.EmpresasDisponiveis = (await _repository.ListarEmpresasAsync())
                    .Select(item => new AdminUsuariosEmpresaDto
                    {
                        Id = item.Id,
                        Nome = item.Nome
                    })
                    .ToList();
            }

            return response;
        }

        public async Task<AdminUsuariosListResponse> ListarUsuariosAsync(int userId, Guid? estabelecimentoId, bool isSuperAdmin)
        {
            var actor = await EnsureActorAsync(userId, estabelecimentoId, isSuperAdmin);
            EnsureCanView(actor);

            if (!actor.EmpresaId.HasValue || !actor.EstabelecimentoId.HasValue)
            {
                throw new InvalidOperationException("Selecione um estabelecimento para listar usuÃ¡rios.");
            }

            var rows = await _repository.ListarUsuariosAsync(actor.EmpresaId.Value, actor.EstabelecimentoId.Value);
            var items = rows.Select(row => MapListItem(actor, row)).ToList();

            return new AdminUsuariosListResponse
            {
                Items = items,
                Total = items.Count
            };
        }

        public async Task<AdminUsuarioListItemDto> CriarUsuarioAsync(int userId, Guid? empresaIdContexto, Guid? estabelecimentoIdContexto, bool isSuperAdmin, CreateAdminUsuarioRequest request)
        {
            var actor = await EnsureActorAsync(userId, estabelecimentoIdContexto, isSuperAdmin);
            EnsureCanCreate(actor);

            var errors = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);

            ValidateRequired(request.Nome, "nome", "Nome obrigatorio.", errors);
            ValidateRequired(request.Email, "email", "E-mail obrigatorio.", errors);
            ValidateOptionalEmail(request.Email, "email", "E-mail invalido.", errors);
            ValidateRequired(request.SenhaInicial, "senhaInicial", "Senha inicial obrigatoria.", errors);
            ValidatePassword(request.SenhaInicial, "senhaInicial", errors);
            ValidateRequired(request.TipoUsuario, "tipoUsuario", "Tipo de usuario obrigatorio.", errors);

            var tipoUsuario = RoleCatalog.Normalize(request.TipoUsuario);
            if (string.IsNullOrWhiteSpace(tipoUsuario))
            {
                AddError(errors, "tipoUsuario", "Tipo de usuario obrigatorio.");
            }

            var empresaId = actor.IsSuperAdmin ? request.EmpresaId : actor.EmpresaId;
            if (!empresaId.HasValue || empresaId == Guid.Empty)
            {
                AddError(errors, "empresaId", "Empresa obrigatoria.");
            }

            if (!actor.IsSuperAdmin && request.EmpresaId.HasValue && actor.EmpresaId.HasValue && request.EmpresaId.Value != actor.EmpresaId.Value)
            {
                AddError(errors, "empresaId", "Gerentes so podem criar usuarios na empresa atual.");
            }

            var ids = request.EstabelecimentoIds?
                .Where(id => id != Guid.Empty)
                .Distinct()
                .ToArray() ?? Array.Empty<Guid>();

            if (ids.Length == 0)
            {
                AddError(errors, "estabelecimentoIds", "Informe ao menos um estabelecimento.");
            }

            var targetEstabelecimentos = ids.Length > 0
                ? (await _repository.ListarEstabelecimentosPorIdsAsync(ids)).ToList()
                : new List<AdminTargetEstabelecimento>();

            if (ids.Length > 0 && targetEstabelecimentos.Count != ids.Length)
            {
                AddError(errors, "estabelecimentoIds", "Um ou mais estabelecimentos informados nao existem.");
            }

            if (empresaId.HasValue)
            {
                foreach (var estabelecimento in targetEstabelecimentos)
                {
                    if (estabelecimento.EmpresaId != empresaId.Value)
                    {
                        AddError(errors, "estabelecimentoIds", "Todos os estabelecimentos devem pertencer a mesma empresa.");
                        break;
                    }
                }
            }

            if (targetEstabelecimentos.Any(item => !item.Ativo || !string.Equals(item.Status, "ativo", StringComparison.OrdinalIgnoreCase)))
            {
                AddError(errors, "estabelecimentoIds", "Selecione apenas estabelecimentos ativos.");
            }

            if (!actor.IsSuperAdmin)
            {
                if (!actor.EstabelecimentoId.HasValue)
                {
                    AddError(errors, "estabelecimentoIds", "Selecione um estabelecimento para criar usuÃ¡rios.");
                }
                else if (ids.Length != 1 || ids[0] != actor.EstabelecimentoId.Value)
                {
                AddError(errors, "estabelecimentoIds", "Gerentes so podem criar usuarios no estabelecimento atual.");
                }
            }

            string companyRole;
            string? establishmentRole;

            switch (tipoUsuario)
            {
                case "gerente_empresa":
                    if (!actor.IsSuperAdmin)
                    {
                        AddError(errors, "tipoUsuario", "Somente o super admin pode criar gerente de empresa.");
                    }

                    if (ids.Length == 0)
                    {
                        AddError(errors, "estabelecimentoIds", "Gerente de empresa precisa de ao menos um estabelecimento.");
                    }

                    companyRole = "gerente_empresa";
                    establishmentRole = "gerente_estabelecimento";
                    break;

                case "gerente_estabelecimento":
                    if (!actor.IsSuperAdmin)
                    {
                        AddError(errors, "tipoUsuario", "Somente o super admin pode criar gerente de estabelecimento.");
                    }

                    if (ids.Length != 1)
                    {
                        AddError(errors, "estabelecimentoIds", "Gerente de estabelecimento exige exatamente um estabelecimento.");
                    }

                    companyRole = "colaborador";
                    establishmentRole = "gerente_estabelecimento";
                    break;

                default:
                    if (RoleCatalog.IsManagerRole(tipoUsuario))
                    {
                        AddError(errors, "tipoUsuario", "Tipo de usuario nao suportado no fluxo atual.");
                    }

                    if (ids.Length != 1)
                    {
                        AddError(errors, "estabelecimentoIds", "Usuarios operacionais exigem exatamente um estabelecimento.");
                    }

                    companyRole = "colaborador";
                    establishmentRole = tipoUsuario;
                    break;
            }

            if (!actor.IsSuperAdmin && RoleCatalog.IsManagerRole(tipoUsuario))
            {
                AddError(errors, "tipoUsuario", "Gerentes nao podem criar outros gerentes.");
            }

            if (!RoleCatalog.IsManagerRole(tipoUsuario) && targetEstabelecimentos.Count == 1)
            {
                var establishmentType = targetEstabelecimentos[0].TipoEstabelecimento;
                if (!RoleCatalog.IsAllowedForEstablishmentType(tipoUsuario, establishmentType))
                {
                    AddError(errors, "tipoUsuario", "Tipo de usuario nao permitido para o estabelecimento selecionado.");
                }
            }

            if (errors.Count == 0 && await _repository.EmailExisteAsync(request.Email.Trim()))
            {
                AddError(errors, "email", "E-mail ja cadastrado.");
            }

            if (errors.Count > 0)
            {
                throw new AdminUsuarioValidationException("Dados invalidos.", errors);
            }

            var command = new AdminCreateUserCommand
            {
                Nome = request.Nome.Trim(),
                Email = request.Email.Trim(),
                SenhaHash = PasswordSecurity.Hash(request.SenhaInicial.Trim()),
                EmpresaId = empresaId!.Value,
                CompanyRole = companyRole,
                EstablishmentRole = establishmentRole,
                EstabelecimentoIds = ids,
                CreatedByUserId = actor.UserId
            };

            var created = await _repository.CriarUsuarioAsync(command);
            created.EmpresaNome = targetEstabelecimentos.FirstOrDefault()?.EmpresaNome ?? actor.EmpresaNome;

            return new AdminUsuarioListItemDto
            {
                Id = created.Id,
                Nome = created.Nome,
                Email = created.Email,
                Status = created.Status,
                CompanyRole = NormalizeCompanyRole(created.CompanyRole),
                Empresa = new AdminUsuariosEmpresaDto
                {
                    Id = created.EmpresaId,
                    Nome = created.EmpresaNome,
                    TipoAcesso = NormalizeCompanyRole(created.CompanyRole)
                },
                EstablishmentRoles = created.Estabelecimentos.Select(item => new AdminUsuarioEstabelecimentoRoleDto
                {
                    Id = item.Id,
                    Nome = item.Nome,
                    TipoEstabelecimento = item.TipoEstabelecimento,
                    TipoAcesso = item.TipoAcesso
                }).ToList(),
                CanEdit = actor.IsSuperAdmin,
                CanDeactivate = actor.IsSuperAdmin
            };
        }

        private async Task<AdminActorSnapshot> EnsureActorAsync(int userId, Guid? estabelecimentoId, bool isSuperAdmin)
        {
            var actor = await _repository.ObterActorSnapshotAsync(userId, estabelecimentoId, isSuperAdmin);
            if (actor == null)
            {
                throw new UnauthorizedAccessException("UsuÃ¡rio nÃ£o encontrado.");
            }

            actor.CompanyRole = RoleCatalog.Normalize(actor.CompanyRole);
            actor.EstablishmentRole = RoleCatalog.Normalize(actor.EstablishmentRole);
            actor.IsSuperAdmin = isSuperAdmin || actor.IsSuperAdmin;
            return actor;
        }

        private static void EnsureCanView(AdminActorSnapshot actor)
        {
            if (!RoleCatalog.CanViewUsers(actor.CompanyRole, actor.EstablishmentRole, actor.IsSuperAdmin))
            {
                throw new UnauthorizedAccessException("Seu perfil nao pode visualizar usuarios.");
            }
        }

        private static void EnsureCanCreate(AdminActorSnapshot actor)
        {
            if (!RoleCatalog.CanCreateUsers(actor.CompanyRole, actor.EstablishmentRole, actor.IsSuperAdmin))
            {
                throw new UnauthorizedAccessException("Seu perfil nao pode criar usuarios.");
            }
        }

        private static List<AdminUsuariosTipoDto> BuildAllowedTargetTypes(string establishmentType)
        {
            return RoleCatalog.GetAllowedTargetUserTypes(establishmentType)
                .Where(role => !RoleCatalog.IsManagerRole(role))
                .Select(CreateRoleDto)
                .ToList();
        }

        private AdminUsuarioListItemDto MapListItem(AdminActorSnapshot actor, AdminUsuarioListRow row)
        {
            var targetHasManagerRole = RoleCatalog.IsManagerRole(row.CompanyRole) || RoleCatalog.IsManagerRole(row.EstablishmentRole);
            var canMutate = actor.IsSuperAdmin
                ? row.Id != actor.UserId
                : !targetHasManagerRole && row.Id != actor.UserId;

            return new AdminUsuarioListItemDto
            {
                Id = row.Id,
                Nome = row.Nome,
                Email = row.Email,
                Status = row.Status,
                CompanyRole = NormalizeCompanyRole(row.CompanyRole),
                Empresa = new AdminUsuariosEmpresaDto
                {
                    Id = row.EmpresaId,
                    Nome = row.EmpresaNome,
                    TipoAcesso = NormalizeCompanyRole(row.CompanyRole)
                },
                EstablishmentRoles = new List<AdminUsuarioEstabelecimentoRoleDto>
                {
                    new AdminUsuarioEstabelecimentoRoleDto
                    {
                        Id = row.EstabelecimentoId,
                        Nome = row.EstabelecimentoNome,
                        TipoEstabelecimento = row.TipoEstabelecimento,
                        TipoAcesso = row.EstablishmentRole
                    }
                },
                CanEdit = canMutate,
                CanDeactivate = canMutate
            };
        }

        private static AdminUsuariosEstabelecimentoDto MapEstabelecimento(AdminEstabelecimentoOption item)
        {
            return new AdminUsuariosEstabelecimentoDto
            {
                Id = item.Id,
                EmpresaId = item.EmpresaId,
                EmpresaNome = item.EmpresaNome,
                Nome = item.Nome,
                TipoEstabelecimento = item.TipoEstabelecimento,
                IsCurrent = item.IsCurrent
            };
        }

        private static AdminUsuariosTipoDto CreateRoleDto(string role)
        {
            return new AdminUsuariosTipoDto
            {
                Code = role,
                Label = role switch
                {
                    "gerente_empresa" => "Gerente da empresa",
                    "gerente_estabelecimento" => "Gerente do estabelecimento",
                    "profissional" => "Profissional",
                    "atendente" => "Atendente",
                    "motoboy" => "Motoboy",
                    "vendedor" => "Vendedor",
                    "funcionario" => "Funcionario",
                    _ => role
                }
            };
        }

        private static string? NormalizeCompanyRole(string? role)
        {
            var normalized = RoleCatalog.Normalize(role);
            return string.Equals(normalized, "colaborador", StringComparison.OrdinalIgnoreCase)
                ? null
                : normalized;
        }

        private static void ValidateRequired(string? value, string field, string message, Dictionary<string, List<string>> errors)
        {
            if (string.IsNullOrWhiteSpace(value))
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

        private static void ValidatePassword(string? value, string field, Dictionary<string, List<string>> errors)
        {
            if (string.IsNullOrWhiteSpace(value))
            {
                return;
            }

            if (value.Trim().Length < 6)
            {
                AddError(errors, field, "Senha inicial deve conter ao menos 6 caracteres.");
            }
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
    }
}
