using System.Text.Json;
using APIBack.Acontecimentos;
using APIBack.GestaoLogica.Documentos;
using Dapper;
using Npgsql;

namespace APIBack.GestaoLogica.Despesas;

// Finalidades da loja com id (auditoria-edicao item 19). O livro guarda a finalidade como texto e não é reescrito: todo nome que a
// finalidade já teve fica em gestao_finalidades_nomes apontando para o id; a tela troca o texto antigo pelo nome atual.

public sealed record FinalidadeDto(Guid Id, string Nome, bool Ativa, Guid? JuntadaEm, IReadOnlyList<string> Nomes, int Versao);

public sealed record FinalidadeEstado(string Nome, bool Ativa, Guid? JuntadaEm);

public sealed record FinalidadeHistoricoDto(Guid Id, Guid FinalidadeId, string Acao, FinalidadeEstado? Antes, FinalidadeEstado Depois, string? Motivo,
    int Por, string? PorNome, DateTimeOffset Em);

public sealed record RenomearFinalidadeRequest(string? Nome, int? Versao = null, string? Motivo = null);

public sealed record JuntarFinalidadeRequest(Guid? DestinoId, string? Motivo);

public static class AcaoFinalidade
{
    /// <summary><c>Juntada</c> fica na que saiu; <c>Recebeu</c>, na que ficou.</summary>
    public const string Criada = "criada", Renomeada = "renomeada", Desativada = "desativada", Reativada = "reativada", Juntada = "juntada",
        Recebeu = "recebeu";
}

public static class FinalidadeRegras
{
    public static string Nome(string? nome)
    {
        var limpo = string.Join(' ', (nome ?? "").Split(' ', StringSplitOptions.RemoveEmptyEntries));
        if (limpo.Length is < 2 or > 60 || DocumentoRegras.NomeChave(limpo).Length < 2) throw Invalido("Finalidade com 2 a 60 letras.");
        return limpo;
    }

    /// <summary>Juntar é para sempre (os nomes da que sai passam a mostrar a que fica): pede o motivo.</summary>
    public static string MotivoDeJuntar(string? motivo) =>
        motivo?.Trim() is { Length: >= 3 and <= 300 } m ? m : throw Invalido("Diga por que juntar (3 a 300 letras).");

    public static GestaoException Invalido(string m) => new("CAMPO_INVALIDO", m, 422);
}

public interface IFinalidadesService
{
    Task<IReadOnlyList<string>> Ativas(Guid loja, CancellationToken ct);
    Task<IReadOnlyList<FinalidadeDto>> Listar(Guid loja, CancellationToken ct);
    Task<string> Criar(Guid loja, int usuario, string? nome, CancellationToken ct);
    Task<FinalidadeDto> Renomear(Guid loja, int usuario, Guid id, RenomearFinalidadeRequest req, CancellationToken ct);
    Task<FinalidadeDto> Desativar(Guid loja, int usuario, Guid id, string? motivo, CancellationToken ct);
    Task<FinalidadeDto> Reativar(Guid loja, int usuario, Guid id, string? motivo, CancellationToken ct);
    Task<FinalidadeDto> Juntar(Guid loja, int usuario, Guid origem, JuntarFinalidadeRequest req, CancellationToken ct);
    Task<IReadOnlyList<FinalidadeHistoricoDto>> Historico(Guid loja, Guid id, CancellationToken ct);
}

