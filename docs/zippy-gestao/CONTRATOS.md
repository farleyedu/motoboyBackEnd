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

---

## 9. Lógica — compras, vínculos, estoque, equipe, caixa e despesas (extensão)

Seção acrescentada pela Lógica (E1 a E6). Não muda nada das seções 1 a 8. Mesmo envelope `{ success, data }` e códigos da seção 6
(mais `NAO_ENCONTRADO`, 404). Rotas de compras e vínculos: só **gerente** com `Financeiro:criar`. Curadoria: só **super admin**.

### 9.1 Documentos (E1)

| Método e rota | Observação |
| --- | --- |
| `POST compras/documentos/xml` | multipart, campo `arquivo` (NF-e/NFC-e, até 10 MB) |
| `POST compras/documentos/foto` | multipart, campo `arquivo` (JPG/PNG/WEBP/GIF, até 10 MB). Lê com Claude; sem chave → `LEITURA_INDISPONIVEL` (503), falha → `LEITURA_FALHOU` (502) |
| `GET compras/documentos/{id}` | `DocumentoDto` |

`DocumentoDto`: `id, origem (xml|foto|manual), status ("lido"), fornecedor {cnpj, nome}, dataEmissao, numero, serie, chaveAcesso,
total, itens[] {linha, descricao, quantidade, unidade, valorUnitario, desconto, valorTotal, codigo, ean, ilegivel}, avisos[],
duplicidades[] {bloqueia, motivo, documentoId}, compraId, criadoPor, criadoEm`.
Duplicado (mesma chave de acesso, mesmo arquivo ou mesmo CNPJ+série+número) → `DUPLICADO` (409). Mesmo fornecedor+dia+total entra,
com aviso em `duplicidades[]` (`bloqueia: false`). O documento não mexe em financeiro nem estoque.

### 9.2 Vínculos de compras (E2)

Vínculo = texto da linha do cupom (por fornecedor e **embalagem**: `unidadeCupom`) → insumo da loja, com `fator` (unidades de estoque
por unidade do cupom). Vale na hora para a loja. Nunca move estoque (a entrada é na conferência da compra).

| Método e rota | Observação |
| --- | --- |
| `GET compras/documentos/{id}/sugestoes` | `SugestaoItem[]`, uma por linha |
| `POST compras/documentos/{id}/itens/{linha}/vinculo` | Confirma. Corpo: `{ insumoId? , globalId? , novo?: {nome, unidade, categoria?}, fator, qualquerFornecedor? }` (uma das três escolhas). Devolve `{ vinculo, conversao, divergencia, insumoCriado }` |
| `GET vinculos?busca=&todos=` | Vínculos da loja (busca no texto do cupom e no nome do insumo); `todos=true` inclui desfeitos |
| `POST vinculos/{id}/desfazer` | `{ motivo }` obrigatório. Não apaga: fica `desfeito`. De novo → `JA_ESTORNADO` |

`SugestaoItem`: `linha, descricao, nomeChave, sugestoes[], conversao?, pergunta?, divergencia?, vinculoId?`.
- `sugestoes[]`: `{ origem, insumoId?, globalId?, nome, unidadeBase?, fator?, confianca (0–1), motivo }`, em ordem de confiança;
  a última é sempre `criar_novo`. Origens: `loja` (0,98 mesmo fornecedor; 0,95 mesmo EAN; 0,90 outro fornecedor; −0,03 se a
  embalagem é outra, e aí sem `fator`), `global` (validado pela curadoria: 0,92 por EAN, 0,88 por texto), `comunidade` (outras
  lojas, sem curadoria: 0,6 + 0,04 por loja, até 0,8), `similar` (nome parecido, até 0,75), `criar_novo`.
- `conversao`: `{ quantidade, unidade (g|ml|un), fator, origem (vinculo|unidade|descricao), explicacao }`. Sem pista → `null` e
  `pergunta` diz o que perguntar ("Quantos un vêm em 1 PCT…?").
- `divergencia`: preço por unidade de estoque ≥ 2,5× acima ou abaixo da mediana dos últimos 12 preços do insumo (só de vínculos
  confirmados) → `{ precoBase, mediana, razao, fatorSugerido, mensagem }`. Só avisa; não bloqueia.
- Confirmar: `insumoId` (insumo da loja), `globalId` (importa o item global: vira insumo da loja, ou reaproveita o que ela já trouxe)
  ou `novo` (cria o insumo e um candidato para a curadoria). Mesma escolha de novo não duplica; outra escolha desfaz o vínculo
  anterior (`Substituído por um novo vínculo.`). Sem a tabela de insumos (Gestão e Inteligência inativa) → `CAMPO_INVALIDO`.

