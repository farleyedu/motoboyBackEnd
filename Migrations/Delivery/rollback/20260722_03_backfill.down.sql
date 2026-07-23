-- O backfill mistura dados legados com dados novos e nao pode ser revertido
-- de forma cega sem risco de apagar identidades de motoboy ja em uso real
-- (pedidos, sessoes, historico). Este script NAO apaga nada: ele apenas
-- lista o que o backfill provavelmente criou/alterou, para decisao manual.
--
-- Rode fora de transacao mutavel (somente leitura). Guarde a saida antes de
-- decidir o que fazer manualmente.

-- Motoboys que existem apenas como alias (nao sao o canonico do proprio usuario).
SELECT id, id_usuario, canonical_motoboy_id, is_simulated
  FROM motoboy
 WHERE canonical_motoboy_id IS NOT NULL
   AND canonical_motoboy_id <> id;

-- Sessoes que o backfill encerrou por normalizacao/duplicidade/falta de vinculo.
SELECT session_id, motoboy_id, id_estabelecimento, end_reason, ended_at_utc
  FROM motoboy_active_sessions
 WHERE end_reason IN ('migration_expired', 'migration_no_active_link', 'migration_duplicate');

-- Vinculos motoboy_estabelecimento inseridos pelo backfill (heuristica: criados
-- no mesmo instante da migracao; ajuste a janela conforme o log de deploy real).
SELECT motoboy_id, estabelecimento_id, created_at_utc
  FROM motoboy_estabelecimento
 ORDER BY created_at_utc;

-- Se, apos revisao manual, decidir remover o schema inteiro, use
-- 20260722_02_schema.down.sql (que via DROP TABLE tambem remove as linhas
-- de motoboy_estabelecimento e motoboy_active_sessions.* adicionadas aqui).
-- A remocao de linhas especificas de "motoboy" criadas pelo backfill deve ser
-- feita manualmente, uma a uma, apos confirmar que nao ha pedido/sessao real
-- dependendo delas.