public sealed class FinalidadesService(NpgsqlDataSource dataSource, TimeProvider? relogio = null) : IFinalidadesService
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    private readonly TimeProvider _relogio = relogio ?? TimeProvider.System;

    private sealed class Row
    {
        public Guid Id { get; set; }
        public string Nome { get; set; } = "";
        public bool Ativa { get; set; }
        public Guid? JuntadaEm { get; set; }
        public int Versao { get; set; }
        public FinalidadeEstado Estado() => new(Nome, Ativa, JuntadaEm);
    }

    public async Task<IReadOnlyList<string>> Ativas(Guid loja, CancellationToken ct) =>
        (await Listar(loja, ct)).Where(f => f.Ativa && f.JuntadaEm is null).Select(f => f.Nome).ToList();

    public async Task<IReadOnlyList<FinalidadeDto>> Listar(Guid loja, CancellationToken ct)
    {
        await using var c = await dataSource.OpenConnectionAsync(ct);
        await using (var tx = await c.BeginTransactionAsync(ct))
        {
            await Sincronizar(c, tx, loja);
            await tx.CommitAsync(ct);
        }
        var nomes = (await c.QueryAsync<(Guid Id, string Nome)>(
                "SELECT finalidade_id, nome FROM gestao_finalidades_nomes WHERE estabelecimento_id = @loja ORDER BY criado_em, nome", new { loja }))
            .ToLookup(n => n.Id, n => n.Nome);
        return (await c.QueryAsync<Row>(@"SELECT id, nome, ativa, juntada_em, versao FROM gestao_finalidades_cadastro
 WHERE estabelecimento_id = @loja ORDER BY nome", new { loja }))
            .Select(r => new FinalidadeDto(r.Id, r.Nome, r.Ativa, r.JuntadaEm, nomes[r.Id].ToList(), r.Versao)).ToList();
    }

    /// <summary>
    /// A mesma escrita de outro jeito ("Gás", "gas") é a mesma finalidade. Um nome que já existiu (renomeada, juntada) devolve a
    /// finalidade de hoje; se ela estava desativada, volta a valer.
    /// </summary>
    public async Task<string> Criar(Guid loja, int usuario, string? nome, CancellationToken ct)
    {
        var limpo = FinalidadeRegras.Nome(nome);
        var chave = DocumentoRegras.NomeChave(limpo);
        await using var c = await dataSource.OpenConnectionAsync(ct);
        await using var tx = await c.BeginTransactionAsync(ct);
        await Sincronizar(c, tx, loja);
        // A tabela antiga continua recebendo (quem ainda lê a lista simples não perde nada).
        await c.ExecuteAsync(@"INSERT INTO gestao_finalidades (estabelecimento_id, nome, chave, criado_por) VALUES (@loja, @limpo, @chave, @usuario)
ON CONFLICT DO NOTHING", new { loja, limpo, chave, usuario }, tx);
        string resultado;
        if (await DonoDaChave(c, tx, loja, chave) is { } id)
        {
            var atual = await Obter(c, tx, loja, id);
            if (!atual.Ativa)
                await Mudar(c, tx, loja, usuario, atual, atual.Nome, true, null, AcaoFinalidade.Reativada, "Escolhida de novo");
            resultado = atual.Nome;
        }
        else
        {
            await Inserir(c, tx, loja, usuario, limpo, chave, null);
            resultado = limpo;
        }
        await tx.CommitAsync(ct);
        return resultado;
    }

    public async Task<FinalidadeDto> Renomear(Guid loja, int usuario, Guid id, RenomearFinalidadeRequest req, CancellationToken ct)
    {
        var nome = FinalidadeRegras.Nome(req.Nome);
        var motivo = RecorrenciaRegras.Motivo(req.Motivo);
        var chave = DocumentoRegras.NomeChave(nome);
        await using var c = await dataSource.OpenConnectionAsync(ct);
        await using var tx = await c.BeginTransactionAsync(ct);
        var f = await Obter(c, tx, loja, id);
        if (f.JuntadaEm is not null) throw Juntada();
        if (req.Versao is { } v && v != f.Versao) throw new GestaoException("IDEMPOTENCIA_CONFLITO", "Outra pessoa mudou esta finalidade. Atualize a tela.", 409);
        if (await DonoDaChave(c, tx, loja, chave) is { } dono && dono != id)
        {
            var outra = await Obter(c, tx, loja, dono);
            throw new GestaoException("FINALIDADE_EXISTE", $"Já existe a finalidade {outra.Nome}. Para unir as duas, use Juntar.", 409);
        }
        if (f.Nome != nome)
        {
            await c.ExecuteAsync(@"INSERT INTO gestao_finalidades_nomes (estabelecimento_id, chave, nome, finalidade_id) VALUES (@loja, @chave, @nome, @id)
ON CONFLICT DO NOTHING", new { loja, chave, nome, id }, tx);
            await Mudar(c, tx, loja, usuario, f, nome, f.Ativa, null, AcaoFinalidade.Renomeada, motivo);
        }
        await tx.CommitAsync(ct);
        return await Dto(loja, id, ct);
    }

    public Task<FinalidadeDto> Desativar(Guid loja, int usuario, Guid id, string? motivo, CancellationToken ct) => MudarAtiva(loja, usuario, id, false, motivo, ct);

    public Task<FinalidadeDto> Reativar(Guid loja, int usuario, Guid id, string? motivo, CancellationToken ct) => MudarAtiva(loja, usuario, id, true, motivo, ct);

    private async Task<FinalidadeDto> MudarAtiva(Guid loja, int usuario, Guid id, bool ativa, string? motivo, CancellationToken ct)
    {
        motivo = RecorrenciaRegras.Motivo(motivo);
        await using var c = await dataSource.OpenConnectionAsync(ct);
        await using var tx = await c.BeginTransactionAsync(ct);
        var f = await Obter(c, tx, loja, id);
        if (f.JuntadaEm is not null) throw Juntada();
        if (f.Ativa != ativa)
            await Mudar(c, tx, loja, usuario, f, f.Nome, ativa, null, ativa ? AcaoFinalidade.Reativada : AcaoFinalidade.Desativada, motivo);
        await tx.CommitAsync(ct);
        return await Dto(loja, id, ct);
    }

    /// <summary>
    /// A finalidade <paramref name="origem"/> passa a ser a do destino: os lançamentos antigos dela mostram o nome do destino. Nada é
    /// reescrito no livro; o que já tinha sido juntado à origem passa a apontar para o destino (nunca em cadeia).
    /// </summary>
    public async Task<FinalidadeDto> Juntar(Guid loja, int usuario, Guid origem, JuntarFinalidadeRequest req, CancellationToken ct)
    {
        var motivo = FinalidadeRegras.MotivoDeJuntar(req.Motivo);
        if (req.DestinoId is not { } destino) throw FinalidadeRegras.Invalido("Escolha a finalidade que fica.");
        if (destino == origem) throw FinalidadeRegras.Invalido("Escolha outra finalidade para juntar.");
        await using var c = await dataSource.OpenConnectionAsync(ct);
        await using var tx = await c.BeginTransactionAsync(ct);
        var sai = await Obter(c, tx, loja, origem);
        var fica = await Obter(c, tx, loja, destino);
        if (sai.JuntadaEm is not null || fica.JuntadaEm is not null) throw Juntada();
        await Mudar(c, tx, loja, usuario, sai, sai.Nome, false, destino, AcaoFinalidade.Juntada, motivo);
        await c.ExecuteAsync(@"UPDATE gestao_finalidades_cadastro SET juntada_em = @destino, versao = versao + 1, atualizado_por = @usuario, atualizado_em = @agora
 WHERE estabelecimento_id = @loja AND juntada_em = @origem", new { loja, origem, destino, usuario, agora = _relogio.GetUtcNow() }, tx);
        await Historico(c, tx, loja, usuario, destino, AcaoFinalidade.Recebeu, fica.Estado(), fica.Estado(), $"{sai.Nome}: {motivo}");
        await tx.CommitAsync(ct);
        return await Dto(loja, destino, ct);
    }

    public async Task<IReadOnlyList<FinalidadeHistoricoDto>> Historico(Guid loja, Guid id, CancellationToken ct)
    {
        await using var c = await dataSource.OpenConnectionAsync(ct);
        return (await c.QueryAsync<(Guid Id, Guid FinalidadeId, string Acao, string? Antes, string Depois, string? Motivo, int Por, string? PorNome, DateTime Em)>(@"
SELECT h.id, h.finalidade_id, h.acao, h.antes::text, h.depois::text, h.motivo, h.por, u.nome::text, h.em
  FROM gestao_finalidades_historico h LEFT JOIN usuario u ON u.id = h.por
 WHERE h.estabelecimento_id = @loja AND h.finalidade_id = @id ORDER BY h.em DESC, h.id", new { loja, id }))
            .Select(h => new FinalidadeHistoricoDto(h.Id, h.FinalidadeId, h.Acao,
                h.Antes is null ? null : JsonSerializer.Deserialize<FinalidadeEstado>(h.Antes, Json), JsonSerializer.Deserialize<FinalidadeEstado>(h.Depois, Json)!,
                h.Motivo, h.Por, h.PorNome, new DateTimeOffset(DateTime.SpecifyKind(h.Em, DateTimeKind.Utc))))
            .ToList();
    }

    // ---------------------------------------------------------------- apoio

    /// <summary>
    /// Trava as finalidades da loja nesta transação e traz para o cadastro com id as que só existem na tabela antiga (criadas antes
    /// desta versão). Rodar de novo não duplica.
    /// </summary>
    private static async Task Sincronizar(NpgsqlConnection c, NpgsqlTransaction tx, Guid loja)
    {
        await c.ExecuteAsync("SELECT pg_advisory_xact_lock(hashtext('gestao_finalidades:' || @loja::text))", new { loja }, tx);
        var faltam = (await c.QueryAsync<(string Nome, string Chave, int Por)>(@"
SELECT f.nome, f.chave, f.criado_por FROM gestao_finalidades f
 WHERE f.estabelecimento_id = @loja
   AND NOT EXISTS (SELECT 1 FROM gestao_finalidades_nomes n WHERE n.estabelecimento_id = f.estabelecimento_id AND n.chave = f.chave)
 ORDER BY f.criado_em", new { loja }, tx)).ToList();
        foreach (var f in faltam) await Inserir(c, tx, loja, f.Por, f.Nome, f.Chave, "Cadastro anterior");
    }

    private static async Task Inserir(NpgsqlConnection c, NpgsqlTransaction tx, Guid loja, int usuario, string nome, string chave, string? motivo)
    {
        var id = Guid.NewGuid();
        await c.ExecuteAsync(@"
INSERT INTO gestao_finalidades_cadastro (id, estabelecimento_id, nome, criado_por, atualizado_por) VALUES (@id, @loja, @nome, @usuario, @usuario);
INSERT INTO gestao_finalidades_nomes (estabelecimento_id, chave, nome, finalidade_id) VALUES (@loja, @chave, @nome, @id);", new { id, loja, nome, chave, usuario }, tx);
        await Historico(c, tx, loja, usuario, id, AcaoFinalidade.Criada, null, new FinalidadeEstado(nome, true, null), motivo);
    }

    private async Task Mudar(NpgsqlConnection c, NpgsqlTransaction tx, Guid loja, int usuario, Row antes, string nome, bool ativa, Guid? juntadaEm,
        string acao, string? motivo)
    {
        await c.ExecuteAsync(@"UPDATE gestao_finalidades_cadastro SET nome = @nome, ativa = @ativa, juntada_em = @juntadaEm, versao = versao + 1,
       atualizado_por = @usuario, atualizado_em = @agora WHERE estabelecimento_id = @loja AND id = @id",
            new { loja, id = antes.Id, nome, ativa, juntadaEm, usuario, agora = _relogio.GetUtcNow() }, tx);
        await Historico(c, tx, loja, usuario, antes.Id, acao, antes.Estado(), new FinalidadeEstado(nome, ativa, juntadaEm), motivo);
    }

    private static Task Historico(NpgsqlConnection c, NpgsqlTransaction tx, Guid loja, int usuario, Guid id, string acao, FinalidadeEstado? antes,
        FinalidadeEstado depois, string? motivo) =>
        c.ExecuteAsync(@"
INSERT INTO gestao_finalidades_historico (id, estabelecimento_id, finalidade_id, acao, antes, depois, motivo, por)
VALUES (@hid, @loja, @id, @acao, @antes::jsonb, @depois::jsonb, @motivo, @usuario)", new
        {
            hid = Guid.NewGuid(), loja, id, acao, antes = antes is null ? null : JsonSerializer.Serialize(antes, Json),
            depois = JsonSerializer.Serialize(depois, Json), motivo = motivo is { Length: > 300 } ? motivo[..300] : motivo, usuario,
        }, tx);

    /// <summary>Id da finalidade de hoje para a chave (seguindo a junção).</summary>
    private static async Task<Guid?> DonoDaChave(NpgsqlConnection c, NpgsqlTransaction tx, Guid loja, string chave) =>
        await c.ExecuteScalarAsync<Guid?>(@"SELECT coalesce(f.juntada_em, f.id) FROM gestao_finalidades_nomes n
  JOIN gestao_finalidades_cadastro f ON f.id = n.finalidade_id WHERE n.estabelecimento_id = @loja AND n.chave = @chave", new { loja, chave }, tx);

    private static async Task<Row> Obter(NpgsqlConnection c, NpgsqlTransaction tx, Guid loja, Guid id)
    {
        await c.ExecuteAsync("SELECT pg_advisory_xact_lock(hashtext('gestao_finalidades:' || @loja::text))", new { loja }, tx);
        return await c.QuerySingleOrDefaultAsync<Row>(@"SELECT id, nome, ativa, juntada_em, versao FROM gestao_finalidades_cadastro
 WHERE estabelecimento_id = @loja AND id = @id", new { loja, id }, tx)
            ?? throw new GestaoException("NAO_ENCONTRADO", "Finalidade não encontrada.", 404);
    }

    private async Task<FinalidadeDto> Dto(Guid loja, Guid id, CancellationToken ct) => (await Listar(loja, ct)).Single(f => f.Id == id);

    private static GestaoException Juntada() => new("CAMPO_INVALIDO", "Esta finalidade foi juntada a outra e não muda mais.", 422);
}