### 9.3 Curadoria do catálogo global (E2, super admin)

| Método e rota | Observação |
| --- | --- |
| `GET curadoria/candidatos` | `CatalogoGlobalDto[]` com `status = candidato`, mais lojas primeiro |
| `POST curadoria/{id}/validar` | `{ nome?, categoria?, ean? }`. Passa a valer para todas as lojas. Nome/EAN já validado → `DUPLICADO` |
| `POST curadoria/{id}/rejeitar` | `{ motivo }` obrigatório |
| `POST curadoria/{id}/mesclar` | `{ destinoId }` (validado, mesma unidade). Os vínculos das lojas passam a apontar para o destino |

`CatalogoGlobalDto`: `id, nome, unidade, categoria, ean, status (candidato|validado|rejeitado|mesclado), mescladoEm, lojas,
nomesNoCupom[], parecidos[] {id, nome, unidade, semelhanca}, criadoEm, motivoCuradoria`. Curar de novo → `DUPLICADO` (409).
Rejeitar ou mesclar nunca desfaz o vínculo de uma loja.

### 9.4 De-para de vendas

Já existe e é reaproveitado (não há outro): `ifood_produto_vinculo`, item iFood → produto do cardápio. Rotas:
`GET /api/v2/cardapio/ifood/pendentes?busca=` (só exceções + busca), `GET /api/v2/cardapio/ifood/resumo`,
`POST /api/v2/cardapio/produtos/{produtoId}/ifood/vinculos`, `DELETE /api/v2/cardapio/ifood/vinculos/{vinculoId}`,
`POST /api/v2/cardapio/ifood/pendentes/{vinculoId}/criar-produto`. Produto vendido → ficha técnica é da Gestão e Inteligência.

### 9.5 Compras, conferência e estoque (E3)

Compra = acontecimento `compra` (módulo compras), origem `compra:{documentoId}`, com o documento do cupom. O id da compra nas
rotas é o **id do documento**. Rotas de compras: só gerente com `Financeiro:criar`.

| Método e rota | Observação |
| --- | --- |
| `POST compras` | `{ documentoId, forma: a_pagar\|pago\|cartao, contaId?, parcelas?, vencimento?, dataPagamento?, categoria? ("insumos"), finalidade? }`. `pago` exige `contaId` (banco/caixa); `cartao` exige o cartão da empresa (o livro faz a fatura). Mesmo pedido de novo devolve a mesma compra |
| `GET compras/{documentoId}` | `CompraDto`; 404 se o documento ainda não virou compra |
| `POST compras/{documentoId}/itens/{linha}/conferencia` | `{ quantidadeRecebida? (unidade do cupom; padrão a do cupom), consumoDireto, confirmacaoHumana }`. Publica `conferencia_item` origem `compra_item:{documentoId}:{linha}` com uma `entrada` = recebida × fator do vínculo. `consumoDireto` registra sem movimento. Linha sem vínculo → `CAMPO_INVALIDO`; já conferida → `DUPLICADO`; compra estornada → `JA_ESTORNADO` |

`CompraDto`: `documentoId, acontecimentoId, fornecedor, dataFato, total, estornada, conferidas, pendentes, linhas[], acontecimento`
(o `AcontecimentoDto` da compra, com lançamentos/parcelas). `linhas[]`: `{ linha, descricao, quantidade, unidade, valorTotal,
status (sem_vinculo|pendente|conferido|consumo_direto), vinculoId, insumoId, insumoNome, fator, unidadeBase, quantidadeEsperada,
quantidadeEntrada (líquida, já com ajustes), conferenciaId, movimentoId }`.
Correção de quantidade conferida = `POST ajustes` com `movimentoId` e `quantidadeNova` (seção 3). A compra existe no financeiro
mesmo sem conferência (regra 4); a conferência registra o preço da linha no histórico do insumo.

Estoque: saldo = soma dos movimentos do livro por insumo. `saldos` e as prévias são só do **gerente** (o operador conta às
cegas). Perda, inventário e baixa aceitam o **operador** com `Estoque:contar`, só hoje (`GestaoPapeis.ValidarPublicacao`).

