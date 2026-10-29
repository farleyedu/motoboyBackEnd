using System.Text.Json;
using APIBack.Acontecimentos;
using Dapper;
using Npgsql;
using static APIBack.GestaoLogica.Vinculos.CuradoriaGlobalRegras;

namespace APIBack.GestaoLogica.Vinculos;

/// <summary>
/// Correção do catálogo global pela Curadoria (só super admin): produto global (nome, unidade, categoria, ilustração),
/// kits e categorias globais, e os critérios de promoção. Cada correção grava uma decisão imutável "corrigir" com caso
/// global:&lt;alvo&gt;:&lt;id&gt; em gestao_curadoria_decisoes; critérios são versões só INSERT. Nunca toca insumo nem
/// vínculo de loja (regra 6): o nome e a categoria que a loja importou continuam os dela.
/// </summary>
public interface ICuradoriaGlobalService
{
    Task<IReadOnlyList<ProdutoGlobalEditavel>> Produtos(string? busca, CancellationToken ct);
    Task<ProdutoCorrigido> CorrigirProduto(int usuario, string responsavel, Guid id, CorrigirProdutoGlobalRequest req, CancellationToken ct);
    Task<IReadOnlyList<KitGlobal>> Kits(CancellationToken ct);
    Task<KitSalvo> CriarKit(int usuario, string responsavel, SalvarKitRequest req, CancellationToken ct);
    Task<KitSalvo> CorrigirKit(int usuario, string responsavel, Guid id, SalvarKitRequest req, CancellationToken ct);
    Task<IReadOnlyList<CategoriaGlobal>> Categorias(CancellationToken ct);
    Task<CategoriaSalva> CriarCategoria(int usuario, string responsavel, SalvarCategoriaRequest req, CancellationToken ct);
    Task<CategoriaSalva> CorrigirCategoria(int usuario, string responsavel, Guid id, SalvarCategoriaRequest req, CancellationToken ct);
    Task<IReadOnlyList<RegistroDecisao>> Historico(string? casoId, CancellationToken ct);
    Task<CriteriosDaCuradoria> Criterios(CancellationToken ct);
    Task<CriteriosDaCuradoria> SalvarCriterios(int usuario, string responsavel, SalvarCriteriosRequest req, CancellationToken ct);
}

public sealed class CuradoriaGlobalService(NpgsqlDataSource dataSource, TimeProvider? relogio = null) : ICuradoriaGlobalService
{
    private readonly TimeProvider _relogio = relogio ?? TimeProvider.System;
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    // ------------------------------------------------------------ produto global

    private const string SelectProduto = @"
SELECT g.id AS Id, g.nome AS Nome, g.unidade AS Unidade, g.categoria AS Categoria, g.ilustracao AS Ilustracao, g.status AS Status,
       (SELECT count(DISTINCT v.estabelecimento_id)::int FROM gestao_vinculos v WHERE v.global_id = g.id AND v.estado = 'confirmado') AS Lojas,
       (SELECT count(*)::int FROM gestao_catalogo_kit_itens i JOIN gestao_catalogo_kits k ON k.id = i.kit_id AND k.ativo
         WHERE i.global_id = g.id) AS Kits
  FROM gestao_catalogo_global g";

    public async Task<IReadOnlyList<ProdutoGlobalEditavel>> Produtos(string? busca, CancellationToken ct)
    {
        await using var c = await dataSource.OpenConnectionAsync(ct);
        var palavras = Chave(busca).Split(' ', StringSplitOptions.RemoveEmptyEntries);
        var itens = await c.QueryAsync<ProdutoLinha>(SelectProduto + " WHERE g.status IN ('validado', 'candidato') ORDER BY g.nome LIMIT 3000");
        return itens.Where(i => palavras.All(p => Chave($"{i.Nome} {i.Categoria}").Contains(p, StringComparison.Ordinal)))
            .Take(200).Select(i => i.Produto()).ToList();
    }

