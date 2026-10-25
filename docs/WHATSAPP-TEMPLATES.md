# Modelos Meta (templates WhatsApp) — contrato

Base: `/api/v2/atendimento/estabelecimentos/{estabelecimentoId}/canais/{canalId}/templates`
Respostas no formato padrão `{ success, data }`; erros `{ success:false, error, code, details:{ comoResolver, campo, link } }`
(400/422 de validação vêm como `{ error, fieldErrors }`).

## Permissões
- Ler (GET lista, GET detalhe, GET diagnostico, POST validar): quem acessa a loja (ou Gestão).
- Criar, sincronizar, excluir: `WhatsApp/configurar` na própria loja ou Gestão. Número central de testes: só Gestão.
- Canal ou modelo de outra loja = 404. `podeGerenciar` na lista diz se os botões devem aparecer.

## Rotas
| Método | Rota | Uso |
|---|---|---|
| GET | `/` | Lista (só banco, sem chamar a Meta): `{ canalId, origem, podeGerenciar, itens[], resumo{total,aprovados,emAnalise,rejeitados,outros}, ultimaSincronizacao }` |
| GET | `/diagnostico` | `{ ok, origem, codigo, mensagem, comoResolver, link }` — token, WABA e permissão `whatsapp_business_management` |
| GET | `/{id}` | Detalhe com `historico[]` e `componentesMeta` (o que a Meta tem) |
| POST | `/validar` | Mesmo corpo do POST; `{ valido, erros, componentesMeta }` sem enviar |
| POST | `/` | Cria e envia para análise. **201** `{ confirmado:true, modelo }`; **202** `{ confirmado:false, modelo }` = Meta não respondeu, modelo fica `verificando` |
| POST | `/sincronizar` | `{ atualizados, alterados, naoEncontrados, ignorados (só Gestão), completa, sincronizadoEm, lista }`. 429 se repetido em menos de 10 s |
| POST | `/{id}/sincronizar` | Atualiza um modelo; devolve o detalhe |
| DELETE | `/{id}?confirmarNome=nome_tecnico` | Exclui só este modelo na Meta (hsm_id). Sem o nome certo = 400 |

## Corpo do POST
```json
{
  "nome": "pedido_confirmado",
  "categoria": "UTILITY",            // ou MARKETING (aceita "utilidade")
  "idioma": "pt_BR",
  "conteudo": {
    "cabecalho": "Pedido {{1}}", "cabecalhoExemplos": ["#123"],
    "corpo": "Ola {{1}}, seu pedido {{2}} foi confirmado.", "corpoExemplos": ["Maria", "#123"],
    "rodape": "Zippy",
    "botoes": [
      { "tipo": "QUICK_REPLY", "texto": "Ok" },
      { "tipo": "URL", "texto": "Acompanhar", "url": "https://x.com/p/{{1}}", "urlExemplo": "https://x.com/p/123" },
      { "tipo": "PHONE_NUMBER", "texto": "Ligar", "telefone": "+5534999990000" },
      { "tipo": "COPY_CODE", "codigo": "ZIPPY10" }   // só MARKETING
    ]
  }
}
```
Regras (422 com todos os campos de uma vez): nome `^[a-z0-9_]+$` até 512; cabeçalho ≤ 60, uma linha, no máximo `{{1}}`;
corpo 1–1024, variáveis `{{1}}..{{n}}` em sequência (máx. 20), sem começar/terminar com variável e com texto entre duas;
um exemplo por variável (≤ 200, uma linha); rodapé ≤ 60 sem variável; até 10 botões (texto ≤ 25, sem repetir), URL ≤ 2,
telefone ≤ 1, copiar código ≤ 1 (Marketing), respostas rápidas agrupadas; URL https com `{{1}}` só no final.

## Modelo (`TemplateView`)
`id, estabelecimentoId, canalId, nome, idioma, categoria, categoriaMeta, conteudo, status, situacao, statusMeta,
disponivelParaEnvio, motivoRejeicao, orientacaoRejeicao, metaTemplateId, qualidade, ultimoErro, ultimoErroCodigo,
criadoPor, criadoEm, enviadoEm, statusAlteradoEm, sincronizadoEm, excluidoEm, historico?, componentesMeta?`

`status` (para filtro/cor): `enviando | verificando | falha | em_analise | aprovado | rejeitado | pausado | desativado | indisponivel | excluido`.
`disponivelParaEnvio` só é `true` com `statusMeta = APPROVED`. Nunca usar outro campo para liberar envio.

## Códigos de erro
| HTTP | code | Quando |
|---|---|---|
| 409 | `WA_TPL_DUPLICADO` | nome + idioma já existe na WABA (ou é de outra loja / de fora do Zippy), envio em curso |
| 409 | `WA_TPL_NOME_EM_EXCLUSAO` | nome de modelo excluído (Meta bloqueia ~30 dias) |
| 409 | `WA_TPL_NAO_EXCLUIVEL` | excluir modelo não confirmado na Meta |
| 422 | `WA_TPL_CONTEUDO_RECUSADO` | a Meta recusou o conteúdo na hora |
| 422 | `WA_TPL_SEM_PERMISSAO_GESTAO` | token sem `whatsapp_business_management` |
| 422 | `WA_TPL_WABA_INCORRETA` | número não está na WABA conectada/autorizada |
| 422 | `WA_TOKEN_INVALIDO`, `WA_TPL_SEM_TOKEN`, `WA_TPL_NUMERO_INATIVO`, `WA_WABA_INVALIDA` | conexão |
| 429 | `WA_TPL_LIMITE` | limite da Meta ou sincronização repetida |
| 502 | `WA_META_ERRO` | resposta inesperada da Meta |
| 503 | `WA_META_INDISPONIVEL`, `WA_TPL_WABA_NAO_CONFIGURADA`, `WA_TOKEN_GLOBAL_AUSENTE` | Meta fora / servidor sem configuração |

## Configuração
- Número próprio: usa a WABA e o token da conexão existente.
- Números da Zippy/central: `WhatsApp__WabaIds` (IDs das WABAs da Zippy, separados por vírgula) + `WhatsApp__AccessToken`.
  O backend confirma em `GET /{waba}/phone_numbers` que o número pertence à WABA antes de operar.
- Migração: `Migrations/Delivery/20261029_01_whatsapp_templates.sql` (verificação `_02_..._verify.sql`, rollback em `rollback/`).

## Garantias
- Timeout na criação: modelo fica `verificando`; antes de qualquer reenvio o backend procura na Meta por nome + idioma e
  adota o ID se achar. Só vira `falha` (liberando reenvio) após 2 min com a lista completa sem o modelo.
- Modelos da WABA desconhecidos do Zippy nunca são alterados, excluídos ou adotados; o nome deles fica bloqueado.
- Sem edição de modelo enviado nesta versão: correção = nova versão com outro nome.
