using APIBack.Attributes;
using APIBack.DTOs.Configuracoes;
using APIBack.Extensions;
using APIBack.Security;
using Dapper;
using Microsoft.AspNetCore.Mvc;
using Npgsql;

namespace APIBack.Controllers;

[ApiController]
[Route("api/configuracoes/estabelecimento")]
public sealed class EstablishmentSettingsController(NpgsqlDataSource dataSource) : ControllerBase
{
    [HttpGet("negocio")]
    [RequirePermission("Configuracoes", "visualizar")]
    public async Task<IActionResult> Business()
    {
        var id = HttpContext.GetEstabelecimentoId();
        if (!id.HasValue) return BadRequest(new { error = "Selecione um estabelecimento." });
        await using var connection = await dataSource.OpenConnectionAsync();
        var data = await connection.QuerySingleOrDefaultAsync<BusinessSettingsDto>(@"
SELECT nome_fantasia AS NomeFantasia, cnpj_loja AS CnpjLoja, telefone AS Telefone,
email AS Email, logradouro AS Logradouro, numero AS Numero, complemento AS Complemento,
bairro AS Bairro, cidade AS Cidade, uf AS Uf, cep AS Cep, url_logo AS UrlLogo
FROM estabelecimentos WHERE id = @id", new { id });
        return data == null ? NotFound() : Ok(new { success = true, data });
    }

    [HttpPut("negocio")]
    [RequirePermission("Configuracoes", "editar")]
    public async Task<IActionResult> SaveBusiness(BusinessSettingsDto input)
    {
        var id = HttpContext.GetEstabelecimentoId();
        if (!id.HasValue) return BadRequest(new { error = "Selecione um estabelecimento." });
        if (string.IsNullOrWhiteSpace(input.NomeFantasia)) return BadRequest(new { error = "Informe o nome do estabelecimento." });
        var parameters = new DynamicParameters(input);
        parameters.Add("id", id);
        await using var connection = await dataSource.OpenConnectionAsync();
        // Fiscal identification belongs to management. Updating this form never changes it.
        var affected = await connection.ExecuteAsync(@"
UPDATE estabelecimentos SET nome_fantasia = @NomeFantasia, telefone = @Telefone, email = @Email,
logradouro = @Logradouro, numero = @Numero, complemento = @Complemento, bairro = @Bairro,
cidade = @Cidade, uf = UPPER(@Uf), cep = @Cep, url_logo = @UrlLogo, data_atualizacao = NOW()
WHERE id = @id", parameters);
        return affected == 0 ? NotFound() : await Business();
    }

    [HttpGet("equipe")]
    [RequirePermission("Configuracoes", "configurar")]
    public async Task<IActionResult> Team()
    {
        var id = HttpContext.GetEstabelecimentoId();
        if (!id.HasValue) return BadRequest(new { error = "Selecione um estabelecimento." });
        if (!RoleCatalog.CanManagePermissions(HttpContext.GetTipoAcesso(), HttpContext.IsSuperAdmin()))
            return StatusCode(403, new { error = "Seu perfil não pode gerenciar permissões." });
        await using var connection = await dataSource.OpenConnectionAsync();
        var members = (await connection.QueryAsync<TeamMember>(@"
SELECT ue.id AS VinculoId, u.id AS UsuarioId, u.nome AS Nome, u.email AS Email,
ue.tipo_acesso AS Papel, ue.status AS Status, COALESCE(ue.ativo, TRUE) AS Ativo,
COALESCE(u.is_super_admin, FALSE) AS SuperAdmin
FROM usuario_estabelecimentos ue JOIN usuario u ON u.id = ue.id_usuario
WHERE ue.id_estabelecimento = @id AND u.deleted_at IS NULL ORDER BY u.nome", new { id })).ToList();
        var rank = RoleCatalog.Rank(HttpContext.GetTipoAcesso(), HttpContext.IsSuperAdmin());
        foreach (var member in members)
            member.PodeEditar = member.UsuarioId != HttpContext.GetUserId() &&
                (HttpContext.IsSuperAdmin() || rank > RoleCatalog.Rank(member.Papel, member.SuperAdmin));
        return Ok(new { success = true, data = new { membros = members, catalogo = EstablishmentPermissions.Catalog() } });
    }

    public sealed class TeamMember
    {
        public Guid VinculoId { get; set; }
        public int UsuarioId { get; set; }
        public string Nome { get; set; } = "";
        public string Email { get; set; } = "";
        public string Papel { get; set; } = "";
        public string Status { get; set; } = "";
        public bool Ativo { get; set; }
        public bool SuperAdmin { get; set; }
        public bool PodeEditar { get; set; }
    }
}
