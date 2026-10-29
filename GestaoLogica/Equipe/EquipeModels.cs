namespace APIBack.GestaoLogica.Equipe;

// Equipe e remuneração. Espelha os tipos do Painel (zippy-admin/src/features/zippy-gestao/equipe/tipos.ts) para a Integração só
// trocar o adaptador simulado. docs/zippy-gestao/CONTRATOS.md, seção 9.6.

public static class VinculoPessoa
{
    public const string Fixo = "fixo", Diarista = "diarista", Freelancer = "freelancer", Hora = "hora", Motoboy = "motoboy",
        Prestador = "prestador";
    public static readonly string[] Todos = { Fixo, Diarista, Freelancer, Hora, Motoboy, Prestador };
}

public static class StatusTurno
{
    public const string Previsto = "previsto", Realizado = "realizado", Falta = "falta", Cancelado = "cancelado";
    public static readonly string[] Todos = { Previsto, Realizado, Falta, Cancelado };
}

/// <summary>
/// Como a pessoa recebe. Os valores se somam (remuneração mista): salário 1/30 por turno + diária ou turno + horas + entregas
/// + comissão sobre a base do turno + adicional noturno (turno com 1 h ou mais entre 22:00 e 05:00).
/// </summary>
public sealed record RegraPagamento(decimal? SalarioMensal = null, decimal? Diaria = null, decimal? PorTurno = null,
    decimal? PorHora = null, decimal? PorEntrega = null, decimal? AdicionalNoturno = null, decimal? ComissaoPercentual = null);

/// <summary>Dia da escala recorrente. <c>Dow</c>: 0 = domingo.</summary>
public sealed record DiaPadrao(int Dow, string Entrada, string Saida);

/// <summary>Pagamento combinado só para aquele turno (jornada avulsa): turno, diaria, hora ou entrega.</summary>
public sealed record PagamentoAvulso(string Forma, decimal Valor);

/// <summary>Adicional do turno (chuva, feriado...). Só conta no turno realizado.</summary>
public sealed record AdicionalTurno(string Rotulo, decimal Valor);

public sealed record ItemRemuneracao(string Rotulo, decimal Valor);

/// <summary><c>Previsto</c> = o que o turno renderia acontecendo normalmente (a falta mostra "era R$ x").</summary>
public sealed record Remuneracao(decimal Valor, decimal Previsto, IReadOnlyList<ItemRemuneracao> Itens);

/// <summary>O que entra no cálculo de um turno.</summary>
public sealed record JornadaTurno(string Entrada, string Saida, string Status, bool Justificada, int Entregas,
    PagamentoAvulso? PagamentoAvulso, IReadOnlyList<AdicionalTurno> Adicionais, decimal BaseComissao);

public sealed record PessoaDto(Guid Id, string Nome, string Funcao, string Vinculo, RegraPagamento Regra, string PagaCom,
    string? ChavePix, int? UsuarioId, string? Cor, IReadOnlyList<DiaPadrao> EscalaPadrao, bool Ativo, DateOnly Desde,
    int CriadoPor, DateTimeOffset CriadoEm, int Versao, string? CriadoPorNome = null);

public sealed record PessoaRequest(string? Nome, string? Funcao, string? Vinculo, RegraPagamento? Regra, string? PagaCom,
    string? ChavePix, int? UsuarioId, string? Cor, IReadOnlyList<DiaPadrao>? EscalaPadrao, bool Ativo = true, DateOnly? Desde = null,
    int? Versao = null);

/// <summary>Linha do histórico do turno: nada se apaga, cada mudança é mais uma linha.</summary>
public sealed record EventoTurno(DateTimeOffset Em, int? Por, string Acao, decimal? ValorAnterior, decimal? ValorNovo,
    string? Motivo, string? Detalhe, string? PorNome = null);

/// <summary>
/// Turno de uma pessoa num dia. <c>Id</c> nulo = turno "virtual" da escala recorrente, ainda não tocado (não existe no banco).
/// <c>Chave</c> = <c>{pessoaId}:{AAAA-MM-DD}</c> (um turno por pessoa por dia), a mesma do Painel.
/// </summary>
public sealed record TurnoDto(Guid? Id, string Chave, Guid PessoaId, DateOnly Data, string Entrada, string Saida, string Status,
    bool Avulso, bool Justificada, int Entregas, PagamentoAvulso? PagamentoAvulso, IReadOnlyList<AdicionalTurno> Adicionais,
    decimal BaseComissao, decimal Valor, decimal Previsto, IReadOnlyList<ItemRemuneracao> Itens, int Revisao,
    Guid? AcontecimentoId, Guid? LancamentoId, IReadOnlyList<EventoTurno> Historico);

/// <summary>Mudança num turno (cria o turno da escala ou avulso se ainda não existir). Campos nulos = não mudam.</summary>
public sealed record MudarTurnoRequest(Guid PessoaId, DateOnly Data, string? Status = null, string? Entrada = null, string? Saida = null,
    int? Entregas = null, bool? Justificada = null, IReadOnlyList<AdicionalTurno>? Adicionais = null, decimal? BaseComissao = null,
    PagamentoAvulso? PagamentoAvulso = null, bool Avulso = false, string? Motivo = null);

public sealed record TotaisSemana(decimal Previsto, decimal Apurado, int Turnos, int Realizados, int Faltas);

public sealed record SemanaDto(DateOnly Inicio, IReadOnlyList<PessoaDto> Pessoas, IReadOnlyDictionary<Guid, IReadOnlyList<TurnoDto?>> Grade,
    TotaisSemana Totais);

/// <summary>Item a pagar/pago de uma pessoa (turno ou saldo de pagamento parcial), com o valor líquido (original + correções).</summary>
public sealed record ItemDevido(Guid LancamentoId, Guid AcontecimentoId, string Tipo, DateOnly Data, string Descricao, decimal Valor,
    string Status, DateOnly MesReferencia);

public sealed record AdiantamentoDto(Guid AcontecimentoId, DateOnly Data, decimal Valor, decimal Compensado, decimal Aberto);

/// <summary>Previsto (escala) × apurado (realizado) × devido (livro) × pago × a pagar, por pessoa, no mês.</summary>
public sealed record ResumoPessoa(Guid PessoaId, string Nome, decimal Previsto, decimal Apurado, decimal Devido, decimal Pago,
    decimal APagar, decimal AdiantamentosAbertos, int Faltas, IReadOnlyList<ItemDevido> Itens, IReadOnlyList<AdiantamentoDto> Adiantamentos);

public sealed record AdiantamentoRequest(string? ChaveIdempotencia, Guid PessoaId, decimal Valor, Guid ContaId, DateOnly? Data, string? Motivo);

public sealed record DescontoRequest(string? ChaveIdempotencia, Guid PessoaId, Guid LancamentoId, decimal Valor, string? Motivo);

/// <summary><c>Valor</c> nulo = paga tudo o que está a pagar; menor = pagamento parcial (o restante vira saldo a pagar).</summary>
public sealed record PagamentoRequest(string? ChaveIdempotencia, Guid PessoaId, Guid ContaId, DateOnly? DataPagamento, decimal? Valor);

/// <summary>Quanto a pessoa tem a receber agora (itens a pagar menos adiantamentos em aberto), em todos os meses.</summary>
public sealed record PessoaAPagar(Guid PessoaId, string Nome, decimal APagar);

public sealed record PagamentoFeito(decimal Pago, decimal Compensado, decimal Restante, IReadOnlyList<Guid> Liquidados,
    Guid? SaldoLancamentoId, ResumoPessoa Resumo);
