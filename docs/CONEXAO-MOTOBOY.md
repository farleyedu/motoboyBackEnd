# Conexão do motoboy ao restaurante — 09/10/2026

## Evidência e correção

O log enviado por Farley registra `POST /api/v2/motoboys/me/session/start` retornando **401** às 21:04:40, bloqueado por `APIBack.Attributes.AuthorizeAttribute`, antes de executar o início do turno. As três permissões do aparelho aparecem concedidas. O log não identifica qual credencial foi enviada nem o estado dos vínculos dessa conta; não houve consulta ao banco de produção nem uso de tokens do usuário.

Foram encontrados e corrigidos estes caminhos reproduzíveis:

- A seleção do estabelecimento gerava um refresh token sem persistir seu hash em `usuario_refresh_tokens`. O app substituía o refresh válido do login por essa credencial desconhecida. `EstabelecimentoSelectionService` agora usa `IAuthService.IssueRefreshTokenAsync`, que compartilha a persistência do login antes de responder.
- A seleção e o início do turno já consideram o vínculo aprovado em `motoboy_estabelecimento`, mas a autenticação da conta procurava exclusivamente o ID de `usuario_estabelecimentos`. Um motoboy legado recebia `Guid.Empty` e era recusado na chamada seguinte. A autenticação do motoboy agora revalida usuário e estabelecimento pelo mesmo repositório da seleção. Novos tokens não incluem ID de vínculo vazio; antigos com esse ID também são aceitos somente após conferir o vínculo real.
- A renovação da conta agora reconhece o vínculo do motoboy canônico quando falta a linha legada. Heartbeat, autenticação operacional e GPS exigem vínculo ativo na loja e associação do usuário ao mesmo motoboy canônico. Remover esse vínculo ou alterar o proprietário continua bloqueando o acesso. Permissões de funcionários e regras do simulador mantêm sua validação própria.
- No mobile, depois de uma renovação definitivamente recusada, `AuthContext` volta ao login. Credenciais principais são retiradas, preservando usuário salvo, loja, sessão operacional e GPS para recuperação pelo mesmo usuário. Respostas antigas de outra credencial/contexto são ignoradas; falhas de rede/503 e recusa de token operacional não encerram a conta.

## Validação local

- TypeScript do `zippygo-motoboy`: sem erros.
- **80 testes mobile** de sincronização, sessão e regressões passaram. Incluem início com token principal, renovação com a mesma tentativa, retorno ao login, preservação do turno/GPS e resposta atrasada.
- **69 testes backend** de sessão, sincronização, permissões, seleção, JWT e banco passaram. PostgreSQL 16 local em loopback, schemas temporários isolados; nenhuma conexão com produção. Dois cenários novos usam serviços, repositórios e JWT reais para seleção → refresh persistido → autenticação → início idempotente → heartbeat → GPS, com/sem vínculo legado; também verificam revogação e troca indevida de proprietário.
- Resultado local: `obj/delivery-validation/results/motoboy-connection-regressions.trx`. Banco de validação parado ao concluir.
- Na ampliação inicial, dois testes antigos de períodos financeiros falharam por `DateTimeOffset` com offset `-03:00` em `MotoboyWorkService.AddPeriod`. São pendências anteriores, fora deste erro de conexão: `WorkPartialWeekIsProratedPersistedAndVisibleToTheRider` e `WorkMigrationVerifiesAndRollbackOnlyAllowsEmptyTables`. Foram excluídos da execução final de 69 testes; não contabilizar essa execução como aprovação da suíte completa.

## Aplicação no ambiente do usuário

As mudanças mobile estão locais no checkout principal `zippygo-motoboy/master`, sem alteração do layout. Durante esta execução, o backend foi commitado/publicado externamente como **`048cbec` — ajuste acesso motoboy**; `git ls-remote origin refs/heads/master` confirmou esse commit no remoto. O agente não executou commit/push nem deploy. As verificações adicionais do teste de conexão e este registro continuam locais no backend. Não há migration nova nem validação autenticada no Render nesta correção.

Confirmar que o deploy do Render já executa `048cbec` e recarregar o bundle do app. Uma credencial de renovação que já foi entregue sem persistência não pode ser recuperada: entrar novamente gera credenciais válidas. Não limpar os dados do aplicativo para isso; conservar dados de recuperação do turno.
