# Conexão do WhatsApp pela Meta (Embedded Signup v4)

Cada restaurante conecta o próprio WhatsApp autorizando o app central **ZippyGo** pela Meta (Independent Tech Provider). A WABA e o número continuam sendo do restaurante, e o Zippy guarda só a autorização (token de integração, cifrado). A conexão manual (5 dados) continua existindo como opção avançada.

Base de todas as rotas: `/api/v2/atendimento/estabelecimentos/{estabelecimentoId}`. Envelope padrão `{ success, data, error, code, details }`. Erros do WhatsApp trazem `code` estável e `details = { comoResolver, campo, link }`.

## Fluxo no painel

1. `GET conexao-meta/configuracao`: se `disponivel=false`, mostrar só a conexão manual. Se `modo="teste"`, avisar que a aprovação da Meta ainda está pendente e que só contas de teste concluem.
2. `POST conexao-meta/iniciar` `{ canalId?: guid, coexistencia?: bool }` → `{ tentativaId, state, expiraEm, modo, extras, appId, configurationId, sdkVersion }`.
3. Carregar o SDK (`https://connect.facebook.net/en_US/sdk.js`) **só nesta tela**, chamar `FB.init({ appId, version: sdkVersion, xfbml: true, autoLogAppEvents: true })` e `FB.login(cb, { config_id: configurationId, response_type: 'code', override_default_response_type: true, extras })`.
4. Ouvir `message` de origem `*.facebook.com` com `type === 'WA_EMBEDDED_SIGNUP'` (guardar `event`, `data.waba_id`, `data.phone_number_id`, `data.business_id`, `data.current_step`, `data.error_message`).
5. Assim que o `FB.login` devolver `authResponse.code` (**o código vale 30 s**), chamar `POST conexao-meta/concluir`:
   ```json
   { "tentativaId": "…", "state": "…", "evento": "FINISH", "code": "…",
     "wabaId": "…", "phoneNumberId": "…", "businessId": "…" }
   ```
   Cancelamento: `{ tentativaId, state, evento: "CANCEL", etapaCancelada }`. Erro do SDK: `evento: "ERROR", erroMensagem, erroCodigo, sessaoMeta`.
6. A resposta é um `EstadoConexaoMetaView`. Com `acoes` contendo `escolher_numero`, mostrar `numeros` (só os `selecionavel=true` podem ser escolhidos) e chamar `POST conexao-meta/ativar { tentativaId, phoneNumberId }`. Com `informar_pin`, pedir o PIN de 6 dígitos e chamar `ativar` com `pin`.
7. Retomar: `GET conexao-meta/estado?tentativaId=` (sem `tentativaId` = a mais recente da loja).

Nunca guardar `state`, `code` ou qualquer dado da Meta em localStorage/sessionStorage nem em logs. O navegador nunca recebe token.

## Estados

`estado` (geral): `aguardando_autorizacao`, `configurando`, `conectado`, `atencao_necessaria`, `erro`, `revogado`, `desconectado`. Só fica `conectado` quando todas as etapas verificáveis estão ok, inclusive a primeira mensagem recebida.

`estadoTentativa`: `nenhuma`, `iniciada`, `trocando`, `autorizada` (falta escolher ou repetir uma etapa; vale 24 h), `ativando`, `concluida`, `cancelada`, `expirada`, `erro`, `substituida`.

`etapas[]`: `{ codigo, estado, mensagem, comoResolver?, responsavel?, acao?, link?, codigoErro? }`
- `codigo`: `autorizacao`, `permissoes`, `waba`, `numero`, `registro`, `inscricao`, `webhook`, `recebimento`, `coexistencia`
- `estado`: `ok`, `pendente`, `atencao`, `erro`, `nao_aplicavel`
- `responsavel`: `loja` (o restaurante resolve), `zippy` (configuração/servidor), `meta` (aguardar a Meta)
- `acao`: `conectar`, `reconectar`, `escolher_numero`, `informar_pin`, `tentar_de_novo`, `reparar`, `enviar_teste`, `aguardar`, `abrir_whatsapp_manager`, `registrar_webhook`, `conectar_manual`, `contatar_suporte`

## Gestão do número

