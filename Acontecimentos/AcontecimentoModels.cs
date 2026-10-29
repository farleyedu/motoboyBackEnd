using System.Text.Json;

namespace APIBack.Acontecimentos;

// Vocabulario do livro de acontecimentos (docs/zippy-gestao/CONTRATOS.md). Os valores sao os gravados no banco.

public static class TipoConta
{
    public const string Banco = "banco", Caixa = "caixa", Recebivel = "recebivel", CartaoEmpresa = "cartao_empresa";
    public static readonly string[] Todos = { Banco, Caixa, Recebivel, CartaoEmpresa };
}

public static class Natureza
{
    public const string Receita = "receita", Despesa = "despesa", Transferencia = "transferencia",
        Aporte = "aporte", Retirada = "retirada";
    public static readonly string[] Todas = { Receita, Despesa, Transferencia, Aporte, Retirada };
    /// <summary>Movimento de socio: fora da operacao (nao e faturamento nem despesa).</summary>
    public static bool ForaDaOperacao(string natureza) => natureza is Aporte or Retirada;
}

public static class Sentido
{
    public const string Entrada = "entrada", Saida = "saida";
}

public static class StatusLancamento
{
    public const string Previsto = "previsto", APagar = "a_pagar", Pago = "pago", AReceber = "a_receber", Recebido = "recebido";
    public static readonly string[] Todos = { Previsto, APagar, Pago, AReceber, Recebido };
    public static bool Liquidado(string status) => status is Pago or Recebido;
}

public static class TipoMovimento
{
    public const string Entrada = "entrada", Saida = "saida", Perda = "perda", Divergencia = "divergencia",
        Ajuste = "ajuste", Estorno = "estorno";
    /// <summary>Tipos que um publicador pode enviar; ajuste e estorno so nascem de correcao.</summary>
    public static readonly string[] Publicaveis = { Entrada, Saida, Perda, Divergencia };
}

public static class Correcao
{
    public const string Estorno = "estorno", Ajuste = "ajuste";
}

public static class ModuloGestao
{
    public const string Financeiro = "financeiro", Caixa = "caixa", Compras = "compras", Estoque = "estoque",
        Equipe = "equipe", Despesas = "despesas", Pedidos = "pedidos", Curadoria = "curadoria", Sistema = "sistema";
    public static readonly string[] Todos = { Financeiro, Caixa, Compras, Estoque, Equipe, Despesas, Pedidos, Curadoria, Sistema };
}

/// <summary>Tipos de acontecimento com regra propria. Outros tipos sao livres (minusculas e _).</summary>
public static class TipoAcontecimento
{
    public const string Perda = "perda", Inventario = "inventario", Pagamento = "pagamento",
        FechamentoMes = "fechamento_mes", Estorno = "estorno", Ajuste = "ajuste";
    /// <summary>Reservados: so o proprio servico cria.</summary>
    public static readonly string[] Reservados = { Pagamento, FechamentoMes, Estorno, Ajuste };
}

public sealed class OrigemRef
{
    public string? Tipo { get; set; }
    public string? Id { get; set; }
}

/// <summary>Contrato de publicacao. Cada modulo monta um destes; o servico decide parcelas, fatura e status.</summary>
public sealed class PublicarAcontecimentoRequest
{
    /// <summary>Obrigatoria. Mesma chave + mesmo conteudo = devolve o ja gravado; mesma chave + outro conteudo = 409.</summary>
    public string? ChaveIdempotencia { get; set; }
    public string? Tipo { get; set; }
    public string? Modulo { get; set; }
    public DateTimeOffset? DataFato { get; set; }
    public string? Descricao { get; set; }
    public OrigemRef? Origem { get; set; }
    /// <summary>Identidade do documento (chave da NFC-e, codigo do boleto...). Nao entra duas vezes.</summary>
    public string? DocumentoChave { get; set; }
    public JsonElement? Dados { get; set; }
    public List<LancamentoRequest>? Lancamentos { get; set; }
    public List<MovimentoRequest>? Movimentos { get; set; }
    /// <summary>Obrigatorio (true) quando ha movimentos de estoque: estoque nunca muda sem uma pessoa confirmar.</summary>
    public bool ConfirmacaoHumana { get; set; }
}

public sealed class LancamentoRequest
{
    public Guid? ContaId { get; set; }
    public string? Natureza { get; set; }
    /// <summary>So para transferencia (as demais naturezas tem sentido fixo).</summary>
    public string? Sentido { get; set; }
    public decimal Valor { get; set; }
    /// <summary>Opcional. Sem status: com DataPagamento = pago/recebido; sem = a_pagar/a_receber.</summary>
    public string? Status { get; set; }
    /// <summary>Mes a que o valor pertence. Padrao: mes da data do fato.</summary>
    public DateOnly? MesReferencia { get; set; }
    public DateOnly? DataVencimento { get; set; }
    public DateOnly? DataPagamento { get; set; }
    public int Parcelas { get; set; } = 1;
    /// <summary>Categoria global (Zippy).</summary>
    public string? Categoria { get; set; }
    /// <summary>Finalidade da loja (por que a loja gastou). Conceito separado da categoria.</summary>
    public string? Finalidade { get; set; }
    public string? Descricao { get; set; }
}

public sealed class MovimentoRequest
{
    public Guid ItemId { get; set; }
    public string? ItemNome { get; set; }
    /// <summary>Com sinal: entrada &gt; 0; saida e perda &lt; 0; divergencia com o sinal da diferenca.</summary>
    public decimal Quantidade { get; set; }
    /// <summary>Unidade base: g, ml ou un.</summary>
    public string? Unidade { get; set; }
    public string? Tipo { get; set; }
    public string? Motivo { get; set; }
}