| Método e rota | Observação |
| --- | --- |
| `GET estoque/saldos` | `[{ insumoId, nome, unidade, saldo }]` |
| `POST estoque/perdas` | `{ chaveIdempotencia, insumoId, quantidade (> 0, unidade do insumo), motivo, confirmacaoHumana }` → acontecimento `perda`, sem lançamento |
| `POST estoque/inventario/previa` | `{ contagens: [{ insumoId, contado }] }` → `[{ insumoId, nome, unidade, esperado, contado, diferenca }]` |
| `POST estoque/inventario` | `{ chaveIdempotencia, contagens, motivo?, confirmacaoHumana }` → `inventario` com `divergencia` só onde a diferença ≠ 0 (a contagem inteira vai em `dados`). Nunca vira perda |
| `POST estoque/baixa-vendas/previa` | `{ pedidos: [{ pedidoId, modalidade: salao\|retirada\|delivery }] }` → `{ consumo[], pedidos[], semFicha[{ pedidoId, itens[] }], jaBaixados[] }` |
| `POST estoque/baixa-vendas` | `{ pedidos, confirmacaoHumana }` → `baixa_vendas` com uma `saida` por insumo. Pedido já baixado → `DUPLICADO`; nenhum baixável → `CAMPO_INVALIDO` |

Baixa por venda: lê a ficha por `IFichaTecnicaLeitor` (componente com `modalidades` = embalagem só daquela modalidade). Pedido com
item sem ficha ou avulso fica de fora inteiro (`semFicha`). A mesma seleção gera a mesma chave (não baixa duas vezes). Hoje a
implementação é `FichaTecnicaPendente` (nenhuma ficha): todo pedido sai como `semFicha` até a Gestão e Inteligência ligar a dela.

### 9.6 Equipe e remuneração (E4)

Espelha os tipos do Painel (`src/features/zippy-gestao/equipe/tipos.ts`): a Integração só troca o adaptador simulado. Rotas em
`/api/gestao/equipe`, só **gerente** (leitura `Financeiro:visualizar`, escrita `Financeiro:criar`, desconto `Financeiro:editar`).
Toda escrita de dinheiro pede `chaveIdempotencia`.

| Método e rota | Observação |
| --- | --- |
| `GET pessoas?todas=` | `PessoaDto[]` (ativas; `todas=true` inclui inativas). Traz `criadoPorNome` (Integração 6A) |
| `POST pessoas` / `PUT pessoas/{id}` | `{ nome, funcao, vinculo, regra, pagaCom (pix\|dinheiro), chavePix?, usuarioId?, cor?, escalaPadrao: [{dow, entrada, saida}], ativo, desde?, versao? }`. A regra é limpa pelo vínculo (campo de outro vínculo é descartado). `versao` velha → `IDEMPOTENCIA_CONFLITO`. Cada versão fica em `gestao_pessoas_historico` |
| `GET semana?inicio=AAAA-MM-DD` | `{ inicio, pessoas, grade: { pessoaId: [7 × TurnoDto\|null] }, totais: { previsto, apurado, turnos, realizados, faltas } }`. Turno da escala ainda não tocado vem com `id: null`. Turno gravado vem com o histórico, e cada linha traz `porNome` (Integração 6A) |
| `POST turnos` | `{ pessoaId, data, status?, entrada?, saida?, entregas?, justificada?, adicionais?: [{rotulo, valor}], baseComissao?, pagamentoAvulso?: {forma: turno\|diaria\|hora\|entrega, valor}, avulso?, motivo? }` → `TurnoDto` com histórico |
| `GET resumo?mes=AAAA-MM-01` | Por pessoa: `previsto, apurado, devido, pago, aPagar, adiantamentosAbertos, faltas, itens[], adiantamentos[]` |
| `POST adiantamentos` | `{ chaveIdempotencia, pessoaId, valor, contaId, data?, motivo? }` → acontecimento `adiantamento` (despesa paga) |
| `POST descontos` | `{ chaveIdempotencia, pessoaId, lancamentoId, valor, motivo }` → ajuste do item a pagar |
| `POST pagamentos` | `{ chaveIdempotencia (até 100), pessoaId, contaId, dataPagamento?, valor? }` → `{ pago, compensado, restante, liquidados[], saldoLancamentoId?, resumo }` |

- **Regra** (`RegraPagamento`, se soma = remuneração mista): `salarioMensal` (1/30 por turno), `diaria`, `porTurno`, `porHora`,
  `porEntrega`, `adicionalNoturno` (turno com ≥ 1 h entre 22:00 e 05:00), `comissaoPercentual` (sobre `baseComissao` do turno).
  Vínculos: fixo (salário, noturno, comissão), diarista, freelancer (turno, comissão), hora, motoboy (diária, entrega), prestador.
- **Turno**: um por pessoa por dia (`chave = {pessoaId}:{AAAA-MM-DD}`). Cancelado = 0; falta sem justificativa = 0 (zera a
  diária); falta justificada paga a base; entregas, comissão e adicionais só no realizado. Realizado ou falta só depois do
  horário de entrada. Previsto acompanha a regra atual; confirmado guarda o valor do dia.
