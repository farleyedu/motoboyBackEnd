# Refatoração Automação WhatsApp/Telegram

Este documento resume todas as alterações implementadas para melhorar o backend de automação. Cada item descreve o problema original, o que foi feito e onde o código foi modificado.

## 1. Identificador único e supressão de alertas (HandoverService)
- **Arquivos**: Automation/Services/HandoverService.cs, Automation/Services/AlertSenderTelegram.cs
- **Motivo**: alertas sem Conversa={guid} impediam deduplicação externa e causavam loops.
- **Ações**:
  - Todos os alertas agora terminam com Conversa={guid} em ambos os fluxos (confirm e ask).
  - Métricas simples (confirmados/ask/suprimidos) e supressão em janelas curtas via ConcurrentDictionary.
  - Logs padronizados com [Conversa={guid}] e mensagens suprimidas registradas.
  - AlertSenderTelegram faz retry (1s/2s/5s), normaliza número e extrai GUID para logs.

## 2. Ignorar mensagens automáticas no webhook
- **Arquivo**: Automation/Services/ConversationProcessor.cs
- **Motivo**: mensagens enviadas pelo sistema voltavam via webhook.
- **Ações**:
  - Sanitização de números (com/sem “+”).
  - Descarte de mensagens cujo remetente coincide com o número do próprio bot.
  - Log explícito de mensagens ignoradas.

## 3. Fábrica de mensagens reutilizável
- **Arquivo**: Automation/Services/MessageFactory.cs
- **Motivo**: criação de Message espalhada e inconsistente.
- **Ações**:
  - Novo factory centraliza criação de mensagens (entrada/saída) com campos padrão.
  - ConversationService, IAResponseHandler e outros usam o factory.

## 4. Serviços auxiliares e separação de responsabilidades
- **Arquivos**: Automation/Services/WebhookValidatorService.cs, ConversationProcessor.cs, IAResponseHandler.cs, WhatsAppSender.cs, Automation/Controllers/WaWebhookController.cs
- **Motivo**: controller concentrava validação, gravação, decisão e envio.
- **Ações**:
  - WebhookValidatorService: leitura do corpo + validação assinatura.
  - ConversationProcessor: garante cliente/conversa, grava entrada e retorna contexto/histórico.
  - IAResponseHandler: executa fluxos Confirm/Ask/Normal, reutiliza factory e WhatsAppSender.
  - WhatsAppSender: encapsula envio ao WhatsApp com retry/backoff e logs.
  - Controller agora orquestra apenas (coordena os serviços, mede latência da IA e expõe endpoint de token).

## 5. Decisão da IA unificada
- **Arquivos**: Automation/Dtos/AssistantDecision.cs, AssistantDecisionDto.cs, Automation/Interfaces/IAssistantService.cs, Automation/Services/AssistantService*.cs, Automation/Services/OpenAIAssistantService.cs
- **Motivo**: respostas diferentes (string vs JSON) exigiam parse manual no controller.
- **Ações**:
  - Novo record AssistantDecision (Reply, HandoverAction, AgentPrompt, ReservaConfirmada, Detalhes).
  - Serviços de IA sempre retornam AssistantDecision, interpretando JSON internamente.
  - Controller apenas recebe a decisão estruturada.

## 6. Resiliência e logs padronizados
- **Arquivos**: Automation/Services/AlertSenderTelegram.cs, Automation/Services/WhatsAppSender.cs, vários logs ajustados.
- **Motivo**: falta de retry/backoff e logs sem identificador dificultavam suporte.
- **Ações**:
  - Retry com backoff exponencial (1s/2s/5s) em Telegram e WhatsApp.
  - Logs sempre incluem [Conversa={guid}] ou extraem Conversa= do texto.
  - Métricas simples registradas no HandoverService.

## 7. Ajustes auxiliares
- Automation/Dtos/HandoverContextDto.cs: garante histórico inicial vazio e mantém substituição por “Não informado”.
- Automation/Services/ConversationProcessor.cs: adiciona contexto ao DTO para handover.
- Automation/Interfaces/IWhatsAppTokenProvider.cs & InMemoryWhatsAppTokenProvider.cs: suporte a SetAccessToken e LastUpdatedUtc.
- Program.cs: registra novos serviços no contêiner de DI.
- Automation/Controllers/WaWebhookController.cs: calcula latência da IA, atualiza token e coordena módulos.
- Automation/Dtos/UpdateWhatsAppTokenRequest.cs: recriado para endpoint /wa/token.

## 8. Arquivos novos
- Automation/Dtos/AssistantDecision.cs
- Automation/Dtos/UpdateWhatsAppTokenRequest.cs
- Automation/Services/ConversationProcessingInput.cs
- Automation/Services/ConversationProcessingResult.cs
- Automation/Services/ConversationProcessor.cs
- Automation/Services/IAResponseHandler.cs
- Automation/Services/MessageFactory.cs
- Automation/Services/WebhookValidatorService.cs
- Automation/Services/WhatsAppSender.cs

## 9. Build e testes
- dotnet build executado (apenas warnings pré-existentes de nulabilidade/entry point).
- Sem testes automatizados adicionados; recomenda-se testar manualmente cenários Confirm/Ask e verificar logs.

---
Essas mudanças tornam o fluxo mais resiliente, desacoplado e rastreável, enquanto mantêm as funcionalidades existentes.
