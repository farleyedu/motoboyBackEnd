using APIBack.GestaoLogica.Documentos;
using APIBack.GestaoLogica.Vinculos;
using Xunit;

namespace APIBack.Tests.Unit;

/// <summary>Conversão cupom → estoque, divergência de preço e ordem das sugestões (itens da compra C-1042 do protótipo).</summary>
public class GestaoVinculoRegrasTests
{
    private static ItemLido Item(string descricao, decimal qtd, string unidade, decimal total) =>
        new(1, descricao, qtd, unidade, qtd == 0 ? 0 : total / qtd, 0, total);

    [Theory]
    [InlineData("PAO HAMB BRIOCHE C/6 ZP", 4, "PCT", null, 24, "un", 6)]
    [InlineData("QJO MUSS FAT 2KG", 1, "PC", null, 2000, "g", 2000)]
    [InlineData("TOMATE ITALIANO KG", 1.85, "KG", null, 1850, "g", 1000)]
    [InlineData("OLEO SOJA 900ML", 2, "UN", "ml", 1800, "ml", 900)]
    [InlineData("CX HAMB KRAFT M C/25", 1, "PCT", null, 25, "un", 25)]
    [InlineData("AGUA MINERAL 6X1,5L", 2, "FD", "ml", 18000, "ml", 9000)]
    [InlineData("OVOS BRANCOS", 2, "DZ", null, 24, "un", 12)]
    [InlineData("SAC KRAFT 30X40", 1, "UN", null, 1, "un", 1)]
    public void Converte_pela_unidade_ou_pela_descricao(string descricao, decimal qtd, string unidade, string? alvo,
        decimal esperado, string unEsperada, decimal fator)
    {
        var c = UnidadeRegras.Converter(Item(descricao, qtd, unidade, 10), alvo, null);
        Assert.NotNull(c);
        Assert.Equal((esperado, unEsperada, fator), (c!.Quantidade, c.Unidade, c.Fator));
        Assert.False(string.IsNullOrWhiteSpace(c.Explicacao));
    }

    [Fact]
    public void Sem_pista_ou_unidade_incompativel_pergunta_para_a_pessoa()
    {
        var pacote = Item("DESENGORDURANTE", 1, "CX", 30);
        Assert.Null(UnidadeRegras.Converter(pacote, null, null));
        Assert.Contains("1 CX", UnidadeRegras.Pergunta(pacote, null));
        // Quilo não vira unidade sozinho.
        Assert.Null(UnidadeRegras.Converter(Item("TOMATE KG", 2, "KG", 16), UnidadeBase.Un, null));
        Assert.Null(UnidadeRegras.Converter(Item("ITEM", 1, "XYZ", 5), null, null));
    }

    [Fact]
    public void Fator_aprendido_pela_loja_vence_a_descricao()
    {
        var c = UnidadeRegras.Converter(Item("SACOLA KRAFT", 1, "UN", 9.90m), UnidadeBase.Un, 50);
        Assert.Equal((50m, "vinculo"), (c!.Quantidade, c.Origem));
    }

    [Fact]
    public void Sacola_de_1_un_com_historico_de_pacote_de_50_e_divergencia()
    {
        // Histórico: pacotes de 50 a R$ 9,90 e R$ 10,40 → ~R$ 0,20/un. Agora 1 UN a R$ 9,90 sem fator.
        var historico = new[] { VinculoRegras.PrecoBase(9.90m, 50), VinculoRegras.PrecoBase(10.40m, 50) };
        var preco = VinculoRegras.PrecoBase(9.90m, 1);
        var d = VinculoRegras.Divergencia(preco, historico, 1, UnidadeBase.Un);
        Assert.NotNull(d);
        Assert.Equal(49m, d!.FatorSugerido); // 9,90 / 0,203 ≈ 48,8×: perto de um pacote de 50
        Assert.Contains("acima", d.Mensagem);

        // Variação normal de preço não é divergência.
        Assert.Null(VinculoRegras.Divergencia(VinculoRegras.PrecoBase(12.50m, 50), historico, 50, UnidadeBase.Un));
        // Muito abaixo: a quantidade já estava em unidades.
        var abaixo = VinculoRegras.Divergencia(VinculoRegras.PrecoBase(9.90m, 2500), historico, 50, UnidadeBase.Un);
        Assert.Equal(1m, abaixo!.FatorSugerido);
        // Sem histórico, nada a comparar.
        Assert.Null(VinculoRegras.Divergencia(preco, Array.Empty<decimal>(), 1, UnidadeBase.Un));
    }

    [Fact]
    public void Semelhanca_casa_abreviacoes_e_ignora_medidas()
    {
        Assert.True(VinculoRegras.Semelhanca("QJO MUSS FAT 2KG", "Mussarela fatiada") >= 0.5m);
        Assert.True(VinculoRegras.Semelhanca("PAO HAMB BRIOCHE C/6 ZP", "Pão de hambúrguer brioche") >= VinculoRegras.SemelhancaMinima);
        Assert.Equal(0m, VinculoRegras.Semelhanca("OLEO SOJA 900ML", "Caixa kraft"));
        Assert.Equal(new[] { "QJO", "MUSS", "FAT" }, VinculoRegras.Tokens("QJO MUSS FAT 2KG"));
        Assert.Equal("Pao Hamb Brioche Zp", VinculoRegras.NomeSugerido("PAO HAMB BRIOCHE C/6 ZP"));
    }

    [Fact]
    public void Ordena_por_confianca_um_por_alvo_e_termina_em_criar_novo()
    {
        var insumo = Guid.NewGuid();
        var global = Guid.NewGuid();
        var s = VinculoRegras.Ordenar(new[]
        {
            new Sugestao(OrigemSugestao.Similar, insumo, null, "Caixa kraft", "un", null, 0.5m, "nome parecido"),
            new Sugestao(OrigemSugestao.Comunidade, null, global, "Caixa kraft hambúrguer M", "un", 25,
                VinculoRegras.ConfiancaComunidade(3), "3 lojas"),
            new Sugestao(OrigemSugestao.Loja, insumo, null, "Caixa kraft", "un", 25, 0.98m, "já vinculado"),
        }, "CX HAMB KRAFT M C/25");
        Assert.Equal(new[] { OrigemSugestao.Loja, OrigemSugestao.Comunidade, OrigemSugestao.CriarNovo }, s.Select(x => x.Origem));
        Assert.Equal(0.72m, s[1].Confianca);
        Assert.Equal("Cx Hamb Kraft", s[2].Nome);
    }
}
