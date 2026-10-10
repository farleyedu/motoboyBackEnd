using APIBack.DTOs.Clientes;
using APIBack.Infrastructure;
using APIBack.Repository;
using APIBack.Service;
using Dapper;
using Npgsql;
using Xunit;

namespace APIBack.Tests.Integration;

public sealed class ClienteEnderecosDatabaseTests
{
    [DeliveryDatabaseFact]
    public async Task Cadastro_antigo_atualiza_principal_sem_apagar_enderecos()
    {
        await using var db = await Database.Create();
        var repo = new ClienteEnderecoRepository(db.Source);
        Assert.Equal(3, (await repo.ListAsync(db.Est, db.Client)).Count);
        await using var cx = await db.Source.OpenConnectionAsync();
        await using var tx = await cx.BeginTransactionAsync();
        await cx.ExecuteAsync("UPDATE clientes SET logradouro='Rua Alterada',numero='77' WHERE id=@Client", new { db.Client }, tx);
        await ClienteEnderecoRepository.SyncCadastroAsync(cx, tx, new ClienteDto
        { Id = db.Client, Ativo = true, Logradouro = "Rua Alterada", Numero = "77", Bairro = "Centro", Cidade = "Uberlândia", Uf = "MG" });
        await tx.CommitAsync();
        var items = await repo.ListAsync(db.Est, db.Client);
        Assert.Equal(4, items.Count); Assert.Equal("Rua Alterada", items.Single(a => a.Principal).Logradouro);
        Assert.Contains(items, a => a.Logradouro == "Rua Cadastro");
    }

    [DeliveryDatabaseFact]
    public async Task Historico_principal_edicao_exclusao_e_isolamento_com_banco_real()
    {
        await using var db = await Database.Create();
        var repo = new ClienteEnderecoRepository(db.Source);
        var initial = await repo.ListAsync(db.Est, db.Client);
        Assert.Equal(3, initial.Count); Assert.Single(initial.Where(a => a.Principal));
        var main = initial.Single(a => a.Principal);
        var second = initial.First(a => !a.Principal);
        second.Principal = true;
        await repo.SaveAsync(db.Est, db.Client, second.Id, second);
        Assert.Equal(second.Id, (await repo.ListAsync(db.Est, db.Client)).Single(a => a.Principal).Id);
        Assert.Equal(second.Logradouro, await db.Scalar<string>("SELECT logradouro FROM clientes"));
        await Task.WhenAll(Enumerable.Range(0, 5).Select(_ => repo.SaveAsync(db.Est, db.Client, null,
            new ClienteEnderecoRequest { Logradouro = "Rua Nova", Numero = "10", Bairro = "Centro", Cidade = "Uberlândia", Uf = "MG", Principal = true })));
        var items = await repo.ListAsync(db.Est, db.Client);
        Assert.Equal(4, items.Count); Assert.Single(items.Where(a => a.Principal));
        var edited = items.Single(a => a.Id == main.Id); edited.Numero = "99";
        await repo.SaveAsync(db.Est, db.Client, main.Id, edited);
        Assert.Equal(4, (await repo.ListAsync(db.Est, db.Client)).Count); // O endereço anterior não reaparece do histórico.
        await repo.DeleteAsync(db.Est, db.Client, main.Id);
        Assert.Equal(3, (await repo.ListAsync(db.Est, db.Client)).Count);
        Assert.Equal("ADDRESS_NOT_FOUND", (await Assert.ThrowsAsync<DeliveryDomainException>(() => repo.ListAsync(Guid.NewGuid(), db.Client))).Code);
        Assert.Equal("ADDRESS_NOT_FOUND", (await Assert.ThrowsAsync<DeliveryDomainException>(() => repo.SaveAsync(db.Est, db.Client, Guid.NewGuid(), second))).Code);
        foreach (var address in await repo.ListAsync(db.Est, db.Client)) await repo.DeleteAsync(db.Est, db.Client, address.Id);
        Assert.Empty(await repo.ListAsync(db.Est, db.Client));
        Assert.Null(await db.Scalar<string?>("SELECT logradouro FROM clientes"));
    }

