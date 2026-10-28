namespace APIBack.GestaoLogica.Documentos;

// Leitura de documentos de compra (cupom por foto, XML de NF-e/NFC-e). docs/zippy-gestao/CONTRATOS.md, seção "Lógica".

public static class OrigemDocumento
{
    public const string Foto = "foto", Xml = "xml", Manual = "manual";
}

public sealed record FornecedorLido(string? Cnpj, string? Nome);

/// <summary>Uma linha do documento como veio impressa. Unidade e quantidade são as do cupom, não as do estoque.</summary>
public sealed record ItemLido(int Linha, string Descricao, decimal Quantidade, string Unidade, decimal ValorUnitario,
    decimal Desconto, decimal ValorTotal, string? Codigo = null, string? Ean = null, bool Ilegivel = false);

public sealed record DocumentoLido(string Origem, FornecedorLido Fornecedor, DateTimeOffset? DataEmissao, string? Numero,
    string? Serie, string? ChaveAcesso, IReadOnlyList<ItemLido> Itens, decimal Desconto, decimal Total,
    IReadOnlyList<string> Avisos);

/// <summary>Bloqueia = já existe esse documento na loja. Aviso = parecido (mesmo fornecedor, dia e total).</summary>
public sealed record Duplicidade(bool Bloqueia, string Motivo, Guid DocumentoId);

public sealed record DocumentoDto(Guid Id, string Origem, string Status, FornecedorLido Fornecedor, DateTimeOffset? DataEmissao,
    string? Numero, string? Serie, string? ChaveAcesso, decimal Total, IReadOnlyList<ItemLido> Itens,
    IReadOnlyList<string> Avisos, IReadOnlyList<Duplicidade> Duplicidades, Guid? CompraId, int CriadoPor,
    DateTimeOffset CriadoEm);

/// <summary>Quem lê a foto do cupom. Trocável no DI (a implementação padrão usa Claude).</summary>
public interface ILeitorCupomFoto
{
    Task<DocumentoLido> Ler(byte[] imagem, string mediaType, CancellationToken ct);
}
