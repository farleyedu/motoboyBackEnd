# Plano da integração iFood no produto Zippy e homologação

Data de referência: 13/10/2026. Idioma: pt-BR.

## 1. Objetivo e autorização

Entregar o backend, o painel administrativo e o app do motoboy com os fluxos completos da integração oficial Food Order/Events, mantendo pedidos iFood diferenciados dos canais próprios. O resultado esperado é uma aplicação pronta para submissão à homologação, validada no ambiente de testes oficial. Aprovação no processo automático e validação final do iFood são marcos externos e não podem ser declarados por inferência.

O usuário aprovou a criação deste plano, IF-02/IF-03, o lote IF-04 de contratos C#, tabelas aditivas e testes em banco isolado e as partes locais de IF-05 (autenticação, segredos e autorização por loja), IF-06 (polling, inbox, ACK durável e recuperação), IF-07 (importação completa preservando dados oficiais), IF-08 (projeção por eventos, cancelamentos, alterações, disputas e ordem variável) e IF-09 (comandos persistentes, prazos e resultados incertos), após apresentação dos escopos. A autorização não inclui commit, push, publicação ou ativação de lojas. Antes de cada novo lote de implementação, apresentar objetivo, alterações, riscos, arquivos e validações e aguardar aprovação explícita. Após aprovação do lote, implementar e validar autonomamente dentro do escopo. Não solicitar novamente autorização para repetir trabalho já coberto pelo mesmo lote.

Este arquivo será o registro de continuidade do trabalho. O DOCX `Zippy_iFood_Plano_Integracao_Segunda_Opiniao.docx` permanece preservado como referência anterior. IF-04 e as partes locais de IF-05/IF-06/IF-07/IF-08/IF-09 foram implementadas e validadas localmente. Polling, importador, projetor e comandos permanecem desabilitados; autenticação, eventos/importação/comandos reais e homologação ainda não foram realizados.

## 2. Escopo e limites

- Backend: `motoboyBackEnd`, .NET 8, PostgreSQL, Dapper, SignalR e workers existentes.
- Painel: `zippy-admin`, React/Next.js, pedidos, modo comando, rotas, configuração, histórico e tratamento de exceções.
- App: `zippygo-motoboy`, React Native/Expo, oferta, rota, conferência, cobrança, código e recibo.
- Canais próprios: cardápio web, WhatsApp e atendente continuam com seus contratos e regras. Simulador é ferramenta de teste, nunca comprovação de integração oficial.
- Categoria inicial proposta: PDV, Food, aplicação centralizada SaaS, módulos Order/Events. Confirmar habilitação real no portal antes de fixar contratos.
- Catalog, Financial, Analytics, Shipping e categoria Logistics não entram automaticamente. Se alguma função exigir outro módulo, registrar a dependência e solicitar decisão antes de ampliar o escopo.
- Não usar scraping, WebView, automação de portal ou handshake não documentado como implementação oficial.
- Não criar backend separado inicialmente. Extrair worker/processo se houver suspensão da hospedagem, necessidade de isolamento, escala ou requisito operacional comprovado.
- Não criar um novo app do cliente nesta entrega. Revisar apenas os recursos próprios de tracking/comunicação que possam receber pedidos iFood.

## 3. Evidências da inspeção e lacunas

Inspeção estática, somente para leitura. Não houve execução de testes, acesso a banco real ou chamadas autenticadas à API iFood nesta avaliação. Caminhos a seguir são relativos a cada repositório.

| Evidência | O que existe | Lacuna para a entrega |
|---|---|---|
| Backend `Model/Delivery/PedidoOrigem.cs` | Origem `ifood`, linhas avulsas e taxa externa permitidas | Origem não representa modalidade, estado remoto ou elegibilidade de código |
| Backend `Service/PedidoCoreService.cs` | Criação centralizada e possibilidade de desativar autoatribuição | Total estruturado calculado como subtotal + taxa; descontos/total remoto não preservados nesse caminho |
| Backend `Service/ManualOrderRules.cs` | Validação de entrega manual | Rua, número, bairro, cidade e coordenadas obrigatórios, inadequados para TAKEOUT/DINE_IN |
| Backend `Repository/PedidoQueueRepository.Orders.cs` | Transação, recuperação por referência e publicação de evento | Pagamento inicial fixado em `Pendente`; falta contrato próprio de importação |
| Backend `Migrations/Delivery/20260925_05_constraints.sql` | Índice por estabelecimento + origem + referência | Verificar aplicação no banco de destino; acrescentar vínculo externo por merchant/order |
| Backend `Repository/PedidoRepository.cs` | Importação legada baseada em `PedidoCapturado` | Usa `DisplayId` em `id_ifood`; índice global pode colidir com números de exibição; evitar importação paralela |
| Backend `Repository/PedidoQueueRepository.Completion.cs` | Conferência local, sessão, versão, pagamento e recibo | Código comparado localmente; requisito depende de configuração da loja e código armazenado, insuficiente para validação oficial |
| Backend `Controllers/EntregaIfoodController.cs` | Chamada específica de handshake | Não constitui autenticação/eventos/ciclo de vida homologados |
| Backend `Services/DeliveryOutboxPublisher.cs` | Outbox com reserva e publicação realtime | Manter separado da inbox e da fila de comandos externos |
| Backend `Atendimento/WaEventoRepository.cs`, `WaEventoWorker.cs` | Fila persistente, deduplicação e recuperação | Reaproveitar padrões após revisão; não compartilhar a semântica das tabelas WhatsApp |
| Backend `Atendimento/TokenProtector.cs` | Cifra de tokens da Meta | Avaliar abstração dedicada, chave forte e rotação para segredos iFood |
| Backend `Model/Enum/StatusPedido.cs` | Situações operacionais de entrega | Não contém toda a preparação nem estados remotos; não mapear tudo para esse enum |
| Backend `Service/CardapioPedidoWebService.cs` | Retirada do canal próprio fora da criação de delivery | Referência útil, mas não substitui contrato de pedido iFood |
| Painel `src/features/pedidos/` | Filtro/rotulagem de origem e detalhes | Identificação principal, ações remotas e tratamento de sincronização precisam ser completos |
| Painel `src/features/delivery/mappers.ts` | Mapeamento usa `idIfood` em situações legadas | Separar número visível de ID externo; atualizar todos os consumidores |
| App `services/completionApi.ts`, `src/contexts/DeliveryCompletionContext.tsx` | Contexto versionado, código, operação persistida e recuperação | Contrato não inclui identidade/origem iFood nem estado da confirmação externa |
| App `src/ui/DeliveryCodeScreen.tsx` | Código validado permanece visível/desabilitado | Quatro dígitos fixos e título pelo ID interno; adaptar ao contrato oficial |

O AGENTS.md do app contém referências antigas de stack/caminhos. Respeitar suas regras funcionais e visuais, mas usar como autoridade técnica o código atual e o ambiente inspecionado: PostgreSQL no backend e os caminhos presentes neste workspace.

## 4. Regras obrigatórias do produto

### 4.1 Identificação e origem

- iFood: etiqueta textual iFood e número de exibição oficial como identificação principal para equipe e motoboy. Exemplo: `iFood #1234`; ID interno apenas secundário em detalhes/suporte.
- Canais próprios: número Zippy e origem específica, sem rotular todo pedido próprio como WhatsApp.
- Campos distintos: `pedidoId` interno, `externalOrderId` completo, `externalDisplayNumber`, `merchantId`, `provider`, `localizer`, quando disponíveis. Nomes definitivos serão compatibilizados com os padrões dos DTOs.
- Número de exibição e localizador não são chaves únicas de pedido nem credenciais de autorização. Preservar zeros à esquerda usando strings.
- Evento é deduplicado por provider/merchant/eventId; efeito de criação por provider/merchant/orderId, além da restrição local por estabelecimento/origem/referência.
- Dados legados em `id_ifood` não devem ser promovidos a UUID oficial por suposição. Registrar origem legada e migrar somente com correspondência comprovada.
- O núcleo novo não preenche automaticamente todos os campos legados. Inventariar leituras de `id_ifood` e atualizar DTOs/queries/mappers para evitar ausência de identificação após importação nova.

### 4.2 Matriz de modalidade

| Origem e modalidade | Operação da loja | Fila/rota Zippy | Confirmação |
|---|---|---|---|
| Zippy com entrega própria | Fluxo atual | Permitida | Política local existente |
| Zippy com retirada | Fluxo próprio de retirada | Não entra | Fluxo próprio de retirada |
| iFood DELIVERY/MERCHANT | Aceite, preparo, pronto e despacho permitidos | Permitida após elegibilidade operacional | Validação oficial do código quando aplicável |
| iFood DELIVERY/IFOOD | Aceite/preparo/pronto e acompanhamento permitido | Nunca atribuir a motoboy Zippy | Entregador iFood realiza seu fluxo; painel acompanha eventos |
| iFood TAKEOUT | Aceite, preparo, pronto e retirada | Não entra | Fluxo oficial de retirada/código quando aplicável, executado pela loja |
| iFood DINE_IN ou outra modalidade habilitada | Fluxo específico documentado | Não entra automaticamente | Validar contrato e ações aplicáveis |
| Modalidade/evento desconhecido | Exceção visível e diagnóstico | Bloqueada até classificação | Não inventar estado ou ação |

