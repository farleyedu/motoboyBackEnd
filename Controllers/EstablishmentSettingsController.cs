using APIBack.Attributes;
using APIBack.DTOs.Configuracoes;
using APIBack.Extensions;
using APIBack.Security;
using Dapper;
using Microsoft.AspNetCore.Mvc;
using Npgsql;
using System.Linq;

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
bairro AS Bairro, cidade AS Cidade, uf AS Uf, cep AS Cep, url_logo AS UrlLogo,
site_url AS SiteUrl, favicon_url AS FaviconUrl, cor_primaria AS CorPrimaria,
cor_secundaria AS CorSecundaria, tipografia AS Tipografia, instagram_url AS InstagramUrl,
facebook_url AS FacebookUrl
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
cidade = @Cidade, uf = UPPER(@Uf), cep = @Cep, url_logo = @UrlLogo, site_url = @SiteUrl,
favicon_url = @FaviconUrl, cor_primaria = @CorPrimaria, cor_secundaria = @CorSecundaria,
tipografia = @Tipografia, instagram_url = @InstagramUrl, facebook_url = @FacebookUrl,
data_atualizacao = NOW()
WHERE id = @id", parameters);
        return affected == 0 ? NotFound() : await Business();
    }

    [HttpGet("negocio/horarios")]
    [RequirePermission("Configuracoes", "visualizar")]
    public async Task<IActionResult> Horarios()
    {
        var id = HttpContext.GetEstabelecimentoId();
        if (!id.HasValue) return BadRequest(new { error = "Selecione um estabelecimento." });
        await using var connection = await dataSource.OpenConnectionAsync();
        var rows = (await connection.QueryAsync<(int Dia, bool Fechado, TimeSpan? Abre, TimeSpan? Fecha)>(@"
SELECT dia_semana, fechado, abre_as, fecha_as FROM estabelecimento_horario
 WHERE estabelecimento_id = @id ORDER BY dia_semana", new { id }))
            .ToDictionary(r => r.Dia);
        var dias = Enumerable.Range(0, 7).Select(dia => rows.TryGetValue(dia, out var row)
            ? new HorarioDiaDto { DiaSemana = dia, Fechado = row.Fechado, AbreAs = row.Abre?.ToString(@"hh\:mm"), FechaAs = row.Fecha?.ToString(@"hh\:mm") }
            : new HorarioDiaDto { DiaSemana = dia, Fechado = true }).ToList();
        return Ok(new { success = true, data = dias });
    }

    [HttpPut("negocio/horarios")]
    [RequirePermission("Configuracoes", "editar")]
    public async Task<IActionResult> SaveHorarios(SalvarHorariosRequest request)
    {
        var id = HttpContext.GetEstabelecimentoId();
        if (!id.HasValue) return BadRequest(new { error = "Selecione um estabelecimento." });
        if (request.Dias.Select(d => d.DiaSemana).Distinct().Count() != 7)
            return BadRequest(new { error = "Informe os 7 dias da semana, sem repetir." });
        foreach (var dia in request.Dias)
        {
            if (dia.Fechado) continue;
            if (!TimeSpan.TryParse(dia.AbreAs, out var abre) || !TimeSpan.TryParse(dia.FechaAs, out var fecha) || fecha <= abre)
                return BadRequest(new { error = $"Horario invalido no dia {dia.DiaSemana}: informe abertura e fechamento, com fechamento depois da abertura." });
        }

        await using var connection = await dataSource.OpenConnectionAsync();
        await using var transaction = await connection.BeginTransactionAsync();
        foreach (var dia in request.Dias)
        {
            await connection.ExecuteAsync(@"
INSERT INTO estabelecimento_horario (estabelecimento_id, dia_semana, fechado, abre_as, fecha_as)
VALUES (@Id, @Dia, @Fechado, @Abre, @Fecha)
ON CONFLICT (estabelecimento_id, dia_semana)
DO UPDATE SET fechado = EXCLUDED.fechado, abre_as = EXCLUDED.abre_as, fecha_as = EXCLUDED.fecha_as;",
                new
                {
                    Id = id.Value,
                    Dia = dia.DiaSemana,
                    dia.Fechado,
                    Abre = dia.Fechado ? null : (TimeSpan?)TimeSpan.Parse(dia.AbreAs!),
                    Fecha = dia.Fechado ? null : (TimeSpan?)TimeSpan.Parse(dia.FechaAs!)
                }, transaction);
        }
        await transaction.CommitAsync();
        return await Horarios();
    }

    [HttpGet("negocio/perfil")]
    [RequirePermission("Configuracoes", "visualizar")]
    public async Task<IActionResult> Perfil()
    {
        var id = HttpContext.GetEstabelecimentoId();
        if (!id.HasValue) return BadRequest(new { error = "Selecione um estabelecimento." });
        await using var connection = await dataSource.OpenConnectionAsync();

        var atual = await connection.QuerySingleOrDefaultAsync<BusinessSettingsDto>(@"
SELECT nome_fantasia AS NomeFantasia, cnpj_loja AS CnpjLoja, telefone AS Telefone, email AS Email,
logradouro AS Logradouro, numero AS Numero, bairro AS Bairro, cidade AS Cidade, uf AS Uf, cep AS Cep,
url_logo AS UrlLogo, cor_primaria AS CorPrimaria, cor_secundaria AS CorSecundaria,
instagram_url AS InstagramUrl, facebook_url AS FacebookUrl
FROM estabelecimentos WHERE id = @id", new { id });
        if (atual == null) return NotFound();

        var horariosCount = await connection.ExecuteScalarAsync<int>(
            "SELECT COUNT(*) FROM estabelecimento_horario WHERE estabelecimento_id = @id", new { id });

        var unidades = (await connection.QueryAsync<UnidadeResumoDto>(@"
SELECT e.id AS Id, e.nome_fantasia AS NomeFantasia, e.cidade AS Cidade, e.uf AS Uf, (e.id = @id) AS EhAtual
  FROM estabelecimentos e
 WHERE e.empresa_id = (SELECT empresa_id FROM estabelecimentos WHERE id = @id)
 ORDER BY e.nome_fantasia", new { id })).ToList();

        var itens = new List<CompletudeItemDto>
        {
            new() { Chave = "negocio", Titulo = "Informacoes do negocio", Completo = !string.IsNullOrWhiteSpace(atual.NomeFantasia) && !string.IsNullOrWhiteSpace(atual.CnpjLoja) && !string.IsNullOrWhiteSpace(atual.Telefone) && !string.IsNullOrWhiteSpace(atual.Email) },
            new() { Chave = "endereco", Titulo = "Endereco principal", Completo = !string.IsNullOrWhiteSpace(atual.Logradouro) && !string.IsNullOrWhiteSpace(atual.Numero) && !string.IsNullOrWhiteSpace(atual.Bairro) && !string.IsNullOrWhiteSpace(atual.Cidade) && !string.IsNullOrWhiteSpace(atual.Uf) && !string.IsNullOrWhiteSpace(atual.Cep) },
            new() { Chave = "horarios", Titulo = "Horarios de funcionamento", Completo = horariosCount >= 7 },
            new() { Chave = "marca", Titulo = "Marca e identidade", Completo = !string.IsNullOrWhiteSpace(atual.UrlLogo) && !string.IsNullOrWhiteSpace(atual.CorPrimaria) && !string.IsNullOrWhiteSpace(atual.CorSecundaria) },
            new() { Chave = "unidades", Titulo = "Unidades e filiais", Completo = true },
            new() { Chave = "redes_sociais", Titulo = "Redes sociais", Completo = !string.IsNullOrWhiteSpace(atual.InstagramUrl) || !string.IsNullOrWhiteSpace(atual.FacebookUrl) },
        };
        var completude = new CompletudePerfilDto
        {
            Percentual = (int)Math.Round(100.0 * itens.Count(i => i.Completo) / itens.Count),
            Itens = itens,
        };

        return Ok(new { success = true, data = new { unidades, completude } });
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
