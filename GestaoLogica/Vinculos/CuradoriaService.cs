using APIBack.Acontecimentos;
using APIBack.GestaoLogica.Documentos;
using Dapper;
using Npgsql;

namespace APIBack.GestaoLogica.Vinculos;

public sealed record ParecidoCatalogo(Guid Id, string Nome, string Unidade, decimal Semelhanca);

public sealed record CatalogoGlobalDto(Guid Id, string Nome, string Unidade, string? Categoria, string? Ean, string Status,
    Guid? MescladoEm, int Lojas, IReadOnlyList<string> NomesNoCupom, IReadOnlyList<ParecidoCatalogo> Parecidos,
    DateTimeOffset CriadoEm, string? MotivoCuradoria);

public sealed record ValidarCatalogoRequest(string? Nome, string? Categoria, string? Ean);

/// <summary>
/// Curadoria Zippy do catálogo global (só super admin). Validar faz o item valer para todas as lojas; rejeitar e mesclar
/// nunca mexem no vínculo que a loja já usa (a curadoria não bloqueia a operação local).
/// </summary>
public interface ICuradoriaService
{
    Task<IReadOnlyList<CatalogoGlobalDto>> Candidatos(CancellationToken ct);
    Task<CatalogoGlobalDto> Validar(int usuario, Guid id, ValidarCatalogoRequest req, CancellationToken ct);
    Task<CatalogoGlobalDto> Rejeitar(int usuario, Guid id, string? motivo, CancellationToken ct);
    Task<CatalogoGlobalDto> Mesclar(int usuario, Guid id, Guid destino, CancellationToken ct);
}

public sealed class CuradoriaService(NpgsqlDataSource dataSource, TimeProvider? relogio = null) : ICuradoriaService
{
    private readonly TimeProvider _relogio = relogio ?? TimeProvider.System;

    private const decimal SemelhancaParecido = 0.5m;

    public async Task<IReadOnlyList<CatalogoGlobalDto>> Candidatos(CancellationToken ct)
    {
        await using var c = await dataSource.OpenConnectionAsync(ct);
        var validados = (await c.QueryAsync<(Guid Id, string Nome, string Unidade)>(
            "SELECT id, nome, unidade FROM gestao_catalogo_global WHERE status = 'validado'")).ToList();
        return (await Ler(c, null, "g.status = 'candidato'", null)).Select(d => d with
        {
            // Ajuda a decidir entre validar e mesclar.
            Parecidos = validados.Where(v => v.Unidade == d.Unidade)
                .Select(v => new ParecidoCatalogo(v.Id, v.Nome, v.Unidade, VinculoRegras.Semelhanca(d.Nome, v.Nome)))
                .Where(p => p.Semelhanca >= SemelhancaParecido).OrderByDescending(p => p.Semelhanca).Take(3).ToList(),
        }).ToList();
    }

    public async Task<CatalogoGlobalDto> Validar(int usuario, Guid id, ValidarCatalogoRequest req, CancellationToken ct)
    {
        await using var c = await dataSource.OpenConnectionAsync(ct);
        await using var tx = await c.BeginTransactionAsync(ct);
        var atual = await Candidato(c, tx, id);
        var nome = string.IsNullOrWhiteSpace(req.Nome) ? atual.Nome : req.Nome.Trim();
        var ean = req.Ean is null ? null : DocumentoRegras.SoDigitos(req.Ean);
        try
        {
            await c.ExecuteAsync(@"
UPDATE gestao_catalogo_global SET status = 'validado', nome = @nome, nome_chave = @nomeChave,
       categoria = coalesce(@categoria, categoria), ean = coalesce(@ean, ean), curado_por = @usuario, curado_em = @agora
 WHERE id = @id", new
            {
                id, nome = Cortar(nome, 120), nomeChave = Cortar(DocumentoRegras.NomeChave(nome), 160),
                categoria = Cortar(req.Categoria?.Trim(), 60), ean, usuario, agora = _relogio.GetUtcNow(),
            }, tx);
        }
        catch (PostgresException ex) when (ex.SqlState == PostgresErrorCodes.UniqueViolation)
        {
            throw new GestaoException("DUPLICADO", "Já existe um item validado com esse nome e unidade (ou esse código de barras): use mesclar.", 409);
        }
        await tx.CommitAsync(ct);
        return await Obter(c, id);
    }

