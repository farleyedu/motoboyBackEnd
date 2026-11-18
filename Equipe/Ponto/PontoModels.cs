namespace APIBack.Equipe.Ponto;

/// <summary>Erro do ponto, com o código do envelope do contrato (§0.1): VALIDACAO, NAO_ENCONTRADO, SEM_PERMISSAO, JA_FEITO, ESTADO_INVALIDO.</summary>
public sealed class PontoException(string codigo, string mensagem, int status) : Exception(mensagem)
{
    public string Codigo { get; } = codigo;
    public int Status { get; } = status;
    public object? Detalhe { get; init; }

    public static PontoException Validacao(string m) => new("VALIDACAO", m, 400);
    public static PontoException NaoEncontrado(string m) => new("NAO_ENCONTRADO", m, 404);
    public static PontoException SemPermissao(string m = "Só o gerente mexe no ponto de outra pessoa.") => new("SEM_PERMISSAO", m, 403);
    public static PontoException EstadoInvalido(string m) => new("ESTADO_INVALIDO", m, 409);
}

/// <summary>Quem está chamando. <c>PessoaId</c> = cadastro da Equipe ligado ao usuário nesta loja (pode não existir para o gerente).</summary>
public sealed record PontoAtor(Guid Loja, int Usuario, Guid? PessoaId, bool Gestor);

public sealed record PontoEntrarRequest(Guid? PessoaId = null, string? Modo = null, Guid? Chave = null);
public sealed record PontoPausarRequest(Guid? PessoaId = null, string? Motivo = null, Guid? Chave = null);
public sealed record PontoVoltarRequest(Guid? PessoaId = null, Guid? Chave = null);
public sealed record PontoSairRequest(Guid? PessoaId = null, string? Motivo = null, Guid? Chave = null);

public sealed record PausaDto(Guid Id, DateTimeOffset Inicio, DateTimeOffset? Fim, string? Motivo, int Minutos);

/// <summary>
/// Um ponto (entrada → saída) com as pausas. <c>Estado</c>: ativo, pausa ou encerrado. <c>EntradaHora</c>/<c>SaidaHora</c> em HH:MM
/// no fuso da loja. <c>TurnoSituacao</c>: o que o fechamento fez em <c>gestao_turnos</c> (preenchido, mantido, nao_aplicado).
/// </summary>
public sealed record PontoDto(Guid Id, Guid PessoaId, string PessoaNome, DateOnly Dia, string Modo, string Estado,
    DateTimeOffset Entrada, string EntradaHora, DateTimeOffset? Saida, string? SaidaHora, string? FechadoPor, string? MotivoSaida,
    int MinutosTrabalhados, int MinutosPausa, IReadOnlyList<PausaDto> Pausas, string? TurnoSituacao, string? TurnoAviso);

/// <summary>Ponto de um dia: os pontos daquela pessoa (pode bater mais de uma vez) e o que está aberto agora.</summary>
public sealed record PontoDiaDto(Guid PessoaId, DateOnly Dia, string Estado, PontoDto? Aberto, IReadOnlyList<PontoDto> Pontos,
    int MinutosTrabalhados, int MinutosPausa);

public sealed record PontoEntrouDto(Guid PontoId, DateTimeOffset Entrada, PontoDto Ponto);
public sealed record PontoPausaFeitaDto(Guid PausaId, PontoDto Ponto);
public sealed record PontoResumoDto(int Mesas, int Minutos);
public sealed record PontoSaiuDto(PontoResumoDto Resumo, PontoDto Ponto);

/// <summary>Turno gravado da pessoa no dia, só o que o ponto precisa para decidir se pode escrever.</summary>
public sealed record TurnoAtual(string Status, string Entrada, string Saida, bool NoLivro);

/// <summary>Ponto anterior já fechado no mesmo dia, com o horário que ele escreveu no turno.</summary>
public sealed record PontoAnteriorNoDia(string? TurnoSituacao, string? TurnoEntrada, string? TurnoSaida);

public enum AcaoTurno { Preencher, Manter, NaoAplicar }

public sealed record DecisaoTurno(AcaoTurno Acao, string? Entrada = null, string? Saida = null, string? Motivo = null, string? Aviso = null);
