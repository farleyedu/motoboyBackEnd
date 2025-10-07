// ================= ZIPPYGO AUTOMATION SECTION (BEGIN) =================
using System;

namespace APIBack.Automation.Models
{
    public class Cliente
    {
        public Guid Id { get; set; }
        public Guid IdEstabelecimento { get; set; }
        public string? Nome { get; set; }
        public string? TelefoneE164 { get; set; }
        public DateTime DataCriacao { get; set; }
        public DateTime DataAtualizacao { get; set; }
    }
}
// ================= ZIPPYGO AUTOMATION SECTION (END) ===================
