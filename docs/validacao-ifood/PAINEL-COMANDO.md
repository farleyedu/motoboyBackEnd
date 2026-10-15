# Painel de comando — gestão completa dos pedidos iFood

Data: 15/10/2026. Lote complementar aprovado na conversa, descrito em [plano do painel de comando](../plano-painel-comando-ifood.md).
Estado: **Validada localmente**. Sem deploy, sem sandbox sobre a versão nova, sem homologação.

## Entrega

O painel de comando passa a receber, acompanhar e operar pedidos de todas as modalidades, sem depender de coordenada ou de elegibilidade para rota. Detalhes oficiais, itens, pagamento, comanda, eventos, conciliação, cancelamento, disputas e ações suportadas ficam no painel lateral, por reuso dos componentes da tela de Pedidos. Nenhuma flag, loja ou configuração de produção foi ativada.

### Visibilidade separada da elegibilidade

- `TrackingRepository.IfoodCommandVisibilitySql`: o pedido aparece por ter vínculo oficial na loja e estado remoto não terminal; terminal entra pela data oficial (`status_occurred_at_utc`) ou pela data confiável do vínculo. Substituiu a condição anterior, que exigia `ifood_delivery_blocked_code(...) IS NULL` — isto é, só mostrava o que já estava liberado para rota.
- Removida a conversão cosmética de `rascunho` para `pendente` no map-state. O `StatusPedido` local não é promovido, nem por migration: `Rascunho` continua fora da fila, do mapa, das métricas e do encerramento automático.
- `IsInOrderWindow` passou a reconhecer o pedido externo aberto pelo estado oficial. Um pedido vivo feito antes do início da janela não desaparece quando `AlwaysShowOpenOrders` está ligado, e o terminal antigo continua recortado.
- Rotas: `ifood_delivery_blocked_code`, os triggers `ifood_protect_imported_order`/`ifood_protect_route_stop`, `EnsureIfoodRoutingAsync` e `canUseZippyRoute` permanecem como estavam. Ver um pedido não autoriza atribuir nem despachar.

### Etapa operacional única

`etapaOperacional` (`orderLifecycle.ts`) é a única fonte de apresentação para cartão, filtros, contadores, pino do mapa e cabeçalho do detalhe. Para canais próprios devolve a fase local existente; para iFood deriva de estado remoto e preparo, sem gravar status. Novos grupos: aguardando aceite, preparo e prontos. `temPendenciaOperacional` alimenta o contador de atenção com recuperação, dados desatualizados, sincronização, revisões e comando ativo.

Mapa: só recebe marcador o pedido com coordenada válida, modalidade `DELIVERY` e dados atuais. Retirada e consumo no local continuam na lista sem marcador e sem alerta de endereço. O pino passou a mostrar o número oficial e a etapa operacional, preservando o ID interno nas operações.

### Detalhes e ações no painel lateral

`IfoodCommandOrder` busca `PedidoDetalhe` na seleção e monta `IfoodOperationsPanel`, `IfoodPickupCompletion`, `IfoodOrderDetails` e `IfoodOrderEvents`. O map-state continua leve: sem itens, sem código de entrega. Ações disponíveis ficam destacadas; as demais seguem visíveis e desabilitadas com motivo específico (`actionBlockedLabel` distingue "já aceito", "já em preparo", "já pronto", "aguarde o aceite" e prazo encerrado). Permissão para ver não concede permissão para operar: sem `integracao_ifood` o operador acompanha e vê as ações desabilitadas. Operações pendentes usam `pendingOperation` por loja/usuário/pedido.

### Recebimento, recuperação e revisões

- O importador reclama `PLC`/`PLACED` **ou** um evento com `orderId` ainda sem snapshot; a projeção só reclama evento cujo snapshot existe. Não há disputa entre os dois.
- Pedido criado por evento não inicial nasce com marcador local `zippyRecovery` e sincronização `pending`; o evento volta à fila. Um evento oficial de ciclo de vida aplicado remove o marcador e confirma a dimensão de estado. Enquanto o estado permanecer desconhecido, comandos, disputas e rota ficam bloqueados (`IFOOD_RECOVERED_ORDER`).
- `zippyRecovery` é marcador exclusivamente local: é removido do payload vindo da API antes de qualquer uso.
- `DELIVERY_DROP_CODE_REQUESTED` passou a ser reconhecido como informativo pelo projetor — a causa do `IFOOD_EVENT_CODE_CONFLICT` era a lista `Known`, não o parser. Revisão já gravada **não** é apagada por deploy: precisa de reprocessamento ou conciliação auditada.
- Revisão separada por causa: `IFOOD_ORDER_MODALITY_REVIEW`, `IFOOD_ORDER_SCHEDULE_REVIEW`, `IFOOD_ORDER_ITEMS_REVIEW`, `IFOOD_PAYMENT_DATA_REVIEW` e `IFOOD_DELIVERY_LOCATION_REQUIRED`. Aceite e preparo toleram apenas pendências independentes (pagamento e localização), com dados atuais e sem evento em revisão/falha; despacho continua exigindo localização.
- Reconsulta oficial: `POST api/v2/delivery/ifood/pedidos/{id}/refresh` com permissão iFood, loja do token, autorização revalidada, reserva com intervalo de 15 s, HTTP fora de lock, checagem de revisão no commit, recusa com comando/rota/conclusão ativos, atualização transacional de snapshot/itens/endereço e auditoria `refresh_details`. Preserva o estado remoto e o marcador de recuperação; não cobra, não conclui e não dispensa evento. O painel tenta uma vez automaticamente para pendência independente e oferece botão para nova tentativa.

