using APIBack.DTOs.Clientes;
using APIBack.Repository.Interface;
using APIBack.Service;
using Dapper;
using Npgsql;

namespace APIBack.Repository;

public sealed class ClienteEnderecoRepository(NpgsqlDataSource source) : IClienteEnderecoRepository
{
    private const string Columns = "id AS Id, apelido AS Apelido, cep AS Cep, logradouro AS Logradouro, numero AS Numero, complemento AS Complemento, bairro AS Bairro, cidade AS Cidade, uf AS Uf, referencia AS Referencia, latitude AS Latitude, longitude AS Longitude, principal AS Principal";
    private static DeliveryDomainException Missing() => new(404, "ADDRESS_NOT_FOUND", "Cliente ou endereço não encontrado.");

    private static async Task LockAsync(NpgsqlConnection cx, NpgsqlTransaction tx, Guid est, Guid client)
    {
        if (await cx.QuerySingleOrDefaultAsync<Guid?>("SELECT id FROM clientes WHERE id=@Client AND id_estabelecimento=@Est AND ativo FOR UPDATE", new { Client = client, Est = est }, tx) == null)
            throw Missing();
    }

    public async Task<IReadOnlyList<ClienteEnderecoDto>> ListAsync(Guid estabelecimentoId, Guid clienteId)
    {
        await using var cx = await source.OpenConnectionAsync();
        await using var tx = await cx.BeginTransactionAsync();
        await LockAsync(cx, tx, estabelecimentoId, clienteId);
        // Recupera cadastro e pedidos anteriores, inclusive rascunhos do atendente. Endereços excluídos
        // permanecem como tombstones para que a próxima consulta não os importe novamente.
        if (!await cx.ExecuteScalarAsync<bool>("SELECT endereco_historico_importado FROM clientes WHERE id=@Client", new { Client = clienteId }, tx))
        {
        var history = await cx.QueryAsync<ClienteEnderecoRequest>(@"
SELECT logradouro, numero, complemento, bairro, cidade, uf, cep, referencia, latitude, longitude FROM clientes WHERE id=@Client
UNION ALL
SELECT entrega_rua, entrega_numero, NULL, entrega_bairro, entrega_cidade, entrega_estado, entrega_cep, NULL,
CASE WHEN replace(latitude::text,',','.') ~ '^-?[0-9]+(\.[0-9]+)?$' THEN replace(latitude::text,',','.')::double precision END,
CASE WHEN replace(longitude::text,',','.') ~ '^-?[0-9]+(\.[0-9]+)?$' THEN replace(longitude::text,',','.')::double precision END
FROM pedido WHERE id_estabelecimento=@Est AND (cliente_id=@Client OR regexp_replace(telefone_cliente,'\D','','g') IN
    (SELECT regexp_replace(telefone_e164,'\D','','g') FROM clientes WHERE id=@Client))
UNION ALL
SELECT endereco_entrega_json->>'logradouro', endereco_entrega_json->>'numero', endereco_entrega_json->>'complemento', endereco_entrega_json->>'bairro',
    endereco_entrega_json->>'cidade', endereco_entrega_json->>'uf', endereco_entrega_json->>'cep', endereco_entrega_json->>'referencia',
    (endereco_entrega_json->>'latitude')::double precision, (endereco_entrega_json->>'longitude')::double precision
FROM cardapio_pedido_publico WHERE id_estabelecimento=@Est AND status IN ('aguardando_aceite','aceito','recusado')
AND regexp_replace(COALESCE(telefone_contato,telefone_cliente),'\D','','g') IN
    (SELECT regexp_replace(telefone_e164,'\D','','g') FROM clientes WHERE id=@Client)", new { Client = clienteId, Est = estabelecimentoId }, tx);
        foreach (var address in history)
        {
            if (string.IsNullOrWhiteSpace(address.Logradouro) || string.IsNullOrWhiteSpace(address.Numero) ||
                string.IsNullOrWhiteSpace(address.Bairro) || string.IsNullOrWhiteSpace(address.Cidade)) continue;
            await SaveLockedAsync(cx, tx, clienteId, null, address, import: true);
        }
        await cx.ExecuteAsync("UPDATE clientes SET endereco_historico_importado=TRUE WHERE id=@Client", new { Client = clienteId }, tx);
        }
        var items = (await cx.QueryAsync<ClienteEnderecoDto>($"SELECT {Columns} FROM cliente_enderecos WHERE id_cliente=@Client AND ativo ORDER BY principal DESC, updated_at_utc DESC, id", new { Client = clienteId }, tx)).ToList();
        await tx.CommitAsync();
        return items;
    }

    public async Task<ClienteEnderecoDto> SaveAsync(Guid estabelecimentoId, Guid clienteId, Guid? id, ClienteEnderecoRequest request)
    {
        ClienteEnderecoRules.Validate(request);
        await using var cx = await source.OpenConnectionAsync();
        await using var tx = await cx.BeginTransactionAsync();
        await LockAsync(cx, tx, estabelecimentoId, clienteId);
        var saved = await SaveLockedAsync(cx, tx, clienteId, id, request);
        await tx.CommitAsync();
        return saved!;
    }

    internal static async Task SyncCadastroAsync(NpgsqlConnection cx, NpgsqlTransaction tx, ClienteDto cliente)
    {
        // O formulário de cadastro antigo continua funcional: trocar seu endereço escolhe/salva
        // um novo principal e conserva os outros endereços do cliente.
        if (!cliente.Ativo || string.IsNullOrWhiteSpace(cliente.Logradouro) || string.IsNullOrWhiteSpace(cliente.Numero) ||
            string.IsNullOrWhiteSpace(cliente.Bairro) || string.IsNullOrWhiteSpace(cliente.Cidade) || string.IsNullOrWhiteSpace(cliente.Uf)) return;
        var input = ClienteEnderecoRules.Validate(new ClienteEnderecoRequest
        {
            Logradouro = cliente.Logradouro, Numero = cliente.Numero, Complemento = cliente.Complemento,
            Bairro = cliente.Bairro, Cidade = cliente.Cidade, Uf = cliente.Uf, Cep = cliente.Cep, Referencia = cliente.Referencia,
            Latitude = cliente.Latitude, Longitude = cliente.Longitude, Principal = true
        });
        await SaveLockedAsync(cx, tx, cliente.Id, null, input);
    }

    private static async Task<ClienteEnderecoDto?> SaveLockedAsync(NpgsqlConnection cx, NpgsqlTransaction tx, Guid client, Guid? id, ClienteEnderecoRequest address, bool import = false)
    {
        var args = new DynamicParameters(address);
        args.Add("Client", client);
        args.Add("PreserveMetadata", !id.HasValue);
        var duplicate = await cx.QuerySingleOrDefaultAsync<(Guid Id, bool Ativo)?>(@"
SELECT id, ativo FROM cliente_enderecos WHERE id_cliente=@Client
AND lower(btrim(logradouro))=lower(btrim(@Logradouro)) AND lower(btrim(numero))=lower(btrim(@Numero))
AND lower(btrim(COALESCE(complemento,'')))=lower(btrim(COALESCE(@Complemento,'')))
AND lower(btrim(bairro))=lower(btrim(@Bairro)) AND lower(btrim(cidade))=lower(btrim(@Cidade))
AND lower(btrim(COALESCE(uf,'')))=lower(btrim(COALESCE(@Uf,''))) ORDER BY ativo DESC, created_at_utc LIMIT 1", args, tx);
        if (import && duplicate != null) return null;
        if (id.HasValue && !await cx.ExecuteScalarAsync<bool>("SELECT EXISTS(SELECT 1 FROM cliente_enderecos WHERE id=@Id AND id_cliente=@Client AND ativo)", new { Id = id, Client = client }, tx)) throw Missing();
        if (id.HasValue && duplicate is { Ativo: true } && duplicate.Value.Id != id.Value)
            throw new DeliveryDomainException(409, "ADDRESS_DUPLICATE", "Este endereço já está salvo. Escolha-o na lista.");
        id ??= duplicate?.Id;
        var principal = address.Principal || !await cx.ExecuteScalarAsync<bool>("SELECT EXISTS(SELECT 1 FROM cliente_enderecos WHERE id_cliente=@Client AND ativo AND principal)", new { Client = client }, tx);
        if (principal)
            await cx.ExecuteAsync("UPDATE cliente_enderecos SET principal=FALSE WHERE id_cliente=@Client AND principal", new { Client = client }, tx);
        args.Add("Id", id ?? Guid.NewGuid());
        args.Add("Principal", principal);
        await cx.ExecuteAsync(@"
INSERT INTO cliente_enderecos(id,id_cliente,apelido,cep,logradouro,numero,complemento,bairro,cidade,uf,referencia,latitude,longitude,principal)
VALUES(@Id,@Client,@Apelido,@Cep,@Logradouro,@Numero,@Complemento,@Bairro,@Cidade,@Uf,@Referencia,@Latitude,@Longitude,@Principal)
ON CONFLICT(id) DO UPDATE SET apelido=CASE WHEN @PreserveMetadata THEN COALESCE(EXCLUDED.apelido,cliente_enderecos.apelido) ELSE EXCLUDED.apelido END, cep=EXCLUDED.cep,
logradouro=EXCLUDED.logradouro,numero=EXCLUDED.numero,complemento=EXCLUDED.complemento,bairro=EXCLUDED.bairro,cidade=EXCLUDED.cidade,uf=EXCLUDED.uf,
referencia=CASE WHEN @PreserveMetadata THEN COALESCE(EXCLUDED.referencia,cliente_enderecos.referencia) ELSE EXCLUDED.referencia END,latitude=EXCLUDED.latitude,longitude=EXCLUDED.longitude,
principal=cliente_enderecos.principal OR EXCLUDED.principal,ativo=TRUE,updated_at_utc=NOW()", args, tx);
        await SyncPrincipalAsync(cx, tx, client);
        return await cx.QuerySingleAsync<ClienteEnderecoDto>($"SELECT {Columns} FROM cliente_enderecos WHERE id=@Id", args, tx);
    }

    public async Task DeleteAsync(Guid estabelecimentoId, Guid clienteId, Guid id)
    {
        await using var cx = await source.OpenConnectionAsync();
        await using var tx = await cx.BeginTransactionAsync();
        await LockAsync(cx, tx, estabelecimentoId, clienteId);
        if (await cx.ExecuteAsync("UPDATE cliente_enderecos SET ativo=FALSE,principal=FALSE,updated_at_utc=NOW() WHERE id=@Id AND id_cliente=@Client AND ativo", new { Id = id, Client = clienteId }, tx) == 0) throw Missing();
        await cx.ExecuteAsync(@"UPDATE cliente_enderecos SET principal=TRUE WHERE id=(SELECT id FROM cliente_enderecos WHERE id_cliente=@Client AND ativo ORDER BY updated_at_utc DESC,id LIMIT 1)
AND NOT EXISTS(SELECT 1 FROM cliente_enderecos WHERE id_cliente=@Client AND ativo AND principal)", new { Client = clienteId }, tx);
        await SyncPrincipalAsync(cx, tx, clienteId);
        await tx.CommitAsync();
    }

    private static Task SyncPrincipalAsync(NpgsqlConnection cx, NpgsqlTransaction tx, Guid client) => cx.ExecuteAsync(@"
UPDATE clientes SET (logradouro,numero,complemento,bairro,cidade,uf,cep,referencia,latitude,longitude)=
(SELECT logradouro,numero,complemento,bairro,cidade,uf,cep,referencia,latitude,longitude FROM cliente_enderecos WHERE id_cliente=@Client AND ativo AND principal),
data_atualizacao=NOW() WHERE id=@Client", new { Client = client }, tx);

    public async Task CreateSessionAsync(Guid estabelecimentoId, Guid clienteId, string hash, DateTimeOffset expiraEm)
    {
        await using var cx = await source.OpenConnectionAsync();
        await cx.ExecuteAsync("DELETE FROM cliente_sessoes WHERE id_estabelecimento=@Est AND expira_em<=NOW()", new { Est = estabelecimentoId });
        await cx.ExecuteAsync("INSERT INTO cliente_sessoes(token_hash,id_estabelecimento,id_cliente,expira_em) VALUES(@Hash,@Est,@Client,@Expira)", new { Hash = hash, Est = estabelecimentoId, Client = clienteId, Expira = expiraEm });
    }

    public async Task<ClienteSessao?> GetSessionAsync(Guid estabelecimentoId, string hash)
    {
        await using var cx = await source.OpenConnectionAsync();
        return await cx.QuerySingleOrDefaultAsync<ClienteSessao>(@"SELECT c.id AS ClienteId,c.id_estabelecimento AS EstabelecimentoId,c.telefone_e164 AS Telefone,COALESCE(c.nome,'') AS Nome,s.expira_em AS ExpiraEm
FROM cliente_sessoes s JOIN clientes c ON c.id=s.id_cliente AND c.id_estabelecimento=s.id_estabelecimento
WHERE s.token_hash=@Hash AND s.id_estabelecimento=@Est AND s.expira_em>NOW() AND c.ativo", new { Hash = hash, Est = estabelecimentoId });
    }

    public async Task RevokeSessionAsync(Guid estabelecimentoId, string hash)
    {
        await using var cx = await source.OpenConnectionAsync();
        await cx.ExecuteAsync("DELETE FROM cliente_sessoes WHERE token_hash=@Hash AND id_estabelecimento=@Est", new { Hash = hash, Est = estabelecimentoId });
    }
}
