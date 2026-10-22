using APIBack.DTOs.Cardapio;
using APIBack.Integrations.Ifood.Orders;
using APIBack.Service;
using Dapper;
using Npgsql;

namespace APIBack.Repository;

/// <summary>
/// Painel do vínculo item iFood → produto do cardápio. A loja vem sempre do token do chamador; produto e vínculo de outra loja
/// respondem 404, e só loja com conexão iFood autorizada enxerga o recurso.
/// </summary>
public sealed class IfoodProdutoVinculoRepository(NpgsqlDataSource source)
{
    private const string Columns = """
v.id AS VinculoId,v.nome_ifood AS NomeIfood,v.external_code AS ExternalCode,v.imagem_url AS ImagemUrl,v.origem AS Origem,
v.confirmado_em AS ConfirmadoEm,v.confirmado_por AS ConfirmadoPor
""";

    private static DeliveryDomainException NotConnected() =>
        new(404, "IFOOD_NAO_CONECTADO", "Esta loja não tem conexão iFood autorizada.");
    private static DeliveryDomainException ProductNotFound() =>
        new(404, "PRODUTO_NAO_ENCONTRADO", "Produto não encontrado neste estabelecimento.");
    private static DeliveryDomainException LinkNotFound() =>
        new(404, "IFOOD_VINCULO_NAO_ENCONTRADO", "Item iFood não encontrado neste estabelecimento.");

    private static Task<bool> ConnectedAsync(NpgsqlConnection connection, NpgsqlTransaction? transaction, Guid store, CancellationToken ct) =>
        connection.ExecuteScalarAsync<bool>(new CommandDefinition("""
SELECT EXISTS(SELECT 1 FROM ifood_connections WHERE estabelecimento_id=@Store AND status='authorized'
 AND authorized_at_utc IS NOT NULL AND revoked_at_utc IS NULL)
""", new { Store = store }, transaction, cancellationToken: ct));

    /// <summary>Tabela ainda não migrada = sem integração para este fim: responde como loja não conectada.</summary>
    private static async Task<bool> AvailableAsync(NpgsqlConnection connection, NpgsqlTransaction? transaction, Guid store, CancellationToken ct) =>
        await connection.ExecuteScalarAsync<bool>(new CommandDefinition(
            "SELECT to_regclass('ifood_produto_vinculo') IS NOT NULL AND to_regclass('ifood_connections') IS NOT NULL", transaction: transaction, cancellationToken: ct))
        && await ConnectedAsync(connection, transaction, store, ct);

    public async Task<IfoodVinculoResumoDto> ResumoAsync(Guid store, CancellationToken ct)
    {
        await using var connection = await source.OpenConnectionAsync(ct);
        if (!await AvailableAsync(connection, null, store, ct)) return new IfoodVinculoResumoDto { Conectado = false, Pendentes = 0 };
        var pending = await connection.ExecuteScalarAsync<int>(new CommandDefinition("""
SELECT COUNT(*)::int FROM ifood_produto_vinculo WHERE estabelecimento_id=@Store AND tipo='item' AND estado IN('pendente','desfeito')
""", new { Store = store }, cancellationToken: ct));
        return new IfoodVinculoResumoDto { Conectado = true, Pendentes = pending };
    }

    public async Task<IfoodVinculoPendentesDto> PendentesAsync(Guid store, string? busca, int pagina, int tamanho, CancellationToken ct)
    {
        pagina = Math.Max(1, pagina);
        tamanho = Math.Clamp(tamanho, 1, 100);
        await using var connection = await source.OpenConnectionAsync(ct);
        if (!await AvailableAsync(connection, null, store, ct)) throw NotConnected();
        var term = string.IsNullOrWhiteSpace(busca) ? null : "%" + busca.Trim().Replace("\\", "\\\\").Replace("%", "\\%").Replace("_", "\\_") + "%";
        var args = new { Store = store, Term = term, Skip = (pagina - 1) * tamanho, Take = tamanho };
        const string filter = """
v.estabelecimento_id=@Store AND v.tipo='item' AND v.estado IN('pendente','desfeito')
AND (@Term::text IS NULL OR v.nome_ifood ILIKE @Term ESCAPE '\' OR v.external_code ILIKE @Term ESCAPE '\')
""";
        var total = await connection.ExecuteScalarAsync<int>(new CommandDefinition($"SELECT COUNT(*)::int FROM ifood_produto_vinculo v WHERE {filter}", args, cancellationToken: ct));
        var items = await connection.QueryAsync<IfoodVinculoPendenteDto>(new CommandDefinition($"""
SELECT v.id AS VinculoId,v.nome_ifood AS Nome,v.external_code AS ExternalCode,v.imagem_url AS ImagemUrl,v.pedidos_count AS Pedidos,
v.visto_ultima_em AS UltimoVistoEm,(v.estado='desfeito') AS Desfeito
FROM ifood_produto_vinculo v WHERE {filter}
ORDER BY v.visto_ultima_em DESC,v.id OFFSET @Skip LIMIT @Take
""", args, cancellationToken: ct));
        return new IfoodVinculoPendentesDto { Itens = items.ToList(), Total = total };
    }

