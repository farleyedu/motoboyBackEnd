using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Xml.Linq;
using APIBack.Acontecimentos;

namespace APIBack.GestaoLogica.Documentos;

/// <summary>Regras puras de documento: leitura do XML fiscal, normalização e identidade (duplicidade).</summary>
public static class DocumentoRegras
{
    private static readonly XNamespace Nfe = "http://www.portalfiscal.inf.br/nfe";

    /// <summary>Lê NF-e (modelo 55) ou NFC-e (65), com ou sem o envelope nfeProc.</summary>
    public static DocumentoLido LerXml(string xml)
    {
        XDocument doc;
        try { doc = XDocument.Parse(xml); }
        catch (Exception) { throw Invalido("O arquivo não é um XML válido."); }
        var inf = doc.Descendants(Nfe + "infNFe").FirstOrDefault() ?? throw Invalido("O XML não é de uma NF-e ou NFC-e.");
        var ide = inf.Element(Nfe + "ide");
        var emit = inf.Element(Nfe + "emit");
        var chave = SoDigitos((string?)inf.Attribute("Id"));
        var itens = new List<ItemLido>();
        foreach (var det in inf.Elements(Nfe + "det"))
        {
            var p = det.Element(Nfe + "prod");
            if (p is null) continue;
            var qtd = Num(p, "qCom");
            var total = Num(p, "vProd");
            var desconto = Num(p, "vDesc");
            var ean = (string?)p.Element(Nfe + "cEAN");
            itens.Add(new ItemLido(itens.Count + 1, ((string?)p.Element(Nfe + "xProd") ?? "").Trim(), qtd,
                ((string?)p.Element(Nfe + "uCom") ?? "UN").Trim().ToUpperInvariant(), Num(p, "vUnCom"), desconto,
                Math.Round(total - desconto, 2), (string?)p.Element(Nfe + "cProd"),
                ean is null || ean == "SEM GTIN" ? null : SoDigitos(ean)));
        }
        if (itens.Count == 0) throw Invalido("O XML não tem itens.");
        var tot = inf.Element(Nfe + "total")?.Element(Nfe + "ICMSTot");
        var dhEmi = (string?)ide?.Element(Nfe + "dhEmi") ?? (string?)ide?.Element(Nfe + "dEmi");
        return new DocumentoLido(OrigemDocumento.Xml,
            new FornecedorLido(SoDigitos((string?)emit?.Element(Nfe + "CNPJ") ?? (string?)emit?.Element(Nfe + "CPF")),
                ((string?)emit?.Element(Nfe + "xFant") ?? (string?)emit?.Element(Nfe + "xNome"))?.Trim()),
            DateTimeOffset.TryParse(dhEmi, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var d) ? d : null,
            (string?)ide?.Element(Nfe + "nNF"), (string?)ide?.Element(Nfe + "serie"), chave?.Length == 44 ? chave : null,
            itens, tot is null ? itens.Sum(i => i.Desconto) : Num(tot, "vDesc"),
            tot is null ? itens.Sum(i => i.ValorTotal) : Num(tot, "vNF"), Conferir(itens, tot is null ? null : Num(tot, "vNF")));
    }

    /// <summary>Avisos de consistência: soma dos itens diferente do total, item sem valor.</summary>
    public static IReadOnlyList<string> Conferir(IReadOnlyList<ItemLido> itens, decimal? total)
    {
        var avisos = new List<string>();
        var soma = itens.Sum(i => i.ValorTotal);
        if (total is { } t && Math.Abs(soma - t) > 0.05m)
            avisos.Add($"A soma dos itens (R$ {soma:N2}) não bate com o total do documento (R$ {t:N2}).");
        foreach (var i in itens.Where(i => i.Ilegivel)) avisos.Add($"Linha {i.Linha} ficou ilegível na foto: confira.");
        return avisos;
    }

    /// <summary>Identidade fiscal: CNPJ do emitente + série + número (vale quando não há chave de acesso).</summary>
    public static string? Impressao(DocumentoLido d) =>
        string.IsNullOrEmpty(d.Fornecedor.Cnpj) || string.IsNullOrEmpty(d.Numero) ? null
            : $"{d.Fornecedor.Cnpj}:{(d.Serie ?? "").TrimStart('0')}:{d.Numero!.TrimStart('0')}";

    /// <summary>Chave para o livro (DocumentoChave): a chave de acesso ou, sem ela, a impressão fiscal.</summary>
    public static string? ChaveLivro(DocumentoLido d) => d.ChaveAcesso ?? (Impressao(d) is { } i ? "doc:" + i : null);

    /// <summary>Fornecedor + dia + total: só levanta suspeita (duas compras iguais no mesmo dia existem).</summary>
    public static string? Semelhanca(DocumentoLido d, TimeZoneInfo fuso) =>
        d.DataEmissao is not { } data || d.Total <= 0 ? null
            : $"{NomeChave(d.Fornecedor.Cnpj ?? d.Fornecedor.Nome)}:{AcontecimentoRegras.DiaLocal(data, fuso):yyyy-MM-dd}:{d.Total.ToString("0.00", CultureInfo.InvariantCulture)}";

    public static string Hash(byte[] conteudo) => Convert.ToHexString(SHA256.HashData(conteudo)).ToLowerInvariant();

    public static string? SoDigitos(string? s) => s is null ? null : new string(s.Where(char.IsDigit).ToArray()) is { Length: > 0 } d ? d : null;

    /// <summary>Texto para comparação: maiúsculas, sem acento, só letras/dígitos separados por um espaço.</summary>
    public static string NomeChave(string? texto)
    {
        if (string.IsNullOrWhiteSpace(texto)) return "";
        var sb = new StringBuilder();
        foreach (var ch in texto.Normalize(NormalizationForm.FormD))
        {
            if (CharUnicodeInfo.GetUnicodeCategory(ch) == UnicodeCategory.NonSpacingMark) continue;
            sb.Append(char.IsLetterOrDigit(ch) ? char.ToUpperInvariant(ch) : ' ');
        }
        return string.Join(' ', sb.ToString().Split(' ', StringSplitOptions.RemoveEmptyEntries));
    }

    private static decimal Num(XElement pai, string nome) =>
        decimal.TryParse((string?)pai.Element(Nfe + nome), NumberStyles.Number, CultureInfo.InvariantCulture, out var v) ? v : 0m;

    private static GestaoException Invalido(string m) => new("CAMPO_INVALIDO", m, 422);
}
