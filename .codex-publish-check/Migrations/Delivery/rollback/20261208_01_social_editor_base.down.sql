-- Desfaz 20261208_01_social_editor_base. Recusa se ja houver receita salva ou arquivo enviado que nao seja de exemplo,
-- a nao ser com SET zippy.rollback_confirmado = 'sim' na sessao. Os objetos no R2 (prefixo social/{loja}/) NAO sao apagados aqui.
BEGIN;

DO $$
BEGIN
    IF COALESCE(current_setting('zippy.rollback_confirmado', true), '') = 'sim' THEN RETURN; END IF;
    PERFORM set_config('zippy.sistema', 'sim', true);
    IF to_regclass('social_receitas') IS NOT NULL AND EXISTS (SELECT 1 FROM social_receitas) THEN
        RAISE EXCEPTION 'Rollback bloqueado: ja existem receitas do editor. Use SET zippy.rollback_confirmado = ''sim''.';
    END IF;
    IF to_regclass('social_arquivos') IS NOT NULL AND EXISTS (SELECT 1 FROM social_arquivos) THEN
        RAISE EXCEPTION 'Rollback bloqueado: ja existem arquivos do Social. Use SET zippy.rollback_confirmado = ''sim''.';
    END IF;
END $$;

DROP TABLE IF EXISTS social_arquivos;
DROP TABLE IF EXISTS social_receitas_historico;
DROP TABLE IF EXISTS social_receitas;
DROP TABLE IF EXISTS social_cortes;
DROP TABLE IF EXISTS social_videos;

DELETE FROM delivery_tracking_schema_versions WHERE version = '20261208_01_social_editor_base';

COMMIT;
