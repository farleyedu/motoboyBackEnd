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
| `Automation__Meta__AppSecret` | App secret usado para validar assinatura dos webhooks. |
| `Automation__Meta__AccessToken` | Access token da conta WhatsApp Business. |
| `Automation__Meta__PhoneNumberId` | ID do numero de telefone configurado no Meta. |
| `Automation__Telegram__BotToken` | Token do bot Telegram usado para alertas internos. |
| `Automation__Telegram__ChatId` | Chat ID de destino dos alertas. |
| `WhatsApp__AccessToken` | Access token do canal WhatsApp usado pelo fluxo de atendimento. |
| `WhatsApp__CentralResetCommand` | Comando de reset do atendimento central (somente producao; ausente no template de Development). |

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

## Integracoes de IA / geocodificacao

| Variavel | Descricao |
| --- | --- |
| `OpenAI__ApiKey` | Chave da API OpenAI. |
| `OpenCage__ApiKey` | Chave da API OpenCage (geocodificacao). |

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
- [ ] `Automation__Telegram__BotToken` rotacionado com o BotFather.
- [ ] `WhatsApp__CentralResetCommand` trocado.
- [ ] `GoogleOAuth__ClientSecret` rotacionado no Google Cloud Console.
- [ ] `OpenAI__ApiKey` / `OpenCage__ApiKey` rotacionados nos respectivos paineis.
- [ ] Variaveis configuradas no ambiente de deploy (Render) e conferidas com `dotnet run` local usando `.env`/`launchSettings.json` nao versionado.

Esta lista foi gerada por inspecao estatica dos arquivos `appsettings*.json` em
2026-07-22. Rotacao efetiva nos provedores externos precisa ser confirmada
manualmente — nao e verificavel por leitura de codigo.
