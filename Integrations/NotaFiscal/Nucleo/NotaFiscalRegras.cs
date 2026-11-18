using System;
using System.Collections.Generic;
using System.Linq;

namespace APIBack.Integrations.NotaFiscal.Nucleo;

/// <summary>Regras puras (sem banco): validacao dos dados fiscais, checklist e padroes.</summary>
public static class NotaFiscalRegras
{
    private static readonly HashSet<string> CsosnValidos = new() { "101", "102", "103", "201", "202", "203", "300", "400", "500", "900" };
    private static readonly HashSet<string> CstValidos = new() { "00", "10", "20", "30", "40", "41", "50", "51", "60", "70", "90" };

    public const int MaxNcm = 8;

    private static string? SoDigitos(string? texto)
    {
        if (string.IsNullOrWhiteSpace(texto)) return null;
        var digitos = new string(texto.Where(char.IsDigit).ToArray());
        return digitos.Length == 0 ? null : digitos;
    }

    /// <summary>NCM tem 8 digitos. Aceita pontos e espacos ("2202.10.00"). Vazio = null.</summary>
    public static string? NormalizarNcm(string? ncm)
    {
        if (string.IsNullOrWhiteSpace(ncm)) return null;
        var d = SoDigitos(ncm);
        if (d == null || d.Length != MaxNcm) throw NotaFiscalException.Invalida("O NCM tem 8 digitos. Confira com seu contador.");
        return d;
    }

    /// <summary>CFOP tem 4 digitos e comeca com 5 (venda dentro do estado), 6 ou 7 em saidas.</summary>
    public static string? NormalizarCfop(string? cfop)
    {
        if (string.IsNullOrWhiteSpace(cfop)) return null;
        var d = SoDigitos(cfop);
        if (d == null || d.Length != 4 || d[0] is not ('5' or '6' or '7'))
            throw NotaFiscalException.Invalida("O CFOP tem 4 digitos e, em venda, comeca com 5, 6 ou 7. Exemplo: 5102.");
        return d;
    }

    public static string? NormalizarCsosn(string? csosn)
    {
        if (string.IsNullOrWhiteSpace(csosn)) return null;
        var d = SoDigitos(csosn);
        if (d == null || !CsosnValidos.Contains(d))
            throw NotaFiscalException.Invalida("CSOSN invalido. Valores aceitos: 101, 102, 103, 201, 202, 203, 300, 400, 500 ou 900.");
        return d;
    }

    public static string? NormalizarCst(string? cst)
    {
        if (string.IsNullOrWhiteSpace(cst)) return null;
        var d = SoDigitos(cst);
        if (d == null) throw NotaFiscalException.Invalida("CST invalido. Use 2 digitos, como 00, 40 ou 60.");
        d = d.PadLeft(2, '0');
        if (!CstValidos.Contains(d)) throw NotaFiscalException.Invalida("CST invalido. Use 2 digitos, como 00, 40 ou 60.");
        return d;
    }

    public static int? ValidarOrigem(int? origem)
    {
        if (origem == null) return null;
        if (origem is < 0 or > 8) throw NotaFiscalException.Invalida("A origem da mercadoria vai de 0 a 8. Nacional e 0.");
        return origem;
    }

    public static string? NormalizarUnidade(string? unidade)
    {
        if (string.IsNullOrWhiteSpace(unidade)) return null;
        var u = unidade.Trim().ToUpperInvariant();
        if (u.Length is < 1 or > 6 || !u.All(char.IsLetter)) throw NotaFiscalException.Invalida("Unidade invalida. Use letras, como UN, KG ou L.");
        return u;
    }

    /// <summary>Completo = tem NCM, CFOP e CSOSN ou CST. Origem e unidade usam o padrao (0 e UN) na hora de emitir.</summary>
    public static bool ProdutoCompleto(string? ncm, string? cfop, string? csosn, string? cst) =>
        !string.IsNullOrWhiteSpace(ncm) && !string.IsNullOrWhiteSpace(cfop) &&
        (!string.IsNullOrWhiteSpace(csosn) || !string.IsNullOrWhiteSpace(cst));

    // ---------- Conexao ----------

    /// <summary>Chave do provedor: sem espacos nem caracteres de controle, de 8 a 200 caracteres.</summary>
    public static string? ValidarChave(string? chave)
    {
        if (chave == null) return null;
        var valor = chave.Trim();
        if (valor.Length == 0) return null;
        if (valor.Length is < 8 or > 200 || valor.Any(char.IsWhiteSpace) || valor.Any(char.IsControl))
            throw NotaFiscalException.Invalida("A chave do provedor parece incompleta. Copie de novo, sem espacos.");
        return valor;
    }

