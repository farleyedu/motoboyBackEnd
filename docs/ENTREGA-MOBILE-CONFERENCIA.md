# Conta e finalização mobile — 08/10/2026

Implementa os contratos faltantes do lote 2.4 e etapas 3/4 do app aprovado. Reutiliza fila, sessões, pedido, pagamento e outbox existentes. Código local, sem publicação ou migração em produção.

## APIs

Todas retornam o envelope `ApiResponse` existente. A conta/loja/motoboy são obtidos do token e revalidados; não podem ser escolhidos no corpo.

| Autenticação | Caminho com `/api` | Operação |
| --- | --- | --- |
| Principal | `GET/PATCH /motoboys/me/perfil` | Perfil próprio; atualização dos campos permitidos. |
| Principal | `PUT /motoboys/me/veiculo` | Moto própria, placa/modelo/ano validados. |
| Principal | `GET/POST /motoboys/me/documentos` | Metadados próprios/upload privado; tipos avatar, identificacao, cnh e moto. |
| Principal | `GET /motoboys/me/documentos/{id}` | Imagem privada do proprietário. |
| Operacional | `GET /v2/motoboys/me/session/store` | Endereço/coordenadas reais da loja da sessão. |
| Operacional | `PATCH /v2/motoboys/me/session/pause` | `{ "paused": true/false }`; persiste pausa. |
| Operacional | `POST /v2/motoboys/me/session/stops/current/pickup` | Coleta versionada em lote, revalidando o conjunto esperado. Sem corpo mantém compatibilidade com o chamador legado. |
| Operacional | `GET /v2/motoboys/me/session/completion/{pedido}` | Versão, total e requisitos reais de código/pagamento/foto. |
| Operacional | `POST .../completion/{pedido}/code` | `{ "codigo": "..." }`; valida sem entregar/cobrar. |
| Operacional | `POST .../completion/{pedido}/proof` | `{ "base64": "..." }`; foto privada da parada atual, retorna UUID. |
| Operacional | `GET .../completion/{pedido}/proof` | Revisar a própria foto da parada atual antes da conclusão. |
| Operacional | `POST /v2/motoboys/me/session/completion` | Transação idempotente abaixo. |
| Principal | `GET /motoboys/me/receipts/{operationId}` | Recibo próprio mesmo depois de expirar o token operacional. |
| Principal | `GET /motoboys/me/receipts/{operationId}/proof` | Comprovante autorizado pelo recibo e proprietário. |

Oferta aceita/recusada pode enviar `expectedOfferId`; validade e identidade são conferidas dentro da transação. Não entrega pode enviar `expectedPedidoId`; um ID antigo não falha a próxima parada. Pausa impede nova atribuição e não apaga GPS, entrega ou fila. Não é permitido pausar com oferta pendente ainda sem decisão.

## Finalização atômica

```json
{
  "operationId": "657344b3-e1b7-4f93-bcbc-94249790ad23",
  "expectedPedidoId": 23,
  "expectedVersion": 8,
  "codigo": "4821",
  "proofId": null,
  "payments": [
    { "method": "pix", "amount": 50.00, "receivedConfirmed": true },
    { "method": "dinheiro", "amount": 36.90, "cashReceived": 50.00, "receivedConfirmed": true }
  ]
}
```

Exemplo exclusivamente de teste. Métodos: `dinheiro`, `pix`, `debito`, `credito`; uma ou duas partes positivas e soma exata em centavos. Dinheiro cobre a própria parte; confirmação explícita obrigatória. Pedido pago não aceita novas partes. Pix/cartão são registro de recebimento conferido, sem gateway ou QR inventado.

A transação trava fila e pedido, revalida vínculo, usuário/loja/motoboy, sessão/epoch/prazo, ID e versão, coleta/chegada, código, total, pagamento e política de foto. Reutiliza `CompleteCurrentInternalAsync` na mesma conexão/transação para registrar status/pagamento, promover a fila, gravar outbox e recibo imutável. Erro desfaz tudo. Consultas usam cancelamento e limite de comando de 10 s.

