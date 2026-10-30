using APIBack.Acontecimentos;
using Dapper;
using Npgsql;

namespace APIBack.GestaoLogica.CaixaListas;

/// <summary>
/// Listas do Caixa por loja. A consulta cria os padrões da loja na primeira vez (idempotente). Incluir um nome que já existe no
/// grupo reativa a opção; nada é excluído. O movimento grava o texto, então mudar a lista não mexe no histórico.
/// </summary>
public interface ICaixaListasService
{
    /// <summary>Gerente vê todas (inclusive desativadas); operador vê só as ativas.</summary>
    Task<IReadOnlyList<OpcaoListaDto>> Listar(Guid loja, bool gerente, CancellationToken ct);
    Task<OpcaoListaDto> Criar(Guid loja, int usuario, CriarOpcaoRequest req, CancellationToken ct);
    Task<OpcaoListaDto> Atualizar(Guid loja, Guid id, AtualizarOpcaoRequest req, CancellationToken ct);
}

public sealed class CaixaListasService(NpgsqlDataSource dataSource) : ICaixaListasService
{
    private const string Colunas = @"id AS Id, grupo AS Grupo, nome AS Nome, CASE WHEN padrao_chave IS NULL THEN 'personalizada' ELSE 'padrao' END AS Origem,
        ativo AS Ativo, ordem AS Ordem";

    public async Task<IReadOnlyList<OpcaoListaDto>> Listar(Guid loja, bool gerente, CancellationToken ct)
    {
        await using var c = await dataSource.OpenConnectionAsync(ct);
        await Semear(c, loja, ct);
        var linhas = await c.QueryAsync<OpcaoListaDto>(new CommandDefinition(
            $"SELECT {Colunas} FROM gestao_caixa_listas WHERE estabelecimento_id = @loja AND (@gerente OR ativo) ORDER BY grupo, ordem, nome",
            new { loja, gerente }, cancellationToken: ct));
        return linhas.ToList();
    }

