# Refatora��o Automa��o WhatsApp/Telegram

Este documento resume todas as altera��es implementadas para melhorar o backend de automa��o. Cada item descreve o problema original, o que foi feito e onde o c�digo foi modificado.

## 1. Identificador �nico e supress�o de alertas (HandoverService)
- **Arquivos**: Automation/Services/HandoverService.cs, Automation/Services/AlertSenderTelegram.cs
- **Motivo**: alertas sem Conversa={guid} impediam deduplica��o externa e causavam loops.
- **A��es**:
  - Todos os alertas agora terminam com Conversa={guid} em ambos os fluxos (confirm e ask).
  - M�tricas simples (confirmados/ask/suprimidos) e supress�o em janelas curtas via ConcurrentDictionary.
  - Logs padronizados com [Conversa={guid}] e mensagens suprimidas registradas.
  - AlertSenderTelegram faz retry (1s/2s/5s), normaliza n�mero e extrai GUID para logs.

## 2. Ignorar mensagens autom�ticas no webhook
- **Arquivo**: Automation/Services/ConversationProcessor.cs
- **Motivo**: mensagens enviadas pelo sistema voltavam via webhook.
- **A��es**:
  - Sanitiza��o de n�meros (com/sem �+�).
  - Descarte de mensagens cujo remetente coincide com o n�mero do pr�prio bot.
  - Log expl�cito de mensagens ignoradas.

## 3. F�brica de mensagens reutiliz�vel
- **Arquivo**: Automation/Services/MessageFactory.cs
- **Motivo**: cria��o de Message espalhada e inconsistente.
- **A��es**:
  - Novo factory centraliza cria��o de mensagens (entrada/sa�da) com campos padr�o.
  - ConversationService, IAResponseHandler e outros usam o factory.

## 4. Servi�os auxiliares e separa��o de responsabilidades
- **Arquivos**: Automation/Services/WebhookValidatorService.cs, ConversationProcessor.cs, IAResponseHandler.cs, WhatsAppSender.cs, Automation/Controllers/WaWebhookController.cs
- **Motivo**: controller concentrava valida��o, grava��o, decis�o e envio.
- **A��es**:
  - WebhookValidatorService: leitura do corpo + valida��o assinatura.
  - ConversationProcessor: garante cliente/conversa, grava entrada e retorna contexto/hist�rico.
  - IAResponseHandler: executa fluxos Confirm/Ask/Normal, reutiliza factory e WhatsAppSender.
  - WhatsAppSender: encapsula envio ao WhatsApp com retry/backoff e logs.
  - Controller agora orquestra apenas (coordena os servi�os, mede lat�ncia da IA e exp�e endpoint de token).

## 5. Decis�o da IA unificada
- **Arquivos**: Automation/Dtos/AssistantDecision.cs, AssistantDecisionDto.cs, Automation/Interfaces/IAssistantService.cs, Automation/Services/AssistantService*.cs, Automation/Services/OpenAIAssistantService.cs
- **Motivo**: respostas diferentes (string vs JSON) exigiam parse manual no controller.
- **A��es**:
  - Novo record AssistantDecision (Reply, HandoverAction, AgentPrompt, ReservaConfirmada, Detalhes).
  - Servi�os de IA sempre retornam AssistantDecision, interpretando JSON internamente.
  - Controller apenas recebe a decis�o estruturada.

## 6. Resili�ncia e logs padronizados
- **Arquivos**: Automation/Services/AlertSenderTelegram.cs, Automation/Services/WhatsAppSender.cs, v�rios logs ajustados.
- **Motivo**: falta de retry/backoff e logs sem identificador dificultavam suporte.
- **A��es**:
  - Retry com backoff exponencial (1s/2s/5s) em Telegram e WhatsApp.
  - Logs sempre incluem [Conversa={guid}] ou extraem Conversa= do texto.
  - M�tricas simples registradas no HandoverService.

## 7. Ajustes auxiliares
- Automation/Dtos/HandoverContextDto.cs: garante hist�rico inicial vazio e mant�m substitui��o por �N�o informado�.
- Automation/Services/ConversationProcessor.cs: adiciona contexto ao DTO para handover.
- Automation/Interfaces/IWhatsAppTokenProvider.cs & InMemoryWhatsAppTokenProvider.cs: suporte a SetAccessToken e LastUpdatedUtc.
- Program.cs: registra novos servi�os no cont�iner de DI.
- Automation/Controllers/WaWebhookController.cs: calcula lat�ncia da IA, atualiza token e coordena m�dulos.
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
- dotnet build executado (apenas warnings pr�-existentes de nulabilidade/entry point).
- Sem testes automatizados adicionados; recomenda-se testar manualmente cen�rios Confirm/Ask e verificar logs.

---
Essas mudan�as tornam o fluxo mais resiliente, desacoplado e rastre�vel, enquanto mant�m as funcionalidades existentes.
