# Contratos do produto para pedidos iFood

Data: 13/10/2026. Lote IF-02. Documento normativo interno para implementação futura, vinculado ao [plano principal](plano-integracao-ifood-produto-homologacao.md).

## 1. Estado e limites

Contrato de produto definido documentalmente após inspeção do backend, painel e app. Os nomes abaixo são propostas de DTOs internos; não representam endpoints implementados nem schema já migrado. API iFood habilitada, formato de código, elegibilidade e efeitos remotos exigem confirmação em IF-01/sandbox. Nenhuma aplicação foi alterada neste lote.

A referência de integração é PDV/Food/Order/Events centralizada. Não confundir os endpoints de Order com os de Logistics. A referência pública atual disponibiliza confirmação por código em Order, mas a disponibilidade real precisa ser comprovada na conta.

## 2. Identidade compartilhada

Manter `id`/`pedidoId` existentes como chave interna, sem trocar parâmetros de rota pelo número exibido. Acrescentar `identificacao` e `integracao` aos DTOs de lista/detalhe/mapa/fila/oferta/contexto/recibo, com projeções reduzidas para cada ator.

| Campo JSON proposto | Tipo | Regra |
|---|---|---|
| `origem` | string | Valores existentes atendente/cardapio_web/ifood/ia_whatsapp/simulador; valor desconhecido não vira atendente silenciosamente |
| `identificacao.numero` | string | Número principal visível; iFood usa displayId oficial preservado, próprios usam número interno convertido para texto |
| `identificacao.rotulo` | string | Ex.: `iFood #001234` ou `Zippy #1842`; derivado da origem e número, não enviado como chave de operação |
| `identificacao.qualidade` | string | `oficial`, `local`, `legada`, `indisponivel`; impede fingir que um número legado é oficial |
| `integracao.provider` | string/null | `ifood` para vínculo oficial; legado não é vínculo oficial por inferência |
| `integracao.externalOrderId` | string/null | UUID externo completo; usado internamente nas requests remotas, não como título da tela |
| `integracao.merchantId` | string/null | Merchant vinculado ao estabelecimento autenticado; nunca aceito do cliente como autorização |
| `integracao.displayNumber` | string/null | Número oficial textual; nunca único globalmente |
| `integracao.localizer` | string/null | Campo restrito aos atores/fluxos permitidos; não é código do cliente |
| `integracao.vinculo` | string | `oficial`, `legado_nao_verificado`, `ausente`; bloqueia comando remoto sem vínculo oficial |

Fila/app não precisa receber clientId/secret, payload bruto, dados fiscais ou merchant de outra loja. Projeções por ator podem omitir campos de suporte como UUID/merchant/localizer. `identificacao` e capacidades suficientes devem estar disponíveis em todas as telas operacionais.

Fallback de iFood sem número: `iFood · número indisponível`, com ID interno secundário; nunca inventar número oficial. Evento recebido sem detalhes não entra em rota antes de classificação. Dois merchants podem usar o mesmo número visível: busca sempre no estabelecimento autenticado e pode retornar múltiplos resultados dentro dele, distinguindo data/modalidade.

### Compatibilidade com o legado

- `origemRef`: reservar referência nova baseada no UUID externo; o índice existente por estabelecimento/origem/referência permanece útil.
- `idIfood`/`id_ifood`: manter compatibilidade temporária, sem alterar significado para UUID automaticamente; novas telas usam `identificacao`.
- Backfill exige prova de correspondência. Dados capturados por DisplayId permanecem classificados como legado até conciliação.
- Índice global legado precisa de estratégia aditiva antes de importar números de várias lojas. Não remover unicidade sem auditoria e sem índice oficial substituto.
- Escritas via importador antigo e novo não podem produzir dois pedidos para o mesmo vínculo oficial; bloquear convivência não controlada por loja/flag.
- App/painel antigos não podem concluir iFood oficialmente por omissão de campo novo. Guardas de backend são obrigatórias, mesmo com atualização aditiva de DTOs.

## 3. Modalidade, endereço e tempo