Retirada e logística iFood continuam visíveis na operação da loja mesmo sem entrega Zippy. A homologação deve abranger todos os tipos exigidos para os módulos e perfil habilitados; não ocultar um tipo obrigatório para reduzir trabalho.

### 4.3 Estados independentes

Manter dimensões separadas: estado remoto iFood; preparação local (aguardando aceite/em preparo/pronto); situação operacional da entrega; estado de sincronização do comando; confirmação externa; pagamento e eventual disputa. Nomes abaixo são conceituais, sujeitos ao contrato final.

- Confirmar localmente não equivale a `/confirm` remoto.
- Atribuir motoboy não equivale a despachar; coleta/saída deve respeitar preparo, prontidão e regras remotas.
- Pedido agendado pode precisar de aceite imediato, embora preparo/despacho sejam futuros. Não aguardar o horário de entrega para aceitar.
- Resposta `202` de cancelamento significa solicitação recebida; concluir cancelamento somente com confirmação correspondente.
- Conclusão remota por timeout não comprova entrega física do motoboy. Preservar evidência e gerar divergência se o estado local for incompatível.
- Encerramento automático do expediente não pode cancelar, concluir ou arquivar silenciosamente obrigações remotas, pedidos agendados ou disputas.
- Evento antigo não deve regredir estado. Tratar ordem variável por pedido e consultar o estado oficial quando houver conflito; não presumir ordenação apenas pelo recebimento.

### 4.4 Valores, pagamento e dados de cliente

- Preservar valores oficiais, total, taxas adicionais, entrega e benefícios; distinguir financiador e valor a cobrar do cliente. Não aplicar preços/taxas do cardápio próprio a pedido externo.
- Identificar pago online e cobrança na entrega, meios múltiplos, bandeira quando informada, valor em dinheiro e troco.
- Total do pedido não é necessariamente saldo a cobrar nem repasse do restaurante. Não prometer conciliação financeira sem módulo correspondente.
- Quantidades e unidades externas não devem ser convertidas cegamente para inteiro; validar tipos exigidos no perfil Food e não perder informação.
- Preservar observações e adicionais, inclusive pizza/meias quando presentes, sem exigir correspondência de catálogo local.
- Dados ausentes ou telefone mascarado não impedem importação legítima. Não identificar cliente real por um telefone intermediado nem alimentar CRM/WhatsApp automaticamente.
- Não gerar endereço fictício/coordenada da loja para fazer retirada passar na validação de delivery.
- Restringir retenção de payloads e informações pessoais; definir acesso, expiração, limpeza e mascaramento.

## 5. Confirmação por código e conclusão

A validação oficial pode confirmar a entrega no iFood, e não apenas conferir o formato do código. Isso exige alterar o desenho atual em que o botão de validar chama uma conferência local e a conclusão ocorre depois por arraste.

Fluxo de produto proposto para pedido iFood elegível:

1. Backend retorna contexto autorizado com origem, número oficial, modalidade, versão, requisitos de código/pagamento/prova e situação externa.
2. Motoboy informa código e confere cobrança/prova aplicáveis. Antes do envio oficial, mostrar `Código informado, aguardando confirmação`; não mostrar código oficialmente validado.
3. Gesto explícito de conclusão cria uma operação persistente, revalida sessão/rota/versão e registra intenção no backend.
4. Backend executa o comando oficial fora de uma transação longa. Persistir resultado e finalizar os efeitos locais necessários de forma recuperável.
5. Após confirmação oficial, código fica visível e desabilitado com sucesso; recibo distingue confirmação oficial, recebimento local e pagamento.
6. Se a resposta se perder, mostrar `Confirmação pendente`; conciliar evento/estado remoto e consultar operação antes de reenviar. Não liberar sucesso falso ou repetir cobrança.
7. Erro de código permite correção; cancelamento ou perda de autorização exige atualização do contexto. Cliente sem código abre ocorrência e orientação conforme fluxo permitido, sem bypass que simule confirmação iFood.

Para canal próprio, preservar comportamento de conferência local e arraste. O fluxo iFood proposto deve ser confirmado contra o contrato real de `/verifyDeliveryCode` e os protótipos aprovados antes de editar telas. Se o contrato oficial não confirmar entrega como efeito da chamada, registrar a evidência e ajustar a sequência explicitamente.

- Código do cliente, código de coleta do entregador iFood e localizador são distintos.
- Configuração local de exigir código não pode desligar um requisito remoto obrigatório.
- Validar comprimento/formato conforme API habilitada, sem fixar quatro dígitos por suposição.
- Não armazenar/logar código em texto puro desnecessariamente; persistência mínima e protegida se imprescindível à recuperação, com limpeza após uso/expiração.
- Pagamento não deve ser marcado ao abrir tela ou apenas digitar código. Registrar recebimento de forma idempotente após ação explícita, mantendo pendência externa distinta se necessário.
- Definir tratamento da rota enquanto uma operação permanece pendente. A regra inicial é não avançar automaticamente sem resolução; eventual intervenção deve ser auditada e não declarar confirmação oficial.
- Disponibilidade de validação para TAKEOUT e requisitos de logística/coleta serão confirmados no módulo Order habilitado. Não importar regras da API Logistics para PDV sem verificar aplicabilidade.

## 6. Arquitetura e persistência propostas

Criar `Integrations/Ifood/` no backend, seguindo convenções finais do projeto, com cliente HTTP tipado, autenticação centralizada, receptor, normalizador, processador, despachante de comandos e conciliação. Serviços internos executam casos de uso; controllers não contêm regras nem SQL avulso.

| Estrutura conceitual | Responsabilidade e restrições |
|---|---|
| Conexões | Provider/merchant/estabelecimento, autorização, status; credencial da aplicação centralizada separada da autorização por loja |
| Inbox | Evento bruto, identificadores, hash/versão quando necessário, estado, tentativas, reserva, erro e retenção; índice único provider/merchant/event |
| Vínculos de pedido | Provider/merchant/order UUID/pedido interno/número visível; unicidade e integridade do estabelecimento |
| Projeção do pedido externo | Modalidade, estado remoto, preparo, horários, pagamento/benefícios e dados necessários à operação |
| Comandos externos | Operação/correlação, pedido, ação, estado, tentativas, reserva, resultado e prazo; chave lógica contra comandos duplicados |
| Disputas | disputeId, prazo, ação de timeout, opções permitidas, resposta e settlement; histórico preservado |
| Outbox local existente | Publicação para painel/app após transação; sem misturar recibo externo ou inbox |

Regras técnicas:

- Persistir evento antes de ACK/resposta de recebimento. Se banco indisponível, não confirmar algo que não pode ser recuperado.
- Mesmo com banco e API sem transação distribuída, garantir efeito local único e conciliar efeito remoto incerto. Não prometer exactly-once de rede.
- Reservas com expiração, renovação e identificação do proprietário impedem processamento concorrente indevido. Testar reinício e múltiplas instâncias.
- Payload desconhecido vai para revisão, sem derrubar lote nem descartar evento silenciosamente.
- Respostas 401/403/409/422/429/5xx e timeout têm tratamentos próprios. Retry somente quando seguro, com limites, espera progressiva e orientação de rate limit.
- Token centralizado usa credenciais da aplicação; acesso à loja depende da permissão do merchant. Não desenhar um client secret por restaurante sem necessidade do modelo oficial.
- Registrar revogação de autorização e impedir operação de outra loja, inclusive código, detalhes, disputa, reprocessamento e comprovantes.
- Requests externas não devem manter locks de fila/rota por todo o timeout HTTP.
- Migrações aditivas, verificações e estratégia de rollback. Não eliminar registros anteriores nem criar comandos remotos ao executar rollback.
- Flags por loja/ambiente e configuração validada ao iniciar. Mocks isolados e inacessíveis em produção.

## 7. Transporte e requisitos oficiais

Decisão proposta: polling para primeira prova autenticada; transporte de submissão definido em IF-01 após verificar portal e hospedagem. Webhook é adequado ao SaaS centralizado, mas deve ser entregue integralmente se escolhido. Abstrair entrada para não duplicar domínio.

- Polling: o critério Events atual pede execução a cada 30 segundos, filtro por merchant e ACK de todos os eventos, inclusive repetidos, após persistência rápida. Confirmar endpoint/versão na API Reference.
- Webhook: validar assinatura sobre bytes originais, HTTPS e resposta de recebimento após inbox durável. Adotar meta interna de até 2 segundos e conferir contrato de resposta vigente; as páginas consultadas apresentam limites diferentes de 2, 5 e 30 segundos. Não usar o maior limite como meta operacional.
- ACK explícito e resposta de webhook não devem ser tratados como mecanismos idênticos. A página Order generaliza ACK, enquanto Events distingue transportes: validar o comportamento na documentação específica/API Reference e sandbox antes de codificar.
- Presença/healthcheck da loja precisa seguir Events e o perfil PDV. `excludeHeartbeat=true` aparece como obrigação de Logistics e não será aplicado indiscriminadamente ao PDV.
- Escolher um transporte por execução do wizard. Se houver webhook com polling de contingência, validar fallback manualmente e registrar evidência, pois o wizard não cobre a combinação.
- A fonte Order consultada exige aceite de DELIVERY/TAKEOUT em até 8 minutos, inclusive agendados. Criar alertas antes do prazo e validar o SLA vigente no ambiente habilitado.
- Respeitar `readyToPickup` antes de `dispatch` para entrega própria e fluxo completo de TAKEOUT, conforme critério atual.
- Não confirmar automaticamente por padrão. Definir responsável pelo aceite entre Gestor e Zippy e testar pedidos já confirmados para evitar conflito operacional.

