BEGIN;

-- ---------------------------------------------------------------------------
-- Expande os avisos ao cliente (Fase 6) para cobrir o ciclo todo do pedido:
--   pedido_enviado_loja, pedido_confirmado_loja (so fazem sentido pra pedido que chega pronto do
--     cliente, hoje so cardapio web), chegou (disparo manual do motoboy/atendente) e, fora do pipeline
--     de rastreio, confirmacao_atendente e pronto_retirada (tambem manuais/por evento).
-- Tudo aditivo e idempotente, mesmo padrao de 20260928_01_avisos_rastreio.sql.
-- ---------------------------------------------------------------------------

ALTER TABLE delivery_settings
    ADD COLUMN IF NOT EXISTS notify_received_enabled BOOLEAN NOT NULL DEFAULT TRUE,
    ADD COLUMN IF NOT EXISTS notify_template_received TEXT NULL,
    ADD COLUMN IF NOT EXISTS notify_accepted_enabled BOOLEAN NOT NULL DEFAULT TRUE,
    ADD COLUMN IF NOT EXISTS notify_template_accepted TEXT NULL,
    ADD COLUMN IF NOT EXISTS notify_arrived_enabled BOOLEAN NOT NULL DEFAULT TRUE,
    ADD COLUMN IF NOT EXISTS notify_template_arrived TEXT NULL,
    ADD COLUMN IF NOT EXISTS notify_confirmacao_atendente_enabled BOOLEAN NOT NULL DEFAULT TRUE,
    ADD COLUMN IF NOT EXISTS notify_template_confirmacao_atendente TEXT NULL,
    ADD COLUMN IF NOT EXISTS notify_pronto_retirada_enabled BOOLEAN NOT NULL DEFAULT TRUE,
    ADD COLUMN IF NOT EXISTS notify_template_pronto_retirada TEXT NULL;

-- pedido_notificacao.tipo ganha os 3 tipos novos que entram no mesmo historico/resend de hoje
-- (pedido_enviado_loja e pedido_confirmado_loja disparam no evento, nao no poll; chegou e manual).
ALTER TABLE pedido_notificacao DROP CONSTRAINT IF EXISTS pedido_notificacao_tipo_check;
ALTER TABLE pedido_notificacao
    ADD CONSTRAINT pedido_notificacao_tipo_check
    CHECK (tipo IN ('saiu_da_loja', 'motoboy_chegando', 'pedido_enviado_loja', 'pedido_confirmado_loja', 'chegou',
                     'confirmacao_atendente'));

INSERT INTO delivery_tracking_schema_versions (version)
VALUES ('20261005_03_avisos_pedido')
ON CONFLICT (version) DO NOTHING;

COMMIT;
