namespace APIBack.Mesas.Sessao;

// Pedidos (CONTRATO-API §0.1 e §3.4). Todo comando leva a "chave" (idempotência). "VersaoMesa" onde a coluna V diz sim.

public sealed record AbrirMesaRequest(Guid? Chave, Guid? MesaId, int? VersaoMesa, int? Pessoas, Guid? GarcomId, string? TipoComanda,
    string? Nome, string? Celular, Guid? ReservaId);
public sealed record IncluirPessoaRequest(Guid? Chave, Guid? MesaId, string? Nome, string? Celular, string? CadeiraId, string? Origem,
    bool? Individual, string? Alergia);
public sealed record SentarRequest(Guid? Chave, Guid? PessoaId, string? CadeiraId);
public sealed record TrocarLugaresRequest(Guid? Chave, Guid? PessoaIdA, Guid? PessoaIdB);
public sealed record MarcarAlergiaRequest(Guid? Chave, Guid? PessoaId, string? Texto);
public sealed record AnotarMesaRequest(Guid? Chave, Guid? MesaId, string? Tipo, string? Texto);
public sealed record GarcomMesaRequest(Guid? Chave, Guid? MesaId, int? VersaoMesa, Guid? GarcomId);
public sealed record TransferirMesaRequest(Guid? Chave, Guid? MesaId, int? VersaoMesa, Guid? ParaMesaId, Guid? ParaGarcomId, string? Motivo);
/// <summary><see cref="Versoes"/> = versão de cada mesa (V em todas). <see cref="VersaoMesa"/> vale quando todas têm a mesma.</summary>
public sealed record JuntarMesasRequest(Guid? Chave, IReadOnlyList<Guid>? MesaIds, IReadOnlyDictionary<Guid, int>? Versoes, int? VersaoMesa);
public sealed record MesaRequest(Guid? Chave, Guid? MesaId, int? VersaoMesa);
/// <summary><see cref="Forcar"/> e motivo: encerrar com saldo (P14 confere o saldo; só dono e gerente).</summary>
public sealed record EncerrarRequest(Guid? Chave, Guid? MesaId, int? VersaoMesa, bool? Forcar, string? Motivo);
public sealed record ReabrirRequest(Guid? Chave, Guid? MesaId, int? VersaoMesa, string? Motivo);
public sealed record ChamarGarcomRequest(Guid? Chave, Guid? MesaId, Guid? PessoaId, string? Tipo, string? Motivo);
public sealed record ChamadoRequest(Guid? Chave, Guid? ChamadoId, Guid? GarcomId);
public sealed record AtenderChamadoRequest(Guid? Chave, Guid? ChamadoId, Guid? MesaId);

// Respostas: sempre eventoId + versoes (mesaId → versão nova).

public sealed record ComandoDto(Guid EventoId, IReadOnlyDictionary<Guid, int> Versoes);
public sealed record MesaAbertaDto(Guid EventoId, IReadOnlyDictionary<Guid, int> Versoes, Guid SessaoId, Guid ComandaColetivaId,
    string TipoComanda, Guid? PessoaId, string QrToken);
public sealed record PessoaIncluidaDto(Guid EventoId, IReadOnlyDictionary<Guid, int> Versoes, Guid SessaoId, Guid PessoaId, Guid? ComandaId,
    bool Retorno);
public sealed record MesaTransferidaDto(Guid EventoId, IReadOnlyDictionary<Guid, int> Versoes, Guid MesaId);
public sealed record MesasJuntadasDto(Guid EventoId, IReadOnlyDictionary<Guid, int> Versoes, Guid SessaoId, Guid MesaPrincipalId);
public sealed record AnotacaoDto(Guid EventoId, IReadOnlyDictionary<Guid, int> Versoes, Guid AnotacaoId);
public sealed record ChamadoCriadoDto(Guid EventoId, IReadOnlyDictionary<Guid, int> Versoes, Guid ChamadoId);
public sealed record ChamadosAtendidosDto(Guid EventoId, IReadOnlyDictionary<Guid, int> Versoes, int Atendidos);

// Leituras (§4.1).

public sealed record MesaDoSalaoDto(
    Guid MesaId, string Numero, string PisoId, string? SetorId, short Capacidade, string Status, int Versao, string? MotivoBloqueio,
    Guid? SessaoId, Guid? JuntaCom, IReadOnlyList<string> Alertas, string? Icone, Guid? GarcomId, string? GarcomNome,
    int QtdPessoas, int Minutos, DateTime? AbertaEm, object? Reserva);

public sealed record ResumoMesaDto(
    Guid MesaId, string Numero, short Capacidade, string PisoId, string? SetorId, string Status, int Versao, string? MotivoBloqueio,
    Guid? SessaoId, SessaoResumoDto? Sessao, IReadOnlyList<MesaDaSessaoDto> Mesas, Guid? JuntaDe, IReadOnlyList<string> Alertas,
    string? Icone, int Minutos, int QtdPessoas, IReadOnlyList<PessoaDto> Pessoas, IReadOnlyList<ComandaDto> Comandas,
    IReadOnlyList<ChamadoDto> Chamados, IReadOnlyList<AnotacaoResumoDto> Anotacoes);

public sealed record SessaoResumoDto(
    Guid Id, string Estado, DateTime AbertaEm, string AbertaPor, string? AbertaPorId, Guid? GarcomId, string? GarcomNome,
    string TipoComanda, int OcupantesAnonimos, DateTime? ContaPedidaEm, DateTime? UltimoPedidoEm, DateTime? EncerradaEm,
    string? EncerradaPor, int ReabertaVezes, Guid? ReservaId, string QrToken);

public sealed record MesaDaSessaoDto(Guid MesaId, string Numero, int Versao, bool Principal);
public sealed record PessoaDto(Guid Id, string? Nome, string? Celular, string? CadeiraId, string Origem, DateTime EntrouEm, string Cor,
    string? Alergia, bool Titular);
public sealed record ComandaDto(Guid Id, string Tipo, Guid? PessoaId, string Nome, int Ordem, bool Aberta, DateTime? FechadaEm);
public sealed record ChamadoDto(Guid Id, Guid? PessoaId, string Tipo, string? Motivo, string Estado, Guid? GarcomId, DateTime CriadoEm,
    DateTime? ACaminhoEm);
public sealed record AnotacaoResumoDto(Guid Id, string Tipo, string Por, string? Texto, DateTime CriadoEm);

public sealed record RelatorioSessaoDto(SessaoResumoDto Sessao, string MesaNumero, IReadOnlyList<PessoaDto> Pessoas,
    IReadOnlyList<ComandaDto> Comandas, IReadOnlyList<EventoAuditoriaDto> Eventos);

// Auditoria.

public sealed record AtorDto(string? Id, string Nome, string Papel);
public sealed record EventoAuditoriaDto(
    Guid Id, DateTime Quando, string Acao, string AcaoRotulo, AtorDto Ator, string? AlvoTipo, Guid? AlvoId, string? AlvoRotulo,
    string? Antes, string? Depois, string? Motivo, Guid? MesaId, string? MesaNumero, Guid? SessaoId, bool Ocorrencia, string Texto);
public sealed record AuditoriaPaginaDto(IReadOnlyList<EventoAuditoriaDto> Itens, string? Cursor);
public sealed record AuditoriaCartaoDto(string Titulo, string Valor, string? Detalhe);
public sealed record FiltroAuditoria(string? Quem, string? Acao, DateTime? De, DateTime? Ate, Guid? MesaId, bool? Ocorrencias, string? Cursor,
    int? Limite);
