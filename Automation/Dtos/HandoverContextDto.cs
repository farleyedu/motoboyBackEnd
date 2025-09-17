// ================= ZIPPYGO AUTOMATION SECTION (BEGIN) =================
using System.Collections.Generic;

namespace APIBack.Automation.Dtos
{
    public class HandoverContextDto
    {
        public string? ClienteNome { get; set; }
        public string? Dia { get; set; }
        public string? Horario { get; set; }
        public string? Telefone { get; set; }
        public string? QueixaPrincipal { get; set; }
        public string? Contexto { get; set; }
        public IReadOnlyList<string>? Historico { get; set; }
    }
}
// ================= ZIPPYGO AUTOMATION SECTION (END) ===================
