-- App central ZippyGo para o "Conectar com a Meta", cadastrado pela Gestao na tela (Gestao > Plataforma), sem variavel de ambiente.
-- Linha unica (id = 1). O App Secret fica cifrado pela aplicacao; vazio = usa o do webhook global (Automation__Meta__AppSecret).
-- Aditiva e idempotente. Rollback em rollback/20261030_03_whatsapp_meta_app.down.sql.
BEGIN;

CREATE TABLE IF NOT EXISTS whatsapp_meta_app (
    id SMALLINT PRIMARY KEY DEFAULT 1 CHECK (id = 1),
    app_id TEXT NULL CHECK (app_id IS NULL OR app_id ~ '^[0-9]+$'),
    -- Configuracao do Facebook Login for Business (WhatsApp Embedded Signup v4).
    configuration_id TEXT NULL CHECK (configuration_id IS NULL OR configuration_id ~ '^[0-9]+$'),
    app_secret_cifrado TEXT NULL,
    -- App Review de whatsapp_business_management e whatsapp_business_messaging aprovado: libera qualquer restaurante.
    app_review_aprovado BOOLEAN NOT NULL DEFAULT FALSE,
    -- Portfolio da Zippy: WABAs dele nunca perdem a inscricao do app quando uma loja desconecta.
    business_id_integrador TEXT NULL CHECK (business_id_integrador IS NULL OR business_id_integrador ~ '^[0-9]+$'),
    atualizado_por INTEGER NULL,
    atualizado_em TIMESTAMPTZ NOT NULL DEFAULT NOW()
);

INSERT INTO delivery_tracking_schema_versions (version)
VALUES ('20261030_03_whatsapp_meta_app')
ON CONFLICT (version) DO NOTHING;

COMMIT;
