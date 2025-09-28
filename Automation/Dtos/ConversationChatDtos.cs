// ================= ZIPPYGO AUTOMATION SECTION (BEGIN) =================
using System;
using System.Collections.Generic;
using System.Text.Json.Serialization;

namespace APIBack.Automation.Dtos
{
    public class ConversationListItemDto
    {
        public Guid Id { get; set; }
        public Guid IdCliente { get; set; }
        public string? ClienteNome { get; set; }
        public string? ClienteNumero { get; set; }
        public string Estado { get; set; } = string.Empty;
        public int? IdAgenteAtribuido { get; set; }
        public DateTime? DataPrimeiraMensagem { get; set; }
        public DateTime? DataUltimaMensagem { get; set; }
        public DateTime DataCriacao { get; set; }
        public DateTime DataAtualizacao { get; set; }
        public DateTime? DataFechamento { get; set; }
        public string? UltimaMensagemConteudo { get; set; }
        public DateTime? UltimaMensagemData { get; set; }
        public string? UltimaMensagemCriadaPor { get; set; }
    }

    public class ConversationDetailsDto
    {
        public Guid Id { get; set; }
        public Guid IdCliente { get; set; }
        public string? ClienteNome { get; set; }
        public string? ClienteNumero { get; set; }
        public string Estado { get; set; } = string.Empty;
        public int? IdAgenteAtribuido { get; set; }
        public DateTime? DataPrimeiraMensagem { get; set; }
        public DateTime? DataUltimaMensagem { get; set; }
        public DateTime DataCriacao { get; set; }
        public DateTime DataAtualizacao { get; set; }
        public DateTime? DataFechamento { get; set; }
        public int? FechadoPorId { get; set; }
        public string? MotivoFechamento { get; set; }
    }

    public class ConversationMessageItemDto
    {
        public Guid Id { get; set; }
        public string CriadaPor { get; set; } = string.Empty;
        public string Conteudo { get; set; } = string.Empty;
        public DateTime? DataEnvio { get; set; }
        public DateTime DataCriacao { get; set; }
    }

    public class ConversationHistoryDto
    {
        public ConversationDetailsDto Conversa { get; set; } = new ConversationDetailsDto();
        public IReadOnlyList<ConversationMessageItemDto> Mensagens { get; set; } = Array.Empty<ConversationMessageItemDto>();
        public int Page { get; set; }
        public int PageSize { get; set; }
        public int Total { get; set; }
    }
}
// ================= ZIPPYGO AUTOMATION SECTION (END) ===================