    public async Task<IfoodProdutoVinculosDto> DoProdutoAsync(Guid store, Guid produtoId, CancellationToken ct)
    {
        await using var connection = await source.OpenConnectionAsync(ct);
        if (!await AvailableAsync(connection, null, store, ct)) throw NotConnected();
        if (!await connection.ExecuteScalarAsync<bool>(new CommandDefinition(
                "SELECT EXISTS(SELECT 1 FROM cardapio_produto WHERE id=@Produto AND id_estabelecimento=@Store AND deleted_at IS NULL)",
                new { Produto = produtoId, Store = store }, cancellationToken: ct))) throw ProductNotFound();
        var rows = await connection.QueryAsync<IfoodProdutoVinculoDto>(new CommandDefinition($"""
SELECT {Columns} FROM ifood_produto_vinculo v
WHERE v.estabelecimento_id=@Store AND v.tipo='item' AND v.estado='confirmado' AND v.produto_id=@Produto
ORDER BY v.confirmado_em,v.id
""", new { Store = store, Produto = produtoId }, cancellationToken: ct));
        return new IfoodProdutoVinculosDto { Vinculos = rows.ToList() };
    }

    private sealed class LinkRow { public Guid Id { get; set; } public string Estado { get; set; } = ""; public Guid? ProdutoId { get; set; } }

    public async Task<IfoodProdutoVinculoDto> VincularAsync(Guid store, int? user, Guid produtoId, Guid vinculoId, CancellationToken ct)
    {
        await using var connection = await source.OpenConnectionAsync(ct);
        await using var transaction = await connection.BeginTransactionAsync(ct);
        if (!await AvailableAsync(connection, transaction, store, ct)) throw NotConnected();
        if (!await connection.ExecuteScalarAsync<bool>(new CommandDefinition(
                "SELECT EXISTS(SELECT 1 FROM cardapio_produto WHERE id=@Produto AND id_estabelecimento=@Store AND deleted_at IS NULL)",
                new { Produto = produtoId, Store = store }, transaction, cancellationToken: ct))) throw ProductNotFound();
        var link = await connection.QuerySingleOrDefaultAsync<LinkRow>(new CommandDefinition("""
SELECT id AS Id,estado AS Estado,produto_id AS ProdutoId FROM ifood_produto_vinculo
WHERE id=@Id AND estabelecimento_id=@Store AND tipo='item' FOR UPDATE
""", new { Id = vinculoId, Store = store }, transaction, cancellationToken: ct)) ?? throw LinkNotFound();
        if (link.Estado == "confirmado" && link.ProdutoId.HasValue && link.ProdutoId != produtoId)
        {
            // Produto excluído não segura o vínculo: o item volta a ser livre para outro produto.
            var other = await connection.QuerySingleOrDefaultAsync<string?>(new CommandDefinition(
                "SELECT nome FROM cardapio_produto WHERE id=@Id AND id_estabelecimento=@Store AND deleted_at IS NULL",
                new { Id = link.ProdutoId, Store = store }, transaction, cancellationToken: ct));
            if (other != null)
                throw new DeliveryDomainException(409, "IFOOD_VINCULO_JA_USADO", "Este item do iFood já está ligado a outro produto.",
                    new { produtoId = link.ProdutoId, produtoNome = other });
        }
        if (!(link.Estado == "confirmado" && link.ProdutoId == produtoId))
            await connection.ExecuteAsync(new CommandDefinition("""
UPDATE ifood_produto_vinculo SET estado='confirmado',origem='manual',produto_id=@Produto,confirmado_por=@User,confirmado_em=NOW()
WHERE id=@Id AND estabelecimento_id=@Store
""", new { Id = vinculoId, Store = store, Produto = produtoId, User = user }, transaction, cancellationToken: ct));
        var result = await connection.QuerySingleAsync<IfoodProdutoVinculoDto>(new CommandDefinition($"SELECT {Columns} FROM ifood_produto_vinculo v WHERE v.id=@Id",
            new { Id = vinculoId }, transaction, cancellationToken: ct));
        await transaction.CommitAsync(ct);
        return result;
    }

