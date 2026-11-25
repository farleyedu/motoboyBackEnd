-- Desfaz 20261122_01_gestao_caixa_chegadas.sql. Só some o destaque de chegada no Caixa; rota e acertos não dependem dela.
BEGIN;
DROP TABLE IF EXISTS gestao_caixa_chegadas;
COMMIT;
