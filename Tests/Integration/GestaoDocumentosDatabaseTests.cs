using APIBack.Acontecimentos;
using APIBack.GestaoLogica.Documentos;
using Dapper;
using Npgsql;
using Xunit;

namespace APIBack.Tests.Integration;

/// <summary>Documentos de compra contra PostgreSQL de verdade: duplicidade (chave, número, mesma foto), aviso de parecido e imutabilidade.</summary>
public class GestaoDocumentosDatabaseTests
{
    private sealed class FotoFake(DocumentoLido lido) : ILeitorCupomFoto
    {
        public int Leituras;
        public Task<DocumentoLido> Ler(byte[] imagem, string mediaType, CancellationToken ct) { Leituras++; return Task.FromResult(lido); }
    }

    private static DocumentoLido Cupom(string? chave, string numero, decimal total = 186.40m) => new(OrigemDocumento.Foto,
        new FornecedorLido("75315333011807", "Atacadão"), new DateTimeOffset(2026, 10, 26, 14, 32, 0, TimeSpan.FromHours(-3)),
        numero, "3", chave, new[] { new ItemLido(1, "PAO HAMB BRIOCHE C/6 ZP", 4, "PCT", 12.90m, 0, 51.60m) }, 0, total, Array.Empty<string>());

    [DeliveryDatabaseFact]
    public async Task Barra_duplicados_avisa_parecidos_e_nao_deixa_alterar()
    {
        var conexao = Environment.GetEnvironmentVariable("TEST_DELIVERY_DATABASE")!;
        APIBack.Infrastructure.DapperConfiguration.Configure();
        var opcoes = new NpgsqlConnectionStringBuilder(conexao);
        if (opcoes.Host != "127.0.0.1" && opcoes.Host != "localhost") throw new InvalidOperationException("Use apenas PostgreSQL local.");
        var schema = "gestao_doc_" + Guid.NewGuid().ToString("N");
        await using (var admin = new NpgsqlConnection(conexao)) { await admin.OpenAsync(); await admin.ExecuteAsync($"CREATE SCHEMA {schema}"); }
        opcoes.SearchPath = schema;
        var fonte = NpgsqlDataSource.Create(opcoes.ConnectionString);
        try
        {
            var loja = Guid.NewGuid();
            await using (var c = await fonte.OpenConnectionAsync())
            {
                await c.ExecuteAsync(@"CREATE TABLE estabelecimentos(id uuid PRIMARY KEY, timezone_iana text);
CREATE TABLE usuario(id integer PRIMARY KEY, nome text);
INSERT INTO estabelecimentos VALUES (@loja, 'America/Sao_Paulo'); INSERT INTO usuario VALUES (7, 'Marina');", new { loja });
                foreach (var m in new[] { "20261105_01_gestao_fundacao.sql", "20261106_01_gestao_documentos.sql", "20261106_01_gestao_documentos.sql" })
                    await c.ExecuteAsync(await File.ReadAllTextAsync(Path.Combine(AppContext.BaseDirectory, "Migrations", "Delivery", m)));
            }
            var chave = "31261075315333011807650030001845521000000001";
            var foto = new FotoFake(Cupom(chave, "184552"));
            var service = new DocumentosService(fonte, foto);
            var imagem = new byte[] { 1, 2, 3, 4 };

            var lido = await service.RegistrarFoto(loja, 7, imagem, "image/jpeg", default);
            Assert.Equal(chave, lido.ChaveAcesso);
            Assert.Empty(lido.Duplicidades);

            // Mesma foto: barrada antes de chamar a leitura de novo.
            var mesmaFoto = await Assert.ThrowsAsync<GestaoException>(() => service.RegistrarFoto(loja, 7, imagem, "image/jpeg", default));
            Assert.Equal("DUPLICADO", mesmaFoto.Codigo);
            Assert.Equal(1, foto.Leituras);
            // Mesma chave de acesso, outra foto.
            Assert.Equal("DUPLICADO", (await Assert.ThrowsAsync<GestaoException>(() =>
                service.Registrar(loja, 7, Cupom(chave, "184552"), "outro-hash", default))).Codigo);
            // Sem chave, mesmo CNPJ + série + número.
            Assert.Equal("DUPLICADO", (await Assert.ThrowsAsync<GestaoException>(() =>
                service.Registrar(loja, 7, Cupom(null, "0184552"), null, default))).Codigo);

            // Outro cupom do mesmo fornecedor, dia e total: entra, com aviso.
            var parecido = await service.Registrar(loja, 7, Cupom(null, "184553"), null, default);
            var aviso = Assert.Single(parecido.Duplicidades);
            Assert.False(aviso.Bloqueia);
            Assert.Equal(lido.Id, aviso.DocumentoId);

            // Outra loja pode lançar o mesmo documento.
            var outraLoja = Guid.NewGuid();
            await using (var c = await fonte.OpenConnectionAsync())
                await c.ExecuteAsync("INSERT INTO estabelecimentos VALUES (@outraLoja, NULL)", new { outraLoja });
            Assert.NotEqual(Guid.Empty, (await service.Registrar(outraLoja, 7, Cupom(chave, "184552"), null, default)).Id);

            // Nada se apaga nem se reescreve.
            await using (var c = await fonte.OpenConnectionAsync())
            {
                var upd = await Assert.ThrowsAsync<PostgresException>(() => c.ExecuteAsync("UPDATE gestao_documentos SET total = 1"));
                Assert.Equal("P0001", upd.SqlState);
                Assert.Equal("P0001", (await Assert.ThrowsAsync<PostgresException>(() => c.ExecuteAsync("DELETE FROM gestao_documentos"))).SqlState);
            }
        }
        finally
        {
            await fonte.DisposeAsync();
            await using var admin = new NpgsqlConnection(conexao); await admin.OpenAsync();
            await admin.ExecuteAsync($"DROP SCHEMA {schema} CASCADE");
        }
    }
}
