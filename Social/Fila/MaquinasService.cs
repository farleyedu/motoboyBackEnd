using APIBack.Options;
using APIBack.Social.Editor;
using APIBack.Social.Nucleo;
using Dapper;
using Microsoft.Extensions.Options;
using Npgsql;

namespace APIBack.Social.Fila;

public sealed record CodigoPareamentoDto(string Codigo, DateTime ExpiraEm);

public sealed record MaquinaDto(long Id, string Nome, bool Minha, bool CompartilhadaEquipe, bool PausadaPorPlano, bool Ativa, bool Online,
    bool UsaGpu, string? Gpu, long? EspacoLivre, string? Versao, DateTime? UltimoSinalEm);

public sealed record MaquinasDto(int Limite, int Usadas, IReadOnlyList<MaquinaDto> Itens);

public sealed record MudarMaquinaRequest(string? Nome, bool? CompartilhadaEquipe, bool? Ativa);

public interface ISocialMaquinasService
{
    Task<CodigoPareamentoDto> NovoCodigoAsync(Guid loja, int usuario, CancellationToken ct);
    Task<MaquinasDto> ListarAsync(Guid loja, int usuario, CancellationToken ct);
    Task<MaquinaDto> MudarAsync(Guid loja, int usuario, long id, MudarMaquinaRequest req, CancellationToken ct);
    Task RemoverAsync(Guid loja, int usuario, long id, CancellationToken ct);
}

