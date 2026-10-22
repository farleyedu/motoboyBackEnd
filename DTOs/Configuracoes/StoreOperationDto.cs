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
    /// <summary>Por que a loja esta fechada: SEM_HORARIO, FORA_HORARIO ou PAUSADA. Nulo quando esta aberta.</summary>
    public string? MotivoFechada { get; set; }
    public string? ComoResolver { get; set; }
    public APIBack.Service.CorrecaoDto? Correcao { get; set; }
    /// <summary>Hoje as vezes: "hoje às 18:00", "amanhã às 11:00". Nulo sem nenhum dia aberto.</summary>
    public string? ProximaAbertura { get; set; }
    /// <summary>Fechada e a loja pode abrir manualmente agora (situacao teste/ativo e prontidao obrigatoria ok).</summary>
    public bool PodeAbrir { get; set; }
    public bool PodeFechar { get; set; }
}

public sealed class OpenStoreTodayRequest
{
    // Confere a loja exibida no modal com a loja do token, inclusive durante uma troca de unidade.
    public Guid EstabelecimentoId { get; set; }
    [Required] public string DataLocal { get; set; } = "";
    [Required] public string FechaAs { get; set; } = "";
}