| Campo | Tipo | Semântica |
|---|---|---|
| `modalidade.tipo` | string | `delivery`, `takeout`, `dine_in`, `unknown`; preservar valor bruto internamente |
| `modalidade.entreguePor` | string/null | `merchant`, `ifood`, `unknown`; para retirada não se presume merchant delivery |
| `modalidade.usaEntregaZippy` | boolean | Calculado no servidor; true somente para entrega própria elegível |
| `modalidade.classificada` | boolean | false bloqueia atribuição/despacho/conclusão e gera exceção visível |
| `horarios.agendado` | boolean | Agendamento não posterga automaticamente o aceite |
| `horarios.janelaInicio`/`janelaFim` | ISO-8601/null | Horários com offset normalizados; preservar valores oficiais |
| `horarios.aceitarAte` | ISO-8601/null | Prazo derivado somente de regra oficial vigente; não baseado na entrega agendada |
| `horarios.atualizadoEm` | ISO-8601 | Data da projeção para identificar desatualização |

Endereço é opcional para modalidades sem entrega. Delivery sem localização suficiente gera exceção operacional sem coordenada inventada e sem impedir recebimento persistente do pedido. Cliente é snapshot do pedido; não exige cadastro de CRM ou telefone real. Pedido não classificável continua visível ao operador para investigação.

## 4. Dimensões de estado

`status`/`statusPedido` atuais permanecem operacionais. Não acrescentar números do iFood ao enum local. Expor os eixos abaixo separadamente:

| Eixo | Campos/valores internos propostos | Regra |
|---|---|---|
| Remoto | `estadoRemoto.codigo`, `atualizadoEm`, `conhecido` | Preservar código remoto bruto; não tratar evento como estado quando sua semântica não for lifecycle |
| Preparação | `aguardando_aceite`, `aceito`, `em_preparo`, `pronto`, `nao_aplicavel` | Transições exigem sucesso/evidência conforme ação; não derivar de presença de motoboy |
| Entrega local | Enum/status existentes | Só representa delivery Zippy; retirada não recebe status de rota fictício |
| Sincronização | `sincronizado`, `pendente`, `resultado_incerto`, `erro`, `requer_revisao` | Exibe divergências e comandos que aguardam confirmação |
| Confirmação | `nao_exigida`, `aguardando_codigo`, `enviando`, `pendente`, `confirmada`, `rejeitada` | Confirmação oficial tem ator, operação, fonte de evidência e horário |
| Cancelamento | `nenhum`, `solicitado`, `confirmado`, `rejeitado` | Resposta de solicitação não marca cancelado definitivamente |
| Disputa | `aberta`, `respondida`, `resolvida`, `expirada` | Separada do ciclo de entrega; pode existir após conclusão |

Entrega local concluída e disputa posterior não apagam prova de entrega ou pagamento. Evento CONCLUDED sem operação do app não valida código do motoboy por inferência. Cancelamento confirmado torna ações mutáveis incompatíveis indisponíveis, preservando ocorrência/histórico.

## 5. Capacidades e ações

Backend calcula permissões e precondições, retornando `acoesDisponiveis` com código, habilitada, motivo de bloqueio e prazo quando aplicável. A UI usa isso para orientar o operador; toda mutação revalida as mesmas regras no servidor.

| Ação interna | Ator | Precondições e efeitos |
|---|---|---|
| `confirmar` | Operador autorizado | Pedido oficial, loja autorizada, lifecycle compatível; sucesso remoto/evidência atualiza preparação |
| `iniciar_preparo` | Operador/cozinha autorizado | Contrato remoto aplicável confirmado; evitar simular comando não habilitado |
| `pronto` | Operador/cozinha autorizado | Fluxo de preparo compatível; comando readyToPickup quando aplicável |
| `atribuir` | Operador autorizado | Delivery/MERCHANT, não cancelado, localização apta, regras de fila respeitadas |
| `despachar` | Operador/ação de coleta autorizada | Pronto confirmado, modalidade própria e precondições locais; dispatch oficial não decorre de mera atribuição |
| `solicitar_cancelamento` | Operador autorizado | Motivo atual retornado para pedido; persistir intenção e aguardar resultado |
| `responder_disputa` | Operador autorizado | disputeId correto, prazo vigente, opção retornada e ainda sem resposta final |
| `concluir_entrega` | Motoboy responsável com sessão | Pedido atual, coleta/chegada, versão, pagamento/prova/código exigidos; operação recuperável |
| `concluir_retirada` | Operador autorizado | TAKEOUT elegível e pronto; contrato próprio, sem exigir sessão de motoboy |
| `consultar_operacao` | Proprietário da operação/operador autorizado | Leitura isolada por loja e ator; consulta não reenvia automaticamente |
| `registrar_ocorrencia` | Motoboy/operador autorizado | Código ausente, impedimento, cancelamento ou exceção; não produz sucesso remoto falso |

