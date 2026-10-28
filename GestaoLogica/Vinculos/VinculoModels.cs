namespace APIBack.GestaoLogica.Vinculos;

// Vínculo de compras: linha do cupom → insumo da loja. docs/zippy-gestao/CONTRATOS.md, seção "Lógica — vínculos".

/// <summary>Unidades de estoque (as mesmas do insumo da Gestão e Inteligência).</summary>
public static class UnidadeBase
{
    public const string G = "g", Ml = "ml", Un = "un";
    public static bool Valida(string? u) => u is G or Ml or Un;
}

/// <summary>De onde veio a sugestão, na ordem de preferência.</summary>
public static class OrigemSugestao
{
    public const string Loja = "loja", Global = "global", Comunidade = "comunidade", Similar = "similar", CriarNovo = "criar_novo";

    public static int Ordem(string origem) => origem switch
    {
        Loja => 0, Global => 1, Comunidade => 2, Similar => 3, _ => 4,
    };
}

/// <summary>
/// Quantidade do cupom convertida para a unidade de estoque. <c>Fator</c> = unidades base por unidade do cupom.
/// Origem: <c>vinculo</c> (fator aprendido), <c>unidade</c> (KG → g), <c>descricao</c> ("C/6", "2KG", "900ML").
/// </summary>
public sealed record Conversao(decimal Quantidade, string Unidade, decimal Fator, string Origem, string Explicacao);

/// <summary>Preço por unidade base fora do padrão do histórico do vínculo. <c>FatorSugerido</c> corrige a quantidade.</summary>
public sealed record DivergenciaPreco(decimal PrecoBase, decimal Mediana, decimal Razao, decimal? FatorSugerido, string Mensagem);

/// <summary>Um alvo possível para a linha. <c>Confianca</c> vai de 0 a 1.</summary>
public sealed record Sugestao(string Origem, Guid? InsumoId, Guid? GlobalId, string Nome, string? UnidadeBase, decimal? Fator,
    decimal Confianca, string Motivo);

/// <summary>O que a tela recebe por linha: sugestões ordenadas, conversão (se deu para fazer) e divergência.</summary>
public sealed record SugestaoItem(int Linha, string Descricao, string NomeChave, IReadOnlyList<Sugestao> Sugestoes,
    Conversao? Conversao, string? Pergunta, DivergenciaPreco? Divergencia, Guid? VinculoId);