- **Livro**: realizado (ou falta justificada) publica `turno` origem `turno:{chave}` (`~{revisao}` depois de estorno), despesa
  `a_pagar`, categoria `equipe`, finalidade `remuneracao`, `dados.pessoaId`. Mudança de valor = ajuste; deixar de gerar despesa =
  estorno. Turno já no livro exige `motivo` (≥ 3 letras); turno já pago não muda (`CAMPO_INVALIDO`: use desconto/adiantamento).
- **Pagamento**: primeiro os adiantamentos em aberto abatem dos itens a pagar (ajuste "Compensação de adiantamento"); depois
  liquida do mais antigo ao mais novo. Sem `valor`, paga tudo. Com `valor` menor, o item que não cabe é ajustado para a parte paga
  e o resto vira `saldo_remuneracao` (a pagar). A mesma chave de novo não paga duas vezes. O pagamento **não é atômico**: se
  falhar no meio, o que já foi liquidado fica; repita com a mesma chave.

### 9.7 Caixa do dia (E5)

Espelha a interface `CaixaFonte` do Painel (`src/features/zippy-gestao/caixa/caixaAdaptador.ts`): a Integração só troca o
adaptador simulado. Rotas em `/api/gestao/caixa`. Gerente e operador de caixa; o operador só vê e registra **hoje**.
Escritas só no dia de hoje (dia passado se corrige pelo ajuste do fechamento).

| Método e rota | `CaixaFonte` | Observação |
| --- | --- | --- |
| `GET dias/{AAAA-MM-DD}` | `carregarDia` | `CaixaDoDia { dia, aberto, abertoEm, por, fundo, fita[], pedidosEmAberto[] (vazio por ora), fechamento? }`; `null` para dia futuro |
| `POST abrir` | `abrir` | `{ dia, fundo }`. Põe a gaveta no fundo de troco: transferência cofre ↔ gaveta pela diferença. Abrir de novo devolve o mesmo dia |
| `POST movimentos` | `registrarMovimento` | `{ chaveIdempotencia, dia, tipo: sang\|sup\|pag\|ret, valor, motivo, referencia? (id do pedido), contaId?, pessoaId?, lancamentoId? }`. No `pag`: `pessoaId` paga a remuneração da pessoa pela gaveta (Equipe, até o que ela tem a receber); `lancamentoId` quita uma compra a pagar (valor inteiro). Os dois quitam o que já existe, sem criar outra despesa (Integração 6A) |
| `GET a-pagar` | `aPagar` | Gerente e operador. `{ equipe: [{ pessoaId, nome, aPagar }], compras: [{ lancamentoId, acontecimentoId, descricao, valor, vencimento }] }`: o que dá para pagar em dinheiro pela gaveta (compras fora do cartão; adiantamento já abatido) (Integração 6A) |
| `POST contagem` | `conferirContagem` | `{ dia, contagem: { notas: {200: n, ...}, moedas } }` → `{ esperado, contado, diferenca }`. Grava a contagem |
| `POST fechar` | `fechar` | `{ dia, contagem, maquininha: {deb, cre, pix}, justificativa?, fica }` |
| `GET fechamentos?mes=` | `fechamentos` | Só gerente. `FechamentoDto[]` do mais novo ao mais antigo |
| `POST fechamentos/{dia}/ajustes` | `ajustarFechamento` | Só gerente com correção. `{ contadoNovo, motivo }` |

- **Contas**: gaveta ("Gaveta do caixa") e cofre ("Cofre") são contas `caixa` do livro (sem subtipo: o livro só aceita subtipo em
  recebível), criadas na primeira abertura; `gestao_caixas` diz qual é qual. **Esperado = saldo da gaveta no livro.**
- **Fita** (lida do livro): `abre` (fundo), `venda` (todas as formas; a forma vem de `dados.forma` da venda: din, pix, deb, cre,
  ifood), e o que mexeu na gaveta: `sup`, `sang`, `pag`, `canc`. Valor com sinal (saída da gaveta negativa).
- **Movimentos**: sangria = transferência gaveta → cofre (ou `contaId`); suprimento = cofre (ou `contaId`) → gaveta; pagamento =
  despesa paga na gaveta (`despesas_caixa`, tipo `pagamento_dinheiro`); devolução = **estorno da venda** quando `referencia` é um
  pedido com venda em dinheiro de mesmo valor, senão despesa `devolucao_de_venda`. Saída maior que a gaveta → `SEM_SALDO`.
