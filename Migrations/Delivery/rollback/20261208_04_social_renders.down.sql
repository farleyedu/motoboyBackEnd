-- Desfaz 20261208_04_social_renders. Recusa se algum render já subiu para o R2 (vai ser publicado), a não ser com
-- SET zippy.rollback_confirmado = 'sim' na sessão. Os objetos no R2 (social/{loja}/renders/) NÃO são apagados aqui.
BEGIN;

DO $$
BEGIN
    IF COALESCE(current_setting('zippy.rollback_confirmado', true), '') = 'sim' THEN RETURN; END IF;
    PERFORM set_config('zippy.sistema', 'sim', true);
    IF to_regclass('social_renders') IS NOT NULL AND EXISTS (SELECT 1 FROM social_renders WHERE r2_key IS NOT NULL) THEN
        RAISE EXCEPTION 'Rollback bloqueado: há vídeos gerados que já subiram para o R2. Use SET zippy.rollback_confirmado = ''sim''.';
    END IF;
END $$;

DROP TABLE IF EXISTS social_renders;

DELETE FROM delivery_tracking_schema_versions WHERE version = '20261208_04_social_renders';

COMMIT;
