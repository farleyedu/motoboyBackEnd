// ================= ZIPPYGO AUTOMATION SECTION (BEGIN) =================
using System.Collections.Generic;
using System.Text.Json.Serialization;

namespace APIBack.Automation.Dtos
{
    // Espelha o formato básico do WhatsApp Cloud API
    public class WebhookPayloadDto
    {
        [JsonPropertyName("object")] public string? Objeto { get; set; }
        [JsonPropertyName("entry")] public List<WebhookEntryDto>? Entradas { get; set; }
    }

    public class WebhookEntryDto
    {
        [JsonPropertyName("changes")] public List<WebhookChangeDto>? Mudancas { get; set; }
        [JsonPropertyName("id")] public string? Id { get; set; }
        [JsonPropertyName("time")] public long? Tempo { get; set; }
    }

    public class WebhookChangeDto
    {
        [JsonPropertyName("field")] public string? Campo { get; set; }
        [JsonPropertyName("value")] public WebhookChangeValueDto? Valor { get; set; }
    }

    public class WebhookChangeValueDto
    {
        [JsonPropertyName("messaging_product")] public string? ProdutoMensagens { get; set; }
        [JsonPropertyName("metadata")] public WebhookMetadataDto? Metadados { get; set; }
        [JsonPropertyName("contacts")] public List<WebhookContactDto>? Contatos { get; set; }
        [JsonPropertyName("messages")] public List<WebhookMessageDto>? Mensagens { get; set; }
    }

    public class WebhookMetadataDto
    {
        [JsonPropertyName("display_phone_number")] public string? NumeroTelefoneExibicao { get; set; }
        [JsonPropertyName("phone_number_id")] public string? IdNumeroTelefone { get; set; }
    }

    public class WebhookContactDto
    {
        [JsonPropertyName("wa_id")] public string? IdWa { get; set; }
        [JsonPropertyName("profile")] public WebhookProfileDto? Perfil { get; set; }
    }

    public class WebhookProfileDto
    {
        [JsonPropertyName("name")] public string? Nome { get; set; }
    }

    public class WebhookMessageDto
    {
        [JsonPropertyName("id")] public string? Id { get; set; }
        [JsonPropertyName("from")] public string? De { get; set; }
        [JsonPropertyName("timestamp")] public string? CarimboTempo { get; set; }
        [JsonPropertyName("type")] public string? Tipo { get; set; }
        [JsonPropertyName("text")] public WebhookTextDto? Texto { get; set; }
    }

    public class WebhookTextDto
    {
        [JsonPropertyName("body")] public string? Corpo { get; set; }
    }
}
// ================= ZIPPYGO AUTOMATION SECTION (END) ===================
