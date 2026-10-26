using System.Threading.Tasks;
using APIBack.Payments.MercadoPago;
using Npgsql;
using Xunit;

namespace APIBack.Tests.Unit;

public sealed class MercadoPagoAmbienteAtivoTests
{
    // A fonte nao conecta enquanto ninguem abre conexao: estes testes nunca tocam o banco.
    private static MercadoPagoAmbienteAtivo Criar(string? ambienteDaConfiguracao)
    {
        var options = new MercadoPagoOptions();
        if (ambienteDaConfiguracao != null) options.Ambiente = ambienteDaConfiguracao;
        return new MercadoPagoAmbienteAtivo(NpgsqlDataSource.Create("Host=localhost;Database=x"), Microsoft.Extensions.Options.Options.Create(options));
    }

    [Theory]
    [InlineData(null, "sandbox")]
    [InlineData("sandbox", "sandbox")]
    [InlineData("producao", "producao")]
    [InlineData("production", "producao")]
    [InlineData("qualquer-coisa", "sandbox")]
    public void SemEscolhaNaGestaoValeAConfiguracao(string? configurado, string esperado) =>
        Assert.Equal(esperado, Criar(configurado).Atual);

    [Theory]
    [InlineData("")]
    [InlineData("production")]
    [InlineData("homologacao")]
    public async Task EscolhaDaGestaoSoAceitaSandboxOuProducao(string ambiente)
    {
        var e = await Assert.ThrowsAsync<PagamentoException>(() => Criar("sandbox").DefinirAsync(ambiente, 1));
        Assert.Equal(400, e.Http);
    }
}
