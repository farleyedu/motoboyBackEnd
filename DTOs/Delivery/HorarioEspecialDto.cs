using System;

namespace APIBack.DTOs.Delivery
{
    /// <summary>Excecao pontual ao horario semanal (feriado, evento) - Fase 2 de Configuracoes.</summary>
    public sealed class HorarioEspecialDto
    {
        public long Id { get; set; }
        /// <summary>"YYYY-MM-DD".</summary>
        public string Data { get; set; } = string.Empty;
        public bool Fechado { get; set; } = true;
        public string? AbreAs { get; set; }
        public string? FechaAs { get; set; }
        public string? Motivo { get; set; }
    }

    public sealed class SalvarHorarioEspecialRequest
    {
        public string Data { get; set; } = string.Empty;
        public bool Fechado { get; set; } = true;
        public string? AbreAs { get; set; }
        public string? FechaAs { get; set; }
        public string? Motivo { get; set; }
    }
}
