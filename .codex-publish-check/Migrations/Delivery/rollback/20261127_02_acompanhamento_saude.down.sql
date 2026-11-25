-- Desfaz 20261127_02_acompanhamento_saude. Apaga os limites ajustados e o historico deles: rode so de proposito.
BEGIN;
DROP TABLE IF EXISTS acompanhamento_teste_aviso;
ALTER TABLE IF EXISTS acompanhamento DROP COLUMN IF EXISTS faixa_motivos, DROP COLUMN IF EXISTS faixa_calculo, DROP COLUMN IF EXISTS faixa_limites;
DROP TABLE IF EXISTS acompanhamento_saude_regra_versao;
DROP TABLE IF EXISTS acompanhamento_saude_regra;
COMMIT;
