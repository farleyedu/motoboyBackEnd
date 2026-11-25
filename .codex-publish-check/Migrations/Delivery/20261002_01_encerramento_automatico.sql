BEGIN;

-- ---------------------------------------------------------------------------
-- Encerramento automatico de pedidos em aberto (status_pedido = 7, EncerradoAuto).
-- Tudo aditivo e idempotente. Sem esta migration a API segue como antes: nada e encerrado
-- sozinho e os comandos novos respondem 503 MIGRATION_PENDING.
-- ---------------------------------------------------------------------------

-- 1) Historico de encerramentos. Uma linha por encerramento; reabrir so preenche reaberto_em.
--    motoboy_id grava com quem o pedido estava (base para, no futuro, contar como "nao entregue"
--    na conta do motoboy). Sem FK de proposito: o historico sobrevive a limpeza de pedido/motoboy.
CREATE TABLE IF NOT EXISTS pedido_encerramento (
    id                   BIGSERIAL PRIMARY KEY,
    estabelecimento_id   UUID        NOT NULL,
    pedido_id            INTEGER     NOT NULL,
    motoboy_id           INTEGER     NULL,
    status_anterior      INTEGER     NOT NULL,
    motivo               TEXT        NOT NULL,
    encerrado_em         TIMESTAMPTZ NOT NULL DEFAULT NOW(),
    reaberto_em          TIMESTAMPTZ NULL,
    reaberto_por_user_id INTEGER     NULL
);

ALTER TABLE pedido_encerramento DROP CONSTRAINT IF EXISTS ck_pedido_encerramento_motivo;
ALTER TABLE pedido_encerramento
    ADD CONSTRAINT ck_pedido_encerramento_motivo CHECK (motivo IN ('expediente', 'manual'));

-- No maximo um encerramento "valendo" por pedido.
CREATE UNIQUE INDEX IF NOT EXISTS ux_pedido_encerramento_aberto
    ON pedido_encerramento (pedido_id) WHERE reaberto_em IS NULL;

CREATE INDEX IF NOT EXISTS ix_pedido_encerramento_motoboy
    ON pedido_encerramento (estabelecimento_id, motoboy_id, encerrado_em);

-- 2) Parametros por estabelecimento: ligado, e quantas horas depois do fechamento encerrar.
ALTER TABLE delivery_settings
    ADD COLUMN IF NOT EXISTS encerramento_auto_ativo BOOLEAN NOT NULL DEFAULT TRUE,
    ADD COLUMN IF NOT EXISTS encerramento_auto_horas INTEGER NOT NULL DEFAULT 4;

ALTER TABLE delivery_settings DROP CONSTRAINT IF EXISTS ck_delivery_settings_encerramento_horas;
ALTER TABLE delivery_settings
    ADD CONSTRAINT ck_delivery_settings_encerramento_horas CHECK (encerramento_auto_horas BETWEEN 0 AND 24);

INSERT INTO delivery_tracking_schema_versions (version)
VALUES ('20261002_01_encerramento_automatico')
ON CONFLICT (version) DO NOTHING;

COMMIT;
