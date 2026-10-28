using APIBack.Acontecimentos;
using APIBack.GestaoLogica.Documentos;
using Xunit;

namespace APIBack.Tests.Unit;

/// <summary>Leitura de XML fiscal, interpretação da leitura por foto e identidade do documento (duplicidade).</summary>
public class GestaoDocumentoRegrasTests
{
    // NFC-e do protótipo (C-1042, Atacadão), resumida a 3 itens.
    private const string NfceXml = """
        <nfeProc xmlns="http://www.portalfiscal.inf.br/nfe"><NFe><infNFe Id="NFe31261075315333011807650030001845521000000001">
          <ide><serie>3</serie><nNF>184552</nNF><dhEmi>2026-10-26T14:32:00-03:00</dhEmi></ide>
          <emit><CNPJ>75315333011807</CNPJ><xNome>ATACADAO S.A.</xNome><xFant>Atacadão Uberlândia</xFant></emit>
          <det nItem="1"><prod><cProd>1001</cProd><cEAN>7891234567895</cEAN><xProd>PAO HAMB BRIOCHE C/6 ZP</xProd><uCom>PCT</uCom><qCom>4.0000</qCom><vUnCom>12.90</vUnCom><vProd>51.60</vProd></prod></det>
          <det nItem="2"><prod><cProd>1002</cProd><cEAN>SEM GTIN</cEAN><xProd>TOMATE ITALIANO KG</xProd><uCom>KG</uCom><qCom>1.850</qCom><vUnCom>7.98</vUnCom><vProd>14.76</vProd></prod></det>
          <det nItem="3"><prod><cProd>1003</cProd><xProd>SAC KRAFT 30X40</xProd><uCom>UN</uCom><qCom>1</qCom><vUnCom>9.90</vUnCom><vProd>10.90</vProd><vDesc>1.00</vDesc></prod></det>
          <total><ICMSTot><vDesc>1.00</vDesc><vNF>76.26</vNF></ICMSTot></total>
        </infNFe></NFe></nfeProc>
        """;

    [Fact]
    public void Le_nfce_com_fornecedor_itens_unidades_e_descontos()
    {
        var d = DocumentoRegras.LerXml(NfceXml);
        Assert.Equal("31261075315333011807650030001845521000000001", d.ChaveAcesso);
        Assert.Equal("75315333011807", d.Fornecedor.Cnpj);
        Assert.Equal("Atacadão Uberlândia", d.Fornecedor.Nome);
        Assert.Equal("184552", d.Numero);
        Assert.Equal(3, d.Itens.Count);
        Assert.Equal(("PCT", 4m, 51.60m), (d.Itens[0].Unidade, d.Itens[0].Quantidade, d.Itens[0].ValorTotal));
        Assert.Null(d.Itens[1].Ean); // SEM GTIN
        Assert.Equal((1.00m, 9.90m), (d.Itens[2].Desconto, d.Itens[2].ValorTotal));
        Assert.Equal(76.26m, d.Total);
        Assert.Empty(d.Avisos);
    }

    [Fact]
    public void Xml_invalido_ou_sem_itens_e_recusado()
    {
        Assert.Equal("CAMPO_INVALIDO", Assert.Throws<GestaoException>(() => DocumentoRegras.LerXml("<nao-e-nota/>")).Codigo);
        Assert.Equal("CAMPO_INVALIDO", Assert.Throws<GestaoException>(() => DocumentoRegras.LerXml("isto não é xml")).Codigo);
    }

    [Fact]
    public void Interpreta_leitura_da_foto_e_avisa_soma_diferente_e_linha_ilegivel()
    {
        var d = LeitorCupomClaude.Interpretar("""
            {"fornecedor_nome":"Atacadão","fornecedor_cnpj":"75.315.333/0118-07","data_emissao":"2026-10-26T14:32",
             "numero":"184552","serie":"3","chave_acesso":"3126 1075 3153 3301 1807 6500 3000 1845 5210 0000 0001",
             "desconto":0,"total":186.40,"itens":[
               {"descricao":"PAO HAMB BRIOCHE C/6 ZP","codigo":null,"quantidade":4,"unidade":"pct","valor_unitario":12.9,"desconto":0,"valor_total":51.6,"ilegivel":false},
               {"descricao":"7791 TEMP ESP","codigo":null,"quantidade":1,"unidade":"UN","valor_unitario":5.78,"desconto":0,"valor_total":5.78,"ilegivel":true}]}
            """);
        Assert.Equal(OrigemDocumento.Foto, d.Origem);
        Assert.Equal("75315333011807", d.Fornecedor.Cnpj);
        Assert.Equal(44, d.ChaveAcesso!.Length);
        Assert.Equal("PCT", d.Itens[0].Unidade);
        Assert.True(d.Itens[1].Ilegivel);
        Assert.Contains(d.Avisos, a => a.Contains("não bate"));
        Assert.Contains(d.Avisos, a => a.Contains("ilegível"));
    }

    [Fact]
    public void Identidade_usa_chave_de_acesso_ou_cnpj_serie_numero()
    {
        var comChave = DocumentoRegras.LerXml(NfceXml);
        Assert.Equal(comChave.ChaveAcesso, DocumentoRegras.ChaveLivro(comChave));
        var semChave = comChave with { ChaveAcesso = null, Serie = "003", Numero = "0184552" };
        Assert.Equal("doc:75315333011807:3:184552", DocumentoRegras.ChaveLivro(semChave));
        Assert.Null(DocumentoRegras.ChaveLivro(semChave with { Fornecedor = new FornecedorLido(null, "Feira") }));
        var fuso = TimeZoneInfo.FindSystemTimeZoneById("America/Sao_Paulo");
        Assert.Equal("75315333011807:2026-10-26:76.26", DocumentoRegras.Semelhanca(comChave, fuso));
    }

    [Fact]
    public void Hash_e_nome_chave_sao_estaveis()
    {
        Assert.Equal(DocumentoRegras.Hash(new byte[] { 1, 2, 3 }), DocumentoRegras.Hash(new byte[] { 1, 2, 3 }));
        Assert.Equal("QJO MUSS FAT 2KG", DocumentoRegras.NomeChave("  qjo. muss/fat 2kg "));
        Assert.Equal("ACUCAR CRISTAL", DocumentoRegras.NomeChave("Açúcar cristal"));
    }
}
