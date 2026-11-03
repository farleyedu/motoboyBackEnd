# Rollback da Parte 1 (delivery tracking V2)

Estes scripts NAO sao aplicados automaticamente pelo `DeliveryMigrationHostedService`
(ele varre apenas `Migrations/Delivery/*.sql`, sem recursao nesta subpasta).
Sao ferramentas de emergencia, para execucao manual e supervisionada.

## Antes de rodar

A estrategia de rollback recomendada pelo plano (secao "Riscos e rollback" da Parte 1)
e desativar `DeliveryTracking:Enabled` e manter os dados novos sem apaga-los. Prefira
essa opcao. Use os scripts abaixo somente se for necessario remover fisicamente o
schema novo (ex.: erro de migracao em ambiente ainda sem trafego real).

Estes scripts assumem que, no banco de producao, as colunas `motoboy.id_usuario` e
`motoboy.id_estabelecimento` **ja existiam antes da Parte 1** (o `ADD COLUMN IF NOT
EXISTS` em `20260722_02_schema.sql` e defensivo/idempotente, nao uma criacao nova) —
por isso elas NAO sao removidas por nenhum script abaixo. Confirme isso no seu
ambiente (`\d motoboy` antes de aplicar a Parte 1) antes de confiar nesta suposicao.
Se no seu ambiente essas colunas forem realmente novas, ajuste o script
`20260722_02_schema.down.sql` para remove-las tambem.

## Ordem de execucao

Rodar em ordem inversa a aplicacao, cada um dentro de sua propria transacao:

1. `20260722_04_constraints.down.sql`
2. `20260722_03_backfill.down.sql` (gera apenas um relatorio; nao apaga motoboys)
3. `20260722_02_schema.down.sql`

Depois de rodar os tres, remova manualmente as linhas correspondentes de
`delivery_tracking_schema_versions` se quiser permitir reaplicar a migracao:

```sql
DELETE FROM delivery_tracking_schema_versions
 WHERE version IN ('20260722_04_constraints', '20260722_03_backfill', '20260722_02_schema');
```

## O que NAO e revertido automaticamente

- Perfis de `motoboy` criados pelo backfill para usuarios com papel "motoboy" que
  ainda nao tinham perfil. Apagar identidades de motoboy e destrutivo (podem já ter
  pedidos/sessoes reais associados apos o cutover) e por isso o
  `20260722_03_backfill.down.sql` apenas lista os candidatos, nao apaga.
- Dados coletados enquanto `DeliveryTracking:Enabled=true` (sessoes, amostras de
  localizacao, eventos). Os scripts de schema fazem `DROP TABLE`, entao esses dados
  sao perdidos ao rodar o passo 3. Se precisar preservar, faca backup/export antes.

## Fase 2 (nucleo de pedido) - migracoes 20260925_*

Ordem de execucao dos rollbacks (inversa da aplicacao), cada um na propria transacao:

1. `20260925_05_constraints.down.sql`
2. `20260925_04_modulo_backfill.down.sql` (retira o modulo PEDIDOS de todos os estabelecimentos)
3. `20260925_03_backfill.down.sql`
4. `20260925_02_schema.down.sql` (**destrutivo**: apaga `pedido_item` e as colunas novas de `pedido`)

`20260925_01_modulo_pedidos.sql` nao tem rollback: o Postgres nao remove valor de enum, e o valor
`PEDIDOS` e inofensivo sem uso. Prefira, antes de qualquer rollback, deixar a API ignorar o
recurso novo (o codigo tolera a ausencia das colunas novas apenas ate a proxima versao da API).

## Seed de demonstracao (Uberlandia) - `20260927_90_seed_uberlandia`

Dados ficticios (cardapio, clientes, conversas, mensagens, motoboys e pedidos) num estabelecimento
que ja existe. So semeia estabelecimento ativo, com Delivery e **sem dado real**. Controle pelo ledger:

- nunca semear (producao): `INSERT INTO delivery_tracking_schema_versions (version) VALUES ('seed_demo_desligado');`
- escolher o alvo antes da subida: `... VALUES ('seed_demo_alvo:<uuid do estabelecimento>');`
- ver o resultado: `SELECT * FROM delivery_tracking_schema_versions WHERE version LIKE 'seed_demo_%';`
  (`seed_demo_alvo:<uuid>` = aplicado; `seed_demo_sem_alvo` = nenhum estabelecimento elegivel;
  `seed_demo_erro: ...` = falhou, nada ficou pela metade e sera tentado na proxima subida)
- semear de novo: apague `20260927_90_seed_uberlandia` e as linhas `seed_demo_%` do ledger.
- desfazer: `20260927_90_seed_uberlandia.down.sql` (remove so o que o seed criou).

## CRM inteligente (24 a 27/11) - ordem e limites

Total real em `Migrations/Delivery`: 199 arquivos `.sql`. Das 13 de 24 a 27/11, 7 ja tinham rollback (`24_01`, `25_01`, `26_01`, `27_01`, `27_02`, `27_04`, `27_08`). Foram adicionados `27_03`, `27_05`, `27_06`, `27_07`, `27_15` e `27_16`. Os de `caixa` (25 e 26) ficam fora desta revisao.

Ordem inversa da aplicacao, cada um na propria transacao: `16` -> `15` -> `07` -> `06` -> `05` -> `03`. O `15` so roda depois do `16` (FK de `crm_objecao`); o `06` so depois do `07` quando ha eventos de Gestao pendentes.

Travas: `03`, `15` e `16` recusam rodar com dados. Para apagar de proposito, exporte as tabelas e rode `SET zippy.rollback_confirmado = 'sim';` na mesma sessao antes do script. `06` e `07` recusam com numero de teste extra, finalidade `gestao` ou mensagem pendente/bloqueada.

Parciais por seguranca (nao apagam dado permanente):
- **`27_05` identidade**: remove so o indice. Vinculo e auditoria nunca sao apagados. Para desligar o uso: `UPDATE usuario_whatsapp_vinculo SET estado='revogado', codigo_hash=NULL, codigo_expira_em=NULL, revogado_em=NOW(), motivo='rollback manual', versao=versao+1 WHERE estado IN ('pendente','verificado','suspenso');` (so com aprovacao, e registre na auditoria).
- **`27_07` ingresso**: remove so os indices. As colunas de rota/destino em `wa_evento` ficam (nulaveis, ignoradas pelo worker antigo) porque provam o roteamento das mensagens ja processadas. Remocao fisica das colunas: manual, depois de exportar `wa_evento`.
- **`27_06` canais de teste**: remove `teste`/`finalidade_teste` e as constraints; o numero principal, tokens e destinos ficam intactos.

Validacao: os scripts foram escritos e revisados sem banco. O ciclo `up -> down -> up` ainda precisa rodar em PostgreSQL descartavel (nunca em producao) com `TEST_DELIVERY_DATABASE`.