    public async Task<CatalogoGlobalDto> Rejeitar(int usuario, Guid id, string? motivo, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(motivo)) throw Invalido("Diga por que o item foi rejeitado.");
        await using var c = await dataSource.OpenConnectionAsync(ct);
        await using var tx = await c.BeginTransactionAsync(ct);
        await Candidato(c, tx, id);
        await c.ExecuteAsync(@"
UPDATE gestao_catalogo_global SET status = 'rejeitado', curado_por = @usuario, curado_em = @agora, motivo_curadoria = @motivo
 WHERE id = @id", new { id, usuario, agora = _relogio.GetUtcNow(), motivo = Cortar(motivo.Trim(), 300) }, tx);
        await tx.CommitAsync(ct);
        return await Obter(c, id);
    }

    public async Task<CatalogoGlobalDto> Mesclar(int usuario, Guid id, Guid destino, CancellationToken ct)
    {
        if (id == destino) throw Invalido("Escolha outro item para mesclar.");
        await using var c = await dataSource.OpenConnectionAsync(ct);
        await using var tx = await c.BeginTransactionAsync(ct);
        var atual = await Candidato(c, tx, id);
        var alvo = await c.QuerySingleOrDefaultAsync<(Guid Id, string Unidade, string Status)>(
            "SELECT id, unidade, status FROM gestao_catalogo_global WHERE id = @destino FOR UPDATE", new { destino }, tx);
        if (alvo.Id == Guid.Empty || alvo.Status != "validado") throw Invalido("Só dá para mesclar num item validado.");
        if (alvo.Unidade != atual.Unidade) throw Invalido("Os dois itens precisam ter a mesma unidade.");
        var agora = _relogio.GetUtcNow();
        await c.ExecuteAsync(@"
UPDATE gestao_catalogo_global SET status = 'mesclado', mesclado_em = @destino, curado_por = @usuario, curado_em = @agora,
       motivo_curadoria = 'Mesclado em outro item validado.' WHERE id = @id", new { id, destino, usuario, agora }, tx);
        // Os vínculos das lojas passam a contar para o item validado; o insumo de cada loja continua o mesmo.
        await c.ExecuteAsync("UPDATE gestao_vinculos SET global_id = @destino WHERE global_id = @id", new { id, destino }, tx);
        await tx.CommitAsync(ct);
        return await Obter(c, id);
    }

    private static async Task<CatalogoGlobalDto> Candidato(NpgsqlConnection c, NpgsqlTransaction tx, Guid id)
    {
        await c.ExecuteAsync("SELECT 1 FROM gestao_catalogo_global WHERE id = @id FOR UPDATE", new { id }, tx);
        var d = (await Ler(c, tx, "g.id = @id", id)).SingleOrDefault()
            ?? throw new GestaoException("NAO_ENCONTRADO", "Item do catálogo não encontrado.", 404);
        if (d.Status != "candidato") throw new GestaoException("DUPLICADO", $"Este item já foi curado ({d.Status}).", 409);
        return d;
    }

    private static async Task<CatalogoGlobalDto> Obter(NpgsqlConnection c, Guid id) => (await Ler(c, null, "g.id = @id", id)).Single();

    private static async Task<IReadOnlyList<CatalogoGlobalDto>> Ler(NpgsqlConnection c, NpgsqlTransaction? tx, string filtro, Guid? id)
    {
        var linhas = await c.QueryAsync<(Guid Id, string Nome, string Unidade, string? Categoria, string? Ean, string Status,
            Guid? MescladoEm, int Lojas, string[]? Nomes, DateTime CriadoEm, string? Motivo)>($@"
SELECT g.id, g.nome, g.unidade, g.categoria, g.ean, g.status, g.mesclado_em,
       count(DISTINCT v.estabelecimento_id)::int,
       array_remove(array_agg(DISTINCT v.descricao), NULL), g.criado_em, g.motivo_curadoria
  FROM gestao_catalogo_global g
  LEFT JOIN gestao_vinculos v ON v.global_id = g.id AND v.estado = 'confirmado'
 WHERE {filtro}
 GROUP BY g.id
 ORDER BY count(DISTINCT v.estabelecimento_id) DESC, g.criado_em
 LIMIT 500", new { id }, tx);
        return linhas.Select(l => new CatalogoGlobalDto(l.Id, l.Nome, l.Unidade, l.Categoria, l.Ean, l.Status, l.MescladoEm, l.Lojas,
            l.Nomes ?? Array.Empty<string>(), Array.Empty<ParecidoCatalogo>(),
            new DateTimeOffset(DateTime.SpecifyKind(l.CriadoEm, DateTimeKind.Utc)), l.Motivo)).ToList();
    }

    private static string? Cortar(string? s, int max) => s is null || s.Length <= max ? s : s[..max];

    private static GestaoException Invalido(string m) => new("CAMPO_INVALIDO", m, 422);
}
