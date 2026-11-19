-- Liga o Zippy Social na estabelecimento01 e na loja administrativa ZippyGo Centro.
-- A loja administrativa tambem e reconhecida pelo conjunto EMPRESAS + ESTABELECIMENTO + USUARIOS.
-- Execute somente depois de 20261207_03_modulo_social.sql. Idempotente.
DO $$
DECLARE
    v_atualizadas integer;
BEGIN
    UPDATE estabelecimentos
       SET modulos_ativos = array_append(
               COALESCE(modulos_ativos, '{}'::modulo_enum[]),
               'SOCIAL'::modulo_enum
           )
     WHERE (
               regexp_replace(lower(nome_fantasia), '[^a-z0-9]+', '', 'g') IN ('estabelecimento01', 'zippygocentro')
               OR COALESCE(modulos_ativos, '{}'::modulo_enum[]) @> ARRAY[
                   'EMPRESAS'::modulo_enum,
                   'ESTABELECIMENTO'::modulo_enum,
                   'USUARIOS'::modulo_enum
               ]
           )
       AND NOT ('SOCIAL'::modulo_enum = ANY (COALESCE(modulos_ativos, '{}'::modulo_enum[])));

    GET DIAGNOSTICS v_atualizadas = ROW_COUNT;
    RAISE NOTICE 'Zippy Social: % loja(s) atualizada(s).', v_atualizadas;
END $$;

INSERT INTO delivery_tracking_schema_versions (version)
VALUES ('20261207_04_social_lojas')
ON CONFLICT (version) DO NOTHING;