`operationId` é persistido pelo app antes do POST. Mesmo UUID e corpo retornam o recibo original e a fila atual, inclusive depois de promover o próximo pedido. O UUID é associado a usuário/loja/motoboy/pedido e hash do corpo; reutilização com outro conteúdo ou proprietário gera conflito. Não há nova cobrança nem entrega da próxima parada. O recibo omite o código secreto; registra somente que foi conferido.

Perda de resposta: consultar o recibo com token principal. Encontrado: usar o recibo como confirmação. 404: reenviar a mesma operação quando autorizado pelo contexto operacional. Timeout/503 não provam falha da transação. O app mantém pendência, bloqueia logout/mutação de rota e nunca inventa avanço local.

Fotos/documentos aceitam JPEG/PNG até 4 MiB e 20 MP. Normalização corrige as oito orientações EXIF, remove metadados e limita dimensões; armazenamento privado em BYTEA. Leitura e substituição respeitam dono/parada. Upload não equivale a entrega nem aprovação administrativa de documento. Respostas de mídia e recibo usam `no-store`.

## Migrações e compatibilidade

No ambiente escolhido, aplicar em ordem:

1. `Migrations/Delivery/20261008_01_motoboy_conta_documentos.sql` — documentos privados, ano da moto e pausa.
2. `Migrations/Delivery/20261008_02_delivery_completion.sql` — política de foto, comprovantes e operações/recibos.
3. `Migrations/Delivery/20261008_03_verify.sql` — verificação das colunas, tabelas e ledger.

Os dois primeiros arquivos são reaplicáveis e registram o ledger. A verificação e o rollback foram exercitados com banco de teste. O rollback em `Migrations/Delivery/rollback/20261008_01_02_conta_completion.down.sql` recusa remover tabelas com evidências e conserva colunas aditivas que podem ter existido antes. Arquivar dados e preparar manutenção são pré-condições para retorno; não usar esse script para apagar histórico.

**Publicar API e app de forma coordenada:** `stops/current/deliver` passa a responder `422 COMPLETION_CONTRACT_REQUIRED` para cliente mobile. O app atualizado usa `/completion`; simulador preserva a entrega legada. Versões antigas do mobile precisam ser atualizadas antes de voltar a finalizar. O novo app também depende dos endpoints de conta, pausa e coleta. A política `requireDeliveryProof` começa false; atualização de settings que omite o campo preserva o valor atual.

Não iniciar API apontando para produção como forma de validar migrations. Não houve commit/push/deploy nesta rodada; `9de357e` é o commit anterior de sincronização.

## Validação executada

**949 testes passaram, zero ignorados**, incluindo **23 testes com PostgreSQL real isolado**. Relatório local: `obj/delivery-validation/results/etapa34-final.trx`. Cobertura nova: finalização, quatro replays concorrentes, corpo conflitante, código/pagamento inválidos, sessão/tenant/versão/chegada, prova exigida/alheia, leitura/substituição de foto, recibo próprio, perfil/documentos entre contas, pausa/coleta/dispatch, alvo de não entrega e migrations/rollback. Oito testes JPEG verificam orientação EXIF real.

```powershell
# Somente com uma instância de PostgreSQL local/de teste.
$env:TEST_DELIVERY_DATABASE='Host=127.0.0.1;Port=56437;Database=postgres;Username=delivery_test'
dotnet test APIBack.csproj --no-restore --logger 'trx;LogFileName=etapa34-final.trx' --results-directory obj/delivery-validation/results
```

Os testes de integração criam/removem seus próprios schemas. Os testes deste repositório fazem parte do projeto `APIBack.csproj`. A validação do APK com API/banco de teste ainda precisa de execução no Android. O bundle Web foi testado com API interceptada; isso não substitui app/API/admin integrados. Guia mobile: `../zippygo-motoboy/docs/prototipo-motoboy/ETAPA-3-4.md`.
