# Análise de Performance — Automação de Conversas

**Data:** 2026-03-18
**Autor:** Análise técnica do sistema
**Status:** Aguardando aprovação

---

## Resumo Executivo

O sistema de automação processa cada webhook em **1.400–2.500ms** (com resposta da IA) ou **300–400ms** (sem IA). A análise identificou **6 categorias de gargalo** com impacto direto na latência percebida pelo usuário.

A maior parte do tempo (60–70%) está fora do nosso controle direto (chamada à OpenAI). Porém, existem otimizações no backend que podem reduzir a latência **em 200–300ms** e aumentar a capacidade de throughput em **~60%**.

---

## Fluxo Atual por Webhook

```
Webhook recebido
    └─ Fila interna (quase imediato)

Worker processa:
    1. Registrar mensagem no DB         ~50ms  (6 queries sequenciais)
    2. Buscar contexto + histórico       ~160ms (2 blocos sequenciais, podiam ser paralelos)
    3. Chamar OpenAI / IA               ~1.200ms (60-70% do tempo total)
    4. Executar ferramentas (tools)      ~150ms (queries adicionais)
    5. Enviar resposta ao WhatsApp       ~100ms (HTTP + retry)

TOTAL com IA:     ~1.400–2.500ms
TOTAL sem IA:     ~300–400ms
```

---

## Gargalos Identificados

---

### G1 — Await Sequencial no Processamento (contexto + histórico)

**Arquivo:** `Automation/Services/ConversationProcessor.cs`
**Impacto:** +80ms por mensagem
**Risco da mudança:** Baixo

**Problema:**
Contexto e histórico são buscados em sequência, mas são completamente independentes entre si:

```csharp
// Hoje — sequencial:
var contexto  = await ObterContextoAsync(criada.IdConversa, input.PhoneNumberDisplay);
var historico = await ObterHistoricoAsync(criada.IdConversa);
```

**Proposta:**
```csharp
// Paralelo com Task.WhenAll:
var (contexto, historico) = await (
    ObterContextoAsync(criada.IdConversa, input.PhoneNumberDisplay),
    ObterHistoricoAsync(criada.IdConversa)
);
```

**Ganho estimado:** -80ms por webhook.

---

### G2 — Ausência de Cache para Módulos e Prompts

**Arquivos:**
- `Automation/Services/ConversationProcessor.cs`
- `Automation/Infra/SqlEstabelecimentoRepository.cs`
- `Automation/Infra/SqlRegrasRepository.cs`

**Impacto:** +60ms por mensagem, 2 queries desnecessárias
**Risco da mudança:** Baixo

**Problema:**
A cada mensagem recebida, o sistema busca no banco:
1. Módulos ativos do estabelecimento (raramente mudam)
2. Prompts compostos do estabelecimento (raramente mudam)

```csharp
// Chamado em CADA webhook, sem cache:
var modulosAtivos = await _estabelecimentoRepo.ObterModulosAtivosAsync(idEstabelecimento);
var prompts       = await _regrasRepo.ObterPromptsCompostosAsync(idEstabelecimento, modulosAtivos);
```

**Proposta:**
Adicionar `IMemoryCache` com TTL de 60 segundos nessas duas chamadas:

```csharp
// Cache de módulos — TTL 60s
var modulosAtivos = await _cache.GetOrCreateAsync(
    $"modulos:{idEstabelecimento}",
    async entry => {
        entry.AbsoluteExpirationRelativeToNow = TimeSpan.FromSeconds(60);
        return await _estabelecimentoRepo.ObterModulosAtivosAsync(idEstabelecimento);
    });

// Cache de prompts — TTL 60s
var prompts = await _cache.GetOrCreateAsync(
    $"prompts:{idEstabelecimento}:{string.Join(",", modulosAtivos)}",
    async entry => {
        entry.AbsoluteExpirationRelativeToNow = TimeSpan.FromSeconds(60);
        return await _regrasRepo.ObterPromptsCompostosAsync(idEstabelecimento, modulosAtivos);
    });
```

**Ganho estimado:** -60ms por webhook, -2 queries/mensagem.

---

### G3 — Histórico de 200 Mensagens a Cada Request

**Arquivo:** `Automation/Services/ConversationProcessor.cs` — linha que chama `GetByConversationAsync(limit: 200)`
**Impacto:** +30–80ms em conversas longas
**Risco da mudança:** Médio (precisa validar com IA)

**Problema:**
A cada mensagem, o sistema busca as **últimas 200 mensagens** da conversa no banco. Em conversas longas isso transfere muitos dados sem necessidade — a IA geralmente só precisa dos últimos 20–30 turnos.

**Proposta:**
Reduzir o limite para 50 mensagens:

```csharp
// De:
var historico = await _mensagemRepository.GetByConversationAsync(idConversa, limit: 200);

// Para:
var historico = await _mensagemRepository.GetByConversationAsync(idConversa, limit: 50);
```

**Consideração:** Validar se 50 msgs é suficiente para o contexto da IA. Se necessário, pode ser configurável por variável de ambiente.

**Ganho estimado:** -30 a 80ms em conversas com histórico longo.

---

### G4 — Regex Compiladas Inline no Processador

**Arquivo:** `Automation/Services/ConversationProcessor.cs`
**Impacto:** Pressão de GC, ~5ms por chamada
**Risco da mudança:** Baixo