## 8. Experiência por aplicação

### 8.1 Painel administrativo

- Lista geral e modo comando mostram iFood/número oficial; busca por número remoto, ID interno e cliente autorizado, com filtro de origem/modalidade.
- Cartões e detalhes apresentam itens, observações, valores, pagamento, modalidade, janela agendada, preparação, status remoto e sincronização.
- Operação de cozinha acessível para todos os pedidos aplicáveis, mesmo sem pins no mapa.
- Confirmar/preparar/pronto/despachar/cancelar aparecem somente quando permitidos; botão bloqueado explica condição real.
- Lista de motivos de cancelamento vem da API para aquele pedido. Solicitação pendente e rejeição não são mostradas como cancelamento concluído.
- Disputas têm alerta, prazo, evidências permitidas, ações disponíveis e resolução. Sem opção fixa que o payload não autoriza.
- Logística iFood não aparece como trabalho disponível aos motoboys Zippy; coleta por entregador iFood recebe identificação apropriada.
- Configuração mostra merchant vinculado, autorização, conectividade, último evento e erro acionável. Segredos nunca retornam ao navegador.
- Histórico/recibo/comanda usam número iFood e não exibem código secreto do cliente indevidamente. Localizador, quando permitido, recebe rótulo próprio.
- Atender atualização concorrente e cancelamento com modal aberto; refazer contexto antes de ação.
- Métricas não contam pedido espelhado duas vezes; separar receita do pedido, cobrança e remuneração de motoboy.

### 8.2 App do motoboy

- Oferta, rota, checklist, chegada, cobrança, código e recibo mostram origem/número principal de forma consistente.
- Recebe apenas entregas MERCHANT elegíveis, preservando sessão operacional, isolamento por loja e regras de coleta/chegada.
- Contexto fornece requisitos por pedido, e não apenas configuração global da loja.
- Pago online não mostra cobrança; cobrança parcial usa saldo efetivo, não total bruto; meios/troco disponíveis seguem contrato.
- Reaproveitar persistência/recovery da conclusão, acrescentando resultado oficial e renovação de contexto.
- Mudança de motoboy/turno/pedido durante a request não valida o próximo pedido com o resultado anterior.
- Cancelamento em rota gera aviso e ocorrência; não mantém conclusão ativa sobre pedido cancelado.
- Código, erro, rede indisponível e código ausente têm instruções simples, mantendo padrões de acessibilidade e telas aprovadas.
- Validar Android em ambiente nativo para persistência/rede/retomada. Navegador com mocks não comprova estes comportamentos.

### 8.3 Canais próprios e acompanhamento

- Preservar criação manual, cardápio, retirada própria, WhatsApp, preços e regras locais.
- Não ativar comunicação Zippy para telefone intermediado iFood automaticamente. Reutilizar bloqueios existentes de atendimento quando aplicáveis.
- Revisar tracking público: autorização, dados permitidos, expiração e exposição da localização. Não criar links sem requisito e base operacional definidos.
- Conteúdo comercial não deve afirmar integração homologada antes da aprovação final. Alteração de site/publicação exige solicitação própria.

## 9. Arquivos previstos por lote

Os caminhos existentes abaixo foram identificados no código. Novos nomes são propostas, não arquivos já criados. Antes de editar um lote, confirmar localização, padrões e alterações existentes com `git status` e inspeção somente para leitura.

| Lote | Backend | Painel | App |
|---|---|---|---|
| Contratos | `DTOs/Delivery/DeliveryOperationDtos.cs`, `PedidoQueueDtos.cs`, `PedidoConsultaDtos.cs`, `DeliveryCompletionDtos.cs`; contratos novos em `Integrations/Ifood/` | `src/features/delivery/operations/contracts.ts`, `types.ts`, `src/features/pedidos/` | `services/completionApi.ts` e contratos de fila/oferta a localizar |
| Persistência | Novas migrações `Migrations/Delivery/`; repositórios iFood; `PedidoQueueRepository.Orders.cs`, `PedidoConsultaRepository.cs`, interfaces correspondentes | Consumidores dos DTOs | Consumidores dos DTOs |
| Ingresso e valores | `Program.cs`, options/DI, módulo iFood; `PedidoCoreService.cs`, `PedidoPricing.cs`, `ManualOrderRules.cs`, `PedidoRepository.cs`, `PedidoController.cs` apenas se necessário à compatibilidade | `mappers.ts`, `operations/pedidosApi.ts`, `operatorApi.ts` | Mapeamentos de pedido/oferta a localizar |
| Operação | `DeliveryOrdersV2Controller.cs`, serviços/repositórios de pedido/rota; `EncerramentoService.cs`, `PedidoQueueRepository.Encerramento.cs`; comandos/disputas novos | `operations/orderLifecycle.ts`, `components/OrderPanels.tsx`, `hooks/useDeliveryData.ts`, `PedidosScreen.tsx`, `PedidoDetalheDrawer.tsx`; componentes iFood novos | `src/delivery/offerDetails.ts`, `src/ui/RouteDeck.tsx`, contextos de operação afetados |
| Conclusão | `DeliveryCompletionController.cs`, `PedidoQueueRepository.Completion.cs`, `DeliveryCompletionRules.cs`, `EntregaIfoodController.cs` conforme substituição compatível | Contexto/histórico da confirmação, recibos e simulador afetados | `src/contexts/DeliveryCompletionContext.tsx`, `src/delivery/completionRules.ts`, `src/ui/DeliveryCodeScreen.tsx`, `FinishScreen.tsx`, `DeliverySuccessScreen.tsx`, kits e rotas afetados |
| Validação | `Tests/Unit/`, `Tests/Integration/`, `docs/validacao-ifood/` | Testes próximos às features e relatório em `docs/` | Testes Jest existentes/novos e relatório em `docs/` |

Materiais auxiliares ficam em `docs/`. Não editar todos os arquivos preventivamente; a lista delimita pontos de investigação e implementação. Preservar estilos, protótipos e regras funcionais do app. Evidência funcional e visual são registros distintos; capturas temporárias seguem a regra de limpeza do AGENTS.md do app.

## 10. Lista de tarefas e acompanhamento

Estados permitidos: `Pendente`, `Em andamento`, `Bloqueada`, `Implementada`, `Validada localmente`, `Validada no sandbox`, `Homologada`, `Não aplicável com justificativa`.

Implementada não equivale a validada. Marcar bloqueio com causa, próxima ação e responsável. Credenciais pendentes não impedem tarefas locais independentes, mas impedem declarar validação oficial. IF-02/IF-03 tiveram seus artefatos documentais produzidos e IF-04/IF-05 passaram nos testes locais; os registros distinguem essas entregas de fluxo oficial, validação visual e testes de sandbox.

| ID | Entrega e aceite | Dependências | Estado inicial |
|---|---|---|---|
| IF-00 | Regularizar CNPJ/CNAE, conta profissional, proprietário do app e loja/credenciais de teste; registrar acesso sem segredos | Usuário/contador/portal | Pendente |
| IF-01 | Confirmar PDV/Food, módulos, transporte, URLs/versionamento, presença, requisitos atuais e poderes do app; matriz de aplicabilidade aprovada | IF-00 para prova real | Pendente |
| IF-02 | Inventariar queries/DTOs/telas/legado; fechar identidade, modalidades, estados, valores, privacidade e casos de uso | Inspeção local; contratos oficiais pendentes em IF-01 | Implementada |
| IF-03 | Definir desenho funcional e sequência do código/arraste, cancelamento, retirada e erros nas aplicações; validar dependências oficiais antes de telas | IF-02; contrato IF-01 para confirmação remota | Implementada |
| IF-04 | Implementar contratos e schema passivo, constraints e rollback; preservar legado sem backfill presumido/ativação e testar duas lojas | IF-02 | Validada localmente |
| IF-05 | Autenticação centralizada, segredos, expiração e revogação; nenhum segredo no painel/app/log; prova autenticada e confirmação de permissões pendentes | IF-01, IF-04 | Validada localmente |
| IF-06 | Inbox, polling ou webhook completo, ACK correto, reservas e recuperação; evento repetido não duplica efeito; transporte entregue: polling, prova oficial pendente | IF-04, IF-05 | Validada localmente |
| IF-07 | Importação externa completa sem recalcular preços; modalidades sem endereço/cadastro próprio obrigatório; prova autenticada pendente | IF-02, IF-04, IF-06 | Validada localmente |
| IF-08 | Projeção e atualização por eventos; cancelamento/patch/disputa/estado desconhecido e ordem variável; variantes incompletas exigem conciliação e prova oficial | IF-06, IF-07 | Validada localmente |
| IF-09 | Comandos persistentes de confirmar/preparar/pronto/despachar; SLA/agendamento e resultados incertos; capacidades/regra de prazo/prova autenticada pendentes | IF-05, IF-07, IF-08 | Validada localmente |
| IF-10 | Cancelamento com motivos dinâmicos, rejeição e evento final; negociações aplicáveis com deadline/settlement | IF-08, IF-09 | Validada localmente |
| IF-11 | Painel completo de pedidos/preparo/retirada/iFood, identidade e sincronização; ações por capacidade | IF-03, IF-07 a IF-10 | Pendente |
| IF-12 | Fila/rota/oferta compatíveis; bloqueio IFOOD/TAKEOUT e de despacho prematuro; cancelamento/encerramento em rota | IF-07 a IF-09 | Pendente |
| IF-13 | Backend de código oficial e conclusão persistente; recuperação sem validar localmente pedido remoto | IF-03, IF-09, IF-12 | Pendente |
| IF-14 | App com identidade, cobrança e conclusão oficial; feedback, recibo e recovery nativo | IF-03, IF-12, IF-13 | Pendente |
| IF-15 | Configuração/permissões/diagnóstico; proteção de dados, retenção e prevenção de bypass | IF-04 a IF-14 | Pendente |
| IF-16 | Instrumentação, alertas, conciliação e contingência; verificar hospedagem sempre ativa e múltiplas instâncias | IF-06 a IF-15 | Pendente |
| IF-17 | Regressão dos canais próprios e falhas distribuídas; contratos backend/painel/app e validação visual | IF-07 a IF-16 | Pendente |
| IF-18 | Ensaiar checklist oficial integral no sandbox com código real das aplicações e registrar evidências | IF-00, IF-01, IF-17 | Pendente |
| IF-19 | Submeter quando solicitado, analisar relatório e corrigir; registrar aprovação automática e validação final separadas | IF-18; autorização de submissão | Pendente |
| IF-20 | Autorizar/ativar loja piloto quando solicitado; acompanhar pedidos e recuperar incidentes | IF-19; autorização comercial/loja | Pendente |