/// <summary>
/// Máquinas vistas pelo painel (R14). Cada pessoa só vê as dela e as que a equipe compartilhou; só o dono muda ou remove.
/// </summary>
public sealed class SocialMaquinasService(NpgsqlDataSource fonte, IOptions<SocialMaquinasOptions> opcoes, ISocialTempoReal? tempoReal = null)
    : ISocialMaquinasService
{
    private sealed class Linha
    {
        public long Id { get; set; }
        public int UsuarioId { get; set; }
        public string Nome { get; set; } = "";
        public bool Compartilhada { get; set; }
        public bool Ativa { get; set; }
        public bool Pausada { get; set; }
        public bool UsaGpu { get; set; }
        public string? Gpu { get; set; }
        public long? EspacoLivre { get; set; }
        public string? Versao { get; set; }
        public DateTime? UltimoSinalEm { get; set; }

        public MaquinaDto Dto(int usuario) => new(Id, Nome, UsuarioId == usuario, Compartilhada, Pausada, Ativa,
            FilaRegras.EstaOnline(UltimoSinalEm, DateTime.UtcNow), UsaGpu, Gpu, EspacoLivre, Versao, UltimoSinalEm);
    }

    private const string Colunas = """
        id AS "Id", usuario_id AS "UsuarioId", nome AS "Nome", compartilhada_equipe AS "Compartilhada", ativa AS "Ativa",
        pausada_por_plano AS "Pausada", usa_gpu AS "UsaGpu", gpu AS "Gpu", espaco_livre AS "EspacoLivre", versao AS "Versao",
        ultimo_sinal_em AS "UltimoSinalEm"
        """;

    private int Limite => Math.Max(0, opcoes.Value.LimitePorLoja);

    internal static Task<int> Usadas(NpgsqlConnection c, NpgsqlTransaction tx, Guid loja, CancellationToken ct) =>
        c.ExecuteScalarAsync<int>(new CommandDefinition("""
            SELECT COUNT(*)::int FROM social_maquinas WHERE estabelecimento_id = @loja AND tipo = 'worker' AND removida_em IS NULL
            """, new { loja }, tx, cancellationToken: ct));

    public async Task<CodigoPareamentoDto> NovoCodigoAsync(Guid loja, int usuario, CancellationToken ct)
    {
        // Como sistema: o código não pode repetir um que esteja em aberto em outra loja.
        for (var tentativa = 1; ; tentativa++)
        {
            try
            {
                return await SocialBanco.ComoSistema(fonte, ct, async (c, tx) =>
                {
                    if (await Usadas(c, tx, loja, ct) >= Limite) throw FilaRegras.Limite(Limite);
                    // um código em aberto por pessoa; os vencidos de todos saem para liberar os números
                    await c.ExecuteAsync(new CommandDefinition("""
                        DELETE FROM social_pareamentos
                         WHERE usado_em IS NULL AND (expira_em <= NOW() OR (estabelecimento_id = @loja AND gerado_por_usuario_id = @usuario))
                        """, new { loja, usuario }, tx, cancellationToken: ct));
                    var codigo = FilaRegras.NovoCodigo();
                    var expira = await c.ExecuteScalarAsync<DateTime>(new CommandDefinition("""
                        INSERT INTO social_pareamentos (estabelecimento_id, codigo, gerado_por_usuario_id, expira_em)
                        VALUES (@loja, @codigo, @usuario, NOW() + make_interval(secs => @validade))
                        RETURNING expira_em
                        """, new { loja, codigo, usuario, validade = FilaRegras.ValidadeCodigo.TotalSeconds }, tx, cancellationToken: ct));
                    return new CodigoPareamentoDto(codigo, expira);
                });
            }
            catch (PostgresException ex) when (ex.SqlState == PostgresErrorCodes.UniqueViolation && tentativa < 5)
            {
                // sorteou um código que já está em aberto: sorteia outro
            }
        }
    }

    public Task<MaquinasDto> ListarAsync(Guid loja, int usuario, CancellationToken ct) =>
        SocialBanco.NaLoja(fonte, loja, ct, async (c, tx) =>
        {
            var linhas = await c.QueryAsync<Linha>(new CommandDefinition($"""
                SELECT {Colunas} FROM social_maquinas
                 WHERE estabelecimento_id = @loja AND tipo = 'worker' AND removida_em IS NULL
                   AND (usuario_id = @usuario OR compartilhada_equipe)
                 ORDER BY (usuario_id = @usuario) DESC, nome, id
                """, new { loja, usuario }, tx, cancellationToken: ct));
            return new MaquinasDto(Limite, await Usadas(c, tx, loja, ct), linhas.Select(l => l.Dto(usuario)).ToList());
        });

    public async Task<MaquinaDto> MudarAsync(Guid loja, int usuario, long id, MudarMaquinaRequest req, CancellationToken ct)
    {
        var nome = req.Nome is null ? null : FilaRegras.Curto(req.Nome, 60) ?? throw SocialException.Validacao("Dê um nome à máquina.");
        var (dto, falhados) = await SocialBanco.NaLoja(fonte, loja, ct, async (c, tx) =>
        {
            await Dona(c, tx, loja, usuario, id, ct);
            var linha = await c.QuerySingleAsync<Linha>(new CommandDefinition($"""
                UPDATE social_maquinas
                   SET nome = COALESCE(@nome, nome), compartilhada_equipe = COALESCE(@compartilhada, compartilhada_equipe),
                       ativa = COALESCE(@ativa, ativa)
                 WHERE id = @id AND estabelecimento_id = @loja
                RETURNING {Colunas}
                """, new { nome, compartilhada = req.CompartilhadaEquipe, ativa = req.Ativa, id, loja }, tx, cancellationToken: ct));
            // Deixou de compartilhar: o que já começou termina; o que os outros pediram e não começou sai da fila.
            IReadOnlyList<RenderDto> sairam = linha.Compartilhada ? [] : await EncerrarTrabalhos(c, tx, loja, id,
                "A máquina deixou de ser compartilhada.", soPendentesDeOutros: linha.UsuarioId, ct);
            return (linha.Dto(usuario), sairam);
        });
        await Avisar(loja, id, falhados, ct);
        return dto;
    }

    public async Task RemoverAsync(Guid loja, int usuario, long id, CancellationToken ct)
    {
        var falhados = await SocialBanco.NaLoja(fonte, loja, ct, async (c, tx) =>
        {
            await Dona(c, tx, loja, usuario, id, ct);
            await c.ExecuteAsync(new CommandDefinition("""
                UPDATE social_maquinas SET token_hash = NULL, removida_em = NOW(), compartilhada_equipe = FALSE
                 WHERE id = @id AND estabelecimento_id = @loja
                """, new { id, loja }, tx, cancellationToken: ct));
            return await EncerrarTrabalhos(c, tx, loja, id, "A máquina foi removida.", soPendentesDeOutros: null, ct);
        });
        await Avisar(loja, id, falhados, ct);
    }

    private async Task Avisar(Guid loja, long maquinaId, IReadOnlyList<RenderDto> renders, CancellationToken ct)
    {
        if (tempoReal is null) return;
        await tempoReal.MaquinaAtualizada(loja, maquinaId, ct);
        foreach (var r in renders) await tempoReal.RenderAtualizado(loja, r, ct);
    }

    private static async Task Dona(NpgsqlConnection c, NpgsqlTransaction tx, Guid loja, int usuario, long id, CancellationToken ct)
    {
        var linha = await c.QuerySingleOrDefaultAsync<Linha>(new CommandDefinition($"""
            SELECT {Colunas} FROM social_maquinas WHERE id = @id AND estabelecimento_id = @loja AND removida_em IS NULL FOR UPDATE
            """, new { id, loja }, tx, cancellationToken: ct));
        // máquina de outra pessoa que não está compartilhada nem aparece
        if (linha is null || !FilaRegras.PodeUsar(linha.UsuarioId, linha.Compartilhada, usuario))
            throw SocialException.NaoEncontrado("Essa máquina não existe.");
        if (linha.UsuarioId != usuario) throw SocialException.SemPermissao("Só o dono da máquina pode mudar ou remover.");
    }

    /// <summary>
    /// Falha os trabalhos abertos da máquina e os renders deles. Com <paramref name="soPendentesDeOutros"/> (o dono), só os que
    /// ainda não começaram e foram pedidos por outra pessoa.
    /// </summary>
    private static async Task<IReadOnlyList<RenderDto>> EncerrarTrabalhos(NpgsqlConnection c, NpgsqlTransaction tx, Guid loja, long maquinaId,
        string motivo, int? soPendentesDeOutros, CancellationToken ct)
    {
        var alvos = (await c.QueryAsync<Guid>(new CommandDefinition("""
            UPDATE social_trabalhos SET status = 'falhou', erro = @motivo, concluido_em = NOW()
             WHERE estabelecimento_id = @loja AND maquina_id = @maquinaId
               AND (CASE WHEN CAST(@dono AS integer) IS NULL THEN status IN ('pendente', 'rodando')
                         ELSE status = 'pendente' AND pedido_por_usuario_id <> CAST(@dono AS integer) END)
            RETURNING alvo_id
            """, new { loja, maquinaId, motivo, dono = soPendentesDeOutros }, tx, cancellationToken: ct))).ToArray();
        return await RenderDaFila.Falhar(c, tx, loja, alvos, motivo, ct);
    }
}
