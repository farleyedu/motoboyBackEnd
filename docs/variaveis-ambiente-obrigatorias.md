# Variaveis de ambiente obrigatorias

Gerado como parte da Parte 1 do plano de delivery (`zippy-admin/docs/plano-implementacao-delivery-3-partes.md`,
item 1.2: "Remover segredos dos `appsettings*.json`, rotacionar os valores expostos e documentar as variaveis obrigatorias").

Nenhum segredo real deve permanecer em `appsettings.json` / `appsettings.Development.json`.
Os arquivos versionados usam `__SET_IN_ENV__` como placeholder; o valor real deve vir de
variavel de ambiente (Render, `.env` local nao versionado, ou secret store), seguindo a
convencao do ASP.NET Core: `Secao__Subsecao__Chave`.

## Banco de dados

| Variavel | Descricao |
| --- | --- |
| `ConnectionStrings__DefaultConnection` | String de conexao Postgres completa. Substitui `__DB_HOST__`, `__DB_NAME__`, `__DB_USER__`, `__DB_PASSWORD__` do template. |

## Autenticacao (JWT)

| Variavel | Descricao |
| --- | --- |
| `Jwt__SecretKey` | Segredo de assinatura do JWT. Minimo 32 bytes. Rotacionar invalida todos os tokens ativos — planejar janela de manutencao. |

## Login social (Google OAuth)

| Variavel | Descricao |
| --- | --- |
| `GoogleOAuth__ClientId` | Client ID do OAuth do Google. |
| `GoogleOAuth__ClientSecret` | Client secret do OAuth do Google. |

## Automacao / WhatsApp (Meta Graph API)

| Variavel | Descricao |
| --- | --- |
| `Automation__VerifyToken` | Token de verificacao do webhook Meta. |
| `Automation__Meta__AppSecret` | App secret usado para validar assinatura dos webhooks. Com ele configurado a assinatura passa a ser exigida. |
| `Atendimento__ChaveToken` | Chave que cifra os tokens da Meta guardados por numero de WhatsApp (nao troque depois de cadastrar tokens, ou eles precisam ser cadastrados de novo). |
| `Atendimento__CardapioBaseUrl` | Endereco publico do site (ex.: `https://app.zippygo.com.br`); o bot monta o link `/cardapio/{loja}` com ele. Sem isso o bot passa o cliente para a equipe. |
| `Automation__Meta__AccessToken` | Access token da conta WhatsApp Business. |
| `Automation__Meta__PhoneNumberId` | ID do numero de telefone configurado no Meta. |
| `WhatsApp__AccessToken` | Access token do canal WhatsApp usado pelo fluxo de atendimento. |

## Delivery / tracking operacional (Parte 2)

Estas nao sao segredos, mas sao **obrigatorias para o modulo de delivery funcionar**: com
`Enabled=false` (o default do template) todo endpoint operacional responde `503
DELIVERY_TRACKING_DISABLED`, o `DeliveryOutboxPublisher` nao publica eventos no SignalR e o
`map-state` cai no caminho legado. O sintoma tipico e "o simulador nao sobe" sem erro obvio.

| Variavel | Descricao |
| --- | --- |
| `DeliveryTracking__Enabled` | Liga o tracking operacional V2 (sessoes, heartbeat, localizacao, snapshot, outbox). Precisa ser `true` em qualquer ambiente que use o painel de delivery. |
| `DeliveryTracking__SimulatorEnabled` | Libera o simulador de motoboy (`/api/v2/delivery/simulator/*`). Manter `true` apenas enquanto o app real nao existe. |
| `DeliveryTracking__ApplyMigrationsOnStartup` | Aplica os `.sql` de `Migrations/Delivery/` no boot. Mantenha `false` em producao e rode as migrations no deploy. |
| `Cors__AllowedOrigins__0`, `__1`, ... | Origens liberadas no CORS. O hub SignalR negocia com credenciais, entao a API nao pode responder `Access-Control-Allow-Origin: *`. Sem configurar, o default cobre `https://zippy-admin-one.vercel.app`, `localhost:3000` e previews `*.vercel.app`. |

