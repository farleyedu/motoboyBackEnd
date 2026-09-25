BEGIN;

-- ---------------------------------------------------------------------------
-- Fase 6 (avisos de rastreio): opt-in por pedido, historico de avisos (um por tipo e pedido, garantido
-- pelo banco), parametros por estabelecimento, autorizacao do motoboy para compartilhar a localizacao e
-- token opaco do link publico de rastreio. Tudo aditivo e idempotente.
-- ---------------------------------------------------------------------------

-- 1) Opt-in do cliente, por pedido e de qualquer origem.
ALTER TABLE pedido ADD COLUMN IF NOT EXISTS rastreio_opt_in BOOLEAN NOT NULL DEFAULT FALSE;
ALTER TABLE pedido ADD COLUMN IF NOT EXISTS rastreio_opt_in_em TIMESTAMPTZ NULL;
ALTER TABLE pedido ADD COLUMN IF NOT EXISTS rastreio_opt_in_origem TEXT NULL;

-- 2) Historico dos avisos. UNIQUE (pedido, tipo): no maximo um disparo de cada tipo por pedido.
CREATE TABLE IF NOT EXISTS pedido_notificacao (
    id BIGSERIAL PRIMARY KEY,
    pedido_id INTEGER NOT NULL REFERENCES pedido (id) ON DELETE CASCADE,
    tipo TEXT NOT NULL CHECK (tipo IN ('saiu_da_loja', 'motoboy_chegando')),
    status TEXT NOT NULL CHECK (status IN ('pendente', 'enviada', 'falhou', 'ignorada')),
    motivo TEXT NULL,
    criada_em TIMESTAMPTZ NOT NULL DEFAULT NOW(),
    enviada_em TIMESTAMPTZ NULL,
    mensagem_id UUID NULL,
    CONSTRAINT ux_pedido_notificacao_tipo UNIQUE (pedido_id, tipo)
);

-- 3) Parametros por estabelecimento (sem linha/coluna valem os padroes do codigo).
ALTER TABLE delivery_settings
    ADD COLUMN IF NOT EXISTS notify_dispatch_enabled BOOLEAN NOT NULL DEFAULT TRUE,
    ADD COLUMN IF NOT EXISTS notify_arriving_enabled BOOLEAN NOT NULL DEFAULT TRUE,
    ADD COLUMN IF NOT EXISTS notify_arriving_minutes INTEGER NOT NULL DEFAULT 5,
    ADD COLUMN IF NOT EXISTS notify_arriving_radius_m INTEGER NOT NULL DEFAULT 400,
    ADD COLUMN IF NOT EXISTS notify_template_dispatch TEXT NULL,
    ADD COLUMN IF NOT EXISTS notify_template_arriving TEXT NULL;

ALTER TABLE delivery_settings DROP CONSTRAINT IF EXISTS ck_delivery_settings_notify_minutes;
ALTER TABLE delivery_settings
    ADD CONSTRAINT ck_delivery_settings_notify_minutes CHECK (notify_arriving_minutes BETWEEN 1 AND 60);
ALTER TABLE delivery_settings DROP CONSTRAINT IF EXISTS ck_delivery_settings_notify_radius;
ALTER TABLE delivery_settings
    ADD CONSTRAINT ck_delivery_settings_notify_radius CHECK (notify_arriving_radius_m BETWEEN 50 AND 5000);

-- 4) O motoboy autoriza compartilhar a posicao com o cliente (padrao: nao).
ALTER TABLE motoboy ADD COLUMN IF NOT EXISTS compartilhar_localizacao_cliente BOOLEAN NOT NULL DEFAULT FALSE;

-- 5) Link publico: token opaco e expiravel por pedido.
CREATE TABLE IF NOT EXISTS pedido_rastreio_token (
    pedido_id INTEGER PRIMARY KEY REFERENCES pedido (id) ON DELETE CASCADE,
    token TEXT NOT NULL UNIQUE,
    criado_em TIMESTAMPTZ NOT NULL DEFAULT NOW(),
    expira_em TIMESTAMPTZ NOT NULL,
    invalidado_em TIMESTAMPTZ NULL
);

INSERT INTO delivery_tracking_schema_versions (version)
VALUES ('20260928_01_avisos_rastreio')
ON CONFLICT (version) DO NOTHING;

COMMIT;
