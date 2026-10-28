# Zippy Gestão — Contratos da Fundação

Financeiro, Caixa, Compras, Estoque, Equipe, Despesas e Curadoria escrevem e leem **um único livro de acontecimentos**.
Este documento diz como cada módulo publica e lê. Existe igual nos dois repositórios
(`zippy-admin/docs/zippy-gestao/` e `motoboyBackEnd/docs/zippy-gestao/`). O andamento e as pendências ficam só no Painel.

- Protótipo (visual e interação): https://claude.ai/artifact/FRj3kHNeGpZExwdVD6zgy7
- Padrão de qualidade: detalhe de pedido do Zippy e Gestão e Inteligência (https://claude.ai/artifact/83Nu7KWz14kdtLna67DrWY)

> Banco: o backend é **PostgreSQL + Dapper** (não SQL Server, como diz o bloco comum). As tabelas novas usam o prefixo `gestao_`.

---

## 1. Modelo

```
acontecimento (fato)  ──┬── lançamentos financeiros  (0..n)  ── liquidações (pagamento/recebimento depois)
   tipo, módulo,        └── movimentos de estoque     (0..n)
   data do fato, origem, responsável
correção (estorno/ajuste) = outro acontecimento com corrige_id → original
fechamento de mês         = acontecimento "fechamento_mes" + linha em gestao_meses_fechados
```

| Tabela | O que guarda |
| --- | --- |
| `gestao_contas` | Carteiras da loja: `banco`, `caixa`, `recebivel` (`ifood`, `maquininha`, `outro`), `cartao_empresa` (dia de fechamento e de vencimento da fatura). Nome e `ativo` podem mudar; conta nunca é apagada. |
| `gestao_acontecimentos` | Tipo, módulo, **data do fato** (`data_fato`), **data de registro** (`registrado_em`), mês de referência, responsável, origem (`origem_tipo` + `origem_id`), documento, descrição, `dados` (JSON livre do módulo), correção (`corrige_id`, `correcao`, `motivo`, `valor_anterior`, `valor_novo`), chave de idempotência e hash do conteúdo. |
| `gestao_lancamentos` | Conta, natureza, sentido, valor, status no registro, mês de referência, vencimento, data de pagamento, parcela x de y, mês da fatura, categoria (global), finalidade (da loja), `corrige_lancamento_id`. |
| `gestao_liquidacoes` | Pagamento ou recebimento posterior de um lançamento (conta de onde saiu ou para onde entrou, valor e data). Estorno de pagamento = linha com sinal oposto. |
| `gestao_movimentos_estoque` | Item (id + nome), quantidade com sinal em unidade base (`g`, `ml`, `un`), tipo, motivo, quem confirmou e quando, `corrige_movimento_id`. |
| `gestao_meses_fechados` | Mês fechado, por quem e em qual acontecimento. |
| `gestao_lancamentos_situacao` (view) | Lançamento + liquidações, com o **status atual** e a data de pagamento atual. Leia sempre por ela. |

Migração: `motoboyBackEnd/Migrations/Delivery/20261105_01_gestao_fundacao.sql`. É aplicada no boot e é idempotente.
As tabelas `financeiro_*` antigas (20260927, nunca usadas) ficam intactas.

### Vocabulário

| Campo | Valores |
| --- | --- |
| `natureza` | `receita`, `despesa`, `transferencia`, `aporte`, `retirada` |
| `sentido` | `entrada`, `saida` (fixo por natureza; só a transferência escolhe) |
| `status` | `previsto`, `a_pagar`, `pago`, `a_receber`, `recebido` |
| tipo de movimento | `entrada`, `saida`, `perda`, `divergencia` (publicáveis); `ajuste` e `estorno` só nascem de correção |
| `modulo` | `financeiro`, `caixa`, `compras`, `estoque`, `equipe`, `despesas`, `pedidos`, `curadoria`, `sistema` |
| tipos reservados | `pagamento`, `fechamento_mes`, `estorno`, `ajuste` (só o livro cria) |
| tipos com regra | `perda` (só movimentos de perda, sem lançamento) e `inventario` (só divergências, sem lançamento) |

Os demais tipos são livres (`[a-z_]`, até 40). Sugestões: `venda`, `compra`, `conferencia_item`, `turno`, `despesa`,
`conta_a_pagar`, `recebimento_avulso`, `transferencia`, `aporte_socio`, `retirada_socio`, `pro_labore`,
`abertura_caixa`, `fechamento_caixa`, `sangria`, `suprimento`, `repasse_ifood`.

---

## 2. Regras garantidas pela Fundação

| # | Regra | Onde é garantida |
| --- | --- | --- |
| 1 | Linha do tempo única; cada acontecimento aponta a origem; nunca contar duas vezes | Índice único `(loja, tipo, origem)` (fora `pagamento`) + chave de idempotência |
| 2 | Nada se apaga; correção é um novo acontecimento com responsável, data, valor anterior, valor novo e motivo; mês fechado só aceita ajuste | Gatilhos recusam UPDATE/DELETE/TRUNCATE (`P0001`); gatilho recusa escrita comum em mês fechado (`P0002`); `AcontecimentoRegras.ValidarMesesFechados` |
| 3 | Venda ≠ receita ≠ recebido ≠ a receber; data do fato ≠ data do pagamento (mês de referência); parcelada não sai inteira do caixa; cartão vai para a fatura; transferência não é receita nem despesa; aporte e retirada ficam fora da operação | `StatusDe`, `Parcelar`, `Fatura`, checagens de transferência e `Natureza.ForaDaOperacao` |
| 4 | Estoque nunca muda sem confirmação humana; vínculo ≠ entrada; a compra existe no financeiro sem a conferência | Movimento exige `confirmacaoHumana: true` **e** responsável; `confirmado_por` é NOT NULL |
| 5 | Sem duplicidade de documento, compra, lançamento ou entrada de estoque | Índices únicos de chave, origem e documento normalizado; lock por chave; um estorno por acontecimento; liquidação recusa lançamento já pago |
| 8 | Divergência fica como divergência; perda não gera saída de caixa | `inventario` só aceita `divergencia`; `perda` não aceita lançamento |
| 11 | Gerente vê tudo; operador de caixa vê só o Caixa do dia e o Estoque e conta às cegas | `GestaoPapeis` (backend) e `gestaoAcesso.ts` (Painel) |

As regras 6, 7, 9, 10 e 12 (catálogo, categoria × finalidade, imagens, de-paras, ficha técnica) são dos outros módulos.
O livro só guarda `categoria` e `finalidade` como campos separados.

---

## 3. Como publicar (backend → livro)

Módulo do backend injeta `APIBack.Acontecimentos.IAcontecimentosService`:

```csharp
var r = await livro.Publicar(lojaId, usuarioId /* null = sistema */, new PublicarAcontecimentoRequest
{
    ChaveIdempotencia = $"compras:compra:{compra.Id}",      // estável: a mesma ação gera a mesma chave
    Tipo = "compra", Modulo = ModuloGestao.Compras,
    DataFato = compra.DataDoCupom, Descricao = "Atacadão — cupom 1234",
    Origem = new OrigemRef { Tipo = "compra", Id = compra.Id.ToString() },
    DocumentoChave = compra.ChaveNfce,                       // normalizado: sem espaços, pontos e traços
    Lancamentos = new()
    {
        new LancamentoRequest { Natureza = Natureza.Despesa, Valor = 300m, Parcelas = 3, ContaId = cartaoId,
            Categoria = "insumos", Finalidade = "producao" },
    },
}, ct);
// r.Repetido == true → já existia (replay). r.Acontecimento traz linhas e ids.
```

Dentro da transação do próprio módulo (ex.: conclusão do pedido), use a sobrecarga
`Publicar(NpgsqlConnection, NpgsqlTransaction, ...)`. Ela grava junto e não dispara evento: o commit é seu.

### Regras do pedido de publicação

- **Chave obrigatória**, até 160 caracteres. Mesma chave + mesmo conteúdo → devolve o já gravado (`Repetido`). Mesma
  chave + conteúdo diferente → `IDEMPOTENCIA_CONFLITO` (409). Formato sugerido: `modulo:origem:id[:acao]`.
- **Origem** (`tipo` + `id`): a mesma origem não gera dois acontecimentos do mesmo tipo (`DUPLICADO`). Para mudar algo
  já registrado, faça ajuste ou estorno, nunca uma nova publicação.
- **Documento**: o mesmo documento não entra duas vezes na loja (`DUPLICADO`).
- **Data do fato** não pode estar no futuro (tolerância de 10 min). O mês é calculado no **fuso da loja**
  (`estabelecimentos.timezone_iana`; sem fuso, `America/Sao_Paulo`).
- **Lançamento**:
  - `valor` > 0, com até 2 casas. O sinal negativo é só das correções.
  - Sem `status`: com `dataPagamento` vira `pago`/`recebido`; sem, vira `a_pagar`/`a_receber`. `previsto` é explícito.
  - `pago`/`recebido` exige `contaId` e não pode ter data futura.
  - `mesReferencia` (opcional) separa a competência da data do fato. Ex.: conta de luz de outubro registrada em novembro.
  - `parcelas` > 1 só para receita ou despesa ainda não paga. Os centavos são exatos (a sobra fica na 1ª parcela), um
    vencimento por mês e todas as parcelas no mesmo mês de referência.
  - **Cartão da empresa**: só despesa, sempre `a_pagar`. O livro calcula a fatura (compra no dia do fechamento ou
    depois vai para a fatura seguinte; o mês da fatura é o mês do vencimento) e o vencimento. Cada parcela cai numa fatura.
  - **Transferência**: só linhas de transferência, pelo menos uma saída e uma entrada, mesmo total, contas diferentes,
    tudo na hora.
- **Movimento de estoque**: `confirmacaoHumana: true` e responsável (usuário) obrigatórios; unidade base; o sinal segue
  o tipo; `perda`/`divergencia` exigem motivo.
- **Mês fechado**: qualquer linha num mês fechado → `MES_FECHADO` (409). Registre com a data de hoje ou peça um ajuste.

### Correções, pagamentos e fechamento

| Operação | Serviço | Efeito |
| --- | --- | --- |
| Estorno | `Estornar(loja, usuario, acontecimentoId, { chave, motivo, confirmacaoHumana })` | Linhas com o sinal oposto ao efeito **líquido** (original + ajustes), ligadas às originais e no mesmo mês de referência delas. O que já foi pago volta na data do estorno. Um estorno por acontecimento; correção não se estorna. |
| Ajuste de valor | `Ajustar(loja, usuario, { chave, motivo, lancamentoId, valorNovo })` | Grava a diferença (novo − atual), mais `valor_anterior`, `valor_novo` e `motivo`. Sempre sobre o lançamento original. |
| Ajuste de estoque | `Ajustar(loja, usuario, { chave, motivo, movimentoId, quantidadeNova, confirmacaoHumana })` | Diferença como movimento `ajuste`. |
| Pagamento/recebimento | `Liquidar(loja, usuario, { chave, lancamentoId, contaId, dataPagamento })` | Quita o valor líquido (original + correções pendentes) saindo/entrando no banco ou no caixa. Uma vez só. Lançamento do cartão não se paga sozinho. |
| Fatura do cartão | `PagarFatura(loja, usuario, cartaoId, faturaMes, { chave, contaId })` | Quita tudo o que está a pagar naquela fatura. O dinheiro sai do banco aqui, não na compra. |
| Fechar mês | `FecharMes(loja, usuario, mes, { chave })` | Só mês que já terminou. Definitivo: depois dele, só correção. |

Toda correção exige responsável humano e motivo (mínimo de 3 caracteres). Um mês fechado aceita correção.

### Evento

Depois do commit (nunca no replay), o livro chama todo `IAcontecimentoAssinante` registrado no DI com
`AcontecimentoRegistrado(lojaId, acontecimento)`. Use para Gestão e Inteligência, tempo real etc. Se quem escuta falhar,
o acontecimento continua gravado: o erro só vai para o log.

### Exemplos por módulo

| Módulo | Acontecimento | Lançamentos | Movimentos |
| --- | --- | --- | --- |
| Pedidos/Caixa | `venda` origem `pedido:{id}` | receita `a_receber` no recebível iFood, ou `recebido` no caixa se foi em dinheiro | — |
| iFood | `repasse_ifood` | liquidação das receitas a receber no banco (`Liquidar`) | — |
| Compras | `compra` origem `compra:{id}` + documento | despesa (parcelada, no cartão ou a pagar) | — |
| Compras/Estoque | `conferencia_item` origem `compra_item:{compra}:{linha}` | — | `entrada`, confirmada por quem conferiu |
| Estoque | `perda` | **nenhum** | `perda` com motivo |
| Estoque | `inventario` | **nenhum** | `divergencia` por item |
| Caixa | `sangria` / `suprimento` | transferência caixa ↔ banco | — |
| Caixa | `fechamento_caixa` (dados: contagem às cegas) | diferença como receita ou despesa (decisão do módulo Caixa) | — |
| Equipe | `turno` origem `turno:{id}` | despesa `a_pagar` (remuneração) | — |
| Despesas | `conta_a_pagar` + documento do boleto | despesa `a_pagar` com vencimento e mês de referência | — |
| Financeiro | `aporte_socio` / `retirada_socio` | `aporte` / `retirada` (fora da operação) | — |

---

## 4. Rotas para o Painel (`/api/gestao`)

Resposta no envelope `{ success, data }`; erro em `{ success: false, error, code }`, com o código da tabela da seção 6.

| Método e rota | Quem | Observação |
| --- | --- | --- |
| `GET acesso` | todos | Papel, áreas, `historico`, `saldoEsperadoNoFechamento`, `podeLancar`, `podeCorrigir`, `podeFecharMes`, `podeConferirEstoque` |
| `GET acontecimentos?mes=AAAA-MM-01&dia=&modulo=&tipo=&origemTipo=&origemId=&limite=` | gerente; operador só caixa/estoque de hoje | Linha do tempo com lançamentos, movimentos, liquidações e `corrigidoPor` |
| `GET acontecimentos/{id}` | idem | Detalhe |
| `POST acontecimentos` | gerente com `Financeiro:criar`; operador: módulo `caixa` (sem movimentos) ou `estoque` (sem lançamentos), só hoje | Publicação manual |
| `POST acontecimentos/{id}/estorno` | gerente com `Financeiro:editar` ou `cancelar` | |
| `POST ajustes` | idem | |
| `POST liquidacoes` | gerente com `Financeiro:criar` | |
| `POST contas/{cartaoId}/faturas/{AAAA-MM-01}/pagamento` | idem | |
| `GET lancamentos?mes=&dia=&contaId=&status=&modulo=` | gerente; operador só hoje | Lê a view de situação. Com `dia`, traz o que foi pago, vence ou aconteceu no dia |
| `GET movimentos-estoque?mes=&dia=&itemId=` | gerente; operador só hoje | |
| `GET contas` | gerente: todas com saldo; operador: só o Caixa, saldo 0 (às cegas) | |
| `POST contas` | gerente com `Financeiro:configurar` | Idempotente pela chave |
| `GET meses-fechados` / `POST meses/{AAAA-MM-01}/fechar` | gerente / `Financeiro:fechar_mes` | |
| `GET papeis/{papel}/permissoes` | gerente | Permissões sugeridas para `operador_caixa` e `gerente` |

---

## 5. Permissões e papéis

- Catálogo (`EstablishmentPermissions.Catalog`):
  - `Financeiro` ganhou `fechar_mes`;
  - novos `Caixa` (`visualizar`, `lancar`, `fechar`) e `Estoque` (`visualizar`, `contar`, `confirmar`);
  - Caixa e Estoque existem quando o módulo comercial **Financeiro** está ativo.
- Papel `operador_caixa` (alias `caixa`) é permitido em restaurante. O papel na gestão sai das permissões:
  - `Financeiro:visualizar` → **gerente** (também dono, gerente da empresa e super admin);
  - senão, `Caixa:visualizar` → **operador de caixa**.
- Operador: áreas Caixa e Estoque (se tiver `Estoque:visualizar`), sem histórico (só hoje), sem saldo esperado, sem
  correção e sem fechamento de mês.
- Curadoria: só super admin (Zippy).
- Painel:
  - `src/features/zippy-gestao/acesso/gestaoAcesso.ts` espelha essas regras;
  - `useGestaoAcesso()` devolve `{ acesso, periodo, estado }` (para o operador, o período é sempre hoje);
  - rotas em `src/utils/permissions.ts`: `/apps/financeiro`, `/apps/compras`, `/apps/catalogo`, `/apps/equipe` e `/apps/despesas` pedem `Financeiro:visualizar`; `/apps/caixa` e `/apps/estoque` aceitam também `Caixa:visualizar` / `Estoque:visualizar`; `/apps/curadoria` é só para super admin.

---

## 6. Códigos de erro

| Código | HTTP | Quando |
| --- | --- | --- |
| `CAMPO_INVALIDO` | 422 | Regra de formato ou de negócio (a mensagem diz qual) |
| `IDEMPOTENCIA_CONFLITO` | 409 | Chave já usada com outro conteúdo |
| `DUPLICADO` | 409 | Mesma origem, mesmo documento, lançamento já pago, mês já fechado |
| `MES_FECHADO` | 409 | Escrita comum em mês fechado |
| `JA_ESTORNADO` | 409 | Estornar de novo, ajustar ou pagar algo estornado |
| `CONFIRMACAO_HUMANA` | 422 | Movimento de estoque sem confirmação ou sem pessoa |
| `SEM_RESPONSAVEL` / `SEM_PERMISSAO` | 403 | Correção sem usuário / papel sem acesso |
| `NAO_ENCONTRADO` | 404 | Id de outra loja ou inexistente |
| `IMUTAVEL` | 409 | Tentativa de UPDATE/DELETE (não deve acontecer pelo serviço) |

---

## 7. Período global (Painel)

`src/features/zippy-gestao/periodo/`

- `Periodo = { mes: 'AAAA-MM', dia: 'AAAA-MM-DD' | null }`, compartilhado por todas as telas e persistido em
  `localStorage` (`zippy:gestao:periodo`) com sincronização entre abas. Sem armazenamento, fica em memória.
- `usePeriodo()` devolve `{ periodo, atual, hoje, definirMes, definirDia, mesAnterior, proximoMes, voltarParaHoje }`. O
  período sempre se normaliza: sem mês futuro, dia dentro do mês e não futuro; o histórico não tem limite para trás.
- `filtrarPorPeriodo(lista, item => [fato, pagamento, vencimento], periodo)` e `useListaNoPeriodo(...)` servem para
  qualquer lista: o item entra se **qualquer** data dele cair no período ("o que entrou, saiu e vence no dia").
- `paramsDoPeriodo(periodo)` → `{ mes: 'AAAA-MM-01', dia? }` para as rotas `/api/gestao`.
- Datas com hora são lidas no fuso da loja (`America/Sao_Paulo`).

## 8. Componentes compartilhados (Painel)

`src/ui/zippy/gestao/` (puros, sem dados) e `src/features/zippy-gestao/` (ligados ao período e ao papel):

| Componente | Uso |
| --- | --- |
| `Papel`, `PapelLinha`, `PapelTracejado`, `PapelTotal` | Documento em papel claro e serrilhado (`serra`: ambos, topo, base; `tipo`: cupom, recibo, fita), com `carimbo` opcional. Sempre claro nos dois temas. |
| `LinhaDoTempo` | Eventos com hora, título, detalhe, cor de origem, `mudanca` (anterior riscado → novo) e `novo` (anima a entrada). |
| `FaixaDeDias` | Dias do mês, hoje marcado, futuro desabilitado, até 3 marcas coloridas por dia; clicar de novo volta para o mês. |
| `SeletorDeMes`, `FaixaHistorico` | Setas + grade de meses por ano, sem futuro; `bloqueado` para quem não tem histórico. A faixa avisa quando a tela mostra um mês passado, aberto ou fechado. |
| `PilulaEstado` | `estado` do livro → texto e tom (Recebido verde, Pago contorno, A receber azul, A pagar amarelo, Previsto roxo, Estornado vermelho, Divergência amarelo…). |
| `ModalDeIntencao`, `AvisoComLink` | Criação: intenção → só os campos do caso → recibo ao vivo → consequências → confirmar → aviso com link para onde o registro foi parar. No celular, campos e recibo viram duas etapas. |
| `DocaModulos` / `DocaGestao` | Doca com ícones coloridos e ponto em "onde estou"; a versão ligada mostra só as áreas do papel e o atalho para a Gestão e Inteligência. |
| `SeletorDoPeriodo`, `DiasDoPeriodo` | Seletor de mês e faixa de dias já ligados ao período global e ao papel. |

Escala: composição e cores do protótipo, nos tamanhos compactos do painel (`docs/redesign-visual/00-base-compacta.md`).
Por isso a doca tem ícones de 34 px, e não os 46 px do protótipo, e o texto não passa de 20 px.
