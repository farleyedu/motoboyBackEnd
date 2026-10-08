# Sincronização do delivery — 07/10/2026

Implementação autorizada por Farley: otimizar heartbeat, GPS, fila e eventos, preservando os endpoints existentes. Commit e push autorizados **somente no backend**. A integração mobile está em `../zippygo-motoboy/services/mobileApi.ts` e `trackingService.ts`.

## Mudanças

- Autenticação: apenas falha na validação do JWT é tratada como token inválido. Falha ao consultar o contexto não é convertida em autorização negada. Indisponibilidade/conexão/timeout de banco retorna `503 DATABASE_UNAVAILABLE`, com `Retry-After: 3`; sessão encerrada, epoch errado e vínculo revogado continuam bloqueados. O middleware usa o `NpgsqlDataSource` já registrado e cancelamento da requisição; não há cache de permissões ou TTL aumentado.
- Heartbeat: `UPDATE ... RETURNING` e leitura dos dados do motoboy na mesma consulta, com horário do PostgreSQL. De três comandos antes da gravação do evento para um. Sessão e evento continuam na mesma transação; a presença do simulador mantém sua política separada. O JWT continua sendo renovado para preservar clientes existentes.
- GPS: endpoint legado continua disponível. O novo lote compartilha validação de sessão/vínculo, consulta de duplicatas e transação. Insere histórico com arrays/`UNNEST`, atualiza a posição corrente e incrementa a versão uma vez por lote. Publica somente a posição final aceita daquele lote. Conflitos de identidade desfazem todas as gravações novas.
- Fila: cancelamento até as consultas que montam o snapshot e timeout de comando de 10 s. `RepeatableRead` mantém versão, paradas, políticas e estado da rota no mesmo snapshot, sem bloquear a fila para leitura. Os demais chamadores continuam usando assinaturas com token opcional.
- Outbox: reserva global de 60 s, renovável, com transações curtas para reserva e ACK. Nenhuma conexão/transação permanece aberta durante o envio ao SignalR. Timeout de envio padrão de 5 s, limite configurável de 1–10 s. ACK/release verificam proprietário; um publicador antigo não confirma nem libera a reserva de outro. Crash recupera eventos não confirmados após expiração. Falha adia o evento e bloqueia os seguintes do mesmo alvo/motoboy, permitindo os outros agregados. EventIds e versões são preservados; entrega continua podendo repetir eventos após uma falha entre envio e ACK.

## Contrato do lote

`POST /api/v2/motoboys/me/session/location/batch`, token operacional obrigatório, corpo limitado a 32 KiB.

```json
{
  "samples": [
    {
      "sampleId": "6dfd31ac-1d8b-48cd-8c15-92c1590e8308",
      "sequence": 1,
      "capturedAtUtc": "2026-10-07T15:00:00Z",
      "latitude": -23.5,
      "longitude": -46.6,
      "trackingMode": "active_route"
    }
  ]
}
```

De 1 a 20 pontos; IDs únicos e sequências em ordem crescente. Campos opcionais são os mesmos do endpoint individual. Resposta `ApiResponse` com `data.samples`, um ACK por ponto, contendo identidade, sequência, outcome, updatedCurrent, sessionVersion e receivedAtUtc.

| Outcome | Comportamento |
| --- | --- |
| accepted | Histórico gravado; somente o último ponto novo atualiza a posição corrente. |
| duplicate | Mesma identidade, sequência e hash já gravados; preserva receivedAtUtc original. |
| stale | Ponto desconhecido anterior à posição corrente; não faz o mapa regredir. |
| rejected + LOCATION_INVALID | Coordenada, horário ou sensor inválido; os outros pontos válidos podem ser aceitos. |

Estrutura de lote inválida retorna 422; reutilizar UUID/sequence com outro conteúdo retorna 409 `SEQUENCE_CONFLICT` e não grava os pontos novos. Sessão/vínculo inválido rejeita a operação. O endpoint individual mantém 409 para sequência antiga.

`session.nextLocationSequence` é um campo adicional nas respostas de sessão/heartbeat: clientes antigos podem ignorá-lo; o mobile usa para retomar um contador local ausente. Histórico acumulado só verifica retorno à loja se o ponto que atualizou a posição foi capturado dentro da janela de frescor (padrão 120 s). Uma posição antiga recebida agora não conclui retorno.

Rate limit separado do lote: rajada de cinco requisições (até os 100 pontos da fila local) e reposição de uma a cada dois segundos por sessão. A taxa sustentada máxima permanece 10 pontos/s; cada lote também tem limite fixo de pontos. Recuperação de histórico não precisa provocar 429 no terceiro lote.

## Integração mobile

