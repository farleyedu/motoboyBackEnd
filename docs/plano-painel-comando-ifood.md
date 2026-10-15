# Gestão iFood no painel de comando

Referência: 15/10/2026. Plano consolidado e implementação local autorizados na conversa.
Complementa [plano principal](plano-integracao-ifood-produto-homologacao.md), [contratos](contratos-produto-ifood.md) e [fluxos](fluxos-produto-ifood.md).

## Objetivo e decisões

O comando mostra entrega própria, logística iFood, retirada e consumo no local. A lista não depende de coordenadas ou elegibilidade para rota. Detalhes, itens, pagamentos, impressão, eventos, conciliação, cancelamento, disputas e ações suportadas ficam no painel lateral, por reuso dos componentes de Pedidos.

- Visibilidade: vínculo oficial na loja, estado remoto e janela operacional. Sem mudança de `StatusPedido`.
- Rotas: preservar `ifood_delivery_blocked_code`, triggers, `EnsureIfoodRoutingAsync` e `canUseZippyRoute`. Ver um pedido não autoriza entrega.
- Estado: situação oficial, preparo, sincronização e estado local separados. A etapa operacional alimenta cartão, filtros e contadores; não grava status.
- Mapa: só marcadores com coordenadas válidas e modalidade DELIVERY com dados atuais. Retirada/consumo continuam na lista sem marcador.
- Ações: disponíveis destacadas, demais desabilitadas e justificadas. Permissão para ver não concede permissão para operar.
- DINE_IN: visível; ações dependentes da confirmação oficial de suporte ficam explicitamente bloqueadas. Não presumir conclusão por código para essa modalidade.
- Configuração: credenciais e manutenção só na Gestão/superadmin; restaurante vê conexão e situação. Ativar a conexão prepara a operação completa; configurações existentes pausadas exigem aplicar o preset e salvar. Kill-switches são preservados. Nenhuma configuração de produção foi ativada nesta entrega.
- Pedidos abertos fora da janela continuam visíveis quando `AlwaysShowOpenOrders` estiver ativo. Terminais usam timestamps oficiais/vínculo confiável; sem horário não ficam eternamente na lista.
- Alertas de aceite, disputa aberta, agendamento e recuperação ficam no comando. Disputa não depende de o pedido estar na janela e abre seu detalhe sob demanda.
- Loja fechada: não interromper ingresso iFood; aviso de recebimento local desligado no detalhe. Não sincroniza abertura/fechamento com o Gestor.

## Recebimento, recuperação e dados

Importador pode reclamar PLC/PLACED ou um evento com `orderId` sem snapshot. Projeção aguarda o snapshot, sem disputar esses eventos com o importador. As reservas, cache e limites de tentativas existentes continuam valendo.

Um evento não inicial cria o pedido com marcador local `zippyRecovery` e sincronização pendente/revisão. O evento volta à fila para projeção. CFM/DSP/CAN/CON oficiais aplicados confirmam a dimensão de estado e removem o marcador; um evento meramente informativo ou de preparo não inventa aceite. Ações e rotas ficam bloqueadas enquanto o estado permanecer desconhecido. A API de detalhes não é presumida como fonte de status; não existe busca geral de pedidos antigos neste lote.

Pendências de modalidade, agendamento, itens, pagamento e localização têm códigos próprios. Aceite/preparo toleram somente problemas independentes de pagamento/localização, com dados atuais e sem eventos em revisão/falha. Conflitos de eventos, recuperação e alterações incompletas continuam bloqueando. Rotas mantêm as regras estritas existentes.

Reconsulta oficial via POST `api/v2/delivery/ifood/pedidos/{id}/refresh`: permissão iFood, loja do token, autorização revalidada, reserva/cache com intervalo de 15 s, checagem de revisão após HTTP, recusa de comando/rota ativos, atualização transacional de snapshot/itens/endereço e auditoria. Preserva estado remoto e marcador de recuperação. Não cobra, conclui ou dispensa eventos. O painel faz uma tentativa automática limitada para problemas independentes ao abrir o pedido; há botão para nova tentativa explícita.

Pagamentos oficiais são preservados sem reprecificar. Falta de dados é pendência da integração, não uma auditoria financeira do lojista. Cobrança indefinida permanece bloqueada. `isTest` vem do payload oficial, é propagado à identidade e excluído das métricas baseadas em entregas reais. Não é criada coluna de ambiente. Nenhuma coordenada de simulação é gravada em `pedido.latitude/longitude`.

## DDCR e pedidos das imagens

Encontrado localmente: parser já reconhecia DDCR, mas `Known` do projetor não incluía `DELIVERY_DROP_CODE_REQUESTED`. Acrescentado como informativo; não deve gerar revisão. Revisão persistida anteriormente não é apagada por deploy.

6485/8999: sem consulta autenticada de produção nesta sessão. Usar `getIfoodOrderEvents` por ID interno para obter `state/outcome/reasonCode`; escolher reprocessamento ou conciliação auditada conforme o resultado. Não executar UPDATE genérico nem atribuir a causa ao endereço fictício. Consulta auxiliar em `diagnostico-pedidos-ifood.sql` é somente leitura.

## Implantação e reversão

Nova migration `20261015_02_ifood_command_panel.sql`: amplia somente as ações permitidas na auditoria para incluir `refresh_details`. Sem promoção de status, habilitação de lojas ou alteração de flags. Aplicar junto ao backend antes de usar o novo botão. Rollback recusa apagar/invalidar auditoria já utilizada.

Sem commit, push, deploy, publicação ou alteração de dados/flags de produção nesta execução. Não iniciar a API local apontada ao banco de produção. PostgreSQL de teste exclusivo em 127.0.0.1:55496, schemas isolados das fixtures.

## Critérios e evidências

1. TAKEOUT/DINE_IN sem coordenada aparecem na lista, não geram marcador nem recebem rota; atribuição via API retorna 409 com motivo oficial.
2. PLACED aparece como aguardando aceite; estado local preservado. `zippy_delivery_enabled=false` não esconde pedido e mantém atribuição bloqueada.
3. Um ativo anterior à janela é preservado conforme a configuração; terminais antigos são recortados. Dados de outra loja não entram.
4. Cartão, filtros e contadores usam a mesma etapa. Canais próprios mantêm a fase existente.
5. Detalhes são buscados na seleção; sem itens/códigos extras no map-state. Operações usam `pendingOperation` por loja/usuário/pedido, sem repetição de intenção.
6. DDCR informativo não cria revisão. CFM sem PLC cria, reaplica e confirma o pedido; sem evidência de estado, recuperação permanece explícita e bloqueada.
7. Disputa em concluído continua em atenção com prazo e navegação ao detalhe. Agendado exibe janela, sem antecipar preparo/despacho.
8. Reconsulta oficial não promove estado nem libera conflito não resolvido. Auditoria registra atualização; reconsulta não opera durante rota/comando ativo.
9. Regressões: encerramento automático, bloqueios de rota, isolamento, métricas/remuneração e canais próprios.

Roteiro sandbox sobre a versão nova: T-01, T-03 a T-07, T-14 a T-18, T-20, T-23 a T-28, T-32 a T-35 e T-38. T-02/T-31 e a nova migration exigem implantação autorizada. T-08 a T-13/T-29/T-30 dependem do app/fluxo físico. IF-01/IF-19 e homologação oficial continuam externos.

Resultados locais e limites serão registrados em `validacao-ifood/PAINEL-COMANDO.md`. Testes locais não são evidência de homologação no iFood.
