BEGIN;

-- A mesma fila de solicitacoes atende os dois sentidos:
--   motoboy          = pedido feito pelo entregador;
--   estabelecimento  = convite feito pelo restaurante.
ALTER TABLE motoboy_link_requests
    ADD COLUMN IF NOT EXISTS origem TEXT NOT NULL DEFAULT 'motoboy';

ALTER TABLE motoboy_link_requests
    ADD COLUMN IF NOT EXISTS solicitado_por_usuario_id INTEGER NULL REFERENCES usuario (id);

UPDATE motoboy_link_requests
   SET solicitado_por_usuario_id = NULL
 WHERE origem = 'motoboy';

ALTER TABLE motoboy_link_requests DROP CONSTRAINT IF EXISTS ck_motoboy_link_request_origin;
ALTER TABLE motoboy_link_requests
    ADD CONSTRAINT ck_motoboy_link_request_origin
    CHECK (origem IN ('motoboy', 'estabelecimento'));

INSERT INTO delivery_tracking_schema_versions (version)
VALUES ('20261006_07_motoboy_convites')
ON CONFLICT (version) DO NOTHING;

COMMIT;
