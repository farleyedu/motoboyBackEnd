BEGIN;

-- Versao da lista de pedidos por loja: o painel consulta GET /api/v2/delivery/pedidos/versao a cada poucos
-- segundos e so busca a lista quando o numero muda. O contador sobe por CONSTRAINT TRIGGER DEFERRED (no COMMIT),
-- por tres razoes: so aparece depois que os dados da transacao ja sao visiveis; a linha do contador so e travada no
-- ultimo instante da transacao (sem ciclo de espera com os locks de pedido/fila); e transacao revertida nao muda a versao.
CREATE TABLE IF NOT EXISTS pedido_lista_versao (
    estabelecimento_id UUID PRIMARY KEY,
    versao BIGINT NOT NULL DEFAULT 0,
    atualizado_em_utc TIMESTAMPTZ NOT NULL DEFAULT NOW()
);

CREATE OR REPLACE FUNCTION pedido_lista_versao_bump_pedido() RETURNS trigger LANGUAGE plpgsql AS $fn$
DECLARE v_store UUID;
BEGIN
    IF TG_OP = 'DELETE' THEN v_store := OLD.id_estabelecimento; ELSE v_store := NEW.id_estabelecimento; END IF;
    IF v_store IS NOT NULL THEN
        INSERT INTO pedido_lista_versao(estabelecimento_id, versao) VALUES (v_store, 1)
        ON CONFLICT (estabelecimento_id) DO UPDATE SET versao = pedido_lista_versao.versao + 1, atualizado_em_utc = NOW();
    END IF;
    RETURN NULL;
END
$fn$;

DROP TRIGGER IF EXISTS trg_pedido_lista_versao ON pedido;
CREATE CONSTRAINT TRIGGER trg_pedido_lista_versao
    AFTER INSERT OR UPDATE OR DELETE ON pedido
    DEFERRABLE INITIALLY DEFERRED
    FOR EACH ROW EXECUTE FUNCTION pedido_lista_versao_bump_pedido();

-- A lista do iFood mostra estado vindo do snapshot (preparo, revisao, cancelamento), que muda sem tocar em "pedido".
CREATE OR REPLACE FUNCTION pedido_lista_versao_bump_ifood() RETURNS trigger LANGUAGE plpgsql AS $fn$
DECLARE v_store UUID;
BEGIN
    IF TG_OP = 'DELETE' THEN v_store := OLD.estabelecimento_id; ELSE v_store := NEW.estabelecimento_id; END IF;
    IF v_store IS NOT NULL THEN
        INSERT INTO pedido_lista_versao(estabelecimento_id, versao) VALUES (v_store, 1)
        ON CONFLICT (estabelecimento_id) DO UPDATE SET versao = pedido_lista_versao.versao + 1, atualizado_em_utc = NOW();
    END IF;
    RETURN NULL;
END
$fn$;

DO $do$
BEGIN
    IF to_regclass('ifood_order_snapshots') IS NOT NULL THEN
        EXECUTE 'DROP TRIGGER IF EXISTS trg_pedido_lista_versao_ifood ON ifood_order_snapshots';
        EXECUTE 'CREATE CONSTRAINT TRIGGER trg_pedido_lista_versao_ifood AFTER INSERT OR UPDATE OR DELETE ON ifood_order_snapshots '
             || 'DEFERRABLE INITIALLY DEFERRED FOR EACH ROW EXECUTE FUNCTION pedido_lista_versao_bump_ifood()';
    END IF;
END
$do$;

INSERT INTO delivery_tracking_schema_versions(version) VALUES ('20261023_03_pedido_lista_versao') ON CONFLICT DO NOTHING;
COMMIT;
