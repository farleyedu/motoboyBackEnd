using Xunit;

namespace APIBack.Tests.Unit;

public sealed class ChamadoAnexosMigrationContractTests
{
    private static string Ler(string relativo) => File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Migrations", "Delivery", relativo));

    [Fact]
    public void MigrationIsAdditiveIdempotentAndLinkedToTheMessage()
    {
        var sql = Ler("20261028_04_chamado_anexos.sql");
        Assert.Contains("CREATE TABLE IF NOT EXISTS chamado_anexo", sql, StringComparison.Ordinal);
        Assert.Contains("mensagem_id BIGINT NOT NULL REFERENCES chamado_mensagem (id) ON DELETE CASCADE", sql, StringComparison.Ordinal);
        foreach (var coluna in new[] { "url         TEXT NOT NULL", "nome        TEXT NOT NULL", "tipo        TEXT NOT NULL", "bytes       INTEGER NOT NULL", "criado_em   TIMESTAMPTZ NOT NULL DEFAULT NOW()" })
            Assert.Contains(coluna, sql, StringComparison.Ordinal);
        Assert.Contains("CREATE INDEX IF NOT EXISTS ix_chamado_anexo_mensagem", sql, StringComparison.Ordinal);
        Assert.DoesNotContain("DROP ", sql, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("ALTER TABLE", sql, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void RollbackOnlyDropsTheAttachmentTable()
    {
        var sql = Ler("rollback/20261028_04_chamado_anexos.down.sql");
        Assert.Contains("DROP TABLE IF EXISTS chamado_anexo;", sql, StringComparison.Ordinal);
        Assert.DoesNotContain("chamado_mensagem", sql, StringComparison.Ordinal);
    }
}