> Ao habilitar `DeliveryTracking__Enabled`, confirme antes que as migrations de
> `Migrations/Delivery/` ja foram aplicadas no banco do ambiente — em especial as
> `20260723_*`, que criam `delivery_route_stops`, `delivery_motoboy_route` e
> `delivery_realtime_outbox`. Ligar a flag com o schema desatualizado troca os 503
> por erros de SQL.

## iFood — autenticação centralizada (IF-05)

O módulo novo permanece desabilitado por padrão e não executa chamadas no boot. Estas variáveis são exclusivas do backend; não usar prefixos públicos do Next.js/Expo nem guardar valores reais em arquivos versionados. Credencial da aplicação e autorização de cada loja são controles diferentes.

> **A partir do IF-13**, `Ifood__Enabled`, `Ifood__ClientId`, `Ifood__ClientSecret` e `Ifood__PollingEnabled`/`Ifood__PollingMerchants__*` **não devem mais ser configurados por variável de ambiente**: a tela `Configuração iFood` (Gestão → `/apps/gestao/ifood`) grava isso em `ifood_app_settings`/`ifood_connections`, e um `IConfigurationProvider` dedicado sobrepõe esses valores por cima do que estiver em env a cada 15s. Deixar essas variáveis configuradas em paralelo não quebra nada (a tela sempre vence quando a tabela existe), mas é redundante e pode confundir leitura de ambiente. `Ifood__CommandsEnabled`/`Ifood__ExceptionsEnabled` também passaram a ser lidos da tela (toggle mestre); `Ifood__CommandMerchants__*`/`Ifood__ExceptionMerchants__*`/`Ifood__AllowedCommands__*`/`Ifood__AllowedExceptionActions__*` continuam só em env até o lote 2 do IF-13.

| Variável | Descrição |
| --- | --- |
| `Ifood__Enabled` | `false` por padrão. **Legado/fallback**: só vale se a tabela `ifood_app_settings` ainda não existir no ambiente. Quando `true`, valida credenciais e chave no início; não habilita loja, eventos ou importação por si só. |
| `Ifood__ClientId` | **Legado/fallback** (ver nota acima). ID da aplicação centralizada, obtido no portal. |
| `Ifood__ClientSecret` | **Legado/fallback** (ver nota acima). Segredo da aplicação centralizada, fornecido por variável/secret store do servidor. |
| `Ifood__ActiveEncryptionKeyId` | Identificador da chave de escrita, por exemplo `key1`. Aceita letras ASCII, números, `_` e `-`, até 64 caracteres. Continua exclusivamente em env mesmo depois do IF-13 — é a chave que cifra/decifra o `client_secret` gravado pela tela; guardá-la no mesmo lugar que o segredo anularia a proteção. |
| `Ifood__EncryptionKeys__key1` | Chave aleatória de exatamente 32 bytes, codificada em Base64. O identificador deve corresponder à chave ativa. Não aceita frase convertida em hash. |
| `Ifood__HttpTimeoutSeconds` | Timeout de autenticação, padrão 15, de 1 a 120 segundos. |
| `Ifood__RenewalSkewSeconds` | Margem máxima, padrão 60, de 0 a 300 segundos. Limitada a 10% da validade recebida para suportar tokens curtos. |
| `Ifood__FailureCooldownSeconds` | Pausa inicial após falha, padrão 10, de 1 a 300 segundos. Cresce até 300 segundos; `Retry-After` maior prevalece. |

O endereço de autenticação é fixo em HTTPS no cliente oficial; não existe opção de redirecionar segredos para outro host. Tokens são mantidos somente na memória do processo e a renovação usa `expiresIn`; não há refresh token no fluxo centralizado. Logging HTTP automático desse cliente está desabilitado para impedir exposição de exceções/corpos de transporte.

