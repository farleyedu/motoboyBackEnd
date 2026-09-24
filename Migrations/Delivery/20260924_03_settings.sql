BEGIN;

-- ---------------------------------------------------------------------------
-- Tela de configuracoes do delivery.
--  1) Politica de transferencia ganha 'disabled': o motoboy nao pode passar pedido para
--     outro (so o atendente transfere).
--  2) Prazo padrao de entrega (minutos) dos pedidos criados sem previsao informada.
-- Idempotente: pode rodar manualmente e depois no boot sem efeito duplicado.
-- A API le default_delivery_minutes com tolerancia (sem a coluna usa 40), mas gravar a
-- politica 'disabled' exige esta migration (a constraint antiga so aceitava 2 valores).
-- ---------------------------------------------------------------------------

ALTER TABLE delivery_settings DROP CONSTRAINT IF EXISTS ck_delivery_settings_transfer_policy;
ALTER TABLE delivery_settings
    ADD CONSTRAINT ck_delivery_settings_transfer_policy
    CHECK (transfer_policy IN ('direct', 'establishment_approval', 'disabled'));

ALTER TABLE delivery_settings
    ADD COLUMN IF NOT EXISTS default_delivery_minutes INTEGER NULL;

ALTER TABLE delivery_settings DROP CONSTRAINT IF EXISTS ck_delivery_settings_default_delivery_minutes;
ALTER TABLE delivery_settings
    ADD CONSTRAINT ck_delivery_settings_default_delivery_minutes
    CHECK (default_delivery_minutes IS NULL OR default_delivery_minutes BETWEEN 1 AND 600);

INSERT INTO delivery_tracking_schema_versions (version)
VALUES ('20260924_03_settings')
ON CONFLICT (version) DO NOTHING;

COMMIT;