    public static string? NormalizarInscricaoEstadual(string? valor)
    {
        if (string.IsNullOrWhiteSpace(valor)) return null;
        var t = valor.Trim().ToUpperInvariant();
        if (t == "ISENTO") return t;
        var d = SoDigitos(t);
        if (d == null || d.Length is < 2 or > 14) throw NotaFiscalException.Invalida("Inscricao estadual invalida. Digite so os numeros ou ISENTO.");
        return d;
    }

    public static IReadOnlyList<ItemChecklist> Checklist(string? regime, bool confirmouCertificado, bool confirmouCsc, bool chaveSalva, string status, int produtosPendentes)
    {
        return new[]
        {
            new ItemChecklist { Id = "regime", Titulo = "Regime da empresa", Ok = !string.IsNullOrEmpty(regime),
                Dica = regime == Regimes.Mei
                    ? "MEI pode nao precisar de nota para consumidor final. Voce pode seguir assim mesmo."
                    : "Diga se a empresa e MEI, Simples ou outro. Seu contador sabe." },
            new ItemChecklist { Id = "certificado", Titulo = "Certificado digital enviado ao provedor", Ok = confirmouCertificado,
                Dica = "O certificado A1 (e-CNPJ) e enviado no painel do provedor, nao aqui." },
            new ItemChecklist { Id = "csc", Titulo = "CSC cadastrado", Ok = confirmouCsc,
                Dica = "O CSC e gerado no portal da SEFAZ do seu estado. O contador costuma fazer." },
            new ItemChecklist { Id = "chave", Titulo = "Chave do provedor salva", Ok = chaveSalva,
                Dica = "Copie a chave no painel do provedor e cole aqui." },
            new ItemChecklist { Id = "teste", Titulo = "Conexao testada", Ok = status == StatusConexao.Conectado,
                Dica = "Use Testar conexao. Comece sempre no ambiente de teste." },
            new ItemChecklist { Id = "produtos", Titulo = "Produtos com dados fiscais", Ok = produtosPendentes == 0,
                Dica = produtosPendentes == 0 ? "Todos os produtos tem NCM, CFOP e CSOSN ou CST."
                    : $"{produtosPendentes} {(produtosPendentes == 1 ? "produto esta" : "produtos estao")} sem dados fiscais. Sem eles a nota nao sai." },
        };
    }

    // ---------- Regras de emissao ----------

    public static RegrasDto RegrasPadrao() => new();

    public static RegrasDto ValidarRegras(RegrasDto regras)
    {
        if (regras == null) throw NotaFiscalException.Invalida("Informe as regras.");
        if (!ModosEmissao.Valido(regras.Modo)) throw NotaFiscalException.Invalida("Escolha quando emitir: manual, automatico ou so com CPF.");
        if (regras.EsperaMinutos is < 0 or > 60) throw NotaFiscalException.Invalida("A espera vai de 0 a 60 minutos.");
        regras.Canais ??= new CanaisDto();
        regras.FormasPagamento ??= new FormasPagamentoDto();
        return regras;
    }

    // ---------- Produtos ----------

    /// <summary>Normaliza e valida todos os campos. Usado no PUT unitario (substitui tudo) e no lote (so os preenchidos).</summary>
    public static ProdutoFiscalRequest NormalizarProduto(ProdutoFiscalRequest r) => new()
    {
        Ncm = NormalizarNcm(r.Ncm), Cfop = NormalizarCfop(r.Cfop), Csosn = NormalizarCsosn(r.Csosn),
        Cst = NormalizarCst(r.Cst), Origem = ValidarOrigem(r.Origem), Unidade = NormalizarUnidade(r.Unidade),
    };

    public static IReadOnlyList<ProvedorInfo> CatalogoProvedores() => new[]
    {
        new ProvedorInfo
        {
            Id = Provedores.Focus, Nome = "Focus NFe", TipoConexao = "chave",
            Passos = new[]
            {
                new PassoDoProvedor { Titulo = "Crie sua conta", Texto = "Cadastre-se no Focus NFe. Ha teste gratis e os planos para uma loja comecam em cerca de R$ 60 por mes.", Link = "https://focusnfe.com.br/precos/" },
                new PassoDoProvedor { Titulo = "Cadastre a empresa", Texto = "No painel do Focus, cadastre a empresa com CNPJ, endereco e regime." },
                new PassoDoProvedor { Titulo = "Envie o certificado digital", Texto = "Envie o certificado A1 (e-CNPJ) da empresa no painel do Focus. Se nao tem, o contador ou uma certificadora fornece." },
                new PassoDoProvedor { Titulo = "Cadastre o CSC", Texto = "Gere o CSC no portal da SEFAZ do seu estado e informe no Focus. O contador costuma fazer isso." },
                new PassoDoProvedor { Titulo = "Copie a chave", Texto = "Copie a chave de acesso da empresa (ha uma de teste e outra de producao) e cole aqui.", Link = "https://doc.focusnfe.com.br/" },
            },
        },
    };
}
