-- Desfaz 20261031_01_google_uso_mensal.sql. Apaga o historico do contador mensal; o backend volta ao contador em memoria.
BEGIN;
DROP TABLE IF EXISTS google_uso_mensal;
COMMIT;
