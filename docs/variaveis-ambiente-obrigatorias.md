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
