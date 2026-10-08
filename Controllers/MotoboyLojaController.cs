using APIBack.Attributes;
using APIBack.DTOs.Common;
using APIBack.Extensions;
using Dapper;
using Microsoft.AspNetCore.Mvc;
using Npgsql;

namespace APIBack.Controllers;

[ApiController, Route("api/v2/motoboys/me/session/store"), RequireOperationalSession]
public sealed class MotoboyLojaController(NpgsqlDataSource source) : ControllerBase
{
    [HttpGet]
    public async Task<IActionResult> Get(CancellationToken ct)
    {
        var id = HttpContext.GetEstabelecimentoId();
        if (!id.HasValue) return Unauthorized(ApiResponse<object>.Fail("Contexto da loja inválido."));
        await using var connection = await source.OpenConnectionAsync(ct);
        var store = await connection.QuerySingleOrDefaultAsync(new CommandDefinition("""
            SELECT e.nome_fantasia AS "nome", to_jsonb(e)->>'latitude' AS "latitude", to_jsonb(e)->>'longitude' AS "longitude",
                   COALESCE(to_jsonb(e)->>'logradouro', to_jsonb(e)->>'rua') AS "rua", to_jsonb(e)->>'numero' AS "numero",
                   to_jsonb(e)->>'bairro' AS "bairro", to_jsonb(e)->>'cidade' AS "cidade", to_jsonb(e)->>'uf' AS "uf"
              FROM estabelecimentos e WHERE e.id = @Id;
            """, new { Id = id }, commandTimeout: 10, cancellationToken: ct));
        return store == null ? NotFound(ApiResponse<object>.Fail("Loja não encontrada.")) : Ok(ApiResponse<object>.Ok(store));
    }
}
