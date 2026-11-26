-- Desfaz 20261208_05_social_fila_maquinas. Recusa se há máquina pareada (ela perderia o token), a não ser com
-- SET zippy.rollback_confirmado = 'sim' na sessão. Os renders ficam; só perdem o vínculo com a máquina.
BEGIN;

DO $$
BEGIN
    IF COALESCE(current_setting('zippy.rollback_confirmado', true), '') = 'sim' THEN RETURN; END IF;
    PERFORM set_config('zippy.sistema', 'sim', true);
    IF to_regclass('social_maquinas') IS NOT NULL AND EXISTS (SELECT 1 FROM social_maquinas WHERE removida_em IS NULL) THEN
        RAISE EXCEPTION 'Rollback bloqueado: há máquinas pareadas. Use SET zippy.rollback_confirmado = ''sim''.';
    END IF;
END $$;

ALTER TABLE IF EXISTS social_renders DROP CONSTRAINT IF EXISTS fk_social_renders_maquina;
DROP TABLE IF EXISTS social_maquina_uso;
DROP TABLE IF EXISTS social_trabalhos;
DROP TABLE IF EXISTS social_pareamentos;
DROP TABLE IF EXISTS social_maquinas;

DELETE FROM delivery_tracking_schema_versions WHERE version = '20261208_05_social_fila_maquinas';

COMMIT;
