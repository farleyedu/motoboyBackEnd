-- Módulo avulso por loja: a Gestão liga ou desliga um módulo só para aquela loja e o ajuste continua valendo quando
-- o plano muda, quando um extra vence ou quando o cadastro é salvo. O plano e a cobrança não mudam.
-- O gatilho aplica os ajustes em toda gravação de modulos_ativos. Aditiva e idempotente.
-- Rollback em rollback/20261213_01_modulos_da_gestao.down.sql.
BEGIN;

ALTER TABLE estabelecimentos
    ADD COLUMN IF NOT EXISTS modulos_ligados_gestao    text[] NOT NULL DEFAULT '{}',
    ADD COLUMN IF NOT EXISTS modulos_desligados_gestao text[] NOT NULL DEFAULT '{}';

CREATE OR REPLACE FUNCTION fn_modulos_da_gestao() RETURNS trigger AS $$
BEGIN
    IF cardinality(NEW.modulos_ligados_gestao) > 0 OR cardinality(NEW.modulos_desligados_gestao) > 0 THEN
        NEW.modulos_ativos := ARRAY(
            SELECT DISTINCT t.m
              FROM unnest(COALESCE(NEW.modulos_ativos, '{}'::modulo_enum[]) || NEW.modulos_ligados_gestao::modulo_enum[]) AS t(m)
             WHERE NOT (t.m::text = ANY (NEW.modulos_desligados_gestao)));
    END IF;
    RETURN NEW;
END
$$ LANGUAGE plpgsql;

DROP TRIGGER IF EXISTS trg_modulos_da_gestao ON estabelecimentos;
CREATE TRIGGER trg_modulos_da_gestao
    BEFORE INSERT OR UPDATE OF modulos_ativos, modulos_ligados_gestao, modulos_desligados_gestao ON estabelecimentos
    FOR EACH ROW EXECUTE FUNCTION fn_modulos_da_gestao();

-- Extra que passa a valer (pago pela loja ou dado pela Gestão) liga o módulo dele, mesmo que estivesse desligado.
CREATE OR REPLACE FUNCTION fn_modulos_da_extra() RETURNS trigger AS $$
BEGIN
    IF NEW.status = 'ativa' AND OLD.status IS DISTINCT FROM 'ativa' THEN
        UPDATE estabelecimentos
           SET modulos_desligados_gestao = ARRAY(
                   SELECT d FROM unnest(modulos_desligados_gestao) AS t(d) WHERE NOT (d = ANY (NEW.modulos_ativos::text[])))
         WHERE id = NEW.id_estabelecimento AND modulos_desligados_gestao && NEW.modulos_ativos::text[];
    END IF;
    RETURN NEW;
END
$$ LANGUAGE plpgsql;

DROP TRIGGER IF EXISTS trg_modulos_da_extra ON compra_extra;
CREATE TRIGGER trg_modulos_da_extra
    AFTER UPDATE OF status ON compra_extra
    FOR EACH ROW EXECUTE FUNCTION fn_modulos_da_extra();

INSERT INTO delivery_tracking_schema_versions (version)
VALUES ('20261213_01_modulos_da_gestao')
ON CONFLICT (version) DO NOTHING;

COMMIT;
