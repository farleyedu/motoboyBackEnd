-- Executar somente em manutenção e após arquivar documentos/recibos. Não aplicado automaticamente.
-- Este retorno preserva dados; não destrói evidências de entregas ou fotos existentes.
BEGIN;
DO $$ BEGIN
 IF EXISTS(SELECT 1 FROM delivery_completions) OR EXISTS(SELECT 1 FROM delivery_completion_proofs) OR EXISTS(SELECT 1 FROM motoboy_documentos)
 THEN RAISE EXCEPTION 'Arquive os recibos/documentos antes de retornar a versão. Nenhum dado foi removido.'; END IF;
END $$;
DROP TABLE delivery_completions;
DROP TABLE delivery_completion_proofs;
DROP TABLE motoboy_documentos;
-- Colunas aditivas são mantidas: podem ter existido antes do ADD IF NOT EXISTS.
-- A versão anterior ignora essas colunas; removê-las apagaria dados potencialmente anteriores.
DELETE FROM delivery_tracking_schema_versions WHERE version IN ('20261008_01_motoboy_conta_documentos','20261008_02_delivery_completion');
COMMIT;
