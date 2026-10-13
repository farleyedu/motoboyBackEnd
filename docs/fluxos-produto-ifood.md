# Fluxos do painel e do app para pedidos iFood

Data: 13/10/2026. Lote IF-03. Referências: [plano](plano-integracao-ifood-produto-homologacao.md) e [contratos](contratos-produto-ifood.md).

## 1. Estado do desenho

Desenho funcional documental, sem edição de tela ou criação de novo protótipo visual. Preservar o visual existente do painel e as referências aprovadas do app em `zippygo-motoboy/docs/prototipo-motoboy/prototype.js`, `styles.css` e assets. Foram examinados os fluxos code/finish/success/pending e handlers validate-code/finishDelivery: o protótipo atual demonstra validação local antes do arraste, não prova o efeito real da API iFood.

Para iFood, a sequência funcional proposta depende de confirmar o efeito de verifyDeliveryCode no sandbox. Mudanças visuais da tela serão apresentadas no lote de UI. Composição/medidas/temas permanecem subordinados ao protótipo; este documento define conteúdo, ações, estados e critérios.

## 2. Cabeçalho e identificação comuns

- Todos os pedidos iFood mostram etiqueta textual `iFood` e número principal `#001234` preservado como string.
- Canais próprios mostram `Zippy #1842` e origem específica. Exemplo de etiquetas: Cardápio, WhatsApp, Balcão/telefone.
- Nome do cliente continua em campo próprio; nunca substitui número oficial.
- ID interno fica secundário em detalhes/suporte. UUID externo e merchant não são expostos ao motoboy sem necessidade.
- Número indisponível aparece explicitamente; não usar id interno com rótulo que sugira número iFood.
- Status remoto/preparo/entrega e alerta de sincronização têm rótulos distintos. Cor reforça texto, não é o único indicador.

## 3. Fluxo A — Recebimento e aceite no painel

1. Inbox recebe evento e vínculo identifica loja; detalhes importados produzem projeção.
2. Pedido aparece na lista operacional com origem/número, modalidade, pagamento, itens, observações e prazo de aceite.
3. Pedido agendado mostra janela futura; alerta de aceite continua ativo conforme SLA oficial.
4. Operador abre detalhes e confirma quando capacidade estiver disponível.
5. Durante envio: botão em processamento; clique repetido reutiliza operação, sem confirmar duas vezes.
6. Sucesso/evidência atualiza preparação e estado remoto; resposta incerta mantém alerta `Aceite aguardando confirmação`.
7. Cancelamento/revogação concorrente atualiza contexto e bloqueia ação incompatível.

Falhas de detalhes/classificação aparecem em exceções da operação da loja; não colocar pedido parcial numa rota. Política de aceite automático só é introduzida após decisão explícita e teste de convivência com Gestor.

Aceite: um pedido real aparece sem reentrada, com número correto, sem duplicação e com resposta remota rastreável.

## 4. Fluxo B — Preparação e despacho de entrega própria

1. Loja acompanha aceito/em preparo/pronto sem depender de atribuição de motoboy.
2. Atribuição segue regras existentes, mas backend verifica DELIVERY/MERCHANT, vínculo e situação operacional.
3. Loja informa pronto e acompanha confirmação de readyToPickup conforme contrato.
4. Motoboy recebe oferta com número iFood, destino, observações e saldo de cobrança. Oferta não significa despacho.
5. Motoboy aceita/coleta conforme fluxo atual; integração dispara dispatch somente na etapa correta e após precondições.
6. Se pronto/despacho falhar, estado externo pendente aparece no painel/app; não seguir silenciosamente como se enviado.
7. Saída, chegada e conclusão mantêm versão/ownership e eventos oficiais separados.

Aceite: despacho prematuro bloqueado; uma rota mista de pedidos próprios/iFood mantém identidade e políticas individuais.

## 5. Fluxo C — Entrega pela logística iFood

1. Pedido permanece na cozinha/operação da loja com etiqueta `Entrega iFood`.
2. Loja executa ações de aceite/preparo/pronto permitidas.
3. Pedido não aparece como entrega livre/oferta/fila para motoboy Zippy.
4. Informações do entregador/coleta/rastreio aparecem somente se disponíveis e autorizadas.
5. Código de coleta, quando habilitado, é tratado como conferência com entregador iFood; não é código de entrega do cliente.
6. Painel acompanha conclusão/cancelamento via eventos, sem atribuir prova de entrega a motoboy Zippy.