Pedidos DELIVERY/IFOOD não têm atribuir/despachar/concluir pelo motoboy Zippy. Validar código de coleta iFood é ação distinta de confirmar entrega ao cliente, somente quando habilitada.

Mutação traz `operationId`, pedido interno e revisão esperada da projeção; ações de rota continuam com versão de fila. Separar `orderVersion` de `queueVersion`: atualização do iFood não deve usar a versão de uma fila como versão universal do pedido. Conflito retorna contexto atual para revisão, sem repetir ação com novo identificador silenciosamente.

## 6. Pagamento e itens

Propor `pagamento` estruturado ao lado dos campos legados:

| Campo | Regra |
|---|---|
| `moeda` | BRL no perfil inicial; moeda desconhecida exige revisão |
| `valorPedido` | Total oficial, não usado sozinho para cobrar |
| `valorPagoOnline` | Snapshot de payments.prepaid quando presente e consistente |
| `valorPendenteNaOrigem` | Snapshot de payments.pending; não diminui por gravação local sem guardar sua versão |
| `valorRecebidoLocal` | Soma de recebimentos locais confirmados, únicos por operação |
| `valorACobrar` | Saldo validado pelo backend, sem descontar pagamento local duas vezes após revisão remota |
| `metodos` | Valores, ONLINE/OFFLINE, método bruto, rótulo, bandeira/troco quando informado |
| `situacao` | `pago`, `pendente`, `parcial`, `requer_revisao`; não derivar apenas de texto tipoPagamento |

Snapshots remotos e recebimentos locais têm proveniência, revisão e chave de conciliação. Mudanças de total/payment após patch/cancelamento geram nova revisão e recalculam saldo com histórico; não sobrescrever pagamento recebido nem gerar crédito/reembolso fictício.

Regra de validação de cobrança: soma dos recebimentos deve corresponder ao saldo a cobrar do contexto corrente, não necessariamente ao total do pedido. Exemplo sintético: total oficial R$65, pago online R$20, pendente R$45; o app cobra R$45. Coupon superior ao pedido pode zerar cobrança. Mesmo assim, não inferir total/repasse por fórmula de coupon.

Métodos como vale-refeição/vale-alimentação/outros não cabem no union atual do app. Preservar método bruto e mapear explicitamente os suportados; método desconhecido não vira dinheiro. Alteração/divisão de um pagamento externo precisa seguir possibilidades do contrato/estabelecimento, não habilitar automaticamente o fluxo local livre.

Valores financeiros internos em decimal ou centavos inteiros BRL; sem arredondamentos intermediários que alterem total oficial. Itens/adicionais são snapshots externos com referência, descrição, quantidade/unidade, preço, total e observação; campos locais inteiros não podem truncar valores. Campos ausentes não viram zero sem política explícita.

