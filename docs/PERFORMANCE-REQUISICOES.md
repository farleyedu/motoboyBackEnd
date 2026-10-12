# Requisições e consultas do motoboy — 12/10/2026

As duas fases foram implementadas e verificadas localmente. A Fase 2 começou depois da aprovação da Fase 1 nos testes. Nenhum deploy, push ou acesso ao PostgreSQL de produção foi realizado nesta tarefa. As duas migrations novas foram mostradas na conversa antes de serem criadas e executadas apenas em esquemas temporários no PostgreSQL local.

## Mudanças por item

| Item | Mudança e arquivos | Como validar | Limites e riscos |
| --- | --- | --- | --- |
| 1 — Presença | `appsettings.json`, `appsettings.Development.json`, `Options/DeliveryTrackingOptions.cs` e `Repository/OperationalSessionRepository.cs`: heartbeat 240 s, presença 720 s, frescor GPS 120 s, JWT operacional 3.600 s. GPS novo aceito também renova presença. No app: `OperationalSessionContext.tsx`, `operationalRequests.ts`, `trackingService.ts`, teto 300 s. `20261012_01_presence_window.sql` corrige presença infinita legada. | Testes `PresenceWindowDatabaseTests`, `OperationalSessionServiceTests`, retomada nativa/web e Jest `operationalRequests.test.js`. Presença vence sem fechar turno/fila; heartbeat/GPS a renovam; encerramento explícito remove o motoboy imediatamente. Retorno ao primeiro plano já usa `restore()` e heartbeat forçado. | Web e nativo passam a aparecer offline após 12 minutos sem sinal aceito. O turno continua aberto para retomada; simulador conserva sua política. Duplicatas de GPS não atualizam artificialmente o ponto. O rollback deve acompanhar a reversão do código. |
| 2 — Autenticação | `Service/AuthDecisionCache.cs`, `Service/Interface/IAuthDecisionCache.cs`, `Options/AuthCacheOptions.cs`, `JwtAuthenticationMiddleware.cs`, `Program.cs`, configurações e invalidadores em `AuthService`, `MotoboyLoginSessions`, `OperationalSessionRepository`, `GestaoRepository`. Resultados positivos por 20 s; `AuthCache:TtlSeconds=0` desliga. Chave inclui usuário, loja, empresa, sessões, epoch, motoboy, uso do token e contexto/permissões relevantes. | Unitários verificam acerto, expiração, ausência de cache negativo, configuração zero, invalidação por escopo e corrida com consulta anterior à revogação. Integração verifica pausa da empresa, encerramento, troca de login, logout e acesso sem vínculo com o cache ligado. | Cache local à instância. Escalar a API para várias instâncias exige distribuir a invalidação. Alterações diretas no banco, fora dos mutadores da API, podem esperar até o TTL. Validação criptográfica do JWT e leitura das permissões do vínculo continuam fora do cache de decisões. |
| 2a — Contatos | `AtendimentoRepository`, `IAtendimentoRepository`, `AtendimentoService`, `MotoboyTrackingV2Controller`, `AtendimentoDtos`. `?paged=true` devolve `contacts`, `hasMore`, `cursor`, `totalContacts`, `onlineContacts`; primeira página 15, demais 10. Cursor usa online/nome/id, validado por loja. Sem parâmetros, lista antiga com teto de 200. App: `communicationApi.ts`, `usePagedContacts.ts`, `CommunicationScreen.tsx`, suporte à rolagem em `Kit.tsx`. | Integração percorre 250 contatos, nomes repetidos, busca e cursor de outra loja. Jest verifica deduplicação, troca de conta e tela inativa. Bundle real verifica 35 contatos em três páginas, busca remota e totais incluindo contatos ainda não carregados. | A presença pode mudar enquanto as páginas são percorridas; o app deduplica por id. A contagem e a busca são da loja/consulta atual. Os totais vêm na mesma consulta, sem acrescentar uma ida ao banco. |
| 2b — Histórico | `ClientCommunicationRepository`, `ClientCommunicationController` e `communicationApi.ts`: histórico limitado à conversa do pedido; padrão de 20 mensagens; cursor data/id para buscar mais antigas. | `CommunicationClientHistorySearchMediaReactionsAndCursorStayInTenant`: outra conversa do mesmo cliente fica fora, cursor preserva mensagens com horários iguais e outra loja não acessa o histórico. | Conversas de outros pedidos deixam de compor o histórico do pedido atual, conforme solicitado. |
| 2c — Custo por consulta | `ClientCommunicationService`, `AtendimentoRepository`, `IAtendimentoRepository`, `CommunicationRepository`: autorização com `EXISTS` em `delivery_route_stops`, sem montar fila. Exclui ofertas não aceitas, pedidos encerrados, outro motoboy e outra loja. Nome, histórico, metadados, reações e lote de anexos respeitam apenas suas dependências; consultas independentes se sobrepõem. Metadados dos anexos vêm em lote, sem carregar seus bytes. | Testes `ClientChatCostDatabaseTests` e `CommunicationMediaTests`: autorização antes do transporte; 20 anexos; autoria preservada; proteção por loja; oferta pendente bloqueada. | Consultas paralelas usam conexões do pool. Os endpoints continuam validando fila, canal e contexto; a consulta leve exige o esquema de ofertas já utilizado pelo projeto. |
| 2d — Reações | `Migrations/Delivery/20261012_02_wa_reaction_index.sql` e rollback: índice parcial em `payload->'message'->'reaction'->>'message_id'` para eventos de reação, com `recebido_em DESC`. | `ReactionIndexDatabaseTests`: aplicação repetida, `EXPLAIN` usando o índice com 5.000 eventos locais e rollback. | `CREATE INDEX` comum é compatível com a transação do migrador e pode bloquear escritas durante sua criação. Planejar a aplicação em produção. |
| 2e — Atividade | `useAppForeground.ts`, `ChatNotices.tsx`, `CommunicationScreen.tsx`, `useChatOutbox.ts`, `ClientConversation.tsx`: reservas da central/avisos/fila de envio a cada 60 s, suspendidas sem foco ou no background. Aba web oculta também pausa. Retorno provoca atualização imediata. A central aproveita a fila já sincronizada e não refaz a consulta ao digitar busca de contato. | Jest `activity-polling.test.js`: background, retorno, perda de foco, preservação do envio, intervalo de 60 s e aba web oculta. Bundle real confirma retirada e retomada dos timers. Teste antigo `StyledText` corrigido para aguardar montagem/desmontagem no React 19; antes sua snapshot era `null`. | Envio já solicitado pode terminar; o armazenamento da fila e os identificadores de idempotência foram preservados. `ClientConversation` conserva o SignalR e a reserva de 30 s já existentes, por instrução explícita; apenas pausa no background foi acrescentada ali. |

