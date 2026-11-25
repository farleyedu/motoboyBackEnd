BEGIN;

-- Tabelas que o codigo criava sozinho em tempo de execucao (CREATE TABLE IF NOT EXISTS dentro do repositorio) passam a
-- nascer aqui, no historico de migrations. Idempotente: em producao elas ja existem e nada muda.
--   * checkout_*                     -> pagamentos pelo Asaas (CheckoutRepository)
--   * empresa_webhook_auditoria      -> mensagens do WhatsApp ignoradas (empresa desativada)
--   * empresas.ativo / empresas.pausada -> usadas pelo webhook para ignorar empresas desativadas

CREATE TABLE IF NOT EXISTS checkout_asaas_customers (
    id_usuario INT PRIMARY KEY,
    asaas_customer_id TEXT NOT NULL,
    data_criacao TIMESTAMPTZ NOT NULL DEFAULT NOW(),
    data_atualizacao TIMESTAMPTZ NOT NULL DEFAULT NOW()
);

CREATE TABLE IF NOT EXISTS checkout_pagamentos (
    id BIGSERIAL PRIMARY KEY,
    id_usuario INT NOT NULL,
    id_estabelecimento UUID NULL,
    asaas_payment_id TEXT NOT NULL UNIQUE,
    asaas_customer_id TEXT NULL,
    tipo_pagamento TEXT NOT NULL,
    status TEXT NOT NULL,
    asaas_status TEXT NULL,
    valor NUMERIC(14,2) NOT NULL,
    descricao TEXT NULL,
    invoice_url TEXT NULL,
    pix_qr_code_base64 TEXT NULL,
    pix_copia_cola TEXT NULL,
    json_retorno_gateway JSONB NULL,
    json_webhook_ultimo JSONB NULL,
    data_criacao TIMESTAMPTZ NOT NULL DEFAULT NOW(),
    data_atualizacao TIMESTAMPTZ NOT NULL DEFAULT NOW()
);

CREATE INDEX IF NOT EXISTS ix_checkout_pagamentos_user ON checkout_pagamentos (id_usuario);
CREATE INDEX IF NOT EXISTS ix_checkout_pagamentos_status ON checkout_pagamentos (status);

CREATE TABLE IF NOT EXISTS checkout_webhook_logs (
    id BIGSERIAL PRIMARY KEY,
    event_id TEXT NOT NULL UNIQUE,
    event_type TEXT NOT NULL,
    asaas_payment_id TEXT NULL,
    payload JSONB NOT NULL,
    sucesso BOOLEAN NOT NULL DEFAULT FALSE,
    mensagem_erro TEXT NULL,
    data_recebimento TIMESTAMPTZ NOT NULL DEFAULT NOW(),
    processado_em TIMESTAMPTZ NULL
);

CREATE INDEX IF NOT EXISTS ix_checkout_webhook_logs_payment ON checkout_webhook_logs (asaas_payment_id);

ALTER TABLE empresas ADD COLUMN IF NOT EXISTS ativo BOOLEAN NOT NULL DEFAULT TRUE;
ALTER TABLE empresas ADD COLUMN IF NOT EXISTS pausada BOOLEAN NOT NULL DEFAULT FALSE;

CREATE TABLE IF NOT EXISTS empresa_webhook_auditoria (
    id UUID PRIMARY KEY,
    id_empresa UUID NULL,
    id_estabelecimento UUID NULL,
    id_mensagem_wa TEXT NULL,
    telefone_cliente TEXT NULL,
    display_phone_number TEXT NULL,
    phone_number_id TEXT NULL,
    motivo TEXT NOT NULL,
    conteudo_preview TEXT NULL,
    data_criacao TIMESTAMPTZ NOT NULL DEFAULT NOW()
);

CREATE INDEX IF NOT EXISTS ix_empresa_webhook_auditoria_empresa_data ON empresa_webhook_auditoria (id_empresa, data_criacao DESC);

INSERT INTO delivery_tracking_schema_versions (version)
VALUES ('20261002_05_tabelas_do_codigo')
ON CONFLICT (version) DO NOTHING;

COMMIT;