    /// <summary>
    /// Antes de criar o produto a partir do item: o item precisa estar livre (pendente, desfeito ou ligado a produto excluído)
    /// e não pode haver produto da loja com o mesmo nome (aí o certo é ligar a ele). Devolve a foto do item no iFood.
    /// </summary>
    public async Task<string?> PrepararCriacaoAsync(Guid store, Guid vinculoId, string nome, CancellationToken ct)
    {
        await using var connection = await source.OpenConnectionAsync(ct);
        if (!await AvailableAsync(connection, null, store, ct)) throw NotConnected();
        var link = await connection.QuerySingleOrDefaultAsync<LinkRow>(new CommandDefinition("""
SELECT id AS Id,estado AS Estado,produto_id AS ProdutoId FROM ifood_produto_vinculo
WHERE id=@Id AND estabelecimento_id=@Store AND tipo='item'
""", new { Id = vinculoId, Store = store }, cancellationToken: ct)) ?? throw LinkNotFound();
        if (link.Estado == "confirmado" && link.ProdutoId.HasValue)
        {
            var owner = await connection.QuerySingleOrDefaultAsync<string?>(new CommandDefinition(
                "SELECT nome FROM cardapio_produto WHERE id=@Id AND id_estabelecimento=@Store AND deleted_at IS NULL",
                new { Id = link.ProdutoId, Store = store }, cancellationToken: ct));
            if (owner != null)
                throw new DeliveryDomainException(409, "IFOOD_VINCULO_JA_USADO", "Este item do iFood já está ligado a outro produto.",
                    new { produtoId = link.ProdutoId, produtoNome = owner });
        }
        var same = await connection.QueryFirstOrDefaultAsync<SameNameRow>(new CommandDefinition($"""
SELECT id AS Id,nome AS Nome FROM cardapio_produto
WHERE id_estabelecimento=@Store AND deleted_at IS NULL AND {IfoodItemLinkRules.NameSql("nome")}=@Nome
ORDER BY ativo DESC,id LIMIT 1
""", new { Store = store, Nome = IfoodItemLinkRules.NormalizeName(nome) }, cancellationToken: ct));
        if (same != null)
            throw new DeliveryDomainException(409, "PRODUTO_NOME_JA_EXISTE", "Já existe um produto com esse nome.",
                new { produtoId = same.Id, produtoNome = same.Nome });
        return await connection.ExecuteScalarAsync<string?>(new CommandDefinition(
            "SELECT imagem_url FROM ifood_produto_vinculo WHERE id=@Id", new { Id = vinculoId }, cancellationToken: ct));
    }

    private sealed class SameNameRow { public Guid Id { get; set; } public string Nome { get; set; } = ""; }

    public async Task<IfoodVinculoDesfeitoDto> DesfazerAsync(Guid store, int? user, Guid vinculoId, CancellationToken ct)
    {
        await using var connection = await source.OpenConnectionAsync(ct);
        await using var transaction = await connection.BeginTransactionAsync(ct);
        if (!await AvailableAsync(connection, transaction, store, ct)) throw NotConnected();
        var link = await connection.QuerySingleOrDefaultAsync<LinkRow>(new CommandDefinition("""
SELECT id AS Id,estado AS Estado,produto_id AS ProdutoId FROM ifood_produto_vinculo
WHERE id=@Id AND estabelecimento_id=@Store AND tipo='item' FOR UPDATE
""", new { Id = vinculoId, Store = store }, transaction, cancellationToken: ct)) ?? throw LinkNotFound();
        if (link.Estado == "pendente")
            throw new DeliveryDomainException(409, "IFOOD_VINCULO_NAO_CONFIRMADO", "Este item do iFood ainda não está ligado a nenhum produto.");
        if (link.Estado == "confirmado")
            await connection.ExecuteAsync(new CommandDefinition("""
UPDATE ifood_produto_vinculo SET estado='desfeito',produto_id=NULL,origem=NULL,desfeito_em=NOW(),desfeito_por=@User
WHERE id=@Id AND estabelecimento_id=@Store
""", new { Id = vinculoId, Store = store, User = user }, transaction, cancellationToken: ct));
        await transaction.CommitAsync(ct);
        return new IfoodVinculoDesfeitoDto { VinculoId = vinculoId, Estado = "desfeito" };
    }
}