- `POST canais/{canalId}/revalidar { reparar?: bool, pin?: string }` → `DiagnosticoConexaoView { canalId, origem, estado, verificadoEm, appId, wabaId, phoneNumberId, etapas, acoes, canal }`. Sem `reparar`, só lê. Com `reparar`, refaz o que é seguro: inscrição do app na WABA e registro do número (no máximo 3 tentativas em 72 h; nunca na coexistência). Funciona para todas as origens.
- `POST canais/{canalId}/reconectar` → igual ao `iniciar` (mesmo canal: conversas e configurações ficam). Depois segue o passo 3.
- `DELETE canais/{canalId}/conexao` → `DesconexaoView { canalId, origem, historicoPreservado, inscricaoRemovida, motivoInscricaoMantida, mensagem }`. **Mudou:** desconectar não apaga mais o canal em nenhuma origem; ele fica `status: "inativo"` com `conexao.desconectadoEm`. Só o número central de testes ainda é removido. Nada é excluído na Meta.
- `CanalView` ganhou `conexao: { autorizacaoEstado, coexistencia, registroEstado, estadoMeta, inscricaoWabaEm, desconectadoEm }` e a origem `autorizado`. `GET canais` passa a listar também canais `inativo` (desconectados).

## Correções de contrato que o painel precisa fazer

| Painel hoje (`lojaApi.ts`) | Backend |
|---|---|
| `POST canais/conectar-proprio` | `POST canais/proprio` |
| `POST canais/{id}/webhook` (espera `WebhookCanal`) | `POST canais/{id}/webhook/registrar` (devolve `CanalView`; o webhook está em `data.webhook`) |
| `POST canais/zippy` | `POST canais` com `{ origem: "zippy", phoneNumberId, … }` |

## Códigos de erro novos

| code | HTTP | Quando |
|---|---|---|
| `WA_META_CONEXAO_INDISPONIVEL` | 503 | Embedded Signup não configurado no servidor |
| `WA_META_ORIGEM_INVALIDA` | 403 | `Origin` fora de `OrigensPermitidas` |
| `WA_META_TENTATIVA_INVALIDA` | 403/404 | state errado, outro usuário ou tentativa de outra loja |
| `WA_META_TENTATIVA_EXPIRADA` | 410 | prazo de 15 min (autorizar) ou 24 h (ativar) passou |
| `WA_META_TENTATIVA_EM_USO` | 409 | repetição/replay ou outra aba processando |
| `WA_META_FLUXO_CANCELADO` | (no estado) | o usuário cancelou na Meta |
| `WA_META_CODIGO_INVALIDO` | 422 | a Meta recusou o código (expirou em 30 s ou já foi usado) |
| `WA_META_APP_DIFERENTE` | 422 | token não é do app ZippyGo |
| `WA_META_AUTORIZACAO_INCOMPLETA` | 422 | faltou permissão ou WABA na autorização |
| `WA_META_WABA_NAO_AUTORIZADA` | 422 | a WABA do navegador não está no token |
| `WA_META_NUMERO_NAO_AUTORIZADO` | 422 | número fora da WABA autorizada |
| `WA_META_TOKEN_REVOGADO` | 422 | acesso removido/expirado → reconectar |
| `WA_META_SEM_NUMERO` | (no estado) | WABA sem número (`FINISH_ONLY_WABA`) |
| `WA_META_PIN_NECESSARIO` | (no estado) | o número já tem PIN de duas etapas |
| `WA_META_REGISTRO_LIMITE` | (no estado) | limite de registros da Meta |
| `WA_META_NUMERO_NAO_VERIFICADO` | (no estado) | número não verificado no WhatsApp Manager |
| `WA_META_COEXISTENCIA_INDISPONIVEL` | 422 | coexistência pedida e não liberada |
| `WA_META_CONEXAO_EXISTENTE` | 409 | o número já está conectado nesta loja de outra forma → reconectar |
| `WA_NUMERO_OUTRA_LOJA` | 409 | número de outra loja (nunca apropriado) |

## Segurança e isolamento