Para rotacionar a chave de criptografia, adicionar uma chave aleatória nova em `EncryptionKeys`, apontar `ActiveEncryptionKeyId` para ela e reiniciar o processo. Manter as chaves anteriores enquanto existirem envelopes cifrados por elas; remover uma chave antiga torna esses envelopes indecifráveis. O envelope autentica o ID da chave e o propósito, impedindo troca de segredo entre usos. Nesta fase não há token/credencial novo persistido no banco.

Trocar `ClientSecret` também exige reiniciar o processo para descartar opções/cache anteriores. Nenhum consumidor novo é executado automaticamente nesta fase; a primeira prova autenticada requer perfil, módulos e loja de teste confirmados em IF-00/IF-01. [Contrato de autenticação centralizada](https://developer.ifood.com.br/en-US/docs/food/guides/modules/authentication/centralized), [validade e permissões](https://developer.ifood.com.br/en-US/docs/food/guides/modules/authentication/intro).

## iFood — ingresso por polling (IF-06)

O worker fica inativo até habilitação **das duas flags**, confirmação de IF-00/IF-01 e configuração explícita das lojas. Não habilitar enquanto a API Reference/permissões do ambiente não estiverem conferidas. Polling PDV sinaliza presença da loja no iFood; não é apenas uma leitura sem efeito operacional.

| Variável | Descrição |
| --- | --- |
| `Ifood__PollingEnabled` | `false` por padrão. **Legado/fallback** (ver nota do IF-13 acima) — com a tela de configuração, o toggle "Polling de eventos" grava isso direto. Com `Ifood__Enabled=true`, ativa o worker de ingresso; não importa pedidos nem executa comandos de aceite/despacho. |
| `Ifood__PollingMerchants__0__MerchantId` | **Legado/fallback.** A partir do IF-13 essa lista é derivada automaticamente de `ifood_connections` (merchants com `status='authorized'`) — autorizar pela tela já basta, sem configurar isso em env. |
| `Ifood__PollingMerchants__0__EstabelecimentoId` | **Legado/fallback**, mesma observação acima. |
| `Ifood__PollingLeaseSeconds` | Reserva de captura no banco, padrão 90 segundos; deve superar `HttpTimeoutSeconds` em pelo menos 10 e ser no máximo 600. |
| `Ifood__AckBatchSize` | Quantidade máxima por ACK, padrão 2000, de 1 a 2000. Há no máximo cinco lotes de ACK por rodada; backlog fica durável para a próxima. |

Aplicar `20261013_01_ifood_integration_schema.sql` e `20261013_04_ifood_event_ingress.sql` no ambiente autorizado antes de habilitar. A segunda migration acrescenta reserva de aplicação, quarentena e estado de ACK; não altera autorização de lojas. O rollback IF-06 recusa dados em uso e deve preceder IF-04. Não usar `dotnet run` apontando para banco real como teste de migração.

O intervalo de polling é controlado no banco por aplicação e não tem opção de redução abaixo de 30 segundos. A reserva é renovada após obter token, antes do envio; erros/`Retry-After` prolongam o prazo. Reiniciar uma instância não contorna esse controle. Segredos/corpos e exceções HTTP originais não são registrados.

A lista atende uma aplicação configurada com até 100 merchants. Não executar duas credenciais diferentes sobre a mesma inbox como estratégia de escala: apenas uma procedência de ACK fica ativa por evento. O hash do client ID identifica a reserva/procedência, mas não é segredo nem credencial. Autorização/revogação oficial continua dependendo de sua sincronização com o portal nos lotes seguintes.

## iFood — importação de pedidos (IF-07)

O importador exige `Ifood__Enabled=true` e `Ifood__ImportEnabled=true`. As flags permanecem falsas por padrão; nenhuma foi alterada em ambiente real. Ele pode consumir a inbox já durável independentemente da flag de polling, mas precisa de autorização atual do merchant antes da consulta/gravação.

| Variável | Descrição |
| --- | --- |
| `Ifood__ImportEnabled` | `false` por padrão. Liga somente importação de PLC/PLACED; não confirma/prepara/despacha pedidos. |
| `Ifood__ImportLeaseSeconds` | Padrão 90; deve superar `2 × HttpTimeoutSeconds + 10`, até 600 segundos. Abrange autenticação/consulta e a projeção; renovação ocorre antes do envio/importação. |
| `Ifood__ImportBatchSize` | Padrão 5, entre 1 e 50. Claims individuais evitam consumir a reserva de pedidos esperando HTTP de outro. |
| `Ifood__ImportMaxAttempts` | Padrão 8, entre 1 e 100. Falhas transitórias têm prazo progressivo; falhas permanentes ficam em revisão. |

Aplicar IF-04, IF-06, schema do núcleo `20260925_02_schema.sql` e IF-07 `20261013_05_ifood_order_import.sql`, além das dependências de outbox do ambiente autorizado, antes da ativação. Cache/snapshots não são credenciais e não usam client secret; documentos/códigos esperados são removidos do payload. Retenção/diagnóstico do cache ainda precisam de IF-15/IF-16.

Pedidos importados ficam em Rascunho local e `zippy_delivery_enabled=false`, sem atribuição. Não alterar esse campo para contornar a proteção: critérios de preparo/rota/comando serão implementados em IF-09/IF-12. Frontends devem usar `identidadeExterna` e `pedidoIfood`, não converter os itens externos em quantidades inteiras. A importação não cria cliente/conversa do canal próprio pelo telefone iFood.

Rollback somente vazio, na ordem **IF-07 → IF-06 → IF-04**; com cache/pedidos existentes ele é recusado. Desligar flags não cancela nem apaga obrigações já persistidas. Nenhuma consulta oficial/implantação ocorreu na validação local: [IF-07](validacao-ifood/IF-07.md).

## iFood — projeção por eventos (IF-08)

O projetor exige `Ifood__Enabled=true` e `Ifood__ProjectionEnabled=true`, falsos por padrão. Consome a inbox durável sem HTTP novo; polling/importação têm flags independentes. Merchant e estabelecimento devem continuar autorizados.

| Variável | Descrição |
| --- | --- |
| `Ifood__ProjectionEnabled` | `false` por padrão. Liga projeção dos eventos restantes, sem comandos remotos nem habilitação de rotas. |
| `Ifood__ProjectionLeaseSeconds` | Padrão 90; de 10 a 600 segundos. Fence final impede efeito de proprietário expirado. |
| `Ifood__ProjectionBatchSize` | Padrão 20; de 1 a 100. Claims individuais com atualização transacional. |
| `Ifood__ProjectionMaxAttempts` | Padrão 8; de 1 a 100. Falha transitória tem retry limitado; espera por importação não gasta tentativa técnica. |

Aplicar IF-04/IF-06/IF-07 e `20261013_06_ifood_order_projection.sql`, além da outbox, no ambiente autorizado antes de habilitar. Não executar API/migrations em banco real como teste. Projeção não confirma/prepara/despacha pela API: comandos são IF-09. CAN confirma cancelamento local; CON permanece conclusão externa e não cria prova física ou recebimento Zippy.

Patch incompleto/desconhecido e alteração de endereço ficam em revisão; consulta oculta valores/detalhes que perderam atualidade. Não habilitar cobrança/rota usando os valores legados. Deadline de disputa não executa timeoutAction no banco; resposta/settlement oficial são distintos. Reconciliação e alertas de backlog/revisão ainda pertencem a IF-15/IF-16.

Rollback vazio na ordem **IF-08 → IF-07 → IF-06 → IF-04**; histórico/efeitos impedem remoção. Não ligar `zippy_delivery_enabled` para contornar a proteção. Nenhuma flag real foi alterada. Evidência: [IF-08](validacao-ifood/IF-08.md).

## iFood — comandos persistentes (IF-09)

Exigem `Ifood__Enabled=true` e `Ifood__CommandsEnabled=true`, falsos por padrão, merchants já autorizados e ações explicitamente habilitadas. Não houve configuração/ativação real. Confirmar capacidades e contrato do perfil na IF-01 antes de ligar.

| Variável | Descrição |
| --- | --- |
| `Ifood__CommandsEnabled` | `false`. Liga somente intenções/envio de confirmar, iniciar preparo, pronto e despacho; sem habilitar rotas. |
| `Ifood__CommandMerchants__0__MerchantId` | UUID de merchant explicitamente autorizado para comandos. Até 100, sem repetição. |
| `Ifood__CommandMerchants__0__EstabelecimentoId` | UUID da loja vinculada a esse merchant; token HTTP e reserva rechecados por esse vínculo. |
| `Ifood__AllowedCommands__0` | Ação habilitada do contrato confirmado: `confirm`, `start_preparation`, `ready_to_pickup` ou `dispatch`. Lista vazia por padrão; ações de cancelamento/disputa não entram neste worker. |
| `Ifood__CommandLeaseSeconds` | Padrão 90; pelo menos `2 × HttpTimeoutSeconds + 10`, no máximo 600. Renovada antes do POST. |
| `Ifood__CommandBatchSize` | Padrão 5, de 1 a 50, com claims individuais. |
| `Ifood__CommandMaxAttempts` | Padrão 8, de 1 a 100. Limitam falha anterior ao envio ou rejeição HTTP explícita 401/429; resultado incerto nunca é reenviado automaticamente. |
| `Ifood__ScheduledConfirmationUsesPreparationStart` | `false`. Padrão conservador usa o menor prazo entre createdAt+8min e preparo+8min. `true` usa preparo+8min no SCHEDULED, somente após confirmação do contrato do perfil em IF-01. IMMEDIATE continua createdAt+8min. |

Aplicar IF-04/IF-06/IF-07/IF-08 e `20261013_07_ifood_outbound_commands.sql` mais outbox no ambiente autorizado. SQL de rollback de IF-08 exige remover IF-09 antes; rollback de IF-09 recusa qualquer operação Food/histórico. Ordem vazia: **IF-09 → IF-08 → IF-07 → IF-06 → IF-04**. Não iniciar API contra banco real como validação.

HTTP 202 significa aceitação assíncrona, não preparo/rota comprovados. Timeout, falha de rede, 5xx e crash após o marcador de envio ficam incertos; novo operationId não contorna o bloqueio. 401/429 têm retry com pausa/limite, respeitando Retry-After; valor acima de 30 dias exige revisão sem envio antecipado. Recuperação por eventos e diagnóstico/conciliador IF-16 permanecem separados.

Não calcular preparationStartDateTime nem preencher horários desconhecidos. Preparo/pronto aguardam o preparo oficial; despacho próprio requer RTP confirmado e, se agendado, não é enviado antes do início da janela nesta política local conservadora. Fonte e amostras do perfil real continuam pendentes; a flag de comandos não habilita `zippy_delivery_enabled`, prova física ou recebimento local.

Endpoints/DTO e prova local: [IF-09](validacao-ifood/IF-09.md). Painel de ações/capacidades: [IF-11](validacao-ifood/IF-11.md); cancelamento/disputa: [IF-10](validacao-ifood/IF-10.md); rota/coleta pertence a IF-12.

## iFood — cancelamento e disputas (IF-10)

Exigem `Ifood__Enabled=true` e `Ifood__ExceptionsEnabled=true`, falsos por padrão, merchants já autorizados e ações explicitamente habilitadas. Não houve configuração/ativação real. Confirmar endpoints, corpos e handshake do perfil na IF-01 antes de ligar.

| Variável | Descrição |
| --- | --- |
| `Ifood__ExceptionsEnabled` | `false`. Liga consulta de motivos, solicitação de cancelamento e resposta a disputas. Não cancela, reembolsa nem executa timeout localmente. |
| `Ifood__ExceptionMerchants__0__MerchantId` | UUID de merchant explicitamente autorizado. Até 100, sem repetição. |
| `Ifood__ExceptionMerchants__0__EstabelecimentoId` | UUID da loja vinculada; token HTTP e reserva rechecados por esse vínculo. |
| `Ifood__AllowedExceptionActions__0` | Ação habilitada: `request_cancellation`, `dispute_accept`, `dispute_reject` ou `dispute_alternative`. Lista vazia por padrão. |
| `Ifood__ExceptionBatchSize` | Padrão 5, de 1 a 50, com claims individuais. |
| `Ifood__CancellationReasonsTtlSeconds` | Padrão 120, de 15 a 300. Validade do catálogo de motivos; o motivo é consultado de novo antes do POST. |

Com exceções ligadas, `Ifood__CommandLeaseSeconds` deve ser pelo menos `3 × HttpTimeoutSeconds + 10` (máximo 600) e `Ifood__CommandMaxAttempts` segue valendo. O worker de exceções é independente do de comandos IF-09.

Aplicar IF-04/IF-06/IF-07/IF-08/IF-09 e `20261013_08_ifood_cancellation_disputes.sql` mais outbox no ambiente autorizado. O rollback de IF-09 exige remover IF-10 antes, e o de IF-10 recusa operações/revisões em uso. Ordem vazia: **IF-10 → IF-09 → IF-08 → IF-07 → IF-06 → IF-04**. Não iniciar API contra banco real como validação.

HTTP 201/202 é só aceite assíncrono: o pedido/disputa mudam por evento oficial. Timeout, rede, 5xx e queda após o marcador de envio ficam incertos; 422 de disputa vai para revisão; novo operationId não contorna. Alternativas e valores vêm do payload da disputa, nunca de padrão local. Painel de ações: [IF-11](validacao-ifood/IF-11.md); rota/coleta: IF-12. Evidência: [IF-10](validacao-ifood/IF-10.md).

## iFood — painel e capacidades (IF-11)

Não acrescenta variável de ambiente nem habilita integração. `GET /api/v2/delivery/ifood/pedidos/{pedidoId}/capabilities` usa as flags, merchants e ações de IF-09/IF-10 já existentes, autorização do vínculo, revisão/estado e operações ativas. Retorna condições de bloqueio mesmo com os comandos desligados, sob `Delivery/integracao_ifood`; leitura sem HTTP externo ou efeito colateral. Respostas do pedido e capacidades não expõem credenciais.

O painel consulta motivos/opções apenas por ação explícita e conserva `operationId` após resultado incerto. Polling visual não substitui inbox, evento oficial ou conciliação. Contratos, filtros de modalidade/preparo e validação: [IF-11](validacao-ifood/IF-11.md).

## Integracoes de IA / geocodificacao

| Variavel | Descricao |
| --- | --- |
| `Google__MapsApiKey` | Chave do Google Maps Platform com a **Geocoding API** ativada (geocodificacao do endereco de entrega). Sem ela o cardapio web recusa pedidos de entrega. |

## Pagamentos (Asaas)

| Variavel | Descricao |
| --- | --- |
| `Payments__Asaas__ApiKey` | Chave da API Asaas. Presente somente no template de producao. |
| `Payments__Asaas__WebhookToken` | Token de validacao dos webhooks Asaas. Presente somente no template de producao. |

## Checklist de rotacao

Toda credencial listada acima que já esteve versionada em texto plano no historico do
git (antes desta limpeza) deve ser tratada como comprometida:

- [ ] `Jwt__SecretKey` rotacionado no provedor/gerador e reemitido.
- [ ] `Payments__Asaas__ApiKey` / `WebhookToken` rotacionados no painel Asaas.
- [ ] `Automation__Meta__AppSecret` / `AccessToken` rotacionados no Meta for Developers.
- [ ] `GoogleOAuth__ClientSecret` rotacionado no Google Cloud Console.
- [ ] `Google__MapsApiKey` rotacionado nos respectivos paineis. (A chave antiga `OpenCage__ApiKey` nao e mais lida: pode ser apagada.)
- [ ] Variaveis configuradas no ambiente de deploy (Render) e conferidas com `dotnet run` local usando `.env`/`launchSettings.json` nao versionado.

Esta lista foi gerada por inspecao estatica dos arquivos `appsettings*.json` em
2026-07-22. Rotacao efetiva nos provedores externos precisa ser confirmada
manualmente — nao e verificavel por leitura de codigo.
