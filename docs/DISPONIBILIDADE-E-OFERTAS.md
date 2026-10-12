# Disponibilidade persistente e ofertas prioritárias — 11/10/2026

> Atualização de 12/10/2026: presença mobile agora vence após 12 minutos sem heartbeat ou GPS aceito. O batimento ocorre a cada 4 minutos; GPS continua fresco por 120 segundos, independentemente da presença. O turno e sua fila continuam abertos para retomada, mas contatos, ofertas, push e eventos filtrados por `expires_at_utc` deixam de considerá-lo online. Offline explícito continua imediato. JWT operacional dura 60 minutos e é renovado pelo heartbeat. Publicar API e `20261012_01_presence_window.sql` antes do app; essa migration ajusta somente a presença infinita legada. As regras abaixo descrevem a implementação anterior.

O motoboy escolhe quando ficar offline. Fechar o app, suspender a web ou perder heartbeat não encerra sua disponibilidade nem sua conta. O heartbeat continua medindo conexão; a idade da localização continua baseada na captura real. Um turno online não prova que o GPS está atualizado.

## Sessão e migração

- Turnos de origem `mobile`, tanto Android/iOS nativos quanto web, usam `expires_at_utc = 9999-12-31T00:00:00Z`. O valor conserva os filtros SQL existentes de sessão aberta.
- `POST /session/start` com `resumeSessionId` recupera o mesmo turno aberto, `sessionId` e `epoch`, renovando apenas o acesso e a presença. Não cria outro turno nem perde os pedidos. Uma sessão antiga encerrada por `heartbeat_timeout` ainda pode ser recuperada pelas regras existentes.
- JWTs operacionais continuam curtos e são renovados automaticamente. Login e refresh vinculados ao motoboy permanecem válidos até revogação. Logout, offline explícito, troca autorizada de aparelho e perda de vínculo continuam sendo verificados; nenhum encerramento explícito é desfeito.
- Aplicar `Migrations/Delivery/20261011_01_disponibilidade_persistente.sql` antes dos novos clientes. A migration é idempotente e prolonga somente sessões/credenciais ainda não revogadas. Não muda horários de GPS nem reabre turnos encerrados.
- O simulador conserva sua política própria de expiração. A disponibilidade do motoboy não usa a janela de frescor do GPS.

## Ofertas e notificações

O diálogo global exige aceite/recusa e conserva identidade da oferta, versão da fila, prazo e regras de conclusão pendente. Ocultar no modo rota não aceita, recusa ou prorroga a oferta: fica um aviso para responder. O servidor continua tratando a expiração normalmente. Uma oferta nova reaparece mesmo que a anterior tenha sido ocultada.

Android conserva a entrega Expo/FCM com prioridade alta, canal de pedidos e inscrição por sessão. Web usa inscrição Push API autenticada, worker de envio e service worker para exibir a oferta fora da página. Os dois envios partem da outbox existente e revalidam oferta aberta, sessão e vínculo. Os tokens `web:` não são enviados ao Expo. Push web desta rodada cobre **ofertas**, não mensagens de chat.

Push web usa `Lib.Net.Http.WebPush` 3.3.1, criptografia RFC 8291 `aes128gcm`, urgência alta e TTL limitado ao prazo da oferta. O servidor guarda a inscrição privada; aceita apenas endpoints HTTPS de provedores permitidos. Uma confirmação HTTP do provedor não comprova que o aviso apareceu no aparelho. Sem inscrição/permissão, o aviso dentro do app continua disponível; entrega pelo sistema operacional exige homologação física.

## Configurar e publicar

1. Publicar a API com as duas migrations novas: `20261011_01_disponibilidade_persistente.sql` e `20261011_02_oferta_web_push.sql`, após as migrations de sessão, chat e aceite obrigatório já existentes.
2. Cadastrar no ambiente **do backend** `Delivery__WebPush__Subject`, `Delivery__WebPush__PublicKey` e `Delivery__WebPush__PrivateKey`. A chave privada nunca vai para variáveis públicas Expo/Vercel.
3. As três variáveis foram geradas localmente, uma vez, em `obj/web-push-keys.env`, ignorado pelo Git. Copiar os valores desse arquivo para o servidor, preservando o mesmo par nas atualizações. O script `node Scripts/generate-web-push-keys.cjs https://URL-PUBLICA` existe para ambientes sem chaves e recusa sobrescrever o arquivo. Não regenerar as chaves já criadas.
4. Atualizar a web e gerar o APK pelo perfil autorizado: `eas build --platform android --profile preview`. No app, ativar **Configurações → Ativar avisos de novos pedidos** se a permissão ainda faltar. Testar oferta com o app aberto, durante rota, em outro aplicativo e fechado, incluindo ocultar/reabrir e expiração.

No iPhone, push exige iOS/iPadOS 16.4 ou superior, web app adicionado à Tela de Início e permissão solicitada por uma ação do usuário. Não exige assinatura Apple Developer para Web Push. Fontes: [Apple](https://developer.apple.com/documentation/usernotifications/sending-web-push-notifications-in-web-apps-and-browsers), [WebKit](https://webkit.org/blog/13878/web-push-for-web-apps-on-ios-and-ipados/) e [biblioteca de envio](https://github.com/tpeczek/Lib.Net.Http.WebPush).

## Validação e limites

78 testes relacionados passaram, com PostgreSQL 16 local isolado em WSL e esquemas temporários. Incluem retomada após 30 dias, mesma sessão/epoch, pedidos preservados, conflito de outro aparelho, offline explícito, migration repetida, GPS sem atualização artificial e inscrição/envio web idempotentes. O teste de push intercepta o HTTP criptografado, verifica `aes128gcm`, urgência e TTL e confirma bloqueio após offline; não envia uma oferta de produção nem comprova entrega APNs/FCM real.

Relatório: `obj/delivery-validation/results/offer-availability-final.trx`. Dois testes financeiros anteriores com `DateTimeOffset -03:00` incompatível com o timestamp UTC do Npgsql foram excluídos da execução direcionada: `WorkMigrationVerifiesAndRollbackOnlyAllowsEmptyTables` e `WorkPartialWeekIsProratedPersistedAndVisibleToTheRider`. A falha é anterior a esta mudança e não foi corrigida aqui.

Nenhum deploy ou commit/push da API foi feito nesta rodada. APK Android `preview` correspondente concluído: [419e8012-6d80-4417-a9cf-f7eef3eab8ce](https://expo.dev/accounts/farleyedu/projects/zippygo-motoboy/builds/419e8012-6d80-4417-a9cf-f7eef3eab8ce), com verificação de Firebase/assinatura/conteúdo e cópia compatível em Downloads. A configuração do ambiente publicado e a aplicação das migrations em produção não foram inspecionadas; a disponibilidade persistente ainda exige essa publicação. Detalhes do cliente: [APK-ANDROID.md](../../zippygo-motoboy/docs/APK-ANDROID.md).