Aceite: tentativas de atribuição pela UI, endpoint antigo ou cliente manipulado são rejeitadas pelo servidor.

## 6. Fluxo D — Retirada

1. TAKEOUT aparece na lista/preparo com número iFood e etiqueta `Retirada na loja`.
2. Não exige endereço/posição fictícia nem cria parada de motoboy.
3. Loja aceita e informa pronto via ação aplicável.
4. Operador conclui retirada por fluxo autorizado; código oficial exigido recebe tratamento próprio no painel, sem sessão de motoboy.
5. Falha/pendência permanece visível e recuperável; não confundir pronto com retirado.

Aceite: retirada completa ensaiada na API oficial e nas interfaces; cardápio próprio conserva seu fluxo anterior.

## 7. Fluxo E — Entrega própria e código no app

Telas existentes envolvidas: oferta/rota, detalhe/checklist, chegada, `DeliveryCodeScreen`, `FinishScreen`, `DeliverySuccessScreen` e tela de entrega pendente.

| Momento | App | Backend/painel |
|---|---|---|
| Oferta/rota | iFood/número, observações e cobrança real | Modalidade e elegibilidade verificadas |
| Chegada | Mantém coleta/chegada e pedido atual | Contexto autorizado e versionado |
| Código | `Informar código iFood`; formato do contrato; preparar sem sucesso falso | Não compara código oficial com segredo local |
| Conferência | Código informado, pagamento/prova prontos; saldo a cobrar | Requisitos derivados por pedido |
| Arraste explícito | `Confirmar entrega no iFood`; explica efeito ao cliente do produto | Registra operação, checa propriedade/versões e envia comando |
| Resposta final | Código validado visível/desabilitado; recibo e próxima entrega | Confirmação oficial e efeitos locais persistidos |
| Resultado incerto | `Aguardando confirmação do iFood`; consultar resultado | Comando/resultado recuperáveis, sem repetir pagamento |
| Código rejeitado | Mensagem simples e correção quando permitida | Rejeição definitiva registrada; nova tentativa vinculada |
| Cliente sem código | Ocorrência/orientação; sem botão de sucesso alternativo | Fluxo permitido e auditado; não simula validação remota |

Para canais próprios, preservar botão de validação local e condições existentes de conclusão. Para iFood, se a chamada de validar também confirma entrega, o envio oficial só acontece na ação explícita de concluir; antes disso não mostrar `validado`. Se sandbox demonstrar semântica diferente, atualizar decisão e tela antes de implementação.

Não marcar pagamento ao abrir tela, tocar em código ou preparar divisão. Saldo recebido é gravado por ação explícita e uma vez; retorno pendente pode significar pagamento recebido com confirmação externa em aberto. A interface precisa informar essa combinação sem sugerir nova cobrança.

Aceite: código inválido não conclui; válido produz resultado oficial; perda de resposta/crash não duplica efeito; código confirmado de outro pedido nunca habilita próximo destino.

## 8. Fluxo F — Pagamento

- Pedido já pago: exibir `Pago pelo iFood`, valor online quando disponível e nenhuma ação Cobrar.
- Parcial: destacar `Cobrar R$45,00` quando total oficial for R$65 e pago online R$20. Exemplo sintético, não dado real.
- Dinheiro: mostrar valor pendente e troco conforme payload/recebimento; confirmar recebimento explicitamente.
- Meio externo não suportado: preservar rótulo e orientar revisão; não converter vale/outro para dinheiro.
- Divisão/alteração: habilitar somente se permitida no contrato e nos meios suportados.
- Mudança de valor após evento: renovar contexto e solicitar revisão; não executar cobrança baseada no valor antigo.

Aceite: cobrança valida saldo e métodos, não total bruto; repetição de conclusão não repete recebimento.

## 9. Fluxo G — Cancelamento e disputa

1. Operador solicita cancelamento usando motivos atuais do pedido; UI mostra solicitação pendente.
2. Cancelamento confirmado atualiza lista/cozinha/mapa/rota/app; rejeição mantém pedido e mostra motivo.
3. Após despacho, motoboy recebe aviso de interrupção e orientação/ocorrência; não apaga itens/histórico/recebimento.
4. Disputa aparece em área de atenção mesmo após entrega, com prazo, solicitação, evidências e opções autorizadas.
5. Operador responde uma vez por disputa; resultado aguardado até settlement.
6. Prazo vencido apresenta resolução/ação automática real, sem inventar que resposta tardia foi aceita.

