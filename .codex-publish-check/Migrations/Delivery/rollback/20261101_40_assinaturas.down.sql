-- Desfaz 20261101_40_assinaturas. APAGA as assinaturas, cobrancas, compras e solicitacoes: so em ambiente de teste
-- ou antes da primeira loja contratar. As intencoes de pagamento (pagamento_intent) ficam.
BEGIN;
DROP VIEW IF EXISTS vw_assinatura_resumo;
DROP INDEX IF EXISTS ux_motoboy_pacotes_compra;
ALTER TABLE motoboy_pacotes DROP COLUMN IF EXISTS id_compra_extra;
DROP TABLE IF EXISTS comercial_aviso;
DROP TABLE IF EXISTS comercial_cobranca;
DROP TABLE IF EXISTS solicitacao_compra;
DROP TABLE IF EXISTS compra_extra;
DROP TABLE IF EXISTS assinatura_ciclo;
DROP TABLE IF EXISTS assinatura;
-- ck_motoboy_pacotes_quantidade fica: pacotes de compra avulsa podem ter quantidades fora de 3/7.
DELETE FROM delivery_tracking_schema_versions WHERE version = '20261101_40_assinaturas';
COMMIT;