IF-02/03 e desenho de IF-04 podem avançar sem CNPJ. Não implementar URLs, formatos de código ou regras remotas como fatos confirmados antes de IF-01. Não esconder tarefas bloqueadas nem substituir seu aceite por mock.

### Registro por tarefa

Ao iniciar/concluir uma tarefa, acrescentar abaixo um registro com este formato:

```text
ID: IF-xx
Estado:
Data:
Objetivo e requisito oficial/produto:
Autorização do lote:
Arquivos alterados:
Decisões e dependências:
Validação funcional executada e resultado:
Validação visual executada e resultado:
Evidência em docs/:
Limitações e bloqueios reais:
Próximo passo:
```

## 11. Matriz de homologação e evidências

Consultar novamente fontes/API Reference ao fechar IF-01 e imediatamente antes de IF-18. Nenhum item aplicável pode ficar somente simulado. A coluna referência aponta à seção 15.

| ID | Requisito e implementação | Tarefas | Evidência de aceite | Referência |
|---|---|---|---|---|
| H-01 | Conta profissional, app funcional, credenciais e merchant de teste | IF-00/01/18 | Cadastro e execução autenticada sem expor segredos | R1/R2/R3 |
| H-02 | Receber eventos pelo transporte escolhido e confirmar recebimento corretamente | IF-06 | Evento real persistido; polling+ACK ou webhook+assinatura/resposta conforme contrato | R3/R7/R8 |
| H-03 | Confirmar DELIVERY/TAKEOUT, inclusive agendado, dentro do SLA | IF-09/11 | Ação no painel, resposta/evento e horários | R2 |
| H-04 | Consultar motivos e solicitar cancelamento; tratar confirmação/rejeição | IF-08/10/11 | Ação real, motivo, solicitação e evento final | R2/R9 |
| H-05 | Processar cancelamento iniciado por cliente/plataforma e negociação aplicável | IF-08/10/11 | Disputa, prazo, resposta e settlement; cancelar após despacho | R2/R9/R10 |
| H-06 | Exibir pagamento/bandeira e calcular troco quando aplicável | IF-07/11/14 | Conferência dos detalhes oficiais, tela e recibo sem cobrança duplicada | R2/R6 |
| H-07 | Exibir observações e informações completas dos itens | IF-07/11/14 | Pedido/comanda/tela comparados com detalhes oficiais | R2/R6 |
| H-08 | Fluxo TAKEOUT pronto para retirada | IF-07/09/11/13 | Pedido real sem rota Zippy; readyToPickup e conclusão aplicável | R2/R5 |
| H-09 | Entrega própria pronta antes de dispatch | IF-09/12/14 | Ordem de comandos comprovada e tentativa prematura bloqueada | R2 |
| H-10 | Confirmar código elegível pelo endpoint oficial Order | IF-13/14 | Código válido/inválido, request real e estado/recibo coerente | R5 |
| H-11 | Token renovado conforme expiração e limites respeitados | IF-05/16 | Logs sem segredo, expiração/401/429 e retomada | R2/R4 |
| H-12 | App completa no wizard: conectividade, confirmar, cancelar, despachar e concluir | IF-18/19 | Relatório do portal e gravação/registro funcional dos sistemas | R2/R3 |
| H-13 | Webhook + fallback, se adotado | IF-06/16/18 | Teste manual com endpoint indisponível e polling; nenhum pedido perdido/duplicado | R3 |
| H-14 | Outros critérios/módulos efetivamente habilitados | IF-01/18 | Checklist específico e justificativa de aplicabilidade | R1/API Reference |

O critério técnico de webhook adotará meta interna mais estrita e persistência rápida. Eventual conflito entre páginas deve ser resolvido por referência específica e prova em sandbox, com registro de decisão; não escolher silenciosamente o comportamento conveniente.

## 12. Matriz de testes do produto

| Grupo | Cenários mínimos e resultado esperado |
|---|---|
| Identidade | Dois merchants com mesmo número visível; zeros à esquerda; evento duplicado; pedido já importado; legado ambíguo; IDs sempre separados |
| Valores | Desconto de loja/plataforma, taxa adicional, adicionais/pizza, pagamento online/presencial/misto, troco; valores e saldo a cobrar iguais à informação oficial |
| Modalidades | MERCHANT, IFOOD, TAKEOUT e demais exigidas; somente entrega elegível na fila; retirada sem endereço fictício |
| Tempo | Imediato/agendado, virada do dia, fuso/janela, pedido atrasado e expediente encerrado; aceite e obrigação remota preservados |
| Eventos | Repetido, fora de ordem, desconhecido, patch, cancelamento antes/depois de despacho/conclusão; nenhum efeito duplicado ou regressão indevida |
| Concorrência | Dois workers, dois atendentes, clique duplo, troca de motoboy/turno e cancelamento concorrente; operação consistente |
| Código | Válido/inválido, comprimento oficial, ausente, expirado, já confirmado, não elegível, resposta perdida e reinício; validação oficial obrigatória respeitada |
| Falhas | API/banco/rede indisponíveis, 401/403/429/5xx, timeout após efeito remoto, reinício entre persistência e envio; estado pendente recuperável |
| Segurança | Tentativa cross-tenant, sessão vencida, request forjada, assinatura inválida, permissão revogada, acesso a código/prova/log; nenhum vazamento ou bypass |
| Pagamento e conclusão | App fechado após arraste, request repetida, recebimento confirmado com pendência externa; um recebimento/recibo por operação |
| Visual | Lista/detalhes/modo comando/rota/código/cobrança/recibo, claro/escuro e larguras suportadas; origem e número legíveis, erros e pendências claros |
| Regressão | Manual, cardápio entrega/retirada, WhatsApp, confirmação local, autoatribuição, provas, fila, rastreio, remuneração e encerramento; comportamento anterior preservado |

Comandos previstos, somente durante implementação/validação autorizada:

- Backend: `dotnet build` e `dotnet test` com seleção de testes adequada; testes de banco em ambiente isolado com migrações aplicadas. Não usar produção.
- Painel: scripts existentes `npm run typecheck`, `npm run lint`, `npm run test`, `npm run build`; conferir compatibilidade do script lint com a versão instalada antes de executar/corrigir. Não migrar tooling fora do escopo.
- App: TypeScript usando dependências locais e Jest sem watch (`npm test -- --watchAll=false`), além de execução nativa Android. Compilação nativa depende das ferramentas disponíveis; browser não substitui aparelho/emulador.
- Funcionais: percurso real painel -> backend -> app -> iFood -> atualização do painel, incluindo retirada operada pela loja e logística iFood sem rota Zippy.
- Documentar testes executados, não executados e falhas. Fixtures sintéticas/oficiais devem ser identificadas e sem dados pessoais reais.

## 13. Critérios de saída e implantação

### Pronta localmente

IF-02 a IF-17 concluídas nas partes aplicáveis, builds/testes relevantes aprovados, contrato entre aplicações consistente, nenhum segredo exposto, nenhuma regressão crítica aberta. Não chamar esse marco de homologado.

### Pronta para submissão

IF-00/01 resolvidas e IF-18 concluída: matriz H preenchida com evidências reais, todos os cenários obrigatórios operacionais nas interfaces finais, credenciais/configuração corretas, hospedagem contínua e recuperação demonstrada. Não usar somente chamadas curl ou simulador.

### Homologada

Registrar resultado automático, relatório, eventuais correções e aprovação final do iFood. Submissão pelo proprietário do app; intervalo de novas tentativas conforme portal, hoje documentado em quatro horas. Não confundir aprovação automática com liberação definitiva.

### Piloto autorizado

Ativação depende de solicitação do usuário, acesso aprovado pelo restaurante e liberação oficial. Obter nova autorização de loja pelo fluxo centralizado, associar merchant e gerar token conforme contrato. Não publicar ou ativar durante a elaboração do plano.

Antes de piloto, preparar procedimento de desligar ingresso/comandos sem perder obrigações existentes, orientação para retomar operação no Gestor, prevenção de duplo aceite e recuperação de eventos. Remover flag não é suficiente para abandonar comandos/disputas pendentes. Critérios de parada: perda de pedidos, cobrança duplicada, mistura entre lojas, despacho indevido ou confirmação incorreta.