Fila local de até 100 amostras, UUID/sequence persistidos antes do HTTP. Drena lotes ordenados de até 20, sem iniciar outro depois do orçamento de 8 s; uma chamada iniciada continua limitada a 20 s. Remove apenas pontos confirmados por identidade/sequence e outcome conhecido. Reenvio após ACK perdido usa as mesmas identidades. Erros transitórios mantêm os pontos e o backoff existente. Validação de dono/sessão e proteção de logout continuam ativas; sensores com valor -1 são enviados como null.

API antiga (404/405): fallback ao endpoint individual por cinco minutos, no máximo cinco pontos por execução. Erro de rede/503 não aciona fallback. Um conjunto de 100 pontos pode usar cinco requisições no backend novo, em lugar de 100; é economia estrutural de HTTP nesse cenário, não benchmark de bateria ou latência de produção.

## Migração e operação

Aplicar `Migrations/Delivery/20261007_01_outbox_publisher_lease.sql` antes de ativar o publicador atualizado. Cria somente a tabela da reserva e um índice parcial para o retry; mantém o histórico e os contratos existentes. O executor de migrações já inclui o arquivo no boot quando `DeliveryTracking:ApplyMigrationsOnStartup=true`. No appsettings de Development a opção está false: aplicar pelo procedimento habitual de migrações locais. Não execute o arquivo novamente sem necessidade nem altere arquivos antigos já registrados no ledger.

Configurações adicionais opcionais: `DeliveryTracking:OutboxLeaseSeconds` (padrão 60; efetivo 30–300) e `OutboxSendTimeoutSeconds` (padrão 5; efetivo 1–10). A reserva global preserva um único publicador. Várias réplicas da API exigem a configuração de distribuição de SignalR apropriada; esta mudança não instala Redis nem altera a topologia existente.

## Medições

Meter `ZippyGo.Delivery.Sync`, usando as APIs nativas do .NET 8, sem nova dependência:

- `delivery.sync.request.duration`: duração total, incluindo autenticação, por operação/status (499 para cancelamento).
- `delivery.sync.database.duration`: operação de heartbeat/gravação GPS e resultado.
- `delivery.sync.pool.wait`: espera para obter conexão nessas operações.
- `delivery.sync.locations`: outcomes das tentativas de GPS, incluindo rejeições.
- `delivery.sync.outbox.age`: idade do evento ao publicar com sucesso.
- `delivery.sync.outbox.failures`: falhas de envio.

Tags não contêm token, usuário, endereço ou ID de sessão. Requests de sincronização acima de 2 s geram log com operação, status, duração e traceId. Instrumentos Npgsql/ASP.NET Core podem complementar com duração de comandos e pool. Para observar localmente com dotnet-counters já instalado:

```powershell
dotnet-counters monitor --process-id <PID_DA_API> --counters ZippyGo.Delivery.Sync,Npgsql
```

Referências técnicas: [métricas nativas do .NET](https://learn.microsoft.com/en-us/dotnet/core/diagnostics/metrics-instrumentation), [Npgsql: pool e viagens ao banco](https://www.npgsql.org/doc/basic-usage.html).

## Validação

Testes unitários e integração em `Tests/Unit/DeliverySyncReliabilityTests.cs`, `DeliveryOutboxPublisherTests.cs` e `Tests/Integration/DeliverySyncDatabaseTests.cs`. A integração aceita somente host local, cria schema aleatório e apaga somente esse schema ao terminar; não usa a connection string da aplicação. Testes de banco aparecem como skipped quando não há TEST_DELIVERY_DATABASE, para não simular aprovação sem executar PostgreSQL.

```powershell
$env:TEST_DELIVERY_DATABASE = 'Host=127.0.0.1;Port=<PORTA_LOCAL>;Database=postgres;Username=<USUARIO_DE_TESTE>'
dotnet test --no-restore
```

Cobertura: lote de 20, duplicação de ACK, conflito com rollback, sequência antiga, sessão expirada, vínculo revogado, tenant errado, TTL do simulador, concorrência entre heartbeat/GPS, cancelamento durante espera de lock/consulta, detalhes/políticas/estado da fila, takeover de reserva, bloqueio de retry por agregado e envio SignalR com pool limitado a UMA conexão. Métricas verificam status 503 e ausência de credenciais.

Resultado: **911 testes backend passaram, incluindo 13 com PostgreSQL 16.10 local; nenhum skipped nessa execução.** Mobile: 58 testes de sincronização, TypeScript e exportação Android/Hermes passaram. Revisão Web de 17 grupos permanece como evidência da rodada anterior; esta rodada não alterou telas. TRX local em `obj/delivery-validation/results/delivery-sync.trx`. PostgreSQL de validação extraído de artefato Zonky/Maven com checksum conferido, iniciado apenas em loopback; sem instalação de serviço ou acesso a banco real da aplicação.

Ainda pendentes: desempenho sob carga no ambiente hospedado, migração/deploy efetivamente concluídos e acompanhamento em Android físico com tela bloqueada/rede interrompida. A origem dos antigos timeouts não foi comprovada; a instrumentação serve para identificar o gargalo real.
