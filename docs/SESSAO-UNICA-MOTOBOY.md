# Sessão única de login no app do motoboy

O app avisa antes de substituir um acesso em outro aparelho. Cancelar não altera a sessão existente; confirmar revoga o acesso anterior e encerra seus turnos mobile pelo fluxo existente de auditoria/presença, sem cancelar pedidos.

## Contrato

`POST /api/auth/login` mantém email/senha e aceita `clientApp: "motoboy"` e `clientInstanceId` (UUID persistido pelo app). Login em outro aparelho retorna 409 `LOGIN_SESSION_ACTIVE` com `details.sessionId`, depois da validação das credenciais.

Para confirmar, repetir o login com `confirmSessionReplacement: true` e `expectedSessionId` igual ao apresentado. Uma sessão diferente retorna outro conflito; a confirmação antiga não autoriza encerrar uma sessão nova. Login no mesmo aparelho é recuperável/idempotente quanto à identidade da sessão.

Os tokens trazem `motoboy_login_session_id`; seleção de restaurante, refresh, início de turno e heartbeat preservam esse vínculo. O middleware recusa sessões revogadas com 401. `GET /api/auth/session` permite ao app conferir o acesso sem turno online. O app interrompe a renovação automática em `SESSION_REPLACED` e volta ao login.

`motoboy_login_sessions` registra um acesso ativo por usuário. A migration `Migrations/Delivery/20261010_03_motoboy_login_sessions.sql` acrescenta o vínculo nos refresh tokens. O prazo da sessão é o prazo de refresh configurado no login. O bloqueio da linha de usuário serializa login/substituição, início autenticado e logout com refresh. O painel e clientes sem `clientApp: motoboy` mantêm o contrato anterior; um turno mobile de versão anterior também é reconhecido no primeiro login novo.

## Verificação e publicação

25 testes direcionados passaram, incluindo 4 cenários com PostgreSQL local real. O teste `MobileLoginWarnsBeforeReplacementAndRejectsOldIdentityRefreshAndLateLogout` confere credenciais antigas, seleção/refresh, cancelamento, sessão esperada incorreta, turno anterior encerrado, início/logout atrasados e duas confirmações concorrentes. `PersistSecurityInfo` foi habilitado somente na fixture local para seus services poderem recriar conexões autenticadas; a conexão da aplicação não mudou.

Para repetir, definir `TEST_DELIVERY_DATABASE` com um PostgreSQL **local** e rodar `dotnet test --filter FullyQualifiedName~MobileLoginWarns`. A fixture cria e remove um schema próprio.

O commit `1030997` foi enviado ao remoto externamente durante a implementação. Correções finais posteriores permanecem locais. O agente não fez commit, push ou deploy. O Swagger público consultado respondeu 200 e ainda não anunciou `/api/auth/session`; publicar o código final e confirmar a aplicação da migration antes de validar contas reais.

APK, testes de tela e limitações de versões anteriores: [SESSAO-UNICA.md](../../zippygo-motoboy/docs/SESSAO-UNICA.md).
