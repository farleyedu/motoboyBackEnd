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
