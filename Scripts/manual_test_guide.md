# Guia de Teste Manual - Webhook WhatsApp

## Pré-requisitos

1. **Banco de dados configurado** com a tabela `waba_phone`
2. **Aplicação rodando** na porta configurada
3. **Ngrok ou túnel público** para expor o webhook
4. **Conta Meta Developer** configurada

## 1. Configuração Inicial

### 1.1 Executar Migration
```sql
-- Execute o script de migração
\i Scripts/001_create_waba_phone_table.sql
```

### 1.2 Inserir Dados de Teste
```sql
-- Inserir mapeamento de teste
INSERT INTO waba_phone (phone_number_id, id_estabelecimento, descricao) 
VALUES 
    ('SEU_PHONE_NUMBER_ID_AQUI', 'SEU_ESTABELECIMENTO_ID_AQUI', 'Teste Manual');
```

### 1.3 Configurar Webhook no Meta
- URL: `https://seu-dominio.ngrok.io/wa/webhook`
- Verify Token: `zippygo123`
- Campos: `messages`

## 2. Testes de Verificação

### 2.1 Teste de Verificação do Webhook
```bash
# Simular verificação do Meta
curl -X GET "http://localhost:5000/wa/webhook?hub.mode=subscribe&hub.verify_token=zippygo123&hub.challenge=CHALLENGE_STRING"

# Resultado esperado: retorna CHALLENGE_STRING
```

### 2.2 Teste de Token Inválido
```bash
# Testar com token inválido
curl -X GET "http://localhost:5000/wa/webhook?hub.mode=subscribe&hub.verify_token=token_errado&hub.challenge=CHALLENGE_STRING"

# Resultado esperado: HTTP 403 Forbidden
```

## 3. Testes de Mensagens

### 3.1 Payload de Teste Básico
```bash
curl -X POST http://localhost:5000/wa/webhook \
  -H "Content-Type: application/json" \
  -H "X-Hub-Signature-256: sha256=SIGNATURE_AQUI" \
  -d '{
    "object": "whatsapp_business_account",
    "entry": [{
      "id": "ENTRY_ID",
      "changes": [{
        "value": {
          "messaging_product": "whatsapp",
          "metadata": {
            "display_phone_number": "15551234567",
            "phone_number_id": "SEU_PHONE_NUMBER_ID_AQUI"
          },
          "messages": [{
            "from": "5511999999999",
            "id": "wamid.test123",
            "timestamp": "1640995200",
            "text": {
              "body": "Olá, preciso de ajuda!"
            },
            "type": "text"
          }]
        },
        "field": "messages"
      }]
    }]
  }'
```

### 3.2 Teste de Handover
```bash
# Mensagem que deve acionar handover
curl -X POST http://localhost:5000/wa/webhook \
  -H "Content-Type: application/json" \
  -d '{
    "object": "whatsapp_business_account",
    "entry": [{
      "id": "ENTRY_ID",
      "changes": [{
        "value": {
          "messaging_product": "whatsapp",
          "metadata": {
            "phone_number_id": "SEU_PHONE_NUMBER_ID_AQUI"
          },
          "messages": [{
            "from": "5511999999999",
            "id": "wamid.handover123",
            "timestamp": "1640995200",
            "text": {
              "body": "Quero falar com um atendente humano"
            },
            "type": "text"
          }]
        },
        "field": "messages"
      }]
    }]
  }'
```

## 4. Verificações no Banco

### 4.1 Verificar Conversas Criadas
```sql
-- Verificar se conversas foram criadas
SELECT 
    id_conversa,
    wa_id,
    id_estabelecimento,
    data_criacao
FROM conversas 
ORDER BY data_criacao DESC 
LIMIT 10;
```

### 4.2 Verificar Mensagens
```sql
-- Verificar mensagens processadas
SELECT 
    c.wa_id,
    m.id_mensagem_wa,
    m.conteudo,
    m.data_criacao
FROM conversas c
JOIN mensagens m ON c.id_conversa = m.id_conversa
ORDER BY m.data_criacao DESC
LIMIT 10;
```

## 5. Testes de Resiliência

### 5.1 Payload Malformado
```bash
# Testar com JSON inválido
curl -X POST http://localhost:5000/wa/webhook \
  -H "Content-Type: application/json" \
  -d '{"invalid": json}'

# Resultado esperado: HTTP 200 (não deve quebrar)
```

### 5.2 Phone Number ID Inexistente
```bash
# Testar com phone_number_id não mapeado
curl -X POST http://localhost:5000/wa/webhook \
  -H "Content-Type: application/json" \
  -d '{
    "entry": [{
      "changes": [{
        "value": {
          "metadata": {
            "phone_number_id": "PHONE_ID_INEXISTENTE"
          },
          "messages": [{
            "from": "5511999999999",
            "id": "wamid.fallback123",
            "text": {"body": "Teste fallback"},
            "type": "text"
          }]
        },
        "field": "messages"
      }]
    }]
  }'

# Deve usar FallbackEstabelecimentoId
```

## 6. Monitoramento

### 6.1 Logs da Aplicação
```bash
# Monitorar logs em tempo real
tail -f logs/app.log

# Filtrar apenas logs do webhook
grep "Webhook" logs/app.log
```

### 6.2 Métricas de Performance
```sql
-- Contar mensagens por hora
SELECT 
    DATE_TRUNC('hour', data_criacao) as hora,
    COUNT(*) as total_mensagens
FROM mensagens 
WHERE data_criacao >= NOW() - INTERVAL '24 hours'
GROUP BY hora
ORDER BY hora;
```

## 7. Troubleshooting

### Problemas Comuns:

1. **Webhook não recebe verificação**
   - Verificar URL pública
   - Confirmar verify_token
   - Checar logs de rede

2. **Mensagens não são processadas**
   - Verificar mapeamento phone_number_id
   - Confirmar estrutura do payload
   - Checar logs de erro

3. **Banco de dados não atualiza**
   - Verificar connection string
   - Confirmar permissões
   - Checar timeouts

### Comandos Úteis:
```bash
# Verificar status da aplicação
curl http://localhost:5000/health

# Testar conectividade do banco
psql -h HOST -p PORT -U USER -d DATABASE -c "SELECT 1;"

# Verificar logs de erro
grep -i "error\|exception" logs/app.log
```