using APIBack.GestaoLogica.Caixa;
using Xunit;

namespace APIBack.Tests.Unit;

/// <summary>6C6: presença do motoboy para o Caixa pela posição (raio de 80 m, o padrão do retorno à loja).</summary>
public sealed class GestaoCaixaChegadaRegrasTests
{
    private const int Raio = 80;
    private static readonly DateTimeOffset Agora = new(2026, 11, 22, 15, 0, 0, TimeSpan.Zero);

    [Theory]
    [InlineData(30, 10, null, ChegadaRegras.Loja)]          // GPS preciso dentro do raio: retorno confirmado
    [InlineData(70, 80, null, ChegadaRegras.Loja)]          // precisão do tamanho do raio ainda confirma
    [InlineData(60, 120, null, ChegadaRegras.Provavel)]     // dentro, mas precisão fraca: só provável
    [InlineData(150, 100, null, ChegadaRegras.Provavel)]    // fora do raio, mas o círculo de erro alcança a loja
    [InlineData(300, 20, null, ChegadaRegras.Chegando)]     // aproximando (até 5× o raio, mínimo 400 m)
    [InlineData(399, 20, null, ChegadaRegras.Chegando)]
    [InlineData(2500, 20, null, ChegadaRegras.Rota)]
    public void Classifica_pela_distancia_e_pela_precisao(double distancia, double precisao, string? anterior, string esperado) =>
        Assert.Equal(esperado, ChegadaRegras.Classificar(distancia, precisao, Raio, anterior));

    [Theory]
    [InlineData(10, null)]     // sem precisão informada
    [InlineData(10, 151d)]     // GPS de antena
    [InlineData(10, double.NaN)]
    public void Posicao_imprecisa_nao_decide_nada(double distancia, double? precisao) =>
        Assert.Null(ChegadaRegras.Classificar(distancia, precisao, Raio, ChegadaRegras.Rota));

    [Fact]
    public void Na_loja_so_sai_com_posicao_boa_claramente_fora()
    {
        // GPS dentro do prédio oscila até 2× o raio: continua na loja (sem piscar o Caixa).
        Assert.Equal(ChegadaRegras.Loja, ChegadaRegras.Classificar(150, 30, Raio, ChegadaRegras.Loja));
        Assert.Equal(ChegadaRegras.Loja, ChegadaRegras.Classificar(200, 140, Raio, ChegadaRegras.Loja));
        Assert.Equal(ChegadaRegras.Chegando, ChegadaRegras.Classificar(240, 30, Raio, ChegadaRegras.Loja));
        Assert.Equal(ChegadaRegras.Rota, ChegadaRegras.Classificar(900, 30, Raio, ChegadaRegras.Loja));
        // Sem a histerese, 150 m com 30 m de precisão não é chegada.
        Assert.Equal(ChegadaRegras.Chegando, ChegadaRegras.Classificar(150, 30, Raio, ChegadaRegras.Rota));
    }

    [Fact]
    public void Raio_configurado_muda_a_chegada_e_a_aproximacao()
    {
        Assert.Equal(ChegadaRegras.Loja, ChegadaRegras.Classificar(180, 60, 200, null));
        Assert.Equal(ChegadaRegras.Chegando, ChegadaRegras.Classificar(900, 20, 200, null));
        Assert.Equal(ChegadaRegras.Rota, ChegadaRegras.Classificar(1100, 20, 200, null));
        // Raio pequeno não exige GPS perfeito para confirmar: piso de 50 m na precisão.
        Assert.Equal(ChegadaRegras.Loja, ChegadaRegras.Classificar(15, 45, 20, null));
    }

    [Theory]
    [InlineData(null, ChegadaRegras.SinalAusente)]
    [InlineData(30, ChegadaRegras.SinalOk)]
    [InlineData(119, ChegadaRegras.SinalOk)]
    [InlineData(121, ChegadaRegras.SinalAntigo)]
    public void Sinal_pela_idade_da_ultima_posicao(int? segundosAtras, string esperado) =>
        Assert.Equal(esperado, ChegadaRegras.Sinal(segundosAtras is { } s ? Agora.AddSeconds(-s) : null, Agora, 120));

    [Fact]
    public void Sem_posicao_recente_so_a_chegada_confirmada_continua()
    {
        var agora = Agora.AddMinutes(-5);
        // GPS some dentro da loja (ou chegada manual sem GPS): continua na loja, com o sinal indicado à parte.
        Assert.Equal(ChegadaRegras.Loja, ChegadaRegras.Estado(ChegadaRegras.Loja, agora, ChegadaRegras.SinalAntigo, Agora));
        Assert.Equal(ChegadaRegras.Loja, ChegadaRegras.Estado(ChegadaRegras.Loja, agora, ChegadaRegras.SinalAusente, Agora));
        // Chegando/provável com posição velha não se sustentam: coordenada antiga nunca vira chegada.
        Assert.Equal(ChegadaRegras.SemSinal, ChegadaRegras.Estado(ChegadaRegras.Chegando, agora, ChegadaRegras.SinalAntigo, Agora));
        Assert.Equal(ChegadaRegras.SemSinal, ChegadaRegras.Estado(ChegadaRegras.Provavel, agora, ChegadaRegras.SinalAntigo, Agora));
        Assert.Equal(ChegadaRegras.SemSinal, ChegadaRegras.Estado(null, null, ChegadaRegras.SinalAusente, Agora));
        // Com sinal bom vale o que foi gravado; sem nada gravado não presume.
        Assert.Equal(ChegadaRegras.Provavel, ChegadaRegras.Estado(ChegadaRegras.Provavel, agora, ChegadaRegras.SinalOk, Agora));
        Assert.Null(ChegadaRegras.Estado(null, null, ChegadaRegras.SinalOk, Agora));
    }

    [Fact]
    public void Presenca_de_outro_expediente_nao_vale()
    {
        var ontem = Agora.AddHours(-13);
        Assert.Equal(ChegadaRegras.SemSinal, ChegadaRegras.Estado(ChegadaRegras.Loja, ontem, ChegadaRegras.SinalAusente, Agora));
        Assert.Null(ChegadaRegras.Estado(ChegadaRegras.Loja, ontem, ChegadaRegras.SinalOk, Agora));
    }
}