Fonte pública para prepaid/pending/meios: [Detalhes Order](https://developer.ifood.com.br/en-US/docs/food/guides/modules/order/details). A regra interna de conciliação ainda precisa de implementação/testes; a fonte descreve o payload, não um ledger Zippy.

## 7. Contexto de conclusão

Estender o contexto atual com identidade, modalidade, revisão do pedido, pagamento, capacidades, política de código e operação pendente. Não retornar código esperado do cliente.

`politicaCodigo` proposta: `origemValidacao` (`local`, `ifood`, `nenhuma`, `desconhecida`), `exigido`, `formato` (numérico/limites quando confirmados), `disponibilidade` (`apta`, `nao_exigida`, `a_confirmar`, `indisponivel`), `confirmaEntregaAoEnviar` e motivo de bloqueio. A presença do código no banco não define elegibilidade oficial.

Backend impede bypass pelos endpoints antigos `/stops/current/deliver`, confirmação manual e handshake legado. Eles preservam canais próprios, mas pedido oficial com requisito remoto exige a operação apropriada, mesmo se o cliente antigo não conhece os campos novos. Inventariar todas as escritas de conclusão/cancelamento/despacho antes de ativar flag.

### Contrato de resultado recuperável

O resultado síncrono atual `{ receipt, queue }` só representa conclusão final. Não acrescentar recibo vazio como se fosse sucesso. Introduzir contrato explicitamente versionado ou endpoint novo para os clientes que suportam operação pendente; decisão de rota definitiva no lote de código.

| Campo proposto | Regra |
|---|---|
| `operationId` | Mesmo identificador persistido no cliente e servidor |
| `pedidoId` | Identidade interna da operação |
| `estado` | `pendente`, `confirmada`, `rejeitada`, `requer_revisao` |
| `resultadoRemoto` | `nao_enviado`, `enviado`, `incerto`, `confirmado`, `rejeitado`; acesso por ator |
| `receipt` | Somente quando finalizado localmente, com confirmação oficial exigida resolvida |
| `queue` | Snapshot atual permitido; não implica sucesso por si só |
| `erro` | Código estável e mensagem simples; sem payload/segredo remoto |
| `podeCorrigir`/`podeReenviar` | Decisão do backend, nunca derivada apenas de HTTP 404/timeout |
| `consultarAposMs` | Orientação de consulta; rate limit e expiração controlados |

Consulta pelo operationId é fonte para recuperar envio. `GET receipt` com 404 significa ausência de recibo final, não ausência de comando/efeito remoto. Distinguir operação pendente, operação ausente e recibo ausente. `POST` repetido com mesma operação e mesmo payload devolve o mesmo estado; payload divergente gera conflito.

Código corrigido após rejeição definitiva gera nova tentativa vinculada à intenção anterior, preservando idempotência de cada payload e garantia de um recebimento/recibo. Nunca reutilizar a mesma operationId com código diferente depois de persistir o hash; nunca criar nova tentativa enquanto resultado anterior for incerto.

Persistir intenção, ownership, hash, pedido/revisões e resultado antes de liberar resposta. HTTP remoto fora de locks longos. Se sucesso remoto ocorrer antes de crash local, recuperação finaliza efeitos locais sem validar novamente código por suposição. Evento oficial pode reconciliar lifecycle; somente prova explícita de validação confirma código. Sucesso/erro do endpoint e efeitos oficiais ainda serão ensaiados em IF-01/18.

## 8. Comandos, cancelamento e disputa

Intenção local e comando remoto têm identidade e revisão. HTTP 202 é recebido, não necessariamente realizado. Cancelamento usa motivo disponível para o pedido atual; não lista de códigos fixa.

Disputa traz disputeId, expiresAt, timeoutAction, ações e alternativas autorizadas. Backend recusa resposta expirada/duplicada/incompatível e projeta settlement. Não apagar histórico se disputa surgir após entrega. Mídias externas não devem ser buscadas por URL arbitrária fornecida pelo navegador; implementar acesso seguro autorizado.

Fonte: [Negociação Order](https://developer.ifood.com.br/en-US/docs/food/guides/modules/order/handshake-platform). Campos remotos e opções variam por cenário; a tela só oferece as alternativas retornadas.

## 9. Erros e comunicação entre aplicações

Propostas de códigos internos novos: `EXTERNAL_LINK_REQUIRED`, `IFOOD_AUTHORIZATION_REVOKED`, `ORDER_MODALITY_UNSUPPORTED`, `ORDER_VERSION_CONFLICT`, `REMOTE_CONFIRMATION_REQUIRED`, `REMOTE_CONFIRMATION_PENDING`, `PAYMENT_REVIEW_REQUIRED`, `CANCELLATION_PENDING`, `DISPUTE_EXPIRED`.

Manter envelope `ApiResponse` e códigos existentes onde semântica coincidir. Mensagens ao operador não incluem stack trace/token/código do cliente. Falha transitória, rejeição de negócio e conflito exigem ações distintas. Nenhuma tela interpreta HTTP 200 com operação pendente como entrega concluída.

Realtime informa mudança da projeção/versão e invalida contexto aberto. Snapshot/reconexão são fonte de recuperação; SignalR não substitui inbox nem garante entrega única. Número/origem são preservados em oferta, rota e recibo e não derivados de nome do cliente.

## 10. Superfícies inspecionadas e alterações futuras

| Repositório | Arquivos reais e impacto identificado |
|---|---|
| Backend | `DTOs/Delivery/PedidoConsultaDtos.cs`: resumo sem número remoto e detalhe com código local; `PedidoQueueDtos.cs`: parada sem identidade externa; `MotoboyPedidoDetalheDto.cs`: origem presente sem identidade oficial completa |
| Backend | `DTOs/Delivery/DeliveryCompletionDtos.cs`, `Controllers/DeliveryCompletionController.cs`: contexto e resultado sem operação pendente; `Repository/PedidoQueueRepository.Completion.cs`: hash de payload, recibo final e comparação local |
| Backend | `Controllers/MotoboyTrackingV2Controller.cs`: `/stops/current/deliver` é entrada alternativa; `Controllers/DeliveryOrdersV2Controller.cs`: mutações operacionais precisam de guardas/capacidades |
| Backend | `Repository/TrackingRepository.cs`, `DTOs/Tracking/TrackingDtos.cs`: mapa legado lê id_ifood/código; `PedidoConsultaRepository.cs`: origem derivada para schema antigo; revisão da busca por número |
| Backend | `Service/PedidoPricing.cs`: quantidade inteira e preços do catálogo; importação oficial terá caminho de snapshot separado |
| Painel | `src/features/delivery/operations/contracts.ts`, `types.ts`, `mappers.ts`: preencher identidade/modalidade/pagamento/estado sem quebrar mapa atual |
| Painel | `src/features/pedidos/pedidosView.ts`, `PedidosScreen.tsx`, `PedidoDetalheDrawer.tsx`: busca, número, ações e tratamento de exceção |
| Painel | `src/features/delivery/operations/orderLifecycle.ts`, `components/OrderPanels.tsx`: fase atual baseada em delivery; acrescentar preparo/remoto separados |
| App | `services/mobileApi.ts`: DeliveryOrder da fila não contém origem/número; OperationalOrderDetail já tem origem; `queueToPedidos`/`toPedido` precisam preservar novos dados |
| App | `services/completionApi.ts`, `src/contexts/DeliveryCompletionContext.tsx`: boolean de código e 404 do recibo não bastam para resultado remoto incerto |
| App | `src/ui/DeliveryCodeScreen.tsx`, `FinishScreen.tsx`, `DeliverySuccessScreen.tsx`, `src/delivery/completionRules.ts`: identidade e requisitos por origem; não cobrar total quando saldo menor |

Inventário é dos pontos centrais e entradas conhecidas; varredura de todas as escritas e permissões continua como pré-requisito do lote que as modificar. Não afirmar auditoria de segurança completa.

## 11. Decisões fechadas documentalmente

1. Identidade interna e número visível são distintos; origem sempre explícita.
2. iFood oficial usa snapshot externo sem reprecificação pelo catálogo próprio.
3. Preparação/remoto/rota/sincronização são eixos independentes.
4. Delivery IFOOD e TAKEOUT não entram na fila Zippy.
5. Código oficial depende de modalidade/capacidade, nunca só da configuração local ou código salvo.
6. Operação pendente é resultado válido e distinto de recibo; recuperação consulta operação antes de reenviar.
7. Novo cliente e servidor devem coexistir com canais próprios; cliente antigo não pode contornar guardas remotas.
8. Cobrança usa saldo validado, com recebimentos locais idempotentes e snapshot oficial preservado.

## 12. Pendências oficiais e critérios de encerramento

Pendências: acesso real/CNPJ; módulos autorizados; endpoint/eligibilidade de código e seus efeitos; comprimento/formato; políticas de pagamento/alteração de meios; fluxo DINE_IN; contratos precisos de transporte/ACK/presença; estado/evidência para conciliação; regras de aceite versus Gestor.

IF-02 possui entrega documental local completa, mas não foi validada em sandbox. IF-03 define desenho funcional no [documento de fluxos](fluxos-produto-ifood.md); validação visual contra protótipo e alteração de interfaces permanecem etapas futuras. Não marcar IF-01, IF-18 ou homologação como concluídas.
