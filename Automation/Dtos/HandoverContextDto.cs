// ================= ZIPPYGO AUTOMATION SECTION (BEGIN) =================
using System;
using System.Collections.Generic;

namespace APIBack.Automation.Dtos
{
    public class HandoverContextDto
    {
        public string? ClienteNome { get; set; }
        public string? NumeroPessoas { get; set; }
        public string? Dia { get; set; }
        public string? Horario { get; set; }
        public string? Telefone { get; set; }
        public string? Motivo { get; set; }
        public string? QueixaPrincipal { get; set; }
        public string? Contexto { get; set; }
        public IReadOnlyList<string>? Historico { get; set; } = Array.Empty<string>();
    }
}
// ================= ZIPPYGO AUTOMATION SECTION (END) ===================

