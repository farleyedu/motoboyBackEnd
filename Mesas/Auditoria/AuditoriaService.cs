using System.Globalization;
using System.Text;
using APIBack.Mesas.Nucleo;
using APIBack.Mesas.Sessao;
using Dapper;
using Npgsql;

namespace APIBack.Mesas.Auditoria;

public interface IAuditoriaService
{
    Task<AuditoriaPaginaDto> ListarAsync(Guid loja, FiltroAuditoria filtro, CancellationToken ct);
    Task<IReadOnlyList<AuditoriaCartaoDto>> ResumoAsync(Guid loja, CancellationToken ct);
}

/// <summary>
/// Leitura do livro <c>mesa_evento</c> (imutável: a trigger do P01 recusa UPDATE e DELETE). Filtros: quem, o quê, quando, mesa,
/// só ocorrências. Página de 50, do mais novo para o mais velho, com cursor.
/// </summary>
public sealed class AuditoriaService(NpgsqlDataSource source) : IAuditoriaService
{
    public const int Pagina = 50;

    private sealed class EventoRow
    {
        public Guid Id { get; set; }
        public DateTime Quando { get; set; }
        public string Acao { get; set; } = "";
        public string? AtorId { get; set; }
        public string AtorNome { get; set; } = "";
        public string AtorPapel { get; set; } = "";
        public string? AlvoTipo { get; set; }
        public Guid? AlvoId { get; set; }
        public string? AlvoRotulo { get; set; }
        public string? Antes { get; set; }
        public string? Depois { get; set; }
        public string? Motivo { get; set; }
        public Guid? MesaId { get; set; }
        public string? MesaNumero { get; set; }
        public Guid? SessaoId { get; set; }
        public bool Ocorrencia { get; set; }
    }

    public async Task<AuditoriaPaginaDto> ListarAsync(Guid loja, FiltroAuditoria f, CancellationToken ct)
    {
        var limite = Math.Clamp(f.Limite ?? Pagina, 1, 200);
        var onde = new List<string>();
        var args = new DynamicParameters(new { loja, limite = limite + 1 });
        if (!string.IsNullOrWhiteSpace(f.Quem))
        {
            var quem = f.Quem.Trim();
            if (int.TryParse(quem, NumberStyles.None, CultureInfo.InvariantCulture, out _)) { onde.Add("e.ator_id = @quem"); args.Add("quem", quem); }
            else { onde.Add("e.ator_nome ILIKE @quemLike"); args.Add("quemLike", "%" + quem.Replace("%", "").Replace("_", "") + "%"); }
        }
        if (!string.IsNullOrWhiteSpace(f.Acao))
        {
            onde.Add("e.acao = ANY(@acoes)");
            args.Add("acoes", f.Acao.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));
        }
        if (f.De is DateTime de) { onde.Add("e.quando >= @de"); args.Add("de", Utc(de)); }
        if (f.Ate is DateTime ate) { onde.Add("e.quando < @ate"); args.Add("ate", Utc(ate)); }
        if (f.MesaId is Guid mesaId) { onde.Add("(e.mesa_id = @mesaId OR e.alvo_id = @mesaId)"); args.Add("mesaId", mesaId); }
        if (f.Ocorrencias == true) onde.Add("e.ocorrencia");
        if (!string.IsNullOrWhiteSpace(f.Cursor))
        {
            if (!LerCursor(f.Cursor, out var quando, out var id)) throw MesasException.Validacao("Página inválida. Recarregue.");
            onde.Add("(e.quando, e.id) < (@cq, @ci)");
            args.Add("cq", quando); args.Add("ci", id);
        }

