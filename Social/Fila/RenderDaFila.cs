using System.Text.Json;
using System.Text.Json.Nodes;
using APIBack.Social.Editor;
using APIBack.Social.Nucleo;
using Dapper;
using Npgsql;

namespace APIBack.Social.Fila;

/*
 * O que liga o render (social_renders, R13) ao trabalho da máquina (social_trabalhos, R14). Tudo roda dentro da transação
 * de quem chamou, já marcada com a loja.
 */
internal static class RenderDaFila
{
    private static readonly JsonSerializerOptions Web = new(JsonSerializerDefaults.Web);

    /// <summary>Andamento vindo da máquina. Só mexe em render que ainda está gerando.</summary>
    public static Task<RenderDto?> Progresso(NpgsqlConnection c, NpgsqlTransaction tx, Guid loja, Guid renderId, decimal progresso,
        CancellationToken ct) =>
        SocialRendersService.UmOuNada(c, tx, $"""
            UPDATE social_renders SET progresso = @progresso, atualizado_em = NOW()
             WHERE id = @renderId AND estabelecimento_id = @loja AND status = 'gerando'
            RETURNING {SocialRendersService.Colunas}
            """, new { renderId, loja, progresso }, ct);

    public static Task<RenderDto?> Concluir(NpgsqlConnection c, NpgsqlTransaction tx, Guid loja, Guid renderId, long bytes, decimal duracao,
        decimal gastoS, string? nome, CancellationToken ct) =>
        SocialRendersService.UmOuNada(c, tx, $"""
            UPDATE social_renders
               SET status = 'pronto', progresso = 100, bytes = @bytes, duracao = @duracao, gasto_s = @gastoS, erro = NULL,
                   nome_arquivo = COALESCE(@nome, nome_arquivo), atualizado_em = NOW(), concluido_em = NOW()
             WHERE id = @renderId AND estabelecimento_id = @loja AND status = 'gerando'
            RETURNING {SocialRendersService.Colunas}
            """, new { renderId, loja, bytes, duracao, gastoS, nome }, ct);

    public static async Task<IReadOnlyList<RenderDto>> Falhar(NpgsqlConnection c, NpgsqlTransaction tx, Guid loja, Guid[] renderIds,
        string motivo, CancellationToken ct) =>
        renderIds.Length == 0 ? [] : await SocialRendersService.Varios(c, tx, $"""
            UPDATE social_renders SET status = 'falhou', erro = @motivo, atualizado_em = NOW(), concluido_em = NOW()
             WHERE estabelecimento_id = @loja AND id = ANY(@renderIds) AND status = 'gerando'
            RETURNING {SocialRendersService.Colunas}
            """, new { loja, renderIds, motivo }, ct);

    /// <summary>
    /// Trava os trabalhos abertos do corte antes de mexer nos renders: as rotas do worker travam nessa ordem.
    /// </summary>
    public static Task TravarTrabalhosDoCorte(NpgsqlConnection c, NpgsqlTransaction tx, Guid loja, long corteId, CancellationToken ct) =>
        c.ExecuteAsync(new CommandDefinition("""
            SELECT t.id FROM social_trabalhos t
              JOIN social_renders r ON r.id = t.alvo_id
             WHERE t.estabelecimento_id = @loja AND r.corte_id = @corteId AND t.status IN ('pendente', 'rodando')
             FOR UPDATE OF t
            """, new { loja, corteId }, tx, cancellationToken: ct));

    /// <summary>Cancela o trabalho aberto dos renders: a máquina sabe no próximo sinal ou progresso.</summary>
    public static Task CancelarTrabalhos(NpgsqlConnection c, NpgsqlTransaction tx, Guid loja, Guid[] renderIds, CancellationToken ct) =>
        renderIds.Length == 0 ? Task.CompletedTask : c.ExecuteAsync(new CommandDefinition("""
            UPDATE social_trabalhos SET status = 'cancelado', concluido_em = NOW()
             WHERE estabelecimento_id = @loja AND alvo_id = ANY(@renderIds) AND status IN ('pendente', 'rodando')
            """, new { loja, renderIds }, tx, cancellationToken: ct));

