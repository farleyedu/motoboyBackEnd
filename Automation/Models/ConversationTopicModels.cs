using System;

namespace APIBack.Automation.Models
{
    public static class ConversationTopicContextKeys
    {
        public const string Active = "topic_active";
        public const string Queue = "topic_queue";
        public const string Candidate = "topic_candidate";
        public const string SwitchPrompt = "topic_switch_prompt";
    }

    public static class ConversationTopicModules
    {
        public const string Servicos = "servicos";
        public const string Faq = "faq";
    }

    public static class ConversationTopicRuntimeStatus
    {
        public const string Ativo = "ativo";
        public const string Pausado = "pausado";
        public const string AguardandoCliente = "aguardando_cliente";
        public const string AguardandoInterno = "aguardando_interno";
        public const string Concluido = "concluido";
        public const string Descartado = "descartado";
    }

    public sealed class ConversationTopicState
    {
        public string TopicId { get; set; } = Guid.NewGuid().ToString("N");
        public string Module { get; set; } = string.Empty;
        public string Label { get; set; } = string.Empty;
        public string RuntimeStatus { get; set; } = ConversationTopicRuntimeStatus.Ativo;
        public string? Summary { get; set; }
        public string? SnapshotJson { get; set; }
        public string? PendingMessage { get; set; }
        public int MatchScore { get; set; }
        public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
        public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;
    }

    public sealed class ConversationTopicSwitchPrompt
    {
        public string ActiveTopicId { get; set; } = string.Empty;
        public string CandidateTopicId { get; set; } = string.Empty;
        public string PromptText { get; set; } = string.Empty;
    }

    public sealed class ServiceTopicSnapshot
    {
        public string? Estado { get; set; }
        public System.Collections.Generic.Dictionary<string, object?> DadosColetados { get; set; } =
            new(StringComparer.OrdinalIgnoreCase);
    }

    public sealed class FaqTopicSnapshot
    {
        public Guid FaqId { get; set; }
        public string Pergunta { get; set; } = string.Empty;
        public string Resposta { get; set; } = string.Empty;
        public string Categoria { get; set; } = string.Empty;
        public string MensagemOrigem { get; set; } = string.Empty;
    }
}
