# Roteiro de Teste Manual - Webhook WhatsApp

## Pré-requisitos

1. **Banco de dados configurado** com as tabelas:
   - `conversas` (com coluna `message_id_whatsapp`)
   - `waba_phone` (nova tabela criada)

2. **Aplicação rodando** com as configurações:
   - `appsettings.json` atualizado com seção `WhatsApp`
   - Dependências registradas no `Program.cs`

3. **Ferramenta de teste** (Postman, curl, ou similar)

## Cenários de Teste

### 1. Teste de Webhook Válido

**Objetivo:** Verificar se o webhook processa mensagens válidas corretamente

**Endpoint:** `POST /api/wa/webhook`

**Payload:**
```json
{
  "object": "whatsapp_business_account",
  "entry": [
    {
      "id": "123456789",
      "changes": [
        {
          "value": {
            "messaging_product": "whatsapp",
            "metadata": {
              "display_phone_number": "+5511999999999",
              "phone_number_id": "123456789012345"
            },
            "messages": [
              {
                "id": "wamid.test123",
                "from": "5511999999999",
                "timestamp": "1642694400",
                "type": "text",
                "text": {
                  "body": "Olá, esta é uma mensagem de teste!"
                }
              }
            ]
          },
          "field": "messages"
        }
      ]
    }
  ]
}
```

**Resultado Esperado:**
- Status: `200 OK`
- Log: Mensagem processada com sucesso
- Banco: Nova conversa criada na tabela `conversas`

### 2. Teste de Phone Number ID Não Cadastrado

**Objetivo:** Verificar fallback quando phone_number_id não existe na tabela waba_phone

**Payload:** Mesmo do teste 1, mas com `phone_number_id`: `"999999999999999"`

**Resultado Esperado:**
- Status: `200 OK`
- Log: Usando estabelecimento fallback
- Banco: Conversa criada com `id_estabelecimento` do fallback

### 3. Teste de Payload Inválido

**Objetivo:** Verificar robustez contra payloads malformados

**Payload:**
```json
{
  "invalid": "payload",
  "missing": "required_fields"
}
```

**Resultado Esperado:**
- Status: `200 OK`
- Log: Erro ao processar payload, mas sem crash

### 4. Teste de Múltiplas Mensagens

**Objetivo:** Verificar processamento de múltiplas mensagens no mesmo webhook

**Payload:** Mesmo do teste 1, mas com array `messages` contendo 3 mensagens diferentes

**Resultado Esperado:**
- Status: `200 OK`
- Banco: 3 conversas criadas (ou atualizadas se mesmo remetente)

### 5. Teste de Idempotência

**Objetivo:** Verificar se mensagens duplicadas são tratadas corretamente

**Passos:**
1. Enviar payload do teste 1
2. Enviar exatamente o mesmo payload novamente

**Resultado Esperado:**
- Ambas as requisições: `200 OK`
- Banco: Apenas 1 conversa (não duplicada)
- Log: Segunda tentativa deve indicar mensagem já processada

### 6. Teste de JSON Inválido

**Objetivo:** Verificar tratamento de JSON malformado

**Payload:** `{ invalid json syntax`

**Resultado Esperado:**
- Status: `200 OK`
- Log: Erro de parsing, mas aplicação continua funcionando

## Validações no Banco de Dados

### Verificar Tabela waba_phone
```sql
SELECT * FROM waba_phone WHERE ativo = true;
```

### Verificar Conversas Criadas
```sql
SELECT 
    id_conversa,
    id_estabelecimento,
    message_id_whatsapp,
    data_criacao
FROM conversas 
ORDER BY data_criacao DESC 
LIMIT 10;
```

### Verificar Idempotência
```sql
SELECT 
    message_id_whatsapp,
    COUNT(*) as quantidade
FROM conversas 
WHERE message_id_whatsapp IS NOT NULL
GROUP BY message_id_whatsapp
HAVING COUNT(*) > 1;
```

## Logs a Monitorar

1. **Sucesso:** `"Mensagem processada com sucesso"`
2. **Fallback:** `"Phone number ID não encontrado, usando fallback"`
3. **Erro:** `"Erro ao processar mensagem"`
4. **Idempotência:** `"Mensagem já processada anteriormente"`

## Checklist de Validação

- [ ] Webhook retorna sempre 200 OK
- [ ] Mensagens válidas são processadas corretamente
- [ ] Fallback funciona para phone_number_id não cadastrado
- [ ] Payloads inválidos não quebram a aplicação
- [ ] Múltiplas mensagens são processadas individualmente
- [ ] Idempotência previne duplicação de mensagens
- [ ] Logs são gerados adequadamente
- [ ] Banco de dados é atualizado corretamente
- [ ] Performance é aceitável (< 2s por requisição)

## Troubleshooting

### Erro 500
- Verificar logs da aplicação
- Verificar conexão com banco de dados
- Verificar se migrações foram executadas

### Mensagens não aparecem no banco
- Verificar se `id_estabelecimento` está sendo resolvido
- Verificar se tabela `waba_phone` tem dados
- Verificar configuração de fallback

### Performance lenta
- Verificar índices nas tabelas
- Verificar queries no log
- Monitorar uso de CPU/memória