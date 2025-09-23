// ================= EXEMPLO DE USO DO LOGGING PARA DEBUG DA OPENAI =================
// Este arquivo mostra como o novo sistema de logging funciona para debugar
// problemas com o campo handoverAction da OpenAI

using System;
using System.Text.Json;
using APIBack.Automation.Dtos;
using APIBack.Automation.Services;
using Microsoft.Extensions.Logging;

namespace APIBack.Examples
{
    public class ExemploLoggingOpenAI
    {
        private readonly ILogger<ExemploLoggingOpenAI> _logger;
        private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

        public ExemploLoggingOpenAI(ILogger<ExemploLoggingOpenAI> logger)
        {
            _logger = logger;
        }

        public void ExemploDeUso()
        {
            var idConversa = Guid.NewGuid();
            
            // Exemplo 1: JSON com handoverAction correto
            var jsonCorreto = @"{
                ""reply"": ""Sua reserva foi confirmada!"",
                ""handoverAction"": ""confirm"",
                ""agent_prompt"": ""Reserva confirmada para João"",
                ""reserva_confirmada"": true
            }";

            _logger.LogInformation("=== TESTE 1: JSON com handoverAction correto ===");
            TestarDesserializacao(jsonCorreto, idConversa);

            // Exemplo 2: JSON com campo alternativo "handover"
            var jsonAlternativo = @"{
                ""reply"": ""Vou encaminhar para um atendente"",
                ""handover"": ""ask"",
                ""agent_prompt"": ""Cliente precisa de ajuda humana""
            }";

            _logger.LogInformation("=== TESTE 2: JSON com campo alternativo 'handover' ===");
            TestarDesserializacao(jsonAlternativo, idConversa);

            // Exemplo 3: JSON com campo "handover_action"
            var jsonUnderScore = @"{
                ""reply"": ""Processando sua solicitação"",
                ""handover_action"": ""confirm"",
                ""reserva_confirmada"": true
            }";

            _logger.LogInformation("=== TESTE 3: JSON com campo 'handover_action' ===");
            TestarDesserializacao(jsonUnderScore, idConversa);

            // Exemplo 4: JSON sem campos de handover (deve retornar "none")
            var jsonSemHandover = @"{
                ""reply"": ""Como posso ajudar você hoje?""
            }";

            _logger.LogInformation("=== TESTE 4: JSON sem campos de handover ===");
            TestarDesserializacao(jsonSemHandover, idConversa);
        }

        private void TestarDesserializacao(string jsonContent, Guid idConversa)
        {
            // Esta é a chamada que agora inclui logging detalhado
            if (AssistantDecisionParser.TryParse(jsonContent, JsonOptions, out var decision, out var extractedJson, _logger, idConversa))
            {
                _logger.LogInformation("✅ Desserialização bem-sucedida!");
                _logger.LogInformation("   Reply: {Reply}", decision.Reply);
                _logger.LogInformation("   HandoverAction: {HandoverAction}", decision.HandoverAction);
                _logger.LogInformation("   AgentPrompt: {AgentPrompt}", decision.AgentPrompt ?? "NULL");
                _logger.LogInformation("   ReservaConfirmada: {ReservaConfirmada}", decision.ReservaConfirmada);
            }
            else
            {
                _logger.LogWarning("❌ Falha na desserialização");
            }
            
            _logger.LogInformation(""); // Linha em branco para separar os testes
        }
    }
}

/* 
EXEMPLO DE LOGS QUE VOCÊ VERÁ:

[18:13:15 INF] [Conversa=12345678-1234-1234-1234-123456789012] [DEBUG] Conteúdo bruto da OpenAI: {
    "reply": "Sua reserva foi confirmada!",
    "handoverAction": "confirm",
    "agent_prompt": "Reserva confirmada para João",
    "reserva_confirmada": true
}

[18:13:15 INF] [Conversa=12345678-1234-1234-1234-123456789012] [DEBUG] JSON extraído: {
    "reply": "Sua reserva foi confirmada!",
    "handoverAction": "confirm",
    "agent_prompt": "Reserva confirmada para João",
    "reserva_confirmada": true
}

[18:13:15 INF] [Conversa=12345678-1234-1234-1234-123456789012] [DEBUG] Análise de campos handover no DTO:
[18:13:15 INF] [Conversa=12345678-1234-1234-1234-123456789012] [DEBUG] - handover: 'NULL'
[18:13:15 INF] [Conversa=12345678-1234-1234-1234-123456789012] [DEBUG] - handoverAction: 'confirm'
[18:13:15 INF] [Conversa=12345678-1234-1234-1234-123456789012] [DEBUG] - handover_action: 'NULL'

[18:13:15 INF] [Conversa=12345678-1234-1234-1234-123456789012] [DEBUG] Todos os campos do DTO:
[18:13:15 INF] [Conversa=12345678-1234-1234-1234-123456789012] [DEBUG] - reply: 'Sua reserva foi confirmada!' 
[18:13:15 INF] [Conversa=12345678-1234-1234-1234-123456789012] [DEBUG] - handover: 'NULL' (CAMPO HANDOVER)
[18:13:15 INF] [Conversa=12345678-1234-1234-1234-123456789012] [DEBUG] - handoverAction: 'confirm' (CAMPO HANDOVER)
[18:13:15 INF] [Conversa=12345678-1234-1234-1234-123456789012] [DEBUG] - handover_action: 'NULL' (CAMPO HANDOVER)
[18:13:15 INF] [Conversa=12345678-1234-1234-1234-123456789012] [DEBUG] - agent_prompt: 'Reserva confirmada para João' 
[18:13:15 INF] [Conversa=12345678-1234-1234-1234-123456789012] [DEBUG] - reserva_confirmada: 'True' 
[18:13:15 INF] [Conversa=12345678-1234-1234-1234-123456789012] [DEBUG] - detalhes: 'NULL' 

[18:13:15 INF] [Conversa=12345678-1234-1234-1234-123456789012] [DEBUG] HandoverAction final normalizado: 'confirm'

[18:13:15 INF] [Conversa=12345678-1234-1234-1234-123456789012] [DEBUG] AssistantDecision desserializado - Reply: 'Sua reserva foi confirmada!', HandoverAction: 'confirm', AgentPrompt: 'Reserva confirmada para João', ReservaConfirmada: True

*/