    public async Task<ProdutoCorrigido> CorrigirProduto(int usuario, string responsavel, Guid id, CorrigirProdutoGlobalRequest req, CancellationToken ct)
    {
        var quem = Responsavel(responsavel);
        var motivo = Motivo(req.Motivo);
        await using var c = await dataSource.OpenConnectionAsync(ct);
        await using var tx = await c.BeginTransactionAsync(ct);
        await Travar(c, tx, IdProduto(id));
        var atual = await c.QuerySingleOrDefaultAsync<ProdutoLinha>(SelectProduto + " WHERE g.id = @id FOR UPDATE OF g", new { id }, tx)
            ?? throw new GestaoException("NAO_ENCONTRADO", "Produto global não encontrado.", 404);
        if (atual.Status is not ("validado" or "candidato"))
            throw new GestaoException("INATIVO", "Este item foi mesclado ou rejeitado: corrija o item que ficou.", 409);

        var nome = req.Nome is null ? atual.Nome : CuradoriaGlobalRegras.Nome(req.Nome, 120, "do produto");
        var unidade = req.Unidade is null ? atual.Unidade : Unidade(req.Unidade);
        var categoria = req.Categoria is null ? atual.Categoria : await CategoriaEscolhida(c, tx, req.Categoria, atual.Categoria);
        var ilustracao = req.Ilustracao is null ? atual.Ilustracao : ArquivoDeIlustracao(req.Ilustracao);
        var (antes, depois) = Diferenca(new[]
        {
            ("Nome", atual.Nome, nome), ("Unidade", atual.Unidade, unidade), ("Categoria", atual.Categoria, categoria),
            ("Ilustração", atual.Ilustracao, ilustracao),
        });
        if (unidade != atual.Unidade && await c.ExecuteScalarAsync<bool>(@"
SELECT EXISTS (SELECT 1 FROM gestao_vinculos WHERE global_id = @id)
    OR EXISTS (SELECT 1 FROM gestao_catalogo_loja WHERE global_id = @id)
    OR EXISTS (SELECT 1 FROM gestao_catalogo_global WHERE mesclado_em = @id)", new { id }, tx))
            throw new GestaoException("EM_USO",
                $"Lojas já usam este item em {atual.Unidade}. Crie outro item com a unidade certa e mescle pela fila da curadoria.", 409);
        try
        {
            await c.ExecuteAsync(@"
UPDATE gestao_catalogo_global SET nome = @nome, nome_chave = @chave, unidade = @unidade, categoria = @categoria, ilustracao = @ilustracao
 WHERE id = @id", new { id, nome, chave = Chave(nome), unidade, categoria, ilustracao }, tx);
        }
        catch (PostgresException ex) when (ex.SqlState == PostgresErrorCodes.UniqueViolation)
        {
            throw new GestaoException("DUPLICADO", "Já existe um item validado com esse nome e unidade.", 409);
        }
        var registro = await Registrar(c, tx, usuario, quem, IdProduto(id), nome, motivo, antes, depois, id, new
        {
            antes = new { atual.Nome, atual.Unidade, atual.Categoria, atual.Ilustracao },
            depois = new { nome, unidade, categoria, ilustracao },
        });
        var produto = (await c.QuerySingleAsync<ProdutoLinha>(SelectProduto + " WHERE g.id = @id", new { id }, tx)).Produto();
        await tx.CommitAsync(ct);
        return new ProdutoCorrigido(produto, registro);
    }

    /// <summary>A categoria do produto sai da lista global (ativa). Manter a que já tinha vale mesmo se ela foi desativada.</summary>
    private static async Task<string?> CategoriaEscolhida(NpgsqlConnection c, NpgsqlTransaction tx, string pedida, string? atual)
    {
        var chave = Chave(pedida);
        if (chave.Length == 0) return null;
        if (chave == Chave(atual)) return atual;
        await RegistrarCategoriasEmUso(c, tx);
        var cat = (await c.QueryAsync<(string Nome, bool Ativa)>(
            "SELECT nome, ativa FROM gestao_catalogo_categorias WHERE nome_chave = @chave", new { chave }, tx)).ToList();
        if (cat.Count == 0 || !cat[0].Ativa) throw Invalido("Escolha uma categoria ativa da lista (ou crie a categoria antes).");
        return cat[0].Nome;
    }

    // ------------------------------------------------------------ kits

    public async Task<IReadOnlyList<KitGlobal>> Kits(CancellationToken ct)
    {
        await using var c = await dataSource.OpenConnectionAsync(ct);
        return await LerKits(c, null, null);
    }

    public async Task<KitSalvo> CriarKit(int usuario, string responsavel, SalvarKitRequest req, CancellationToken ct)
    {
        var quem = Responsavel(responsavel);
        var motivo = Motivo(req.Motivo);
        var nome = CuradoriaGlobalRegras.Nome(req.Nome, 80, "do kit");
        var descricao = Descricao(req.Descricao);
        await using var c = await dataSource.OpenConnectionAsync(ct);
        await using var tx = await c.BeginTransactionAsync(ct);
        var itens = await ItensValidos(c, tx, req.Itens);
        if (itens.Count == 0) throw Invalido("Escolha os itens do kit.");
        var id = Guid.NewGuid();
        await c.ExecuteAsync("INSERT INTO gestao_catalogo_kits (id, nome, descricao, ativo, criado_por) VALUES (@id, @nome, @descricao, @ativo, @usuario)",
            new { id, nome, descricao, ativo = req.Ativo ?? true, usuario }, tx);
        await GravarItens(c, tx, id, itens);
        var kit = (await LerKits(c, tx, id)).Single();
        var registro = await Registrar(c, tx, usuario, quem, IdKit(id), nome, motivo, "Kit novo", Resumo(kit), null,
            new { antes = (object?)null, depois = new { nome, descricao, kit.Ativo, itens = itens.Select(i => i.GlobalId) } });
        await tx.CommitAsync(ct);
        return new KitSalvo(kit, registro);
    }

    public async Task<KitSalvo> CorrigirKit(int usuario, string responsavel, Guid id, SalvarKitRequest req, CancellationToken ct)
    {
        var quem = Responsavel(responsavel);
        var motivo = Motivo(req.Motivo);
        await using var c = await dataSource.OpenConnectionAsync(ct);
        await using var tx = await c.BeginTransactionAsync(ct);
        await Travar(c, tx, IdKit(id));
        var atual = (await LerKits(c, tx, id)).SingleOrDefault() ?? throw new GestaoException("NAO_ENCONTRADO", "Kit não encontrado.", 404);
        var nome = req.Nome is null ? atual.Nome : CuradoriaGlobalRegras.Nome(req.Nome, 80, "do kit");
        var descricao = req.Descricao is null ? atual.Descricao : Descricao(req.Descricao);
        var itens = req.Itens is null ? atual.Itens : await ItensValidos(c, tx, req.Itens);
        if (itens.Count == 0) throw Invalido("Um kit precisa de pelo menos um item. Para tirar de uso, desative.");
        var ativo = req.Ativo ?? atual.Ativo;
        static string Lista(IEnumerable<ItemDoKit> i) => string.Join(", ", i.Select(x => x.Nome));
        var (antes, depois) = Diferenca(new[]
        {
            ("Nome", atual.Nome, nome), ("Descrição", atual.Descricao, descricao), ("Itens", Lista(atual.Itens), Lista(itens)),
            ("Situação", atual.Ativo ? "ativo" : "desativado", ativo ? "ativo" : "desativado"),
        });
        await c.ExecuteAsync("UPDATE gestao_catalogo_kits SET nome = @nome, descricao = @descricao, ativo = @ativo WHERE id = @id",
            new { id, nome, descricao, ativo }, tx);
        if (req.Itens is not null)
        {
            // Os itens do kit são configuração: a lista anterior fica inteira no efeito da decisão.
            await c.ExecuteAsync("DELETE FROM gestao_catalogo_kit_itens WHERE kit_id = @id", new { id }, tx);
            await GravarItens(c, tx, id, itens);
        }
        var kit = (await LerKits(c, tx, id)).Single();
        var registro = await Registrar(c, tx, usuario, quem, IdKit(id), nome, motivo, antes, depois, null, new
        {
            antes = new { atual.Nome, atual.Descricao, atual.Ativo, itens = atual.Itens.Select(i => i.GlobalId) },
            depois = new { nome, descricao, ativo, itens = itens.Select(i => i.GlobalId) },
        });
        await tx.CommitAsync(ct);
        return new KitSalvo(kit, registro);
    }

    private static async Task<IReadOnlyList<ItemDoKit>> ItensValidos(NpgsqlConnection c, NpgsqlTransaction tx, IReadOnlyList<Guid>? pedidos)
    {
        var ids = (pedidos ?? Array.Empty<Guid>()).Distinct().ToArray();
        if (ids.Length > MaxItensDoKit) throw Invalido($"Um kit pode ter até {MaxItensDoKit} itens.");
        if (ids.Length == 0) return Array.Empty<ItemDoKit>();
        var achados = (await c.QueryAsync<ItemDoKit>(@"
SELECT id AS GlobalId, nome AS Nome, unidade AS Unidade, ilustracao AS Ilustracao FROM gestao_catalogo_global
 WHERE id = ANY(@ids) AND status = 'validado'", new { ids }, tx)).ToDictionary(i => i.GlobalId);
        if (achados.Count != ids.Length) throw Invalido("Só itens validados do catálogo global entram em kit.");
        return ids.Select(i => achados[i]).ToList();
    }

    private static async Task GravarItens(NpgsqlConnection c, NpgsqlTransaction tx, Guid kit, IReadOnlyList<ItemDoKit> itens)
    {
        for (var i = 0; i < itens.Count; i++)
            await c.ExecuteAsync("INSERT INTO gestao_catalogo_kit_itens (kit_id, global_id, ordem) VALUES (@kit, @global, @ordem)",
                new { kit, global = itens[i].GlobalId, ordem = i }, tx);
    }

    private static async Task<IReadOnlyList<KitGlobal>> LerKits(NpgsqlConnection c, NpgsqlTransaction? tx, Guid? id)
    {
        var kits = (await c.QueryAsync<(Guid Id, string Nome, string? Descricao, bool Ativo)>(
            $"SELECT id, nome, descricao, ativo FROM gestao_catalogo_kits {(id is null ? "ORDER BY nome" : "WHERE id = @id FOR UPDATE")}",
            new { id }, tx)).ToList();
        var ids = kits.Select(k => k.Id).ToArray();
        var itens = (await c.QueryAsync<(Guid Kit, Guid GlobalId, string Nome, string Unidade, string? Ilustracao)>(@"
SELECT i.kit_id, g.id, g.nome, g.unidade, g.ilustracao FROM gestao_catalogo_kit_itens i
  JOIN gestao_catalogo_global g ON g.id = i.global_id
 WHERE i.kit_id = ANY(@ids) ORDER BY i.ordem, g.nome", new { ids }, tx)).ToLookup(i => i.Kit);
        return kits.Select(k => new KitGlobal(k.Id, k.Nome, k.Descricao, k.Ativo,
            itens[k.Id].Select(i => new ItemDoKit(i.GlobalId, i.Nome, i.Unidade, i.Ilustracao)).ToList())).ToList();
    }

    private static string Resumo(KitGlobal k) =>
        Cortar($"Nome: {k.Nome} · Itens: {string.Join(", ", k.Itens.Select(i => i.Nome))}{(k.Ativo ? "" : " · desativado")}", 300);

    private static string? Descricao(string? d)
    {
        var t = (d ?? "").Trim();
        if (t.Length > 300) throw Invalido("A descrição pode ter até 300 letras.");
        return t.Length == 0 ? null : t;
    }

    // ------------------------------------------------------------ categorias

    public async Task<IReadOnlyList<CategoriaGlobal>> Categorias(CancellationToken ct)
    {
        await using var c = await dataSource.OpenConnectionAsync(ct);
        await using var tx = await c.BeginTransactionAsync(ct);
        await RegistrarCategoriasEmUso(c, tx);
        var lista = await LerCategorias(c, tx, null);
        await tx.CommitAsync(ct);
        return lista;
    }

    public async Task<CategoriaSalva> CriarCategoria(int usuario, string responsavel, SalvarCategoriaRequest req, CancellationToken ct)
    {
        var quem = Responsavel(responsavel);
        var motivo = Motivo(req.Motivo);
        var nome = CuradoriaGlobalRegras.Nome(req.Nome, 60, "da categoria");
        await using var c = await dataSource.OpenConnectionAsync(ct);
        await using var tx = await c.BeginTransactionAsync(ct);
        await RegistrarCategoriasEmUso(c, tx);
        var id = Guid.NewGuid();
        try
        {
            await c.ExecuteAsync(@"INSERT INTO gestao_catalogo_categorias (id, nome, nome_chave, ativa, criado_por)
VALUES (@id, @nome, @chave, true, @usuario)", new { id, nome, chave = Chave(nome), usuario }, tx);
        }
        catch (PostgresException ex) when (ex.SqlState == PostgresErrorCodes.UniqueViolation)
        {
            throw new GestaoException("DUPLICADO", "Essa categoria já existe.", 409);
        }
        var registro = await Registrar(c, tx, usuario, quem, IdCategoria(id), nome, motivo, "Categoria nova", $"Nome: {nome}", null,
            new { antes = (object?)null, depois = new { nome, ativa = true } });
        var categoria = (await LerCategorias(c, tx, id)).Single();
        await tx.CommitAsync(ct);
        return new CategoriaSalva(categoria, registro, 0);
    }

    /// <summary>Renomear troca a categoria nos itens do global (todos os status). A categoria do insumo da loja não muda.</summary>
    public async Task<CategoriaSalva> CorrigirCategoria(int usuario, string responsavel, Guid id, SalvarCategoriaRequest req, CancellationToken ct)
    {
        var quem = Responsavel(responsavel);
        var motivo = Motivo(req.Motivo);
        await using var c = await dataSource.OpenConnectionAsync(ct);
        await using var tx = await c.BeginTransactionAsync(ct);
        await Travar(c, tx, IdCategoria(id));
        await RegistrarCategoriasEmUso(c, tx);
        var linhas = (await c.QueryAsync<(string Nome, string NomeChave, bool Ativa)>(
            "SELECT nome, nome_chave, ativa FROM gestao_catalogo_categorias WHERE id = @id FOR UPDATE", new { id }, tx)).ToList();
        if (linhas.Count == 0) throw new GestaoException("NAO_ENCONTRADO", "Categoria não encontrada.", 404);
        var atual = linhas[0];
        var nome = req.Nome is null ? atual.Nome : CuradoriaGlobalRegras.Nome(req.Nome, 60, "da categoria");
        var ativa = req.Ativa ?? atual.Ativa;
        var (antes, depois) = Diferenca(new[]
        {
            ("Nome", atual.Nome, nome), ("Situação", atual.Ativa ? "ativa" : "desativada", ativa ? "ativa" : "desativada"),
        });
        try
        {
            await c.ExecuteAsync("UPDATE gestao_catalogo_categorias SET nome = @nome, nome_chave = @chave, ativa = @ativa WHERE id = @id",
                new { id, nome, chave = Chave(nome), ativa }, tx);
        }
        catch (PostgresException ex) when (ex.SqlState == PostgresErrorCodes.UniqueViolation)
        {
            throw new GestaoException("DUPLICADO", "Já existe outra categoria com esse nome.", 409);
        }
        var atualizados = 0;
        if (nome != atual.Nome)
        {
            var grafias = (await c.QueryAsync<string>("SELECT DISTINCT categoria FROM gestao_catalogo_global WHERE categoria IS NOT NULL",
                transaction: tx)).Where(g => Chave(g) == atual.NomeChave).ToArray();
            if (grafias.Length > 0)
                atualizados = await c.ExecuteAsync("UPDATE gestao_catalogo_global SET categoria = @nome WHERE categoria = ANY(@grafias)",
                    new { nome, grafias }, tx);
        }
        var registro = await Registrar(c, tx, usuario, quem, IdCategoria(id), nome, motivo, antes,
            atualizados > 0 ? Cortar($"{depois} · {atualizados} itens do global", 300) : depois, null, new
            {
                antes = new { atual.Nome, atual.Ativa },
                depois = new { nome, ativa, itensDoGlobal = atualizados },
            });
        var categoria = (await LerCategorias(c, tx, id)).Single();
        await tx.CommitAsync(ct);
        return new CategoriaSalva(categoria, registro, atualizados);
    }

    /// <summary>Categorias em uso no global que ainda não estão na lista entram nela (antes eram só texto livre).</summary>
    private static async Task RegistrarCategoriasEmUso(NpgsqlConnection c, NpgsqlTransaction tx)
    {
        var emUso = await c.QueryAsync<string>(@"
SELECT DISTINCT trim(categoria) FROM gestao_catalogo_global WHERE categoria IS NOT NULL AND length(trim(categoria)) > 0", transaction: tx);
        var existentes = (await c.QueryAsync<string>("SELECT nome_chave FROM gestao_catalogo_categorias", transaction: tx)).ToHashSet();
        foreach (var nome in emUso.GroupBy(Chave).Where(g => g.Key.Length > 0 && !existentes.Contains(g.Key)).Select(g => g.First()))
            await c.ExecuteAsync(@"INSERT INTO gestao_catalogo_categorias (id, nome, nome_chave, ativa) VALUES (@id, @nome, @chave, true)
ON CONFLICT (nome_chave) DO NOTHING", new { id = Guid.NewGuid(), nome = Cortar(nome, 60), chave = Chave(nome) }, tx);
    }

    private static async Task<IReadOnlyList<CategoriaGlobal>> LerCategorias(NpgsqlConnection c, NpgsqlTransaction tx, Guid? id)
    {
        var usos = (await c.QueryAsync<(string Categoria, int N)>(@"
SELECT categoria, count(*)::int FROM gestao_catalogo_global WHERE categoria IS NOT NULL AND status IN ('validado', 'candidato')
 GROUP BY categoria", transaction: tx)).GroupBy(u => Chave(u.Categoria)).ToDictionary(g => g.Key, g => g.Sum(u => u.N));
        var cats = await c.QueryAsync<(Guid Id, string Nome, string NomeChave, bool Ativa)>(
            $"SELECT id, nome, nome_chave, ativa FROM gestao_catalogo_categorias {(id is null ? "" : "WHERE id = @id")} ORDER BY nome",
            new { id }, tx);
        return cats.Select(k => new CategoriaGlobal(k.Id, k.Nome, k.Ativa, usos.GetValueOrDefault(k.NomeChave))).ToList();
    }

    // ------------------------------------------------------------ histórico e critérios

    public async Task<IReadOnlyList<RegistroDecisao>> Historico(string? casoId, CancellationToken ct)
    {
        await using var c = await dataSource.OpenConnectionAsync(ct);
        var caso = string.IsNullOrWhiteSpace(casoId) ? null : casoId.Trim();
        return (await c.QueryAsync<RegistroLinha>($@"
SELECT id AS Id, caso_id AS CasoId, caso_nome AS CasoNome, caso_tipo AS CasoTipo, decisao AS Decisao, responsavel_nome AS Responsavel,
       motivo AS Motivo, em AS Em, anterior AS Anterior, novo AS Novo, produto_global_id AS ProdutoGlobalId
  FROM gestao_curadoria_decisoes WHERE caso_tipo = 'global' {(caso is null ? "" : "AND caso_id = @caso")}
 ORDER BY em DESC, id DESC LIMIT 500", new { caso })).Select(l => l.Registro()).ToList();
    }

    public async Task<CriteriosDaCuradoria> Criterios(CancellationToken ct)
    {
        await using var c = await dataSource.OpenConnectionAsync(ct);
        return await LerCriterios(c, null);
    }

    public async Task<CriteriosDaCuradoria> SalvarCriterios(int usuario, string responsavel, SalvarCriteriosRequest req, CancellationToken ct)
    {
        var quem = Responsavel(responsavel);
        var motivo = Motivo(req.Motivo);
        var novos = CuradoriaGlobalRegras.Criterios(req);
        await using var c = await dataSource.OpenConnectionAsync(ct);
        await using var tx = await c.BeginTransactionAsync(ct);
        await Travar(c, tx, "criterios");
        if (await Vigentes(c, tx) == novos) throw Invalido("Nada mudou.", "SEM_MUDANCA");
        await c.ExecuteAsync(@"
INSERT INTO gestao_curadoria_criterios (id, lojas, confirmacoes, divergencia_max, responsavel_id, responsavel_nome, motivo, em)
VALUES (@id, @Lojas, @Confirmacoes, @DivergenciaMax, @usuario, @quem, @motivo, @em)",
            new { id = Guid.NewGuid(), novos.Lojas, novos.Confirmacoes, novos.DivergenciaMax, usuario, quem, motivo, em = _relogio.GetUtcNow().UtcDateTime }, tx);
        var resultado = await LerCriterios(c, tx);
        await tx.CommitAsync(ct);
        return resultado;
    }

    /// <summary>Critérios em vigor (última versão). Sem versão gravada, ou banco sem a migração 20261117_01, vale o padrão.</summary>
    public static async Task<CriteriosCuradoria> Vigentes(NpgsqlConnection c, NpgsqlTransaction? tx)
    {
        if (!await c.ExecuteScalarAsync<bool>("SELECT to_regclass('gestao_curadoria_criterios') IS NOT NULL", transaction: tx))
            return CriteriosCuradoria.Padrao;
        var v = (await c.QueryAsync<(int Lojas, int Confirmacoes, decimal Divergencia)>(
            "SELECT lojas, confirmacoes, divergencia_max FROM gestao_curadoria_criterios ORDER BY em DESC, id DESC LIMIT 1", transaction: tx)).ToList();
        return v.Count == 1 ? new CriteriosCuradoria(v[0].Lojas, v[0].Confirmacoes, v[0].Divergencia) : CriteriosCuradoria.Padrao;
    }

    private static async Task<CriteriosDaCuradoria> LerCriterios(NpgsqlConnection c, NpgsqlTransaction? tx)
    {
        var historico = (await c.QueryAsync<CriterioLinha>(@"
SELECT id AS Id, lojas AS Lojas, confirmacoes AS Confirmacoes, divergencia_max AS DivergenciaMax, responsavel_nome AS Responsavel,
       motivo AS Motivo, em AS Em FROM gestao_curadoria_criterios ORDER BY em DESC, id DESC LIMIT 100", transaction: tx))
            .Select(l => l.Versao()).ToList();
        var p = CriteriosCuradoria.Padrao;
        return new CriteriosDaCuradoria(historico.FirstOrDefault() ?? new VersaoDosCriterios(null, p.Lojas, p.Confirmacoes, p.DivergenciaMax, null, null, null),
            historico);
    }

    // ------------------------------------------------------------ registro imutável

    private async Task<RegistroDecisao> Registrar(NpgsqlConnection c, NpgsqlTransaction tx, int usuario, string responsavel, string casoId,
        string casoNome, string motivo, string antes, string depois, Guid? produtoGlobalId, object efeito)
    {
        var r = new RegistroDecisao(Guid.NewGuid(), casoId, Cortar(casoNome, 300), TipoGlobal, DecisaoCuradoria.Corrigir, responsavel, motivo,
            _relogio.GetUtcNow(), Cortar(antes, 300), Cortar(depois, 300), null, null, null, null, produtoGlobalId);
        await c.ExecuteAsync(@"
INSERT INTO gestao_curadoria_decisoes (id, caso_id, caso_nome, caso_tipo, decisao, responsavel_id, responsavel_nome, motivo, em,
       anterior, novo, produto_global_id, efeito)
VALUES (@Id, @CasoId, @CasoNome, @CasoTipo, @Decisao, @usuario, @Responsavel, @Motivo, @Em, @Anterior, @Novo, @ProdutoGlobalId, @efeito::jsonb)",
            new
            {
                r.Id, r.CasoId, r.CasoNome, r.CasoTipo, r.Decisao, usuario, r.Responsavel, r.Motivo, Em = r.Em.UtcDateTime, r.Anterior, r.Novo,
                r.ProdutoGlobalId, efeito = JsonSerializer.Serialize(efeito, Json),
            }, tx);
        return r;
    }

    private static Task Travar(NpgsqlConnection c, NpgsqlTransaction tx, string caso) =>
        c.ExecuteAsync("SELECT pg_advisory_xact_lock(hashtext('curadoria:' || @caso))", new { caso }, tx);

    private static string Responsavel(string doLogin)
    {
        if (string.IsNullOrWhiteSpace(doLogin))
            throw new GestaoException("RESPONSAVEL_OBRIGATORIO", "Toda correção precisa de um responsável.", 422);
        return Cortar(doLogin.Trim(), 120);
    }

    private static DateTimeOffset Utc(DateTime d) => new(DateTime.SpecifyKind(d, DateTimeKind.Utc));

    // ------------------------------------------------------------ linhas do banco

    private sealed class ProdutoLinha
    {
        public Guid Id { get; set; }
        public string Nome { get; set; } = "";
        public string Unidade { get; set; } = "";
        public string? Categoria { get; set; }
        public string? Ilustracao { get; set; }
        public string Status { get; set; } = "";
        public int Lojas { get; set; }
        public int Kits { get; set; }
        public ProdutoGlobalEditavel Produto() => new(Id, Nome, Unidade, Categoria, Ilustracao, Status, Lojas, Kits);
    }

    private sealed class RegistroLinha
    {
        public Guid Id { get; set; }
        public string CasoId { get; set; } = "";
        public string CasoNome { get; set; } = "";
        public string CasoTipo { get; set; } = "";
        public string Decisao { get; set; } = "";
        public string Responsavel { get; set; } = "";
        public string Motivo { get; set; } = "";
        public DateTime Em { get; set; }
        public string Anterior { get; set; } = "";
        public string Novo { get; set; } = "";
        public Guid? ProdutoGlobalId { get; set; }
        public RegistroDecisao Registro() => new(Id, CasoId, CasoNome, CasoTipo, Decisao, Responsavel, Motivo, Utc(Em), Anterior, Novo,
            null, null, null, null, ProdutoGlobalId);
    }

    private sealed class CriterioLinha
    {
        public Guid Id { get; set; }
        public int Lojas { get; set; }
        public int Confirmacoes { get; set; }
        public decimal DivergenciaMax { get; set; }
        public string Responsavel { get; set; } = "";
        public string Motivo { get; set; } = "";
        public DateTime Em { get; set; }
        public VersaoDosCriterios Versao() => new(Id, Lojas, Confirmacoes, DivergenciaMax, Responsavel, Motivo, Utc(Em));
    }
}