- **Fechamento**: a contagem é gravada (às cegas: o esperado só vem depois de contar); diferença ≠ 0 exige justificativa; falta =
  despesa `quebra_de_caixa`, sobra = receita `sobra_de_caixa` (na gaveta); o que não `fica` de troco é recolhido ao cofre
  (acontecimento `recolhimento`). A maquininha é conferida contra as vendas do dia por forma (`maquininhaEsperado`, não bloqueia).
- **Correção**: novo acontecimento `ajuste_fechamento_caixa` com a diferença (mais sobra ou mais quebra) e linha em
  `gestao_caixa_ajustes`; o fechamento original não muda.
- **Nomes** (Integração 6A): `CaixaDoDia.porNome`, `FechamentoDto.operadorNome` e `ajustes[].porNome`, além dos ids. O id do
  fechamento é o dia (`AAAA-MM-DD`), o mesmo da rota de ajuste.
- **Fita** (Integração 6A): a liquidação feita com dinheiro da gaveta (diária, compra) também entra (`pag`; estorno dela = `canc`).
- Códigos novos (extensão da seção 6): `SEM_SALDO` (422), `SEM_CAIXA` (409, caixa não aberto), `FECHADO` (409, dia já fechado).
- **Para a Integração**: a venda deve ser publicada com `dados.forma` e, em dinheiro, como receita `recebido` na conta da gaveta
  (`gestao_caixas.conta_gaveta_id`); Pix/cartão no recebível da maquininha; iFood no recebível iFood.

### 9.8 Contas fixas, lembretes e finalidades (E6)

Espelha `ContaFixa` do Painel (`src/features/zippy-gestao/despesas/despesasRegras.ts`) e o acontecimento que ele já lê
(`despesa_fixa` com `dados.contaFixaId`). Rotas em `/api/gestao/despesas`, só **gerente** (escrita com `Financeiro:criar`).

| Método e rota | Observação |
| --- | --- |
| `GET contas-fixas?todas=` | `ContaFixaDto[] { id, nome, finalidade, valorPrevisto, diaVencimento, valorVaria, arquivada, contaId, versao }` |
| `POST contas-fixas` / `PUT contas-fixas/{id}` | `{ nome, finalidade?, valorPrevisto, diaVencimento (1–31), valorVaria, contaId?, versao? }`. Mudar o valor vale do próximo lançamento em diante |
| `POST contas-fixas/{id}/arquivar` | Sai da lista e dos meses seguintes; o que foi lançado continua |
| `GET contas-fixas/ocorrencias?mes=` | `[{ contaFixaId, nome, mes, vencimento, valor, estado: lancada\|prevista\|aguardando_valor, acontecimentoId?, status? }]` (a partir do mês em que a conta foi cadastrada) |
| `POST contas-fixas/gerar?mes=` | Publica as de valor fixo que faltam no mês (até o mês que vem). `{ mes, geradas[], aguardandoValor[] }`. Repetir não duplica |
| `POST contas-fixas/{id}/lancar` | `{ chaveIdempotencia?, mes, valor, vencimento? }` — o valor do mês (conta variável ou fixa ainda não gerada). Já lançada → `DUPLICADO` |
| `GET lembretes?dias=3` | `[{ contaFixaId, nome, mes, vencimento, valor, estado, vencida, diasParaVencer, lancamentoId? }]`: não pago, vencendo em até N dias (0–60) ou vencido; olha o mês passado, o atual e o próximo |
| `GET finalidades` / `POST finalidades` | Finalidades da loja (regra 7). `{ nome }`; a mesma escrita sem acento/maiúscula devolve a existente |

- **Livro**: `despesa_fixa`, módulo `despesas`, origem `recorrencia:{contaFixaId}:{AAAA-MM}` (depois de estorno, `~n`), despesa
  `a_pagar` com `mesReferencia` do mês, `dataVencimento` no dia da conta (dia 31 em mês curto = último dia), categoria
  `Despesas fixas`, finalidade da conta, `dados: { contaFixaId, recorrencia: true, mes }`.
- **Lançada** = qualquer `despesa_fixa` não estornada com `dados.contaFixaId` e competência no mês (vale também o que a tela lançou
  pelo `POST acontecimentos`). Mudar o valor de um lançamento já feito é `POST ajustes`.
- **Geração automática** (Integração 6A): `ContasFixasWorker` (a cada 6 h) chama `gerar` do mês em toda loja com conta fixa de valor
  fixo; o responsável é quem cadastrou a conta mais antiga da loja. O Painel também chama `gerar` ao abrir Despesas no mês atual
  (gerente). Conta fixa nova criada junto com o primeiro lançamento: a conta nasce antes e o lançamento já leva `dados.contaFixaId`.
