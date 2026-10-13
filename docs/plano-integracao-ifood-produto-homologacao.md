# Plano da integração iFood no produto Zippy e homologação

Data de referência: 13/10/2026. Idioma: pt-BR.

## 1. Objetivo e autorização

Entregar o backend, o painel administrativo e o app do motoboy com os fluxos completos da integração oficial Food Order/Events, mantendo pedidos iFood diferenciados dos canais próprios. O resultado esperado é uma aplicação pronta para submissão à homologação, validada no ambiente de testes oficial. Aprovação no processo automático e validação final do iFood são marcos externos e não podem ser declarados por inferência.

O usuário aprovou a criação deste plano, IF-02/IF-03, o lote IF-04 de contratos C#, tabelas aditivas e testes em banco isolado e a parte local da IF-05 de autenticação, segredos e autorização por loja, após apresentação do escopo. A autorização não inclui commit, push, publicação ou ativação de lojas. Antes de cada novo lote de implementação, apresentar objetivo, alterações, riscos, arquivos e validações e aguardar aprovação explícita. Após aprovação do lote, implementar e validar autonomamente dentro do escopo. Não solicitar novamente autorização para repetir trabalho já coberto pelo mesmo lote.

Este arquivo será o registro de continuidade do trabalho. O DOCX `Zippy_iFood_Plano_Integracao_Segunda_Opiniao.docx` permanece preservado como referência anterior. A infraestrutura passiva IF-04 e a parte local da IF-05 foram implementadas e validadas localmente; autenticação real, integração operacional e homologação ainda não foram realizadas.

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
| IF-06 | Inbox, polling ou webhook completo, ACK correto, reservas e recuperação; evento repetido não duplica efeito | IF-04, IF-05 | Pendente |
| IF-07 | Importação externa completa sem recalcular preços; modalidades sem endereço/cadastro próprio obrigatório | IF-02, IF-04, IF-06 | Pendente |
| IF-08 | Projeção e atualização por eventos; cancelamento/patch/disputa/estado desconhecido e ordem variável | IF-06, IF-07 | Pendente |
| IF-09 | Comandos persistentes de confirmar/preparar/pronto/despachar; SLA/agendamento e resultados incertos | IF-05, IF-07, IF-08 | Pendente |
| IF-10 | Cancelamento com motivos dinâmicos, rejeição e evento final; negociações aplicáveis com deadline/settlement | IF-08, IF-09 | Pendente |
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

### Procedimento para retomar o trabalho

1. Ler este plano, instruções vigentes e registros das últimas tarefas.
2. Inspecionar `git status` dos três projetos e os arquivos do lote, sem desfazer alterações existentes.
3. Atualizar apenas os requisitos que mudaram, identificando fonte e efeito no escopo.
4. Selecionar tarefas cujas dependências estejam satisfeitas; apresentar lote e aguardar aprovação.
5. Implementar e executar validações apropriadas; registrar evidências funcionais e visuais separadamente.
6. Atualizar estados com resultados reais, pendências e próximo passo. Não repetir testes aprovados sem nova razão.
7. Não fazer commit, push, publicação, submissão ou ativação sem solicitação correspondente.