public sealed class EstornoRequest
{
    public string? ChaveIdempotencia { get; set; }
    public string? Motivo { get; set; }
    public DateTimeOffset? DataFato { get; set; }
    public bool ConfirmacaoHumana { get; set; }
}

/// <summary>Ajuste de um lancamento (ValorNovo) ou de um movimento de estoque (QuantidadeNova).</summary>
public sealed class AjusteRequest
{
    public string? ChaveIdempotencia { get; set; }
    public string? Motivo { get; set; }
    public Guid? LancamentoId { get; set; }
    public decimal? ValorNovo { get; set; }
    public Guid? MovimentoId { get; set; }
    public decimal? QuantidadeNova { get; set; }
    public DateTimeOffset? DataFato { get; set; }
    public bool ConfirmacaoHumana { get; set; }
}

public sealed class LiquidarRequest
{
    public string? ChaveIdempotencia { get; set; }
    public Guid LancamentoId { get; set; }
    public Guid ContaId { get; set; }
    public DateOnly? DataPagamento { get; set; }
    public string? Descricao { get; set; }
}

public sealed class FecharMesRequest
{
    public string? ChaveIdempotencia { get; set; }
}

public sealed class ContaRequest
{
    public string? ChaveIdempotencia { get; set; }
    public string? Tipo { get; set; }
    public string? Subtipo { get; set; }
    public string? Nome { get; set; }
    public int? DiaFechamento { get; set; }
    public int? DiaVencimento { get; set; }
}

/// <summary>Filtro comum das leituras: mes obrigatorio (historico de todos os meses), dia opcional. Com <c>Ate</c>, o dia vira o
/// inicio de um intervalo dentro do mes (quinzena ou semana): <c>Dia</c> a <c>Ate</c>, inclusive.</summary>
public sealed class PeriodoFiltro
{
    public DateOnly? Mes { get; set; }
    public DateOnly? Dia { get; set; }
    public DateOnly? Ate { get; set; }
    public string? Modulo { get; set; }
    public string? Tipo { get; set; }
    public string? OrigemTipo { get; set; }
    public string? OrigemId { get; set; }
    public Guid? ContaId { get; set; }
    public Guid? ItemId { get; set; }
    public string? Status { get; set; }
    public int Limite { get; set; } = 200;
}

public sealed record ContaDto(Guid Id, string Tipo, string? Subtipo, string Nome, int? DiaFechamento, int? DiaVencimento,
    bool Ativo, decimal Saldo);

public sealed record LancamentoDto(Guid Id, Guid AcontecimentoId, int Linha, Guid? ContaId, string Natureza, string Sentido,
    decimal Valor, string Status, string StatusRegistro, DateOnly MesReferencia, DateOnly? DataVencimento,
    DateOnly? DataPagamento, int Parcela, int Parcelas, DateOnly? FaturaMes, string? Categoria, string? Finalidade,
    string? Descricao, Guid? CorrigeLancamentoId, bool ForaDaOperacao);

public sealed record MovimentoDto(Guid Id, Guid AcontecimentoId, int Linha, Guid ItemId, string ItemNome, decimal Quantidade,
    string Unidade, string Tipo, string? Motivo, DateOnly MesReferencia, int ConfirmadoPor, DateTimeOffset ConfirmadoEm,
    Guid? CorrigeMovimentoId);

public sealed record LiquidacaoDto(Guid Id, Guid AcontecimentoId, Guid LancamentoId, Guid ContaId, decimal Valor,
    DateOnly DataPagamento, Guid? CorrigeLiquidacaoId);

public sealed record AcontecimentoDto(Guid Id, string Tipo, string Modulo, DateTimeOffset DataFato, DateTimeOffset RegistradoEm,
    DateOnly MesReferencia, int? ResponsavelId, string? ResponsavelNome, OrigemRef? Origem, string? DocumentoChave,
    string Descricao, JsonElement Dados, Guid? CorrigeId, string? Correcao, string? Motivo, decimal? ValorAnterior,
    decimal? ValorNovo, IReadOnlyList<Guid> CorrigidoPor, IReadOnlyList<LancamentoDto> Lancamentos,
    IReadOnlyList<MovimentoDto> Movimentos, IReadOnlyList<LiquidacaoDto> Liquidacoes);

/// <summary>Resposta de toda escrita. Repetido = a chave ja existia com o mesmo conteudo (nada novo foi gravado).</summary>
public sealed record AcontecimentoPublicado(Guid Id, bool Repetido, AcontecimentoDto Acontecimento);

/// <summary>Evento em processo disparado depois do commit (nunca no replay).</summary>
public sealed record AcontecimentoRegistrado(Guid EstabelecimentoId, AcontecimentoDto Acontecimento);

/// <summary>Quem quiser reagir a acontecimentos (Gestao e Inteligencia, tempo real) registra um destes no DI.</summary>
public interface IAcontecimentoAssinante
{
    Task AoRegistrar(AcontecimentoRegistrado evento, CancellationToken ct);
}

/// <summary>Erro de regra: Codigo estavel para o front, Status HTTP sugerido.</summary>
public sealed class GestaoException(string codigo, string mensagem, int status = 422) : Exception(mensagem)
{
    public string Codigo { get; } = codigo;
    public int Status { get; } = status;
}
