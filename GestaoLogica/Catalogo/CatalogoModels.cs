namespace APIBack.GestaoLogica.Catalogo;

// Catálogo da loja e global (Integração 6B). docs/zippy-gestao/CONTRATOS.md, seção "Integração 6B — Catálogo".
// Regras 6, 7 e 9: o global é consulta/vínculo; categoria é global e finalidade é da loja; ilustração Zippy ≠ foto de vitrine.

public static class OrigemCatalogo
{
    public const string Global = "global", Loja = "loja", Novo = "novo", Desconhecido = "desconhecido";
}

/// <summary>Estado da curadoria visto pela loja. Nunca bloqueia a operação local.</summary>
public static class CuradoriaLoja
{
    public const string NaoEnviado = "nao_enviado", EmAnalise = "em_analise", Validado = "validado", Rejeitado = "rejeitado";

    /// <summary>Status do item global (candidato/validado/rejeitado/mesclado) na linguagem da tela.</summary>
    public static string De(string? statusGlobal) => statusGlobal switch
    {
        "candidato" => EmAnalise,
        "validado" or "mesclado" => Validado,
        "rejeitado" => Rejeitado,
        _ => NaoEnviado,
    };
}

/// <summary>Um acontecimento do histórico do produto ou do nome do cupom (nada se apaga).</summary>
public sealed record EventoCatalogoDto(Guid Id, DateTimeOffset Quando, string Titulo, string? Detalhe, string? Responsavel);

/// <summary>
/// Insumo da loja. <c>Ilustracao</c> é o arquivo da ilustração Zippy (pode faltar: a tela usa o ícone da categoria);
/// nunca a foto do insumo nem foto de vitrine. <c>Estoque</c> é só leitura (saldo do livro).
/// </summary>
public sealed record ProdutoLojaDto(Guid Id, string Nome, string Categoria, bool CategoriaPendente, IReadOnlyList<string> Finalidades,
    string Unidade, string? Ilustracao, string Origem, string Curadoria, Guid? GlobalId, IReadOnlyList<string> NomesNoCupom,
    decimal Estoque, IReadOnlyList<EventoCatalogoDto> Historico, DateTimeOffset CriadoEm);

public sealed record ProdutoGlobalDto(Guid Id, string Nome, string? Categoria, string Unidade, string? Ilustracao, int LojasQueUsam,
    bool NaLoja, Guid? InsumoId);

public sealed record KitDto(Guid Id, string Nome, string? Descricao, IReadOnlyList<Guid> Itens);

public sealed record GlobalResultadoDto(IReadOnlyList<ProdutoGlobalDto> Produtos, int Total, IReadOnlyList<KitDto> Kits);

public sealed record KitImportadoDto(IReadOnlyList<ProdutoLojaDto> Importados, IReadOnlyList<string> JaExistiam);

/// <summary>
/// Uma linha do de-para de compras: vínculo confirmado, nome lido sem vínculo ou nome marcado "não controlar".
/// <c>Id</c> é o id do vínculo ou, sem vínculo, <c>nome:{cnpj}:{nomeChave}</c>. <c>DocumentoId</c>/<c>Linha</c> apontam para a última
/// compra em que o nome apareceu (é por ela que o vínculo se confirma). <c>UsosPorMes</c>: compras por mês 'AAAA-MM'.
/// </summary>
public sealed record DeParaCompraDto(string Id, Guid? VinculoId, string NomeNoCupom, string NomeChave, string FornecedorCnpj,
    string? FornecedorNome, Guid? InsumoId, string? InsumoNome, string Origem, string Curadoria, Guid? GlobalId,
    IReadOnlyDictionary<string, int> UsosPorMes, int UsosTotal, Guid? DocumentoId, int? Linha, DateTimeOffset? UltimaCompra,
    string? Conversao, bool NaoControlar, IReadOnlyList<EventoCatalogoDto> Historico);

public sealed record BuscaInsumoDto(Guid InsumoId, string Nome, string Unidade, string? Categoria, Guid? GlobalId, decimal Confianca);

public sealed record BuscaGlobalDto(Guid GlobalId, string Nome, string Unidade, string? Categoria, Guid? InsumoId, decimal Confianca);

/// <summary>Busca da conferência: insumos da loja e itens validados do catálogo global.</summary>
public sealed record BuscaCatalogoDto(IReadOnlyList<BuscaInsumoDto> Loja, IReadOnlyList<BuscaGlobalDto> Global);

public sealed record ImportarProdutoRequest(IReadOnlyList<string>? Finalidades);

/// <summary>Produto criado pela loja. <c>NovaCategoria</c> vai para a curadoria (o produto já funciona). Unidade: un, kg, g, L, ml.</summary>
public sealed record CriarProdutoRequest(string? Nome, string? Categoria, string? NovaCategoria, string? Unidade,
    IReadOnlyList<string>? Finalidades);

public sealed record FinalidadesRequest(IReadOnlyList<string>? Finalidades);

/// <summary>Marca (ou desmarca) um nome de cupom como "não controlar". Com <c>VinculoId</c>, o vínculo é desfeito com esse motivo.</summary>
public sealed record NaoControlarRequest(Guid? VinculoId, string? Descricao, string? FornecedorCnpj, bool Controlar = false);

/// <summary>
/// Confirma o vínculo de um nome do cupom a partir do Catálogo: pela última compra (<c>DocumentoId</c>, <c>Linha</c>) em que ele
/// apareceu. <c>Conversao</c> ("1 PC = 2 kg") vira o fator; sem ela vale o fator do vínculo atual ou 1.
/// </summary>
public sealed record VincularDeParaRequest(Guid DocumentoId, int Linha, Guid? InsumoId, Guid? GlobalId, CriarProdutoRequest? Novo,
    string? Conversao, decimal? Fator);
