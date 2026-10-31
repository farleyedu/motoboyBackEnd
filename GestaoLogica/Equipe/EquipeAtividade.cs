using Dapper;
using Npgsql;

namespace APIBack.GestaoLogica.Equipe;

// Atividade real do turno (6C3-C): entregas concluídas atribuídas à pessoa. O turno continua sendo confirmado por uma pessoa;
// a apuração só traz o número conferível. docs/zippy-gestao/CONTRATOS.md, seção 9.6.

public static class OrigemAtividade
{
    /// <summary>Número vindo das entregas concluídas (pode ser zero de verdade).</summary>
    public const string Apurado = "apurado";
    /// <summary>A pessoa não está ligada a um usuário do Zippy: não há como atribuir nada.</summary>
    public const string SemVinculo = "sem_vinculo";
    /// <summary>O usuário ligado não é motoboy: não existe entrega para atribuir.</summary>
    public const string SemEntregador = "sem_entregador";
    /// <summary>Venda sem garçom registrado no pedido: a base da comissão não é apurada, nem presumida.</summary>
    public const string SemAtribuicao = "sem_atribuicao";
    /// <summary>A regra da pessoa não tem comissão.</summary>
    public const string SemComissao = "sem_comissao";
}

/// <summary>
/// O que o sistema sabe do turno. <c>Entregas</c> nulo = sem apuração (veja <c>EntregasOrigem</c>), diferente de zero entregas.
/// <c>BaseComissao</c> nulo = sem atribuição verificável. <c>De</c>/<c>Ate</c> = janela usada (Ate exclusivo).
/// </summary>
public sealed record AtividadeTurno(Guid PessoaId, DateOnly Data, int? Entregas, string EntregasOrigem, IReadOnlyList<int> PedidoIds,
    decimal? BaseComissao, string ComissaoOrigem, DateTimeOffset De, DateTimeOffset Ate, string? Aviso);

public sealed partial class EquipeService
{
    /// <summary>Entregas concluídas pela pessoa na janela do turno, uma vez por pedido, na loja certa.</summary>
    public async Task<AtividadeTurno> AtividadeDoTurno(Guid loja, Guid pessoaId, DateOnly data, CancellationToken ct)
    {
        await using var c = await dataSource.OpenConnectionAsync(ct);
        var pessoa = await ObterPessoa(c, loja, pessoaId);
        var gravado = await c.QuerySingleOrDefaultAsync<(string Entrada, string Saida)?>(
            "SELECT entrada, saida FROM gestao_turnos WHERE estabelecimento_id = @loja AND pessoa_id = @pessoaId AND data = @data",
            new { loja, pessoaId, data = data.ToDateTime(TimeOnly.MinValue) });
        var horario = gravado ?? (EquipeRegras.DaEscala(pessoa, data) is { } padrao ? (padrao.Entrada, padrao.Saida) : ((string, string)?)null);
        return await Atividade(c, loja, pessoa, data, horario?.Item1, horario?.Item2, ct);
    }

    private async Task<AtividadeTurno> Atividade(NpgsqlConnection c, Guid loja, PessoaDto pessoa, DateOnly data, string? entrada, string? saida,
        CancellationToken ct)
    {
        var fuso = await livro.Fuso(loja, ct);
        var (de, ate) = JanelaDoTurno(fuso, data, entrada, saida);
        var comissao = pessoa.Regra.ComissaoPercentual is > 0;
        var (baseComissao, comissaoOrigem, avisoComissao) = comissao
            ? ((decimal?)null, OrigemAtividade.SemAtribuicao,
                "A comissão não é apurada: o pedido ainda não registra qual garçom fez a venda. Informe a base conferindo a conta.")
            : (null, OrigemAtividade.SemComissao, null);

        if (pessoa.UsuarioId is not { } usuario)
            return new AtividadeTurno(pessoa.Id, data, null, OrigemAtividade.SemVinculo, Array.Empty<int>(), baseComissao, comissaoOrigem, de, ate,
                "Esta pessoa não está ligada a um usuário do Zippy: as entregas não são apuradas.");
        if (!await c.ExecuteScalarAsync<bool>("SELECT EXISTS (SELECT 1 FROM motoboy WHERE id_usuario = @usuario)", new { usuario }))
            return new AtividadeTurno(pessoa.Id, data, null, OrigemAtividade.SemEntregador, Array.Empty<int>(), baseComissao, comissaoOrigem, de, ate,
                avisoComissao);

        // Uma linha por pedido: vale a última parada concluída, então transferência ou reatribuição não conta duas vezes.
        var pedidos = (await c.QueryAsync<int>(@"
WITH ultima AS (
    SELECT DISTINCT ON (s.pedido_id) s.pedido_id, s.motoboy_id, s.completed_at_utc
      FROM delivery_route_stops s
     WHERE s.estabelecimento_id = @loja AND s.stop_status = 'completed' AND s.completed_at_utc IS NOT NULL
       AND s.pedido_id IN (SELECT x.pedido_id FROM delivery_route_stops x
                            WHERE x.estabelecimento_id = @loja AND x.stop_status = 'completed'
                              AND x.completed_at_utc >= @de AND x.completed_at_utc < @ate)
     ORDER BY s.pedido_id, s.completed_at_utc DESC, s.id DESC)
SELECT u.pedido_id
  FROM ultima u
  JOIN motoboy m ON m.id = u.motoboy_id
  JOIN pedido p ON p.id = u.pedido_id AND p.id_estabelecimento = @loja AND p.status_pedido = 3
 WHERE m.id_usuario = @usuario AND u.completed_at_utc >= @de AND u.completed_at_utc < @ate
 ORDER BY u.completed_at_utc, u.pedido_id", new { loja, usuario, de = de.ToUniversalTime(), ate = ate.ToUniversalTime() })).ToList();
        return new AtividadeTurno(pessoa.Id, data, pedidos.Count, OrigemAtividade.Apurado, pedidos, baseComissao, comissaoOrigem, de, ate, avisoComissao);
    }

    /// <summary>
    /// O dia do turno na hora da loja, da meia-noite à meia-noite. Turno que vira a noite (saída menor que a entrada) vai até a hora
    /// de saída do dia seguinte.
    /// </summary>
    public static (DateTimeOffset De, DateTimeOffset Ate) JanelaDoTurno(TimeZoneInfo fuso, DateOnly data, string? entrada, string? saida)
    {
        static DateTimeOffset Em(TimeZoneInfo fuso, DateOnly d, int minutos)
        {
            var local = d.ToDateTime(TimeOnly.FromTimeSpan(TimeSpan.FromMinutes(minutos)));
            return new DateTimeOffset(local, fuso.GetUtcOffset(local));
        }
        var virou = EquipeRegras.Minutos(entrada ?? "") is { } e && EquipeRegras.Minutos(saida ?? "") is { } s && s <= e;
        return (Em(fuso, data, 0), Em(fuso, data.AddDays(1), virou ? EquipeRegras.Minutos(saida!)!.Value : 0));
    }
}