## 14. Riscos, dependências e decisões abertas

| Tema | Tratamento e condição de fechamento |
|---|---|
| CNPJ/CNAE | Requisito externo; usuário e contador definem empresa/atividade. CNPJ do fornecedor e CNPJ do restaurante são papéis diferentes |
| PDV versus Logistics | Categoria PDV para pedidos do restaurante; não assumir poderes da API de integradora logística. Fechar em IF-01 |
| Código oficial e arraste | Conferir efeito de verifyDeliveryCode e aprovar sequência das telas em IF-03; não manter botão que conclua remotamente sem avisar |
| Identidade legada | Número visível não vira UUID; verificar índice global e evitar importadores paralelos |
| Duplicação e perda | Inbox, vínculo único, operação persistente, reservas e conciliação; falhas reais ensaiadas |
| Financeiro | Valores externos preservados; cobrança e repasse distintos; escopo Financial depende de decisão futura |
| Cancelamento e negociação | Deadline, timeoutAction, solicitação versus evento final e cancelamento em rota; UI e histórico completos |
| Hospedagem | Verificar sleep, deploy/restart, URL HTTPS, healthcheck, réplicas, relógio e alertas. Separar worker se comprovadamente necessário |
| Atendimento/privacidade | Telefone intermediado não autoriza contato; limitar armazenamento/exposição e impedir identificação errada de clientes |
| Critérios em evolução | Revalidar fontes/API Reference, versões e módulos em IF-01/18 e registrar divergências |
| Prazo | Sem cronograma fechado antes de acesso e matriz oficial. Estimar cada lote após IF-02/03; separar engenharia de espera externa |

Decisões já propostas: backend modular, identificação principal pelo número iFood, estados separados, integração oficial, código validado oficialmente quando exigido, nenhuma atribuição Zippy para logística iFood/retirada. Confirmar transporte de produção, estratégia de aceite, ações DINE_IN, formatos de código, retenção e impacto financeiro/operacional antes do respectivo lote.

## 15. Referências oficiais

Consultadas para planejamento em 13/10/2026. Páginas públicas não comprovam módulos habilitados na conta do usuário. Trechos foram obtidos via busca; alguns acessos diretos ao portal retornaram 403. Confirmar conteúdo/API Reference no portal ao obter acesso.

