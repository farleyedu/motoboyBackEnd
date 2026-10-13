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

| Variável | Descrição |
| --- | --- |
| `Ifood__Enabled` | `false` por padrão. Quando `true`, valida credenciais e chave no início; não habilita loja, eventos ou importação por si só. |
| `Ifood__ClientId` | ID da aplicação centralizada, obtido no portal. |
| `Ifood__ClientSecret` | Segredo da aplicação centralizada, fornecido por variável/secret store do servidor. |
| `Ifood__ActiveEncryptionKeyId` | Identificador da chave de escrita, por exemplo `key1`. Aceita letras ASCII, números, `_` e `-`, até 64 caracteres. |
| `Ifood__EncryptionKeys__key1` | Chave aleatória de exatamente 32 bytes, codificada em Base64. O identificador deve corresponder à chave ativa. Não aceita frase convertida em hash. |
| `Ifood__HttpTimeoutSeconds` | Timeout de autenticação, padrão 15, de 1 a 120 segundos. |
| `Ifood__RenewalSkewSeconds` | Margem máxima, padrão 60, de 0 a 300 segundos. Limitada a 10% da validade recebida para suportar tokens curtos. |
| `Ifood__FailureCooldownSeconds` | Pausa inicial após falha, padrão 10, de 1 a 300 segundos. Cresce até 300 segundos; `Retry-After` maior prevalece. |

O endereço de autenticação é fixo em HTTPS no cliente oficial; não existe opção de redirecionar segredos para outro host. Tokens são mantidos somente na memória do processo e a renovação usa `expiresIn`; não há refresh token no fluxo centralizado. Logging HTTP automático desse cliente está desabilitado para impedir exposição de exceções/corpos de transporte.

Para rotacionar a chave de criptografia, adicionar uma chave aleatória nova em `EncryptionKeys`, apontar `ActiveEncryptionKeyId` para ela e reiniciar o processo. Manter as chaves anteriores enquanto existirem envelopes cifrados por elas; remover uma chave antiga torna esses envelopes indecifráveis. O envelope autentica o ID da chave e o propósito, impedindo troca de segredo entre usos. Nesta fase não há token/credencial novo persistido no banco.

Trocar `ClientSecret` também exige reiniciar o processo para descartar opções/cache anteriores. Nenhum consumidor novo é executado automaticamente nesta fase; a primeira prova autenticada requer perfil, módulos e loja de teste confirmados em IF-00/IF-01. [Contrato de autenticação centralizada](https://developer.ifood.com.br/en-US/docs/food/guides/modules/authentication/centralized), [validade e permissões](https://developer.ifood.com.br/en-US/docs/food/guides/modules/authentication/intro).

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
