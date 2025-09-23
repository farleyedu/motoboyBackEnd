// ================= ZIPPYGO AUTOMATION SECTION (BEGIN) =================
using System;
using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace APIBack.Automation.Models
{
    /// <summary>
    /// Representa o mapeamento entre phone_number_id do WhatsApp Business API e estabelecimentos
    /// </summary>
    [Table("waba_phone")]
    public class WabaPhone
    {
        [Key]
        [Column("id")]
        public Guid Id { get; set; }

        [Required]
        [Column("phone_number_id")]
        [MaxLength(255)]
        public string PhoneNumberId { get; set; } = string.Empty;

        [Required]
        [Column("id_estabelecimento")]
        public Guid IdEstabelecimento { get; set; }

        [Column("ativo")]
        public bool Ativo { get; set; } = true;

        [Column("descricao")]
        [MaxLength(500)]
        public string? Descricao { get; set; }

        [Column("data_criacao")]
        public DateTime DataCriacao { get; set; } = DateTime.UtcNow;

        [Column("data_atualizacao")]
        public DateTime DataAtualizacao { get; set; } = DateTime.UtcNow;
    }
}
// ================= ZIPPYGO AUTOMATION SECTION (END) ===================