### Atenção, teste e configuração

- Alertas no comando (`IfoodAlerts` no painel esquerdo): prazo de aceite (3 e 6 min), revisão, fila e falha de evento, comando e conclusão incertos, polling parado, e os novos `IFOOD_DISPUTE_OPEN`, `IFOOD_SCHEDULED_ORDER`, `IFOOD_RECOVERED_ORDER` e `IFOOD_EVENT_REVIEW`. Disputa e agendamento não dependem de o pedido estar na lista ativa; o aviso abre o pedido no próprio painel.
- `isTest` do payload oficial é propagado à identidade, exibido no cartão e no detalhe, e excluído das métricas e das estatísticas por motoboy — que antes só excluíam `origem='simulador'`. Nenhuma coluna de ambiente foi criada e nenhuma coordenada de simulação é gravada em `pedido.latitude/longitude`.
- Configuração: `api/v2/ifood/config` continua restrita a superadmin e **deixou de devolver o `client_secret`**, inclusive decifrado; valor vazio no PUT preserva o segredo gravado. A loja segue em `api/v2/ifood/store`, sem credenciais. Os kill-switches permanecem no banco e na Gestão.
- Loja fechada na Zippy: aviso no detalhe de que os pedidos iFood continuam chegando e que o funcionamento no iFood é controlado separadamente. Nenhuma sincronização de abertura/fechamento foi prometida ou implementada.

## Arquivos

- Backend: `Repository/TrackingRepository.cs`, `DTOs/Delivery/IfoodPedidoDtos.cs`, `Integrations/Ifood/Orders/` (`IfoodImportedOrder`, `IfoodOrderImportRepository`, `IfoodOrderImportService`, `IfoodOrderProjectionRepository`, `IfoodOrderProjectionService`, `IfoodOrderDetailsClient`, `IfoodOrderReadModel`, novo `IfoodOrderRefreshService`), `Integrations/Ifood/Events/IfoodInboxRepository.cs`, `Commands/IfoodCommandRules.cs` e `IfoodCommandRepository.cs`, `Exceptions/IfoodExceptionRules.cs`, `Reconciliation/IfoodReconciliationRepository.cs`, `Controllers/IfoodOrderRefreshController.cs`, `IfoodDiagnosticsController.cs`, `IfoodConfigController.cs`, DI.
- Migration `20261015_02_ifood_command_panel.sql` e rollback: amplia apenas as ações auditadas para incluir `refresh_details`; o rollback recusa apagar auditoria já usada.
- Painel: `features/delivery/operations/orderLifecycle.ts`, `mappers.ts`, `types.ts`, `constants.ts`, `hooks/useOrderFilters.ts`, `hooks/useDeliveryData.ts`, `hooks/useMapRendering.ts`, `map/orderPin.ts`, `components/command/` (`CommandListPanel`, `OrderCard`, `OrderDetailPanel`), `DeliveryScreen.tsx`, `features/ifood/` (`IfoodCommandOrder` novo, `IfoodOperationsPanel`, `IfoodOrderDetails`, `IfoodAlerts`, `IfoodConfigSettings`, `api`, `contracts`, `view`, `routing`, `diagnostics`).
- Consulta de diagnóstico somente leitura: `docs/diagnostico-pedidos-ifood.sql`.
- App do motoboy: **sem alterações**. Os campos novos da identidade são aditivos.

## Resultados locais

Executados em 15/10/2026, nesta máquina, com PostgreSQL de teste local em `127.0.0.1:55496` (`TEST_DELIVERY_DATABASE` definido somente no processo de teste; schemas das fixtures, removidos ao final).

| Validação | Resultado |
|---|---|
| Backend `dotnet build` | OK (exit 0) |
| Backend `dotnet test --filter ~Ifood` | **349 passaram, 0 falharam, 0 skipped** (6 m 23 s). Nenhum teste de banco foi pulado. `command-panel-final-review.trx` |
| Painel `npm run typecheck` | OK (exit 0) |
| Painel `npm run lint` | OK (exit 0); apenas avisos pré-existentes de `react-hooks/exhaustive-deps` e `no-img-element`, nenhum nos arquivos deste lote |
| Painel `npm run build` | OK (exit 0), "Compiled successfully", 179 páginas geradas |
| Painel `npm run test` | 78 arquivos / 780 testes. 77 arquivos OK; 2 falhas de tempo em `conversasPanel.test.tsx`, que passa 19/19 isolado (ver abaixo) |
| `DeliveryScreen.smoke.test.tsx` isolado | 3/3 OK, 6,8 s |
| `conversasPanel.test.tsx` isolado | 19/19 OK, 16,2 s |

