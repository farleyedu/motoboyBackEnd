using System;
using System.Threading.Tasks;
using Dapper;
using Npgsql;

namespace APIBack.Service;

public static class MotoboyVinculoLimites
{
    public const string MensagemLimite = "Limite de vínculos atingido: encerre um vínculo ou compre mais motoboys.";

    // A linha da loja serializa aprovações concorrentes, inclusive entre caminhos distintos.
    public static async Task<bool> PodeAtivarAsync(
        NpgsqlConnection connection, NpgsqlTransaction transaction, Guid estabelecimentoId, int motoboyId)
    {
        var existe = await connection.ExecuteScalarAsync<bool>(@"
SELECT EXISTS (SELECT 1 FROM estabelecimentos WHERE id=@EstabelecimentoId FOR UPDATE);",
            new { EstabelecimentoId = estabelecimentoId }, transaction);
        if (!existe) return false;
        var contratados = await connection.ExecuteScalarAsync<int>(
            "SELECT " + MotoboyLimites.SqlContratados + " FROM estabelecimentos e WHERE e.id=@EstabelecimentoId;",
            new { EstabelecimentoId = estabelecimentoId }, transaction);

        var atual = await connection.ExecuteScalarAsync<bool>(@"
SELECT EXISTS (
 SELECT 1 FROM motoboy_estabelecimento me
 JOIN motoboy m ON m.id=me.motoboy_id
 WHERE me.estabelecimento_id=@EstabelecimentoId AND me.ativo=TRUE
   AND COALESCE(m.canonical_motoboy_id,m.id)=COALESCE(
     (SELECT canonical_motoboy_id FROM motoboy WHERE id=@MotoboyId),@MotoboyId)
);", new { EstabelecimentoId = estabelecimentoId, MotoboyId = motoboyId }, transaction);
        if (atual) return true;

        var quantidade = await connection.ExecuteScalarAsync<int>(@"
SELECT COUNT(DISTINCT COALESCE(m.canonical_motoboy_id,m.id))
 FROM motoboy_estabelecimento me
 JOIN motoboy m ON m.id=me.motoboy_id
 WHERE me.estabelecimento_id=@EstabelecimentoId AND me.ativo=TRUE
   AND m.is_simulated=FALSE;",
            new { EstabelecimentoId = estabelecimentoId }, transaction);
        return quantidade < MotoboyLimites.Vinculados(contratados);
    }
}