- R1: [Categorias e política de homologação](https://developer.ifood.com.br/en-US/docs/getting-started/homologation/categories).
- R2: [Critérios de homologação Order](https://developer.ifood.com.br/en-US/docs/food/guides/modules/order/homologation).
- R3: [Critérios de homologação Events](https://developer.ifood.com.br/en-US/docs/food/guides/modules/events/homologation).
- R4: [Autenticação centralizada](https://developer.ifood.com.br/en-US/docs/food/guides/modules/authentication/centralized) e [autorização de lojas](https://developer.ifood.com.br/en-US/docs/getting-started/first-steps/request-access).
- R5: [Endpoints Order, incluindo código de entrega e coleta](https://developer.ifood.com.br/en-US/docs/food/guides/modules/order/endpoints).
- R6: [Detalhes de pedido](https://developer.ifood.com.br/en-US/docs/food/guides/modules/order/details).
- R7: [Request e assinatura de webhook](https://developer.ifood.com.br/en-US/docs/food/guides/modules/events/webhook-request).
- R8: [Boas práticas de webhook](https://developer.ifood.com.br/en-US/docs/food/guides/modules/events/webhook-best-practices).
- R9: [Cancelamento de pedido](https://developer.ifood.com.br/en-US/docs/food/guides/modules/order/cancellation).
- R10: [Plataforma de negociação](https://developer.ifood.com.br/en-US/docs/food/guides/modules/order/handshake-platform) e [guia de testes](https://developer.ifood.com.br/en-US/docs/food/guides/modules/order/handshake-platform-guide).
- R11: [Portal e requisitos cadastrais](https://developer.ifood.com.br/pt-BR/).
- R12: [Eventos de pedido](https://developer.ifood.com.br/en-US/docs/food/guides/modules/order/events).

## 16. Registro de continuidade

### 13/10/2026 — Criação do plano

- Resultado: plano criado após aprovação explícita do usuário, abrangendo produto, arquitetura, arquivos, tarefas e homologação.
- Implementação: não iniciada. Nenhum fluxo novo está implementado ou validado no sandbox por este trabalho.
- Arquivo autorizado: somente este plano em `motoboyBackEnd/docs/`.
- Evidência disponível: inspeção estática dos três repositórios e referências públicas. DOCX existente preservado.
- Validação documental: 21 tarefas IF-00 a IF-20 sem duplicação/ausência, 14 critérios de homologação, tabelas com colunas consistentes, blocos de código balanceados e leitura UTF-8 sem caracteres de substituição. Não foram executados testes de aplicação por se tratar de alteração exclusivamente documental.
- Dependências externas: empresa/conta profissional, credenciais, módulos, merchant de testes, homologação e autorização da loja.
- Próximo lote proposto: IF-02/IF-03, inventário completo e contratos/desenho funcional, com confirmação de requisitos oficiais em paralelo à regularização cadastral. Apresentar arquivos e escopo concreto para aprovação antes de editar.

### 13/10/2026 — Lote IF-02/IF-03

- Autorização: usuário solicitou executar o primeiro lote e anotar o trabalho; escopo documental informado antes da edição.
- Resultado IF-02: criado [contratos-produto-ifood.md](contratos-produto-ifood.md), com identidade por ator, modalidade/tempo, eixos de estado, capacidades, pagamento por saldo, conclusão recuperável, compatibilidade legada e inventário dos pontos de código inspecionados.
- Resultado IF-03: criado [fluxos-produto-ifood.md](fluxos-produto-ifood.md), com percursos de aceite, preparo/despacho próprio, logística iFood, retirada, código/arraste, cobrança, cancelamento/disputa e recuperação, incluindo critérios funcionais/visuais.
- Descobertas: `DeliveryOrder` da fila do app não carrega origem/número; detalhe operacional já carrega origem. DTO de conclusão devolve recibo final, sem operação pendente. Existe entrada alternativa `/stops/current/deliver`. Ausência de recibo não prova ausência de efeito remoto. Union de meios do app não inclui todos os métodos externos. Esses pontos foram incorporados aos contratos.
- Decisões: preservar ID interno e número remoto separados; cobrar saldo oficial validado; introduzir resultado de operação pendente com contrato compatível/versionado; código oficial não pode ser validado localmente; envio que confirma remotamente fica ligado à ação explícita de conclusão; guardas também cobrem clientes/endpoints antigos.
- Arquivos alterados: somente este plano e os dois documentos acima. Nenhum DTO, endpoint, banco ou tela foi alterado.
- Validação: revisão estática dos contratos contra DTOs, endpoints, contexto de recuperação e protótipo do app. Checagem documental de IDs, tabelas, links relativos, estados e UTF-8. Testes de aplicação não se aplicam a este lote documental.
- Limitações: IF-02/IF-03 marcadas Implementada referem-se aos artefatos de especificação; não significam implementação do produto. Confirmação das semânticas oficiais, aprovação/transposição de UI e testes de sandbox permanecem nas tarefas correspondentes.
- Pendências: CNPJ/credenciais/módulos; formato/efeito do código e elegibilidade; pagamento/alteração de meios; DINE_IN; transporte/ACK/presença; convivência de aceite com Gestor. Não marcar IF-01 ou IF-18 como concluídas.
- Próximo passo: apresentar lote IF-04 de contratos executáveis e persistência aditiva, com schema/arquivos e testes de banco, antes de editar código. Conexão real continua dependente de IF-00/IF-01.

### Proposta concreta do próximo lote IF-04

Status: aprovado pelo usuário, implementado e validado localmente. A lista abaixo registra o escopo aprovado; resultado e limites no registro IF-04.

- Objetivo: estabelecer contratos internos executáveis e armazenamento aditivo da integração, sem habilitar transporte, importar pedidos reais ou mudar as interfaces dos canais próprios.
- Contratos novos previstos: `Integrations/Ifood/Contracts/IfoodOrderIdentity.cs`, `IfoodOrderSnapshot.cs`, `IfoodOperationState.cs`. Nomes/agrupamento poderão ser ajustados aos padrões encontrados antes da edição, sem ampliar escopo.
- SQL novo previsto: `Migrations/Delivery/20261013_01_ifood_integration_schema.sql`, `20261013_02_ifood_integration_verify.sql` e rollback correspondente em `Migrations/Delivery/rollback/`. Confirmar disponibilidade dos nomes na inspeção prévia ao lote.
- Persistência prevista: conexões merchant/estabelecimento, inbox, vínculo oficial de pedidos, snapshot/projeção, operações/comandos e disputas. Unicidade por merchant/order e merchant/event, reserva de processamento, revisão e integridade do estabelecimento; preservar índices/dados legados neste lote.
- Modelagem de integridade: o projeto referencia `estabelecimentos(id)` como UUID e `pedido(id)` como INTEGER. Garantir que vínculo oficial não associe pedido de outra loja; não confiar apenas em duas FKs independentes. Escolher constraint composta ou proteção equivalente validada em banco.
- Testes novos previstos: `Tests/Unit/IfoodOrderContractTests.cs` para invariantes relevantes e `Tests/Integration/IfoodIntegrationSchemaDatabaseTests.cs` para aplicação repetida, unicidade, isolamento entre lojas e rollback em banco isolado. Não criar testes que apenas reproduzam texto do SQL.
- Riscos: migrator varre SQL no boot quando `DeliveryTracking:ApplyMigrationsOnStartup` está habilitado; falhas individuais de migração podem deixar API em funcionamento com módulo incompleto. Guardas devem impedir uso do schema incompleto e dependências de migrações não podem ser ignoradas silenciosamente.
- Validações: build/testes adequados, aplicação repetida das migrações e testes reais de constraints em banco isolado via mecanismo `TEST_DELIVERY_DATABASE`; rollback somente em banco de teste. Não iniciar API apontando para banco real nem executar migração de produção.
- Materiais auxiliares: resultados em `docs/validacao-ifood/` e atualização deste plano. Nenhuma alteração de painel/app ou publicação neste lote.
- Dependências: formato/efeito remoto do código e contratos de API ainda pendentes não serão transformados em constantes oficiais. Objetos internos preservam valores desconhecidos e campos opcionais até confirmação em IF-01.
- Aprovação: usuário autorizou IF-04 após resumo do escopo. Não solicitar novamente aprovação para concluir/corrigir as entregas deste lote. Nenhum lote posterior está automaticamente aprovado.

### 13/10/2026 — Lote IF-04

- Estado: Validada localmente. Autorização: usuário respondeu `pode fzer` à proposta de contratos C#, tabelas e testes em banco isolado.
- Resultado: três contratos em `Integrations/Ifood/Contracts/`, seis tabelas passivas, SQL de verificação e rollback que recusa dados em uso, com testes de invariantes e integridade real.
- Integridade: FK composta pedido/estabelecimento e merchant/estabelecimento impede vínculo cross-tenant. Snapshot/comando/disputa seguem a mesma fronteira. Eventos têm deduplicação por merchant/event; número visível pode repetir em merchants diferentes.
- Legado: nenhum campo/registro antigo foi removido ou convertido; índice novo é aditivo. Nenhuma flag/worker/transporte ou loja foi ativado. Contratos públicos e telas permanecem sem alteração.
- Validação: build aprovado após restore explícito pelo NuGet público; 74 testes passaram sem falhas/skips, sendo 21 novos e 53 regressões existentes. PostgreSQL 17.11 temporário, loopback e schemas isolados, encerrado após os testes; não houve uso de banco real.
- Evidência: [relatório IF-04](validacao-ifood/IF-04.md), [TRX final](validacao-ifood/if04-regression.trx) e [TRX dos novos testes](validacao-ifood/if04-tests.trx).
- Ajustes durante validação: corrigida fixture para testar FK cross-tenant sem colisão prévia de unicidade; acrescida verificação contra FK removida e lock no rollback para impedir novas escritas após checagem.
- Limites: sem API real, interfaces ou suíte integral; 35 avisos do build em arquivos existentes. Payloads/tabelas não substituem implementação futura de criptografia, autorização de requests, workers, idempotência de efeitos e conciliação.
- Pendências: IF-00/IF-01 e lotes IF-05 em diante. Regra/formato de código oficial não foi fixada por suposição.
- Próximo passo: preparar IF-05 (autenticação centralizada/segredos e testes locais) e confirmar API Reference; apresentar arquivos/riscos/validações antes de editar. Validação autenticada depende de CNPJ/credenciais.

### 13/10/2026 — Lote IF-05 (parte local)

- Estado: Validada localmente. Autorização: usuário respondeu `pode fzer` ao escopo de autenticação centralizada, proteção de credenciais, autorização por loja e testes locais. IF-00/IF-01 não foram encerradas por inferência.
- Referências públicas oficiais reconsultadas: autenticação centralizada, introdução à autenticação e solicitação de acesso. O acesso direto ao portal havia retornado 403; os textos indexados permitem preparar o contrato local, mas não comprovam perfil/módulos da conta ou chamadas reais. `grantType=client_credentials`, `clientId` e `clientSecret` são enviados em formulário HTTPS; o fluxo centralizado não usa refresh token. As páginas citam prazos padrão divergentes; o código usa exclusivamente `expiresIn` da resposta.
- Arquivos: sete serviços/contratos internos em `Integrations/Ifood/`, registro em `Program.cs`, testes em `Tests/Unit/IfoodAuthenticationTests.cs`, `IfoodSecurityTests.cs` e `Tests/Integration/IfoodMerchantAccessDatabaseTests.cs`. A fixture de schema IF-04 foi compartilhada por classe parcial sem alterar seus cenários.
- Resultado: token em memória, renovação sob demanda com exclusão concorrente por processo, margem proporcional e invalidação que não remove uma renovação por causa de 401 antigo. Falhas impedem fallback para token anterior; pausa progressiva respeita `Retry-After`. Cancelamento do chamador não cria pausa de falha.
- Segurança: endpoint HTTPS fixo, redirects/cookies desabilitados, resposta limitada, validação de token e erros sem corpo/segredo/exceção original. Logging HTTP automático removido apenas do cliente de autenticação. Opções e token ocultam segredos em serialização JSON/ToString; não são DTOs públicos.
- Criptografia: AES-GCM com nonce aleatório, envelope versionado, ID de chave e propósito autenticado; chave Base64 de 32 bytes, sem derivar frases fracas. Rotação permite decifrar com chaves anteriores mantidas no servidor. Não há credencial/token novo persistido; o protetor prepara futuras necessidades de persistência.
- Loja: leitura de `ifood_connections` exige merchant/estabelecimento exatos, status autorizado, evidência temporal de autorização e ausência de revogação. Rechecagem após obter token impede liberação quando a loja foi revogada durante a renovação. Nenhuma autorização foi criada ou atualizada em banco real.
- Validação: compilação aprovada e 108 testes aprovados, sem falhas/skips: 34 casos novos e os 74 casos da regressão anterior. PostgreSQL 16.10 portátil já disponível, instância exclusiva `127.0.0.1:55474` com diretório de dados novo em `obj/ifood-validation/pgdata`; schemas temporários por teste. HTTP somente simulado. Evidência: [relatório IF-05](validacao-ifood/IF-05.md) e [TRX final](validacao-ifood/if05-regression.trx).
- Configuração: `Ifood:Enabled=false` por padrão; sem worker, endpoint público ou chamada no boot. Configuração habilitada exige credenciais e chave válidas. Variáveis e rotação em [variaveis-ambiente-obrigatorias.md](variaveis-ambiente-obrigatorias.md).
- Limites: reserva/cache de token por processo, sem coordenação entre réplicas; sincronização de permissões/revogação oficial e consumidores dos tokens ficam nos lotes seguintes. Nenhum replay automático de comando de pedido foi criado. Sem chamada autenticada real, API/banco de produção, migração nova, painel/app, commit/push/deploy ou habilitação de loja nesta rodada.
- Próximo passo: obter acesso/credenciais e fechar IF-00/IF-01 para prova de autenticação/permissões; apresentar lote IF-06 de inbox e transporte oficial antes de editar. Parte local da IF-06 pode ser planejada com o contrato confirmado, sem declarar recebimento real de eventos.

### 13/10/2026 — Lote IF-06 (parte local, polling)

- Estado: Validada localmente. Autorização: usuário respondeu `pode fzer` ao lote de recepção, persistência antes do ACK, deduplicação e recuperação no backend. A prova oficial continua dependente de IF-00/IF-01; não houve acesso autenticado ou ativação.
- Transporte: polling Events preparado com header explícito de merchants, sem filtros de tipos/grupos que descartariam eventos antes da aplicação. Não foi implementado webhook; adoção posterior requer assinatura, resposta e presença completas, sem tratar ACK de polling como resposta de webhook.
- Persistência: `Integrations/Ifood/Events/` contém envelopes brutos, cliente HTTP, repositório, serviço e worker. Migration aditiva `20261013_04_ifood_event_ingress.sql` cria reserva de polling por aplicação, quarentena de payloads e controle persistente de ACK na inbox existente. O número 03 já era usado pela frente Zippy Experience. Não alteramos essa frente.
- Reservas: limite de intervalo salvo no banco, inclusive após reinício; aquisição única entre instâncias, renovação antes do HTTP após obter token e rejeição de proprietário expirado. A identidade da aplicação usa hash do client ID, nunca do client secret. Cache de token continua por processo, como IF-05.
- Inbox: deduplicação merchant/event, conservação do payload JSON completo, transação antes de ACK e rechecagem de autorização merchant/estabelecimento. Evento repetido volta a ser elegível para ACK sem recriar efeito. Identidade inválida/merchant fora do lote ficam em quarentena sem ACK; código/campos incompletos identificáveis ficam em revisão. Colisão de ID preserva variante em quarentena e não sobrescreve o original.
- ACK: seleção apenas de dados duráveis da aplicação/lojas configuradas; pendência sobrevive a timeout, resposta perdida e reinício. Um polling vazio também retoma ACKs antigos. Prazo de retry e contador ficam no banco; resposta aceita não marca efeito de domínio como processado.
- Processamento futuro: primitivas de claim/renew/finish/retry com reserva, prazo e tentativa máxima foram testadas; não há consumidor que marque eventos processados sem importar/atualizar pedido. Esses efeitos pertencem a IF-07/IF-08 e devem ser transacionais com sua confirmação de processamento.
- Validação: compilação aprovada; **148 testes aprovados, zero falhas/skips**, incluindo 31 novos (20 unitários/HTTP simulado e 11 de banco/percurso completo) e 117 regressões selecionadas. PostgreSQL 16.10 portátil, dados novos em `obj/ifood-events-validation/pgdata`, loopback `127.0.0.1:55475`, schemas isolados removidos pelas fixtures. Sem credenciais reais. Evidência: [relatório IF-06](validacao-ifood/IF-06.md) e [TRX final](validacao-ifood/if06-regression.trx).
- Configuração: exige `Ifood:Enabled` e `Ifood:PollingEnabled`, ambos falsos por padrão, mais lista explícita de lojas/merchants já autorizados. Nenhuma flag/configuração real foi alterada. Worker registrado fica inativo quando desabilitado; migração em produção e execução real não ocorreram.
- Riscos/limites: fonte pública tem limites e versões divergentes; API Reference/módulos reais precisam ser confirmados em IF-01. Lote cobre até 100 merchants por aplicação configurada; escala/batching maior e múltiplas credenciais ativas no mesmo banco requerem novo desenho. Quarentena/backlog e retenção ainda precisam da gestão/diagnóstico IF-15/IF-16. Ordenação para claim não substitui conciliação de estados fora de ordem em IF-08.
- Rollback: IF-06 recusa infraestrutura com dados/obrigações; IF-04 agora recusa remoção enquanto existe dependência IF-06. Aplicar rollback IF-06 antes de IF-04 somente se vazios. SQLs do boot continuam dependentes de `DeliveryTracking:ApplyMigrationsOnStartup`; não iniciar API contra banco real para validar este lote.
- Próximo passo: IF-07, importação completa a partir da inbox, preservando preços/pagamentos oficiais e modalidades sem rota/endereço obrigatório. Apresentar alterações concretas e validações antes de editar. Resolver IF-00/IF-01 em paralelo e comprovar autenticação, presença/polling e ACK no sandbox antes de declarar integração oficial.

### 13/10/2026 — Lote IF-07 (parte local, importação)

- Estado: Validada localmente. Autorização: usuário solicitou `continue o proxima fase`, após identificação de IF-07 como próxima entrega. Não houve ativação nem ampliação de módulos oficiais; IF-00/IF-01 seguem pendentes.
- Arquivos: `Integrations/Ifood/Orders/` (normalizador, cliente de detalhes, cache/reservas, importador e leitura), migration `20261013_05_ifood_order_import.sql`/rollback, options/DI, DTOs e repositórios de consulta/mutação de pedido afetados. Painel/app não foram editados.
- Ingresso: consumidor seleciona somente PLC/PLACED. Consulta detalhes fora de transação/locks e grava cache sanitizado durável antes da projeção. Reserva por merchant/order evita GET concorrente duplicado; cache pronto é reutilizado após falha/reinício. Confirmação/cancelamento/patch e demais eventos continuam na inbox para IF-08, sem descarte.
- Importação: pedido local, vínculo externo, snapshot completo, itens externos, outbox e conclusão do evento são gravados na mesma transação. Locks por pedido externo e fence de reserva impedem duplo efeito/proprietário expirado. Evento PLC diferente do mesmo pedido devolve vínculo existente sem sobrescrever valores/estado ou publicar outro efeito.
- Valores: total oficial não passa pelo catálogo/fórmula local; saldo vem de payments.pending e valores online de prepaid. Métodos/valores/moeda, bandeira/carteira/troco, benefícios/patrocinadores/taxas, itens/adicionais e quantidades decimais são preservados. Ausência/inconsistência/moeda/método desconhecidos exigem revisão, sem zero ou dinheiro por fallback. Cache/projeção não são ledger de recebimentos locais.
- Modalidades: TAKEOUT/DINE_IN não exigem endereço/GPS ou cadastro próprio de cliente. DELIVERY/IFOOD e modalidade desconhecida também são importadas sem atribuição; desconhecida fica em revisão. Horários reais/agendados/preparo são preservados; janela parcial permanece no payload para revisão, sem fabricar intervalo no schema.
- Privacidade: CPF/documentos fiscais, segmentação, transações fiscais e códigos esperados são removidos antes do cache. Telefone/localizador/expiração têm proveniência iFood e não preenchem telefone/cliente/conversa Zippy; nenhum contato/cliente/WhatsApp foi criado. Código de entrega legado permanece vazio, sem validação local de código remoto.
- Leitura: lista/detalhe recebem `identidadeExterna`; detalhe recebe `pedidoIfood` com quantidades, preços e opções originais. O array legado de itens inteiros fica vazio para esses pedidos, evitando truncar quantidade ou recalcular linha. Frontends precisam consumir o novo contrato em IF-11/IF-14; ainda não declarar suas telas prontas.
- Proteção operacional temporária: pedidos importados ficam em Rascunho local (não representa o estado remoto), sem motoboy/rota, até IF-09/IF-12 implementarem capacidades. Mutações manuais do núcleo retornam `IFOOD_ORDER_MANAGED_EXTERNALLY`; trigger também bloqueia promoção de status/atribuição por caminhos legados. Não ligar `zippy_delivery_enabled` manualmente. IF-08 deverá permitir projeção comprovada de cancelamento/conclusão remota sem liberar rota ou bypass manual.
- Validação: **196 testes aprovados, zero falhas/skips**, incluindo 26 novos (17 unitários e 9 PostgreSQL/percurso completo) e 170 regressões selecionadas. Compilação aprovada com 35 avisos preexistentes. Instância PostgreSQL 16.10 exclusiva em `127.0.0.1:55476`, dados novos em `obj/ifood-import-validation/pgdata`, schemas por teste, HTTP simulado. [Relatório IF-07](validacao-ifood/IF-07.md), [TRX final](validacao-ifood/if07-regression.trx).
- Limites: sem HTTP oficial, rollout, cadastro/credenciais, implantação/migrations reais, app/painel ou suíte integral. Fonte pública sustenta contrato local, mas permissões/API Reference/sandbox continuam obrigatórios. Tipos/limites legados incompatíveis falham sem deixar pedido parcial; preservar cache para diagnóstico.
- Próximo passo: **IF-08**, projeção de estados e atualização por eventos, incluindo cancelamento/patch/disputa e ordem variável, sem regressão do estado remoto nem mistura de lojas. Apresentar lote concreto; resolver IF-00/IF-01 em paralelo para prova oficial de autenticação, ingresso e importação.

### 13/10/2026 — Lote IF-08 (parte local, projeção de eventos)

- Autorização: usuário respondeu `pode fzer` ao próximo lote de projeção, após a entrega IF-07. IF-00/IF-01 continuam pendentes.
- Implementação: consumidor independente de PLC, desabilitado por padrão, com reservas individuais, recuperação filtrada por consumidor e tentativas limitadas para falhas técnicas. Evento conhecido antes da importação espera 30 segundos sem esgotar tentativas; backlog sem PLC exige diagnóstico IF-16.
- Atomicidade: payload autoritativo lido da inbox sob reserva, autorização merchant/loja, lock por pedido, snapshot/revisão, itens/disputas, histórico, outbox e processamento final na mesma transação. Fence final usa relógio real; falha desfaz todos os efeitos. Histórico conserva outcome/código/motivo; metadata original permanece na inbox.
- Ordenação: timestamps oficiais com timezone, controles separados de status/preparo/dados/cancelamento/rastreamento; evento antigo é ignorado, conflito simultâneo ou regressão recente exige revisão. Estados terminais não reabrem por confirmação/preparo atrasados. Código curto e fullCode contraditórios não dirigem efeitos.
- Cancelamento: CAR/CARF só alteram solicitação/rejeição. CAN final projeta CANCELLED e status local Cancelado. Trigger permite somente essa alteração comprovada nesta transação, com reserva/autorização válidas; não habilita motoboy/rota. CON projeta conclusão remota, sem marcar entrega física Zippy, código validado ou recebimento; isso pertence a IF-12/IF-13.
- Alterações: ADD/DELETE/EDIT_ITEMS e operações de opções verificam identidade e valores `from/to`; não usam catálogo nem fórmula. Total/saldos só ficam atuais com dados explícitos suficientes. Variante parcial, inconsistente, endereço alterado ou desconhecida fica em revisão e oculta preço/saldo/detalhe desatualizados. Recuperação automática dessas variantes depende de contrato oficial IF-01 e conciliação IF-16; não presumir que novo GET imutável contém o patch.
- Disputas: preserva disputeId/deadline/timeoutAction/payload e settlement, inclusive resolução recebida antes da abertura. Não inventa prazo, reabre disputa resolvida, aceita mudança de pedido/loja nem executa timeout/cancelamento/reembolso a partir da negociação. Ações/timeout/status desconhecidos exigem revisão. Resposta à disputa pertence a IF-10.
- Leitura: identidade externa expõe revisão, estados e atualidade; detalhe inclui resumo das disputas da loja sem payload bruto/mídia arbitrária. Valores auxiliares e quantidade vêm do snapshot; cobrança fica ausente para terminal/revisão. Frontends ainda precisam consumir os contratos em IF-11/IF-14.
- Validação: **229 casos distintos aprovados, zero falhas/skips**: regressão selecionada de 228 e validação complementar de 33 casos IF-08 após reforço final do parser (32 sobrepostos, um novo). São 18 casos unitários e 15 PostgreSQL novos, mais 196 casos anteriores. Build aprovado com 35 avisos preexistentes, nenhum nos arquivos IF-08. PostgreSQL exclusivo `127.0.0.1:55477`, schemas por teste, sem API real; servidor encerrado. [Relatório IF-08](validacao-ifood/IF-08.md), [regressão](validacao-ifood/if08-regression.trx) e [complemento final](validacao-ifood/if08-projection.trx). Não executada a suíte integral.
- Configuração/migration: IF-08 requer IF-07/outbox; `Ifood:ProjectionEnabled=false`. Rollback vazio **IF-08 → IF-07 → IF-06 → IF-04**; efeitos/histórico impedem remoção. Nenhuma migration, ativação ou chamada oficial real ocorreu.
- Próximo lote: **IF-09**, intenções persistentes de confirmar/iniciar preparo/pronto/despachar, versão esperada, precondições de modalidade/horário/SLA e tratamento de resposta incerta. HTTP aceito não substitui evento comprovando o estado. Preparar contratos/clientes/repositório/worker/testes antes de nova implementação; não incluir cancelamento/negociação IF-10 ou rota IF-12 automaticamente.

### 13/10/2026 — Lote IF-09 (parte local, comandos persistentes)

- Autorização: usuário respondeu `pode fzer` após apresentação de IF-09. IF-00/IF-01 continuam pendentes; nenhum perfil/loja real foi habilitado.
- Arquivos: `Integrations/Ifood/Commands/`, controller/DTO de comandos, migration `20261013_07_ifood_outbound_commands.sql`/rollback, options/DI, projeção/leitura/horários do pedido e testes. Frontends e integração legado não foram substituídos.
- Intenção: operationId, loja/pedido externo, ator, payload/hash e revisão esperada persistidos antes do HTTP. Repetição idêntica retorna a mesma operação; mudança de ator/pedido/payload/revisão conflita. Só uma intenção ativa por pedido; resultado incerto/revisão não é contornado por novo ID.
- Envio: worker desabilitado, merchants/ações explicitamente habilitados, autorização rechecada após token, revisão/precondições/horários rechecados no fence final. HTTP fora de transação; início de envio gravado duravelmente. Crash anterior ao marcador pode recuperar sem repetir POST; após marcador vira incerto e não é reenviado automaticamente.
- Resultados: HTTP 202 registra aceite de transporte e aguarda evento, sem promover preparo/status/rota. 401/429 permitem retry com pausa/limite; timeout/rede/5xx/status inesperado ficam incertos, 400/422/403 rejeitados, 404/409 em revisão. Retry-After longo é respeitado; acima de 30 dias exige revisão, sem antecipar envio.
- Evidência: CFM/PRS/RTP/DSP projetados depois do início do envio resolvem a intenção correspondente como efeito observado, sem atribuir autoria remota ao operador por inferência. Evento anterior não confirma envio posterior. Cancelamento encerra intenção incompatível; conclusão sem evento correspondente exige revisão. Evento que chega durante HTTP não é rebaixado pela resposta tardia.
- Precondições: confirm só em PLACED e antes do prazo; preparo/pronto depois de confirmação; despacho só DELIVERY/MERCHANT com RTP comprovado. TAKEOUT/DINE_IN/IFOOD não permitem dispatch. Cancelamento pendente, revisão, modalidade/categoria/horário desconhecidos e versão obsoleta bloqueiam comando. Não habilita `zippy_delivery_enabled` nem cria prova física/cobrança/atribuição.
- Horários: lê horários fornecidos em `schedule`/`scheduled` e preparo na raiz/objeto; divergência/timezone ausente exige revisão. Não calcula preparo por janela/tempo médio. Agendamento posterga preparo e despacho, mas confirmação pode ocorrer antes do início do preparo. Até IF-01 confirmar o contrato do perfil, prazo de confirmação usa o mais restritivo entre createdAt+8min e preparationStartDateTime+8min; opção explícita para âncora de preparo, desativada por padrão. Ver fontes/limites em [IF-09](validacao-ifood/IF-09.md).
- API: POST `api/v2/delivery/ifood/pedidos/{pedidoId}/commands` e GET `api/v2/delivery/ifood/operations/{operationId}`, com permissão `Delivery/integracao_ifood`, loja/ator do token e sem payload/segredo do cliente. Leitura de pedido expõe `activeCommand` e sincronização derivada sem alterar estado remoto; cobrança bloqueada enquanto há intenção ativa.
- Validação: **277 casos aprovados, zero falhas/skips** na regressão selecionada, incluindo 48 novos (33 unitários/HTTP e 15 PostgreSQL) e 229 anteriores. Complemento final de 51 casos sobrepostos aprovado após reforçar divergência de preparo entre aliases de horário. Build aprovado com 35 avisos preexistentes, nenhum nos arquivos IF-09. PostgreSQL exclusivo `127.0.0.1:55478`, schema por teste, HTTP simulado, servidor encerrado; sem suíte integral. [Relatório IF-09](validacao-ifood/IF-09.md), [regressão](validacao-ifood/if09-regression.trx) e [complemento de horários](validacao-ifood/if09-schedule.trx).
- Rollback: somente sem operações/histórico, na ordem **IF-09 → IF-08 → IF-07 → IF-06 → IF-04**. Nenhuma migration real, chamada oficial, commit/push/publicação ou ativação ocorreu.
- Próxima fase: **IF-10**, consulta de motivos atuais, intenção de solicitar cancelamento e rejeição/evento final; respostas a disputas conforme ações/alternativas autorizadas, prazo e settlement, preservando idempotência e resultado incerto. Reutilizar infraestrutura de IF-09 sem estorno/reembolso local fictício; preparar contrato/clientes/endpoints/testes e validar perfil IF-01 antes de habilitar.

### 13/10/2026 — Lote IF-10 (parte local, cancelamento e disputas)

- Autorização: usuário pediu para concluir a IF-10 (código já iniciado na árvore de trabalho). IF-00/IF-01 continuam pendentes; nenhum perfil/loja real foi habilitado.
- Arquivos: `Integrations/Ifood/Exceptions/`, `Integrations/Ifood/Commands/IfoodExceptionRepository.cs`, controller/DTOs de exceções, migration `20261013_08_ifood_cancellation_disputes.sql`/rollback, options/DI, projeção/leitura de disputas e testes (`IfoodExceptionTests`, `IfoodExceptionDatabaseTests`). Painel/app não foram editados.
- Cancelamento: motivos consultados na hora e guardados em catálogo curto vinculado a pedido/loja/revisão (sem lista fixa; código é texto). Intenção persistente, nova consulta de motivos e revalidação antes do POST, marcador de envio antes do HTTP, 202 só como aceite. CAR registra o evento de solicitação; CAN resolve a intenção e atualiza o pedido pela projeção; CARF só rejeita se correlacionado ao evento de solicitação, senão revisão. Retentativa apenas encadeada a pai rejeitado.
- Disputas: opções derivadas só do payload da disputa aberta e dentro do prazo, filtradas pelas ações habilitadas. Resposta (aceitar/rejeitar/alternativa) validada por revisão do pedido e da disputa, prazo, motivo, valor máximo/moeda e minutos permitidos; trava de uma resposta ativa por disputa. Prazo/revisão rechecados no envio; settlement posterior ao envio resolve apenas a resposta correspondente. Sem reembolso/estorno/cancelamento local nem execução de timeoutAction.
- Incerteza: 5xx/timeout/rede/queda após marcador ficam incertos sem reenvio; 422 de disputa vai para revisão; novo operationId não contorna.
- Validação: **316 casos aprovados, zero falhas/skips** (277 anteriores + 39 novos: 29 unitários/HTTP e 10 PostgreSQL). PostgreSQL exclusivo `127.0.0.1:55479`, schema por teste, HTTP simulado. [Relatório IF-10](validacao-ifood/IF-10.md), [regressão](validacao-ifood/if10-regression.trx) e [unitários](validacao-ifood/if10-unit.trx). Suíte integral não executada.
- Configuração/rollback: `Ifood:ExceptionsEnabled=false`, lista explícita de merchants/ações. Rollback vazio **IF-10 → IF-09 → IF-08 → IF-07 → IF-06 → IF-04**; operações/revisões em uso o impedem. Nenhuma migration real, chamada oficial, commit/push/publicação ou ativação ocorreu.
- Limites: endpoints/corpos/semântica de handshake seguem o contrato local e precisam ser confirmados com API Reference/perfil em IF-01 e exercitados em IF-18. Alternativas além de reembolso/benefício/tempo adicional e evidências não são suportadas.
- Próxima fase: **IF-11**, painel de pedidos/preparo/retirada/iFood, identidade, sincronização e ações por capacidade consumindo IF-07 a IF-10. Apresentar lote concreto antes de editar o painel.

### Procedimento para retomar o trabalho

1. Ler este plano, instruções vigentes e registros das últimas tarefas.
2. Inspecionar `git status` dos três projetos e os arquivos do lote, sem desfazer alterações existentes.
3. Atualizar apenas os requisitos que mudaram, identificando fonte e efeito no escopo.
4. Selecionar tarefas cujas dependências estejam satisfeitas; apresentar lote e aguardar aprovação.
5. Implementar e executar validações apropriadas; registrar evidências funcionais e visuais separadamente.
6. Atualizar estados com resultados reais, pendências e próximo passo. Não repetir testes aprovados sem nova razão.
7. Não fazer commit, push, publicação, submissão ou ativação sem solicitação correspondente.