    [DeliveryDatabaseFact]
    public async Task Sessao_persistente_expira_revoga_e_nao_atravessa_lojas()
    {
        await using var db = await Database.Create();
        var repo = new ClienteEnderecoRepository(db.Source);
        await repo.CreateSessionAsync(db.Est, db.Client, "hash-valido", DateTimeOffset.UtcNow.AddDays(30));
        Assert.NotNull(await repo.GetSessionAsync(db.Est, "hash-valido"));
        Assert.Null(await repo.GetSessionAsync(Guid.NewGuid(), "hash-valido"));
        await repo.CreateSessionAsync(db.Est, db.Client, "hash-expirado", DateTimeOffset.UtcNow.AddMinutes(-1));
        Assert.Null(await repo.GetSessionAsync(db.Est, "hash-expirado"));
        await repo.RevokeSessionAsync(db.Est, "hash-valido");
        Assert.Null(await repo.GetSessionAsync(db.Est, "hash-valido"));
    }

    private sealed class Database : IAsyncDisposable
    {
        public NpgsqlDataSource Source { get; private set; } = null!;
        public Guid Est { get; } = Guid.NewGuid();
        public Guid Client { get; } = Guid.NewGuid();
        private string _schema = "cliente_enderecos_" + Guid.NewGuid().ToString("N");
        private string _base = "";
        public static async Task<Database> Create()
        {
            var options = new NpgsqlConnectionStringBuilder(Environment.GetEnvironmentVariable("TEST_DELIVERY_DATABASE"));
            if (options.Host is not ("localhost" or "127.0.0.1")) throw new InvalidOperationException("Use somente PostgreSQL local.");
            var db = new Database { _base = options.ConnectionString };
            await using (var cx = new NpgsqlConnection(db._base)) { await cx.OpenAsync(); await cx.ExecuteAsync($"CREATE SCHEMA {db._schema}"); }
            options.SearchPath = db._schema;
            db.Source = NpgsqlDataSource.Create(options.ConnectionString);
            SqlMapper.AddTypeHandler(new DateTimeOffsetTypeHandler());
            await db.Execute(@"
CREATE TABLE delivery_tracking_schema_versions(version text PRIMARY KEY);
CREATE TABLE clientes(id uuid PRIMARY KEY,id_estabelecimento uuid,telefone_e164 text,nome text,ativo bool DEFAULT TRUE,
logradouro text,numero text,complemento text,bairro text,cidade text,uf text,cep text,referencia text,
latitude double precision,longitude double precision,data_atualizacao timestamptz);
CREATE TABLE pedido(id serial,cliente_id uuid,id_estabelecimento uuid,telefone_cliente text,
entrega_rua text,entrega_numero text,entrega_bairro text,entrega_cidade text,entrega_estado text,entrega_cep text,latitude text,longitude text);
CREATE TABLE cardapio_pedido_publico(id_estabelecimento uuid,status text,telefone_contato text,telefone_cliente text,endereco_entrega_json jsonb);
INSERT INTO clientes(id,id_estabelecimento,telefone_e164,nome,logradouro,numero,bairro,cidade,uf)
VALUES(@Client,@Est,'+5534991230001','Maria','Rua Cadastro','1','Centro','Uberlândia','MG');
INSERT INTO pedido(cliente_id,id_estabelecimento,entrega_rua,entrega_numero,entrega_bairro,entrega_cidade,entrega_estado,latitude,longitude)
VALUES(@Client,@Est,'Rua Rascunho','2','Centro','Uberlândia','MG','-18,91','-48.27');
INSERT INTO cardapio_pedido_publico VALUES(@Est,'aguardando_aceite',NULL,'+5534991230001','{""logradouro"":""Rua Web"",""numero"":""3"",""bairro"":""Centro"",""cidade"":""Uberlândia"",""uf"":""MG""}');", new { db.Client, db.Est });
            foreach (var name in new[] { "20261005_01_cliente_enderecos.sql", "20261010_01_cliente_sessoes.sql" })
                await db.Execute(await File.ReadAllTextAsync(Path.Combine(AppContext.BaseDirectory, "Migrations", "Delivery", name)));
            return db;
        }
        public async Task Execute(string sql, object? args = null) { await using var cx = await Source.OpenConnectionAsync(); await cx.ExecuteAsync(sql, args); }
        public async Task<T?> Scalar<T>(string sql) { await using var cx = await Source.OpenConnectionAsync(); return await cx.ExecuteScalarAsync<T>(sql); }
        public async ValueTask DisposeAsync()
        {
            await Source.DisposeAsync();
            await using var cx = new NpgsqlConnection(_base); await cx.OpenAsync(); await cx.ExecuteAsync($"DROP SCHEMA {_schema} CASCADE");
        }
    }
}