Cobertura dos critérios do plano pelos testes novos: visibilidade sem rota/endereço e sem mutar o status local, e 409 oficial na atribuição por API para DELIVERY/TAKEOUT/DINE_IN (`CommandVisibilityDoesNotRequireRoutingOrAddressAndDoesNotMutateLocalStatus`); janela pelo estado oficial sem promover rascunho (`WindowRecognizesOfficialOpenStateWithoutPromotingDraft`); DDCR informativo sem revisão (`DeliveryCodeRequestIsInformationalAndDoesNotPutOrderInReview`); recuperação por CFM sem PLC com reaplicação antes de liberar comandos (`RecoveryImportsWithoutPlacedEventAndReplaysConfirmationBeforeEnablingCommands`); pendência independente não impede aceite mas conflito de evento impede (`IndependentDataIssueDoesNotPreventAcceptanceButEventConflictDoes`); recuperado não aceita de novo (`RecoveredUnknownStateCannotBeAcceptedAgain`); reconsulta auditada sem inventar estado e com rollback recusado (`RefreshOfficialDetailsRepairsDataWithoutInventingStatusAndIsAudited`); disputa sobrevive ao pedido terminal e respeita a loja (`DisputeAlertSurvivesTerminalOrderAndRemainsScopedToStore`); etapa única em cartão/filtros/contadores e pino com número oficial (`useOrderFilters.test.tsx`, `orderPin.test.ts`).

### Dois ajustes feitos na revisão

1. **Regressão de carregamento corrigida.** `OrderDetailPanel` importava `IfoodCommandOrder` estaticamente, trazendo a árvore inteira de Pedidos (operação, itens, comanda, eventos, disputas, MUI) para o grafo de importação do painel de comando de todo operador, inclusive quem não usa iFood. O smoke do `DeliveryScreen` passou de ~7 s para mais de 30 s e estourou o limite. Passou a `next/dynamic` com `ssr: false`, idioma já usado no repositório: o detalhe só existe no clique de um pedido iFood e não precisa de servidor. Com isso o smoke voltou a 6,8 s.
2. **Projeção normalizava o payload duas vezes** no mesmo evento de ciclo de vida. Passou a reaproveitar uma única normalização (`official`), sem mudança de comportamento.

### Fragilidade pré-existente, não corrigida aqui

`conversasPanel.test.tsx` depende do tempo de carga: o primeiro teste importa o painel de conversas inteiro e leva ~16 s isolado, enquanto um teste irmão do mesmo arquivo corre no limite padrão de 5 s. Em suíte completa, com a máquina carregada, os dois estouram. As duas execuções completas desta sessão falharam em arquivos diferentes (primeiro o smoke, depois esse), e o arquivo passa 19/19 isolado — assinatura de carga, não de defeito. O arquivo não foi tocado por este lote e não importa nada do grafo iFood (`CommandListPanel`, `OrderDetailPanel`, `IfoodAlerts` e `IfoodCommandOrder` não aparecem em `features/delivery/chat/`). A correção natural é o mesmo `vi.setConfig({ testTimeout })` que o smoke já usa, mas isso é decisão do dono daquele teste e ficou fora deste lote.

## Limites e pendências

- Nada aqui é evidência de homologação. O roteiro de sandbox precisa ser repetido sobre esta versão: T-01, T-03 a T-07, T-14 a T-18, T-20, T-23 a T-28, T-32 a T-35 e T-38. T-02/T-31 e a migration nova exigem implantação autorizada; T-08 a T-13, T-29 e T-30 dependem do app e do fluxo físico.
- 6485 e 8999 não foram consultados: exigiria chamada autenticada ao banco de produção, que não foi feita nesta sessão. O caminho é `getIfoodOrderEvents` pelo ID interno (ou a consulta de leitura em `docs/diagnostico-pedidos-ifood.sql`) e, conforme o `outcome`/`reasonCode`, reprocessar o evento ou conciliar com anotação. A hipótese documentada é o conflito de código do DDCR; o endereço fictício não é a causa provável e não deve ser assumido.
- Revisão visual e de design das telas novas (lista com os novos chips, avisos no painel esquerdo, detalhe iFood no comando) em tema claro/escuro e largura de celular continua pendente, como nas demais telas do lote iFood.
- O código foi commitado em 15/10/2026 — `7ea3a30` no painel e `6308e0e` no backend, ambos "ajuste ifood integração" — enquanto estas validações rodavam. Os commits contêm a versão validada aqui, inclusive os dois ajustes da revisão. **Não** houve push, deploy, migration aplicada em produção, publicação, ativação de loja ou alteração de flags. A API local não foi apontada para o banco de produção; a integração de banco usou PostgreSQL de teste local em schemas isolados.
