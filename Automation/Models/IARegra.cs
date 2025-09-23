// ================= ZIPPYGO AUTOMATION SECTION (BEGIN) =================
using System;

namespace APIBack.Automation.Models
{
    public class IARegra
    {
        public Guid Id { get; set; }
        public Guid? IdEstabelecimento { get; set; }
        public string Contexto { get; set; } = string.Empty;
        public bool Ativo { get; set; } = true;
        public DateTime? DataCriacao { get; set; }
        public DateTime? DataAtualizacao { get; set; }
        public string Modulo { get; set; } = "GERAL";
    }
}
// ================= ZIPPYGO AUTOMATION SECTION (END) ===================