## Verificação realizada

- `dotnet build APIBack.csproj --no-restore`: passou; execução final incremental sem avisos ou erros. A compilação completa também passou, com avisos antigos de nullable/analisadores.
- `dotnet test APIBack.csproj --no-build --no-restore`, sem variável de banco: **1.029 passaram; 77 integrações foram ignoradas** por exigir PostgreSQL. Relatório `obj/performance-validation/unit-no-database.trx`.
- Suite direcionada com PostgreSQL 16 local em WSL, host `127.0.0.1`, usuário temporário e esquemas isolados: **1.048 passaram; zero ignorados**. São 1.029 testes unitários e 19 integrações relevantes. Relatório `obj/performance-validation/performance-local-final.trx`. As outras integrações não fazem parte dessa execução direcionada.
- `npx tsc --noEmit`: passou. Também foram verificados os conteúdos preparados para os commits parciais, preservando o restante da edição visual do app.
- `npx jest --runInBand --watchAll=false --modulePathIgnorePatterns=.expo`: **10 testes passaram**, quatro suites e uma snapshot. A exclusão impede que arquivos de builds antigos em `.expo` colidam com o pacote principal.
- Testes de domínio de sessão, sincronização, fila de mensagens, rascunhos, avisos e anexos passaram. Incluem reprodução com um toque e preservação da gravação após falha de envio.
- Exportação web atual passou. Chromium com APIs simuladas: **10 cenários de conversa/áudio** e **4 de paginação/atividade**, nos temas claro/escuro e larguras 320/390 px. Sem exceções de página ou acesso à API publicada.
- Teste e relatório de contatos: [`contacts-activity-tests.cjs`](../../zippygo-motoboy/docs/validacao-performance/contacts-activity-tests.cjs), [`contacts-activity-results.json`](../../zippygo-motoboy/docs/validacao-performance/contacts-activity-results.json). Prints temporários removidos após inspeção.