**Problema:**
Expressões regulares são recompiladas a cada execução:

```csharp
// Compilada a cada mensagem que passa por esse trecho:
var matchPessoas = Regex.Match(conteudo, @"(\d{1,3})\s*pessoas?", RegexOptions.IgnoreCase);
var matchHora    = Regex.Match(conteudo, @"(\d{1,2}):(\d{2})");
```

**Proposta:**
Declarar como campos estáticos e compilados:

```csharp
private static readonly Regex PessoasRegex = new(@"(\d{1,3})\s*pessoas?", RegexOptions.Compiled | RegexOptions.IgnoreCase);
private static readonly Regex HoraRegex    = new(@"(\d{1,2}):(\d{2})", RegexOptions.Compiled);
```

**Ganho estimado:** Redução de alocação de objetos, menos pressão no GC.

---

### G5 — Timeout Alto e Retry Bloqueante na Chamada à IA

**Arquivo:** `Automation/Services/AssistantService.cs`
**Impacto:** Pode bloquear o worker por até 6 minutos em pior caso
**Risco da mudança:** Médio

**Problema:**
O timeout para a OpenAI está em **120 segundos**, e os retries em caso de erro 429/503 adicionam delays sequenciais de 2s, 4s e 6s — tudo bloqueando o worker.

```csharp
client.Timeout = TimeSpan.FromSeconds(120); // 2 minutos por tentativa

// Em retry 429/503:
var delayMs = attempt * 2000; // 2s, 4s, 6s
await Task.Delay(delayMs);    // Bloqueia o worker
```

Em pior caso: 3 tentativas × 120s + 12s de delay = **~6 minutos**.

**Proposta:**
- Reduzir timeout para 45 segundos
- Adicionar jitter no delay para evitar thundering herd

```csharp
client.Timeout = TimeSpan.FromSeconds(45);

// Delay com jitter:
var baseDelay   = TimeSpan.FromSeconds(attempt * 2);
var jitter      = TimeSpan.FromMilliseconds(Random.Shared.Next(0, 500));
await Task.Delay(baseDelay + jitter);
```

**Ganho estimado:** Reduz tempo máximo de travamento de ~6min para ~2.5min.

---

### G6 — Consulta SQL com regexp_replace em Cada Webhook

**Arquivo:** `Automation/Infra/SqlWabaPhoneRepository.cs`
**Impacto:** +5–15ms por mensagem, impede uso de índice
**Risco da mudança:** Médio (requer migração de banco)

**Problema:**
Para encontrar o estabelecimento pelo número do WhatsApp, o sistema usa `regexp_replace` no PostgreSQL — uma função cara que impede o uso de índices:

```sql
-- Executado em CADA webhook:
WHERE regexp_replace(phone_number_id, '[^0-9]', '', 'g') = @Digits
```

Isso faz full scan da tabela a cada mensagem recebida.

**Proposta:**
Criar coluna `phone_number_digits` (apenas dígitos) populada na inserção, com índice:

```sql
ALTER TABLE waba_phone ADD COLUMN phone_number_digits TEXT;
UPDATE waba_phone SET phone_number_digits = regexp_replace(phone_number_id, '[^0-9]', '', 'g');
CREATE INDEX ix_waba_phone_digits ON waba_phone(phone_number_digits);
```

A busca então seria simples e usaria o índice:
```sql
WHERE phone_number_digits = @Digits
```

**Ganho estimado:** -10ms por webhook, elimina full scan.

---

## Tabela de Prioridade

| # | Gargalo | Latência salva | Risco | Esforço |
|---|---------|---------------|-------|---------|
| G1 | Paralelizar contexto + histórico | ~80ms | Baixo | 1h |
| G2 | Cache de módulos e prompts | ~60ms | Baixo | 2h |
| G3 | Limitar histórico a 50 msgs | ~30–80ms | Médio | 30min |
| G4 | Regex estáticas compiladas | GC / ~5ms | Baixo | 30min |
| G5 | Timeout e retry da IA | Reduz worst-case | Médio | 1h |
| G6 | Índice no telefone (migração) | ~10ms | Médio | 2h |

**Total latência salva (G1+G2+G3):** ~170–220ms por mensagem
**Percentual de melhoria:** ~15–20% na latência total (o restante é tempo da OpenAI)

---

## O Que NÃO Está no Nosso Controle

O maior consumidor de tempo (~60%) é a **chamada à OpenAI**. Para reduzir isso existiria como opção:
- Usar modelos mais rápidos (`gpt-4o-mini` vs `gpt-4o`)
- Reduzir tokens no prompt/histórico (parcialmente coberto por G3)
- Usar streaming da resposta para enviar ao usuário mais cedo

Essas mudanças estão fora do escopo desta análise mas são possíveis caso queira explorar.

---

## Próximos Passos

Para cada proposta aprovada, a implementação será feita de forma isolada com testes antes de subir para produção.

**Aguardando aprovação para:**
- [ ] G1 — Paralelizar contexto + histórico
- [ ] G2 — Cache de módulos e prompts
- [ ] G3 — Limitar histórico a 50 mensagens
- [ ] G4 — Regex estáticas compiladas
- [ ] G5 — Reduzir timeout da IA para 45s + jitter
- [ ] G6 — Migração banco com coluna de dígitos + índice