- Toda rota confere se o usuário é da loja (Gestão ou `WhatsApp/configurar`). Canal ou tentativa de outra loja responde 404.
- O `state` (32 bytes aleatórios) só existe no navegador; o banco guarda o SHA-256. O concluir exige o mesmo usuário e a mesma loja, e a passagem `iniciada → trocando` é atômica (replay e abas concorrentes recebem 409).
- Código, token, App Secret e PIN nunca vão para resposta, log nem auditoria. Token e PIN ficam cifrados (`Atendimento__ChaveToken`).
- WABA e número são sempre conferidos na Meta com o token (`debug_token` → `granular_scopes`, `GET /{waba}/phone_numbers`). Os IDs do navegador só escolhem entre os autorizados.
- WABA compartilhada: cada loja tem a própria autorização; o número continua com dono único (índices únicos de `phone_number_id` e telefone); eventos do webhook global são roteados por `phone_number_id` e conferidos contra a WABA da entrada.
- Desconectar só tira a inscrição do app da WABA quando: a WABA não é da Zippy (`WhatsApp__WabaIds` / `BusinessIdIntegrador`), o portfólio foi confirmado pela Meta, nenhum outro número de nenhuma loja usa a WABA e nenhuma outra loja tem autorização ativa nela.
- `account_update` `PARTNER_REMOVED` (o restaurante removeu o ZippyGo) revoga as autorizações daquela WABA e marca os números com erro.
- Coexistência: o número nunca é registrado nem desregistrado pela API; contatos e histórico são pedidos uma única vez; `smb_message_echoes` ficam gravados em `wa_evento` (tipo `eco`) e **nunca** disparam o robô. Ainda não aparecem no chat (pendência).

## Implantação

Configuração (`WhatsApp__EmbeddedSignup__*`), sem valores padrão inventados:

| Variável | Valor |
|---|---|
| `Habilitado` | `true` quando tudo abaixo estiver pronto |
| `AppId` | ID do app ZippyGo |
| `ConfigurationId` | ID da configuração do Facebook Login for Business (WhatsApp Embedded Signup, v4) — **pendente** |
| `AppSecret` | App Secret do ZippyGo (ou cai em `Automation__Meta__AppSecret`) |
| `AppReview` | `aprovada` só depois da aprovação de `whatsapp_business_messaging` e `whatsapp_business_management` — **em andamento** |
| `CoexistenciaHabilitada` | `true` para oferecer manter o app WhatsApp Business |
| `OrigensPermitidas` | domínio(s) https do painel, separados por vírgula — **pendente** (o mesmo de Allowed domains / Valid OAuth redirect URIs) |
| `BusinessIdIntegrador` | Business ID do portfólio Zippy Food |
| `MinutosParaAutorizar` / `HorasParaAtivar` | 15 / 24 |

No painel da Meta (app ZippyGo), pendências que o código não resolve:
- Criar a configuração do **Facebook Login for Business** com o produto WhatsApp Embedded Signup (v4) e copiar o Configuration ID.
- Cadastrar o domínio https do painel em **Allowed domains** e **Valid OAuth redirect URIs** (sem isso o evento `WA_EMBEDDED_SIGNUP` não chega ao painel).
- Webhook do app (objeto `whatsapp_business_account`): assinar `messages` e `account_update`; com coexistência, também `smb_message_echoes`, `history` e `smb_app_state_sync`. O diagnóstico confere isso (só leitura) e acusa o que faltar.
- Concluir o App Review das duas permissões. Até lá, `AppReview` fica diferente de `aprovada` e o painel mostra modo teste.
- O modo Ao vivo do app não basta para onboarding de terceiros.

Migração: `Migrations/Delivery/20261030_01_whatsapp_conexao_meta.sql` (aditiva; o serviço de migração aplica na subida). Verificação em `20261030_02_whatsapp_conexao_meta_verify.sql`; rollback em `rollback/20261030_01_whatsapp_conexao_meta.down.sql` (recusa se já houver canal `autorizado`). Canais `zippy`, `proprio` e `central` não mudam.

Teste controlado com conta real: com `AppReview` ainda pendente, só um usuário com função no app ZippyGo conclui o fluxo. Use uma WABA de teste e confira no diagnóstico: autorização, inscrição, registro e a primeira mensagem recebida.
