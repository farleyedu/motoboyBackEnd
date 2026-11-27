-- Desfaz a 20261213_01_modulos_da_gestao. Os módulos que a loja tem hoje ficam como estão; os ajustes deixam de ser reaplicados.
-- Com ajustes gravados, para: apague-os à mão se for isso mesmo. Publique antes uma versão do servidor sem as rotas de módulo avulso.
BEGIN;
DO $$
BEGIN
    IF EXISTS (SELECT 1 FROM estabelecimentos
                WHERE cardinality(modulos_ligados_gestao) > 0 OR cardinality(modulos_desligados_gestao) > 0) THEN
        RAISE EXCEPTION 'ha ajustes de modulo gravados pela Gestao; o rollback nao apaga';
    END IF;
END $$;
DROP TRIGGER IF EXISTS trg_modulos_da_extra ON compra_extra;
DROP FUNCTION IF EXISTS fn_modulos_da_extra();
DROP TRIGGER IF EXISTS trg_modulos_da_gestao ON estabelecimentos;
DROP FUNCTION IF EXISTS fn_modulos_da_gestao();
ALTER TABLE estabelecimentos
    DROP COLUMN IF EXISTS modulos_ligados_gestao,
    DROP COLUMN IF EXISTS modulos_desligados_gestao;
DELETE FROM delivery_tracking_schema_versions WHERE version = '20261213_01_modulos_da_gestao';
COMMIT;