Não foi medida a carga no Render nem instalado um novo APK com estas melhorias no Android. O aparelho continua com o APK de vínculos/áudio da validação anterior. A validação física da frequência de requisições, bateria e retomada em Android/iPhone permanece para a próxima publicação.

## Redução prevista por gatilho

| Gatilho com app aberto | Antes | Agora |
| --- | --- | --- |
| Heartbeat periódico | 144/h, intervalo 25 s | 15/h, intervalo 240 s |
| Avisos globais | 180/h, intervalo 20 s | 60/h, intervalo 60 s; nenhum ciclo no background |
| Busca da central | 450/h, intervalo 8 s | 60/h, intervalo 60 s; apenas com foco/primeiro plano |
| Reserva da fila de envio | 240 ciclos/h, intervalo 15 s | 60 ciclos/h, intervalo 60 s; apenas com foco/primeiro plano |
| Metadados de 20 anexos | até 20 consultas e conteúdo dos arquivos | uma consulta com somente descritores |

São frequências teóricas dos gatilhos, não uma medição de requisições totais. GPS, eventos em tempo real, retomadas e ações do usuário continuam gerando tráfego. Uma fila de envio vazia não faz uma requisição por ciclo.

## Commits e publicação

Os itens foram separados por assunto, em cada repositório, com complementos de testes/contagens também separados. O commit do usuário `008ee34` já incluiu parte da presença enquanto o trabalho estava em andamento; ele foi preservado.

| Item | Backend | App |
| --- | --- | --- |
| Presença | `5ca6a98`, `9f9e0c0` e ajustes já em `008ee34` | `3c1842b` |
| Cache | `e5ee1e0`, `179a080` | — |
| Contatos | `642fe24`, `3494f57` | `d675567`, `8bf9083`; validação em `ae32f43` |
| Histórico | `cd997c9` | `7d7ab2e` |
| Consultas/anexos | `7f27c81`, `9d51988` | — |
| Índice | `45e630a` | — |
| Atividade | — | `b2becaf` |

As alterações anteriores de telas, cadastro, áudio e configuração Android que ainda estavam pendentes no app foram preservadas fora dos commits de performance. A integração visual foi validada com o estado atual do workspace. O aviso WhatsApp em `SqlMessageRepository.NotifyMotoboysAsync` e a assinatura existente de `ClientConversation` não foram refeitos.

Ordem de publicação, a ser executada separadamente:

1. Revisar os commits da API e as duas migrations mostradas, incluindo rollbacks.
2. Publicar **backend antes do app**, com o esquema anterior já aplicado e as migrations `20261012_01_presence_window.sql` e `20261012_02_wa_reaction_index.sql`.
3. Confirmar presença/configuração e cache na instância única. O intervalo é entregue pela API; o app antigo ainda limita seu batimento a 60 s.
4. Publicar a versão web e gerar/instalar o APK contendo os novos tetos de intervalo e a paginação, junto com as mudanças visuais anteriores que devam entrar na versão.
5. Validar no Android/iPhone: voltar de Maps/bloqueio de tela; doze minutos sem sinal; encerramento explícito; troca de aparelho; conta sem vínculo; rolagem/busca; mensagens e anexos em tempo real.
