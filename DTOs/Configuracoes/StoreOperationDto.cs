using System.ComponentModel.DataAnnotations;

namespace APIBack.DTOs.Configuracoes;

public sealed class StoreOperationDto
{
    public Guid EstabelecimentoId { get; set; }
    public bool AbertoAgora { get; set; }
    public bool AceitaPedidos { get; set; }
    public bool DentroDoHorario { get; set; }
    public string DataLocal { get; set; } = "";
    public string HoraLocal { get; set; } = "";
    public DateTimeOffset AgoraUtc { get; set; }
    public string Timezone { get; set; } = "America/Sao_Paulo";
    public string Origem { get; set; } = "sem_horario";
    public string? AbreAs { get; set; }
    public string? FechaAs { get; set; }
}

public sealed class OpenStoreTodayRequest
{
    // Confere a loja exibida no modal com a loja do token, inclusive durante uma troca de unidade.
    public Guid EstabelecimentoId { get; set; }
    [Required] public string DataLocal { get; set; } = "";
    [Required] public string FechaAs { get; set; } = "";
}