        await using var c = await source.OpenConnectionAsync(ct);
        var itens = await Eventos(c, loja, onde.Count == 0 ? "TRUE" : string.Join(" AND ", onde), args, "e.quando DESC, e.id DESC", limite + 1, ct);
        string? cursor = null;
        if (itens.Count > limite)
        {
            itens = itens.Take(limite).ToList();
            cursor = Cursor(itens[^1].Quando, itens[^1].Id);
        }
        return new AuditoriaPaginaDto(itens, cursor);
    }

    public async Task<IReadOnlyList<AuditoriaCartaoDto>> ResumoAsync(Guid loja, CancellationToken ct)
    {
        await using var c = await source.OpenConnectionAsync(ct);
        var r = await c.QuerySingleAsync<(long Acoes, long Ocorrencias, long Pessoas)>(new CommandDefinition("""
            SELECT count(*), count(*) FILTER (WHERE ocorrencia), count(DISTINCT ator_id)
              FROM mesa_evento WHERE estabelecimento_id = @loja AND quando >= NOW() - INTERVAL '24 hours'
            """, new { loja }, cancellationToken: ct));
        return new[]
        {
            new AuditoriaCartaoDto("Registros nas últimas 24 h", r.Acoes.ToString(CultureInfo.InvariantCulture), null),
            new AuditoriaCartaoDto("Ocorrências", r.Ocorrencias.ToString(CultureInfo.InvariantCulture), r.Ocorrencias == 0 ? "Nada fora do comum" : "Reaberturas, encerramentos com saldo e afins"),
            new AuditoriaCartaoDto("Pessoas que mexeram", r.Pessoas.ToString(CultureInfo.InvariantCulture), null)
        };
    }

    /// <summary>Linhas da auditoria já com o texto pronto. Usado também pelo relatório da sessão.</summary>
    internal static async Task<List<EventoAuditoriaDto>> Eventos(NpgsqlConnection c, Guid loja, string onde, object args, string ordem, int limite,
        CancellationToken ct)
    {
        var rows = await c.QueryAsync<EventoRow>(new CommandDefinition($"""
            SELECT e.id, e.quando, e.acao, e.ator_id AS atorId, e.ator_nome AS atorNome, e.ator_papel AS atorPapel, e.alvo_tipo AS alvoTipo,
                   e.alvo_id AS alvoId, e.alvo_rotulo AS alvoRotulo, e.antes, e.depois, e.motivo, e.mesa_id AS mesaId, m.numero AS mesaNumero,
                   e.sessao_id AS sessaoId, e.ocorrencia
              FROM mesa_evento e
              LEFT JOIN mesa m ON m.id = e.mesa_id
             WHERE e.estabelecimento_id = @loja AND {onde}
             ORDER BY {ordem}
             LIMIT {limite}
            """, args, cancellationToken: ct));
        return rows.Select(r => new EventoAuditoriaDto(r.Id, r.Quando, r.Acao, SessaoRegras.RotuloAcao(r.Acao), new AtorDto(r.AtorId, r.AtorNome, r.AtorPapel),
            r.AlvoTipo, r.AlvoId, r.AlvoRotulo, r.Antes, r.Depois, r.Motivo, r.MesaId, r.MesaNumero, r.SessaoId, r.Ocorrencia,
            SessaoRegras.TextoEvento(r.AtorNome, r.Acao, r.AlvoRotulo, r.Antes, r.Depois))).ToList();
    }

    private static DateTime Utc(DateTime d) => d.Kind switch
    {
        DateTimeKind.Utc => d,
        DateTimeKind.Local => d.ToUniversalTime(),
        _ => DateTime.SpecifyKind(d, DateTimeKind.Utc)
    };

    public static string Cursor(DateTime quando, Guid id) =>
        Convert.ToBase64String(Encoding.UTF8.GetBytes(Utc(quando).Ticks.ToString(CultureInfo.InvariantCulture) + "|" + id.ToString("N")))
            .TrimEnd('=').Replace('+', '-').Replace('/', '_');

    public static bool LerCursor(string cursor, out DateTime quando, out Guid id)
    {
        quando = default; id = default;
        try
        {
            var b = cursor.Replace('-', '+').Replace('_', '/');
            b = b.PadRight(b.Length + (4 - b.Length % 4) % 4, '=');
            var partes = Encoding.UTF8.GetString(Convert.FromBase64String(b)).Split('|');
            if (partes.Length != 2 || !long.TryParse(partes[0], NumberStyles.None, CultureInfo.InvariantCulture, out var ticks)
                || !Guid.TryParseExact(partes[1], "N", out id)) return false;
            quando = new DateTime(ticks, DateTimeKind.Utc);
            return true;
        }
        catch (FormatException) { return false; }
        catch (ArgumentOutOfRangeException) { return false; }
    }
}