    /// <summary>
    /// Confere que a pessoa pode usar a máquina. Máquina de outra pessoa e não compartilhada = 403; pausada ou desligada pelo
    /// dono = 409.
    /// </summary>
    public static async Task ConferirMaquina(NpgsqlConnection c, NpgsqlTransaction tx, Guid loja, int usuario, long maquinaId,
        CancellationToken ct)
    {
        var m = await c.QuerySingleOrDefaultAsync<(int Dono, bool Compartilhada, bool Ativa, bool Pausada)?>(new CommandDefinition("""
            SELECT usuario_id, compartilhada_equipe, ativa, pausada_por_plano FROM social_maquinas
             WHERE id = @maquinaId AND estabelecimento_id = @loja AND removida_em IS NULL FOR SHARE
            """, new { maquinaId, loja }, tx, cancellationToken: ct));
        if (m is not { } x || !FilaRegras.PodeUsar(x.Dono, x.Compartilhada, usuario))
            throw SocialException.SemPermissao("Essa máquina não é sua nem está compartilhada com você.");
        if (x.Pausada) throw new SocialException("MAQUINA_PAUSADA", "Essa máquina está pausada pelo plano.", 409);
        if (!x.Ativa) throw new SocialException("MAQUINA_DESLIGADA", "Essa máquina foi desativada pelo dono.", 409);
    }

    /// <summary>
    /// Enfileira o trabalho do render com a foto da receita e dos dados do corte. A receita salva precisa estar na versão que a
    /// pessoa está vendo.
    /// </summary>
    public static async Task Enfileirar(NpgsqlConnection c, NpgsqlTransaction tx, Guid loja, int usuario, RenderDto render,
        NovoRenderRequest req, CancellationToken ct)
    {
        var corte = await c.QuerySingleAsync<CorteLinha>(new CommandDefinition("""
            SELECT co.ini AS "Ini", co.fim AS "Fim", co.momento_forte_s AS "Forte", co.dados::text AS "Dados", co.video_id AS "VideoId",
                   v.exemplo AS "Exemplo", r.versao AS "Versao", r.receita::text AS "Receita"
              FROM social_cortes co
              JOIN social_videos v ON v.id = co.video_id AND v.estabelecimento_id = co.estabelecimento_id
              LEFT JOIN social_receitas r ON r.corte_id = co.id AND r.estabelecimento_id = co.estabelecimento_id
             WHERE co.id = @corte AND co.estabelecimento_id = @loja
            """, new { corte = render.CorteId, loja }, tx, cancellationToken: ct));
        if (corte.Receita is null || corte.Versao != render.ReceitaVersao)
            throw new SocialException("RECEITA_MUDOU", "A receita salva mudou. Espere salvar e gere de novo.", 409);

        var original = FilaRegras.Original(corte.VideoId, corte.Exemplo, corte.Ini, req.Original?.Nome, req.Original?.Bytes);
        var payload = new JsonObject
        {
            ["corteId"] = render.CorteId,
            ["receitaVersao"] = render.ReceitaVersao,
            ["receita"] = JsonNode.Parse(corte.Receita),
            ["dados"] = FilaRegras.Dados(render.CorteId, corte.Ini, corte.Fim, corte.Forte, corte.Dados),
            ["destino"] = render.Destino,
            ["qualidade"] = render.Qualidade,
            ["tema"] = FilaRegras.Tema(req.Tema),
            ["nomeArquivo"] = render.NomeArquivo ?? $"zippy-corte{render.CorteId}-{render.Destino}.mp4",
            ["original"] = JsonSerializer.SerializeToNode(original, Web)
        };
        await c.ExecuteAsync(new CommandDefinition("""
            INSERT INTO social_trabalhos (estabelecimento_id, tipo, maquina_id, pedido_por_usuario_id, alvo_tipo, alvo_id, payload,
                                          chave_idempotencia)
            VALUES (@loja, @tipo, @maquina, @usuario, 'render', @render, CAST(@payload AS jsonb), @chave)
            """, new
        {
            loja, tipo = FilaRegras.TipoRenderizar, maquina = render.MaquinaId, usuario, render = render.Id,
            payload = payload.ToJsonString(),
            chave = FilaRegras.ChaveIdempotencia(render.CorteId, render.ReceitaVersao, render.MaquinaId!.Value, render.Destino,
                render.Qualidade)
        }, tx, cancellationToken: ct));
    }

    private sealed class CorteLinha
    {
        public decimal Ini { get; set; }
        public decimal Fim { get; set; }
        public decimal? Forte { get; set; }
        public string? Dados { get; set; }
        public long VideoId { get; set; }
        public bool Exemplo { get; set; }
        public int? Versao { get; set; }
        public string? Receita { get; set; }
    }
}
