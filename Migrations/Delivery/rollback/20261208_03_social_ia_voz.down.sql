-- Desfaz 20261208_03_social_ia_voz. Recusa se ja houver uso registrado, credito comprado ou prova de voz clonada,
-- a nao ser com SET zippy.rollback_confirmado = 'sim' na sessao.
-- Nao apaga nada nos provedores: vozes clonadas ativas precisam ser apagadas antes (DELETE /api/social/voz/clone).
-- As falas geradas continuam em social_arquivos (origem voz_ia) e no R2.
BEGIN;

DO $$
BEGIN
    IF COALESCE(current_setting('zippy.rollback_confirmado', true), '') = 'sim' THEN RETURN; END IF;
    PERFORM set_config('zippy.sistema', 'sim', true);
    IF to_regclass('social_uso_ia') IS NOT NULL AND EXISTS (SELECT 1 FROM social_uso_ia) THEN
        RAISE EXCEPTION 'Rollback bloqueado: ja existe uso de IA registrado. Use SET zippy.rollback_confirmado = ''sim''.';
    END IF;
    IF to_regclass('social_creditos') IS NOT NULL AND EXISTS (SELECT 1 FROM social_creditos WHERE comprados > 0) THEN
        RAISE EXCEPTION 'Rollback bloqueado: ha creditos comprados. Use SET zippy.rollback_confirmado = ''sim''.';
    END IF;
    IF to_regclass('social_voz_consentimentos') IS NOT NULL AND EXISTS (SELECT 1 FROM social_voz_consentimentos) THEN
        RAISE EXCEPTION 'Rollback bloqueado: ha provas de consentimento de voz clonada. Use SET zippy.rollback_confirmado = ''sim''.';
    END IF;
END $$;

DROP TABLE IF EXISTS social_voz_consentimentos;
DROP TABLE IF EXISTS social_voz_cache;
DROP TABLE IF EXISTS social_uso_ia;
DROP TABLE IF EXISTS social_creditos;
DROP TABLE IF EXISTS social_config_ia;
DROP TABLE IF EXISTS social_chaves;
DROP TABLE IF EXISTS social_provedores;

DELETE FROM delivery_tracking_schema_versions WHERE version = '20261208_03_social_ia_voz';

COMMIT;