Aceite: modal aberto não permite ação obsoleta; cancelamento remoto não é confundido com solicitação; disputa não some porque entrega saiu da fila.

## 10. Fluxo H — Indisponibilidade e recuperação

| Situação | Mensagem/ação | Regra de servidor |
|---|---|---|
| Pedido pendente de importação | Pedido em sincronização, sem rota disponível | Persistir/retry/classificar |
| Token inválido/autorização revogada | Integração precisa de atenção | Suspender ação remota insegura, registrar estado por loja |
| Sem rede antes de enviar | Conferência salva, reconectar | Não afirmar pedido recebido pelo servidor |
| Timeout após envio | Confirmação pendente, consultar | Não concluir que a API não realizou efeito |
| Recibo ainda inexistente | Aguardar operação | 404 do recibo não autoriza reenvio cego |
| Sessão/troca de loja | Recuperar acesso e consultar operação anterior | Ownership e scope preservados |
| Operação definitivamente rejeitada | Corrigir quando permitido | Nova tentativa vinculada, sem mudar payload de operação imutável |
| Evento remoto inconclusivo | Revisão necessária | Lifecycle remoto não comprova validação de código por si só |

Durante operação pendente, não liberar automaticamente Próxima entrega/alteração de rota/logout se isso perder o contexto. Eventual intervenção operacional deve ser explícita, auditada e não mudar pendente para confirmado artificialmente.

Aceite: fechar/reabrir app recupera operação e identifica pedido/número corretos; store/sessão diferentes não exibem dados de outra loja.

## 11. Histórico, comanda, atendimento e métricas

- Histórico e recibo preservam origem, número, horários, pagamento, prova e fonte da confirmação. Não imprimir código secreto do cliente; localizador permitido tem rótulo distinto.
- Cancelamento/disputa posterior não apaga evidência ou transforma automaticamente recebimento local em reembolso.
- Contagem e remuneração de motoboy não duplicam pedido externo nem incluem logística iFood como trabalho Zippy.
- Telefone intermediado não cria conversa/CRM/WhatsApp automaticamente. Recursos próprios só são usados quando autorizados e com dados adequados.
- Tracking público não expõe payload/código/dados fiscais nem localização fora do escopo; confirmar permissões antes de habilitar.

## 12. Critérios de validação das telas futuras

| Área | Evidências funcionais | Evidências visuais |
|---|---|---|
| Painel lista/detalhe | Número/origem, busca, capacidades, modalidade e saldo | Legibilidade, filtros, estados e ausência de ID interno como título iFood |
| Cozinha/retirada | Aceite/preparo/pronto/retirada e falhas | Distinção clara de etapa e estado remoto |
| Mapa/rota | Exclusão IFOOD/TAKEOUT e cancelamento em rota | Rota mista identificável sem alterar mapa desnecessariamente |
| App código/conclusão | Envio explícito, rejeição, pendente, recovery e recibo | Referência aprovada, claro/escuro, texto ampliado e formato variável |
| App cobrança | Pago online, saldo parcial, troco e revisão | Valor a cobrar destacado, método original claro |
| Exceções/disputa | Prazo, alternativas, resolução e reprocessamento seguro | Ações e bloqueios explicados em linguagem operacional |

Validar Android nativo para gesto/persistência/rede/retomada. Testes browser com mocks são auxiliares. Não declarar fidelidade visual apenas porque o fluxo está descrito neste documento.

## 13. Decisões e pendências deste lote

Fechadas documentalmente: identidade comum; telas por modalidade; preparo separado de rota; cobrança por saldo; pendência como estado visível; ação explícita para efeito oficial; ocorrência sem bypass; proteção dos canais próprios.

Pendentes de IF-01/sandbox: disponibilidade/formato/efeito do código, ações DINE_IN, opções de pagamento e transporte. Pendentes do lote de UI: transposição de componentes, comparação visual com protótipo e validação nativa. Nenhuma delas é considerada implementada/homologada.

Próximo lote: contrato executável/schema e migrações aditivas após apresentação de arquivos, riscos e testes. Não alterar telas antes de estabelecer guardas no backend que impeçam conclusão/despacho indevidos por endpoints legados.