    public async Task<OpcaoListaDto> Criar(Guid loja, int usuario, CriarOpcaoRequest req, CancellationToken ct)
    {
        var grupo = req.Grupo?.Trim() ?? "";
        if (!GrupoLista.Todos.Contains(grupo)) throw Invalido("Grupo inválido.");
        var nome = NomeValido(req.Nome);
        var chave = string.IsNullOrWhiteSpace(req.ChaveIdempotencia) ? null : req.ChaveIdempotencia.Trim();
        if (chave is { Length: > 100 }) throw Invalido("Chave inválida.");

        await using var c = await dataSource.OpenConnectionAsync(ct);
        await Semear(c, loja, ct);
        await using var tx = await c.BeginTransactionAsync(ct);
        if (chave is not null)
        {
            var repetida = await c.QuerySingleOrDefaultAsync<OpcaoListaDto>(new CommandDefinition(
                $"SELECT {Colunas} FROM gestao_caixa_listas WHERE estabelecimento_id = @loja AND chave = @chave", new { loja, chave }, tx, cancellationToken: ct));
            if (repetida is not null) return repetida;
        }
        var inserida = await c.QuerySingleOrDefaultAsync<Guid?>(new CommandDefinition(@"
            INSERT INTO gestao_caixa_listas (id, estabelecimento_id, grupo, nome, chave, ordem, criado_por)
            VALUES (@id, @loja, @grupo, @nome, @chave,
                    (SELECT COALESCE(MAX(ordem), 0) + 1 FROM gestao_caixa_listas WHERE estabelecimento_id = @loja AND grupo = @grupo), @usuario)
            ON CONFLICT DO NOTHING RETURNING id", new { id = Guid.NewGuid(), loja, grupo, nome, chave, usuario }, tx, cancellationToken: ct));
        if (inserida is null)
        {
            // Nome já existe no grupo: reativa e devolve. Se o conflito foi da chave (corrida), devolve a opção da chave.
            var existente = await c.QuerySingleOrDefaultAsync<Guid?>(new CommandDefinition(@"
                UPDATE gestao_caixa_listas SET ativo = true, atualizado_em = now()
                WHERE estabelecimento_id = @loja AND grupo = @grupo AND lower(trim(nome)) = lower(@nome) RETURNING id",
                new { loja, grupo, nome }, tx, cancellationToken: ct));
            inserida = existente ?? await c.QuerySingleOrDefaultAsync<Guid?>(new CommandDefinition(
                "SELECT id FROM gestao_caixa_listas WHERE estabelecimento_id = @loja AND chave = @chave", new { loja, chave }, tx, cancellationToken: ct));
        }
        var dto = await Carregar(c, loja, inserida ?? throw new GestaoException("CONFLITO", "Não foi possível salvar a opção.", 409), tx, ct);
        await tx.CommitAsync(ct);
        return dto;
    }

    public async Task<OpcaoListaDto> Atualizar(Guid loja, Guid id, AtualizarOpcaoRequest req, CancellationToken ct)
    {
        if (req.Nome is null && req.Ativo is null && req.Ordem is null) throw Invalido("Nada para alterar.");
        var nome = req.Nome is null ? null : NomeValido(req.Nome);
        if (req.Ordem is < 0 or > 9999) throw Invalido("Ordem inválida.");
        await using var c = await dataSource.OpenConnectionAsync(ct);
        try
        {
            var n = await c.ExecuteAsync(new CommandDefinition(@"
                UPDATE gestao_caixa_listas SET nome = COALESCE(@nome, nome), ativo = COALESCE(@ativo, ativo), ordem = COALESCE(@ordem, ordem),
                       atualizado_em = now()
                WHERE estabelecimento_id = @loja AND id = @id", new { loja, id, nome, ativo = req.Ativo, ordem = req.Ordem }, cancellationToken: ct));
            if (n == 0) throw new GestaoException("NAO_ENCONTRADO", "Opção não encontrada.", 404);
        }
        catch (PostgresException e) when (e.SqlState == PostgresErrorCodes.UniqueViolation)
        {
            throw new GestaoException("NOME_REPETIDO", "Já existe uma opção com esse nome neste grupo.", 409);
        }
        return await Carregar(c, loja, id, null, ct);
    }

    private static async Task<OpcaoListaDto> Carregar(NpgsqlConnection c, Guid loja, Guid id, NpgsqlTransaction? tx, CancellationToken ct) =>
        await c.QuerySingleAsync<OpcaoListaDto>(new CommandDefinition($"SELECT {Colunas} FROM gestao_caixa_listas WHERE estabelecimento_id = @loja AND id = @id",
            new { loja, id }, tx, cancellationToken: ct));

    /// <summary>Cria os padrões que faltam na loja. Padrão desativado ou renomeado não volta nem muda (a chave do padrão já existe).</summary>
    private static async Task Semear(NpgsqlConnection c, Guid loja, CancellationToken ct)
    {
        var grupos = GrupoLista.Padroes.Select(p => p.Grupo).ToArray();
        var chaves = GrupoLista.Padroes.Select(p => p.Chave).ToArray();
        var nomes = GrupoLista.Padroes.Select(p => p.Nome).ToArray();
        var ordens = GrupoLista.Padroes.Select((_, i) => i + 1).ToArray();
        await c.ExecuteAsync(new CommandDefinition(@"
            INSERT INTO gestao_caixa_listas (id, estabelecimento_id, grupo, nome, padrao_chave, ordem)
            SELECT gen_random_uuid(), @loja, p.grupo, p.nome, p.chave, p.ordem
            FROM unnest(@grupos, @chaves, @nomes, @ordens) AS p(grupo, chave, nome, ordem)
            ON CONFLICT DO NOTHING", new { loja, grupos, chaves, nomes, ordens }, cancellationToken: ct));
    }

    private static string NomeValido(string? nome)
    {
        var limpo = nome?.Trim() ?? "";
        if (limpo.Length < 2 || limpo.Length > 80) throw Invalido("O nome precisa ter de 2 a 80 caracteres.");
        return limpo;
    }

    private static GestaoException Invalido(string mensagem) => new("CAMPO_INVALIDO", mensagem, 422);
}
