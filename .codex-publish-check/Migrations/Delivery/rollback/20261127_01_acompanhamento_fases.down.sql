-- Desfaz 20261127_01_acompanhamento_fases. Apaga o historico de fases e as versoes de proposta: rode so de proposito.
BEGIN;
DROP TABLE IF EXISTS acompanhamento_proposta_versao;
DROP TABLE IF EXISTS acompanhamento_historico;
DROP TABLE IF EXISTS acompanhamento_passagem;
DROP TABLE IF EXISTS acompanhamento;
COMMIT;
