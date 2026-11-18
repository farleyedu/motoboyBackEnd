using System.Text.Json.Nodes;

namespace APIBack.Mesas.Planta;

/// <summary>Uma versão da planta. <see cref="VersaoRascunho"/> só no rascunho: é o número que o editor manda de volta para salvar e publicar.</summary>
public sealed record PlantaDto(
    Guid Id, int Versao, int? VersaoRascunho, string Estado, JsonObject Documento,
    string? SalvoPorNome, DateTime AtualizadoEm, DateTime? PublicadoEm, string? PublicadoPorNome);

public sealed record PlantaVersaoDto(
    int Versao, string Estado, int Revisao, int Andares, int Mesas,
    string? SalvoPorNome, DateTime AtualizadoEm, DateTime? PublicadoEm, string? PublicadoPorNome);

public sealed record SalvarRascunhoPlantaRequest(Guid? Chave, JsonNode? Documento, int? VersaoRascunho);
public sealed record PublicarPlantaRequest(Guid? Chave, int? VersaoRascunho);
public sealed record DescartarRascunhoPlantaRequest(Guid? Chave, int? VersaoRascunho);
public sealed record MesaBloqueioRequest(Guid? Chave, Guid? MesaId, int? VersaoMesa, string? Motivo);

public sealed record RascunhoSalvoDto(Guid EventoId, int VersaoRascunho, int VersaoPlanta, DateTime SalvoEm);
public sealed record PlantaPublicadaDto(Guid EventoId, int VersaoPlanta, int MesasCriadas, int MesasDesativadas, int MesasRenumeradas);
public sealed record RascunhoDescartadoDto(Guid EventoId, int VersaoPlanta);
public sealed record MesaComandoDto(Guid EventoId, IReadOnlyDictionary<Guid, int> Versoes);

/// <summary>Leitura enxuta para o app do garçom: a planta publicada (sem a foto de fundo) e as mesas ativas com o estado.</summary>
public sealed record PlantaAppDto(int VersaoPlanta, DateTime PublicadoEm, JsonArray Pisos, IReadOnlyList<PlantaAppMesaDto> Mesas);

public sealed record PlantaAppMesaDto(
    Guid MesaId, string Chave, string Numero, string PisoId, string? SetorId, short Capacidade, string Forma, bool Reservavel,
    string Status, int Versao, string? MotivoBloqueio, JsonObject? Desenho);
