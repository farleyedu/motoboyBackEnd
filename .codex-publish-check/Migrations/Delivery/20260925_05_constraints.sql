BEGIN;

-- ---------------------------------------------------------------------------
-- Integridade do nucleo de pedido:
--  * origem restrita aos valores conhecidos (PedidoOrigem.cs);
--  * idempotencia: no maximo um pedido por (estabelecimento, origem, origem_ref);
--  * consulta dos itens de um pedido.
-- ---------------------------------------------------------------------------

ALTER TABLE pedido DROP CONSTRAINT IF EXISTS ck_pedido_origem;
ALTER TABLE pedido
    ADD CONSTRAINT ck_pedido_origem
    CHECK (origem IN ('atendente', 'cardapio_web', 'ifood', 'ia_whatsapp', 'simulador'));

CREATE UNIQUE INDEX IF NOT EXISTS ux_pedido_origem_ref
    ON pedido (id_estabelecimento, origem, origem_ref)
    WHERE origem_ref IS NOT NULL;

CREATE INDEX IF NOT EXISTS ix_pedido_item_pedido
    ON pedido_item (pedido_id, ordem);

INSERT INTO delivery_tracking_schema_versions (version)
VALUES ('20260925_05_constraints')
ON CONFLICT (version) DO NOTHING;

COMMIT;
