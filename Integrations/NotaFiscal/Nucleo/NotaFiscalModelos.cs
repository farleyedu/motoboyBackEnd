using System;
using System.Collections.Generic;

namespace APIBack.Integrations.NotaFiscal.Nucleo;

// Valores em texto, como no contrato (docs/nota-fiscal/CONTRATO.md do zippy-admin).
public static class Provedores
{
    public const string Focus = "focus";
    public static bool Valido(string? valor) => valor == Focus;
}

public static class Ambientes
{
    public const string Homologacao = "homologacao";
    public const string Producao = "producao";
    public static bool Valido(string? valor) => valor is Homologacao or Producao;
}

public static class Regimes
{
    public const string Mei = "mei";
    public const string Simples = "simples";
    public const string Presumido = "presumido";
    public const string Real = "real";
    public static bool Valido(string? valor) => valor is Mei or Simples or Presumido or Real;
}

public static class StatusConexao
{
    public const string NaoConectado = "nao_conectado";
    public const string Conectado = "conectado";
    public const string Erro = "erro";
}

public static class ModosEmissao
{
    public const string Manual = "manual";
    public const string Automatico = "automatico";
    public const string SoComCpf = "so_com_cpf";
    public static bool Valido(string? valor) => valor is Manual or Automatico or SoComCpf;
}

public static class ModosConta
{
    public const string DaLoja = "da_loja";
    public const string DoZippy = "do_zippy";
}

// ---------- DTOs de entrada e saída (contrato) ----------

public sealed class ItemChecklist
{
    public string Id { get; set; } = string.Empty;
    public string Titulo { get; set; } = string.Empty;
    public bool Ok { get; set; }
    public string Dica { get; set; } = string.Empty;
}

public sealed class ConfirmacoesDto
{
    public bool Certificado { get; set; }
    public bool Csc { get; set; }
}

public sealed class ConexaoDto
{
    public string? Provedor { get; set; }
    public string Ambiente { get; set; } = Ambientes.Homologacao;
    public string Status { get; set; } = StatusConexao.NaoConectado;
    public string? Regime { get; set; }
    public string? InscricaoEstadual { get; set; }
    /// <summary>So diz se existe uma chave guardada. A chave nunca volta.</summary>
    public bool ChaveSalva { get; set; }
    public ConfirmacoesDto Confirmacoes { get; set; } = new();
    public DateTime? UltimoTesteEm { get; set; }
    public string? Mensagem { get; set; }
    public IReadOnlyList<ItemChecklist> Checklist { get; set; } = Array.Empty<ItemChecklist>();
    public string ModoConta { get; set; } = ModosConta.DaLoja;
}

public sealed class SalvarConexaoRequest
{
    public string? Provedor { get; set; }
    public string? Ambiente { get; set; }
    public string? Regime { get; set; }
    public string? InscricaoEstadual { get; set; }
    /// <summary>Ausente = manter a que ja existe.</summary>
    public string? Chave { get; set; }
    public ConfirmacoesDto Confirmacoes { get; set; } = new();
}

public sealed class PassoDoTeste
{
    public string Titulo { get; set; } = string.Empty;
    public bool Ok { get; set; }
    public string? Detalhe { get; set; }
}

public sealed class TesteConexaoDto
{
    public bool Ok { get; set; }
    public string Mensagem { get; set; } = string.Empty;
    public IReadOnlyList<PassoDoTeste> Passos { get; set; } = Array.Empty<PassoDoTeste>();
}

public sealed class CanaisDto
{
    public bool Cardapio { get; set; } = true;
    public bool Balcao { get; set; } = true;
    public bool Mesa { get; set; } = true;
    public bool Ifood { get; set; } = true;
}

public sealed class FormasPagamentoDto
{
    public bool Dinheiro { get; set; } = true;
    public bool Pix { get; set; } = true;
    public bool Debito { get; set; } = true;
    public bool Credito { get; set; } = true;
    public bool Online { get; set; } = true;
}

public sealed class RegrasDto
{
    public string Modo { get; set; } = ModosEmissao.Manual;
    public CanaisDto Canais { get; set; } = new();
    public FormasPagamentoDto FormasPagamento { get; set; } = new();
    public int EsperaMinutos { get; set; } = 5;
}

public sealed class ProdutoFiscalDto
{
    public Guid ProdutoId { get; set; }
    public string Nome { get; set; } = string.Empty;
    public string? Foto { get; set; }
    public string? Categoria { get; set; }
    public string? Ncm { get; set; }
    public string? Cfop { get; set; }
    public string? Csosn { get; set; }
    public string? Cst { get; set; }
    public int? Origem { get; set; }
    public string? Unidade { get; set; }
    public bool Completo { get; set; }
}

