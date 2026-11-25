-- Backfill dos modulos novos: as lojas que ja existem NAO perdem nenhuma tela (decisao do dono).
-- Antes, estas telas apareciam sem modulo proprio:
--   iFood, Impressao, Remuneracao e Simulador  -> dependiam de DELIVERY
--   Chats/Atendimento                          -> sem modulo (agora WHATSAPP)
--   Zippy Experience e Financeiro              -> sem modulo
-- Tambem retira dos estabelecimentos os modulos removidos do produto (LEADS, ESTOQUE, VITRINE, AGENDAMENTOS, RELATORIOS),
-- so se o valor existir no enum. Rode DEPOIS de 20261020_03_modulos_novos.sql. Idempotente.

DO $$
DECLARE
    novo TEXT;
    removido TEXT;
BEGIN
    -- Quem tem DELIVERY ganha os modulos que antes vinham junto com ele.
    FOREACH novo IN ARRAY ARRAY['IFOOD', 'IMPRESSAO', 'REMUNERACAO', 'SIMULADOR'] LOOP
        EXECUTE format(
            'UPDATE estabelecimentos SET modulos_ativos = array_append(modulos_ativos, %L::modulo_enum)
              WHERE %L::modulo_enum <> ALL (COALESCE(modulos_ativos, ''{}''))
                AND ''DELIVERY''::modulo_enum = ANY (COALESCE(modulos_ativos, ''{}''))', novo, novo);
    END LOOP;

    -- Telas que todo mundo via: todos ganham o modulo correspondente.
    FOREACH novo IN ARRAY ARRAY['WHATSAPP', 'EXPERIENCE', 'FINANCEIRO'] LOOP
        EXECUTE format(
            'UPDATE estabelecimentos SET modulos_ativos = array_append(COALESCE(modulos_ativos, ''{}''), %L::modulo_enum)
              WHERE %L::modulo_enum <> ALL (COALESCE(modulos_ativos, ''{}''))', novo, novo);
    END LOOP;

    -- Modulos sem tela, removidos do produto.
    FOREACH removido IN ARRAY ARRAY['LEADS', 'ESTOQUE', 'VITRINE', 'AGENDAMENTOS', 'RELATORIOS'] LOOP
        IF EXISTS (SELECT 1 FROM pg_enum e JOIN pg_type t ON t.oid = e.enumtypid
                    WHERE t.typname = 'modulo_enum' AND e.enumlabel = removido) THEN
            EXECUTE format(
                'UPDATE estabelecimentos SET modulos_ativos = array_remove(modulos_ativos, %L::modulo_enum)
                  WHERE %L::modulo_enum = ANY (COALESCE(modulos_ativos, ''{}''))', removido, removido);
        END IF;
    END LOOP;
END $$;

INSERT INTO delivery_tracking_schema_versions (version)
VALUES ('20261020_04_modulos_backfill')
ON CONFLICT (version) DO NOTHING;
