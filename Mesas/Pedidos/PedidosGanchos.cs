using System.Text.Json;
using APIBack.Mesas.Nucleo;
using APIBack.Mesas.Pedidos;
using APIBack.Mesas.Sessao;
using Dapper;
using Npgsql;

namespace APIBack.Mesas.Nucleo
{
    /// <summary>
    /// Executa a ação guardada numa <c>mesa_solicitacao</c> quando o gerente aprova (<c>responderAutorizacao</c>, P07).
    /// Cada família de comandos registra o seu (<c>AddScoped&lt;IMesasAcaoExecutor, ...&gt;</c>); P08 atende os comandos de item.
    /// </summary>
    public interface IMesasAcaoExecutor
    {
        bool Atende(string comando);
        /// <summary><paramref name="ctx"/> = o gerente que aprovou; <paramref name="chave"/> = chave derivada (idempotência).</summary>
        Task<object> Executar(MesasContexto ctx, string comando, JsonElement corpo, Guid chave, CancellationToken ct);
    }
}

namespace APIBack.Mesas.Pedidos
{
    /// <summary>
    /// Ganchos da sessão (P07) para os itens: ao encerrar, o que ficou aberto fecha como entrega presumida (se <c>entrega.fechaSozinho</c>);
    /// ao juntar mesas, pedidos e itens vão para a sessão que continuou e a parte da coletiva vai para a coletiva nova.
    /// </summary>
    public sealed class PedidosSessaoGancho(ICozinhaTempoReal cozinha) : IMesasSessaoGancho
    {
        public async Task AntesDeEncerrar(NpgsqlConnection c, NpgsqlTransaction tx, MesasContexto ctx, Guid sessaoId, bool forcar, string? motivo,
            CancellationToken ct)
        {
            var dados = await c.ExecuteScalarAsync<string?>(new CommandDefinition(
                "SELECT dados::text FROM mesas_config WHERE estabelecimento_id = @loja", new { loja = ctx.Loja }, tx, cancellationToken: ct));
            var cfg = MesasConfigRegras.Efetiva(dados is null ? null : System.Text.Json.Nodes.JsonNode.Parse(dados) as System.Text.Json.Nodes.JsonObject);
            var estacoes = await PedidosService.FecharPresumidas(c, tx, ctx.Loja, sessaoId, null, "encerrar", ct, cfg);
            if (estacoes.Count > 0)
            {
                // Depois do commit seria o ideal; o gancho não tem fila de avisos, então avisa já (o app só relê a leitura).
                await cozinha.CozinhaMudou(ctx.Loja, estacoes, null);
            }
        }

        public async Task AoJuntar(NpgsqlConnection c, NpgsqlTransaction tx, Guid loja, Guid de, Guid para, Guid coletivaDe, Guid coletivaPara,
            CancellationToken ct)
        {
            await c.ExecuteAsync(new CommandDefinition("""
                UPDATE mesa_pedido SET sessao_id = @para, atualizado_em = NOW() WHERE estabelecimento_id = @loja AND sessao_id = @de;
                UPDATE mesa_item SET sessao_id = @para WHERE estabelecimento_id = @loja AND sessao_id = @de;
                -- A coletiva antiga cede a parte para a nova; se o item já tinha parte na nova, soma.
                UPDATE mesa_item_parte n SET percentual = LEAST(100, n.percentual + o.percentual), valor = n.valor + o.valor
                  FROM mesa_item_parte o
                 WHERE o.comanda_id = @coletivaDe AND n.comanda_id = @coletivaPara AND n.item_id = o.item_id;
                DELETE FROM mesa_item_parte o
                 WHERE o.comanda_id = @coletivaDe AND EXISTS (SELECT 1 FROM mesa_item_parte n WHERE n.item_id = o.item_id AND n.comanda_id = @coletivaPara);
                UPDATE mesa_item_parte SET comanda_id = @coletivaPara WHERE comanda_id = @coletivaDe;
                """, new { loja, de, para, coletivaDe, coletivaPara }, tx, cancellationToken: ct));
        }
    }
}