/// <summary>Valores fiscais de um produto. No PUT unitario substituem tudo; no lote so entram os preenchidos.</summary>
public sealed class ProdutoFiscalRequest
{
    public string? Ncm { get; set; }
    public string? Cfop { get; set; }
    public string? Csosn { get; set; }
    public string? Cst { get; set; }
    public int? Origem { get; set; }
    public string? Unidade { get; set; }
}

public sealed class ProdutoFiscalLoteRequest
{
    public List<Guid> ProdutoIds { get; set; } = new();
    public string? Ncm { get; set; }
    public string? Cfop { get; set; }
    public string? Csosn { get; set; }
    public string? Cst { get; set; }
    public int? Origem { get; set; }
    public string? Unidade { get; set; }
}

public sealed class PassoDoProvedor
{
    public string Titulo { get; set; } = string.Empty;
    public string Texto { get; set; } = string.Empty;
    public string? Link { get; set; }
}

public sealed class ProvedorInfo
{
    public string Id { get; set; } = string.Empty;
    public string Nome { get; set; } = string.Empty;
    public string TipoConexao { get; set; } = "chave";
    public IReadOnlyList<PassoDoProvedor> Passos { get; set; } = Array.Empty<PassoDoProvedor>();
}

// ---------- Tipos internos do emissor (assinatura fixa no contrato) ----------

/// <summary>A chave ja vem revelada, so em memoria. Nunca persistir, logar nem imprimir: o ToString nao a expoe.</summary>
public sealed record ConexaoFiscal(Guid EstabelecimentoId, string Provedor, string Ambiente, string Chave, string? Cnpj, string? Regime, string? InscricaoEstadual)
{
    public override string ToString() => $"ConexaoFiscal({Provedor}, {Ambiente})";
}

public sealed record ItemNota(string Nome, decimal Quantidade, decimal ValorUnitario, string Ncm, string Cfop, string? Csosn, string? Cst, int Origem, string Unidade);
public sealed record PagamentoNota(string Forma, decimal Valor);
public sealed record ConsumidorNota(string? CpfCnpj, string? Nome);

public sealed record NotaParaEmitir(string Referencia, IReadOnlyList<ItemNota> Itens, IReadOnlyList<PagamentoNota> Pagamentos,
    ConsumidorNota? Consumidor, decimal Total, decimal Desconto, decimal Taxas);

public sealed record ResultadoTeste(bool Ok, string Mensagem, IReadOnlyList<PassoDoTeste> Passos);
public sealed record ResultadoEmissao(bool Ok, string? Situacao, string? Chave, int? Numero, int? Serie, string? UrlPdf, string? UrlXml, string? Codigo, string? Mensagem);
public sealed record ResultadoCancelamento(bool Ok, string? Codigo, string? Mensagem);

public sealed record DadosEmpresaParceiro(string RazaoSocial, string? NomeFantasia, string Cnpj, string? InscricaoEstadual, string? Regime,
    string? Logradouro, string? Numero, string? Bairro, string? Cidade, string? Uf, string? Cep, string? Email, string? Telefone);

/// <summary>Os tokens aparecem uma vez, para serem gravados cifrados. Nunca devolver nem logar.</summary>
public sealed record ResultadoEmpresa(bool Ok, string? EmpresaIdNoProvedor, string? TokenHomologacao, string? TokenProducao, string? Codigo, string? Mensagem)
{
    public override string ToString() => $"ResultadoEmpresa(Ok={Ok}, Codigo={Codigo})";
}

/// <summary>Erro com mensagem para o usuario em portugues e codigo estavel.</summary>
public sealed class NotaFiscalException : Exception
{
    public string Codigo { get; }
    public int Http { get; }
    public NotaFiscalException(string codigo, int http, string mensagem) : base(mensagem) { Codigo = codigo; Http = http; }

    public static NotaFiscalException Invalida(string mensagem, string codigo = "NF_DADO_INVALIDO") => new(codigo, 400, mensagem);
    public static NotaFiscalException SemPermissao(string mensagem) => new("NF_SEM_PERMISSAO", 403, mensagem);
    public static NotaFiscalException NaoEncontrada(string mensagem) => new("NF_NAO_ENCONTRADO", 404, mensagem);
    public static NotaFiscalException NaoConfigurado(string mensagem) => new("NF_NAO_CONFIGURADO", 503, mensagem);
}
