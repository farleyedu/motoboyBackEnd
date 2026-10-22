-- Ciclo de vida das conexoes iFood: historico por loja, saude do polling por merchant e auditoria.
-- Antes, merchant_id era a chave unica da conexao: um merchant nunca mudava de loja e uma conexao com
-- eventos/pedidos nao podia ser apagada (FKs compostas), o que exigia SQL manual. Agora cada par
-- (merchant, loja) e uma linha de historico; so UMA fica ativa (revoked_at_utc IS NULL) por merchant.
-- Aditiva e idempotente. Nenhuma linha, pedido ou evento e apagado ou movido.
BEGIN;

DO $$
BEGIN
    IF to_regclass('ifood_connections') IS NULL THEN
        RAISE EXCEPTION 'ifood_connection_lifecycle requer ifood_connections (20261013_01_ifood_integration_schema)';
    END IF;
END $$;

-- Saude por merchant: o polling isola o merchant que o iFood recusa (403) sem parar os demais.
ALTER TABLE ifood_connections ADD COLUMN IF NOT EXISTS poll_state TEXT NULL;
ALTER TABLE ifood_connections ADD COLUMN IF NOT EXISTS poll_error_code TEXT NULL;
ALTER TABLE ifood_connections ADD COLUMN IF NOT EXISTS poll_failures INTEGER NOT NULL DEFAULT 0;
ALTER TABLE ifood_connections ADD COLUMN IF NOT EXISTS poll_blocked_until_utc TIMESTAMPTZ NULL;
ALTER TABLE ifood_connections ADD COLUMN IF NOT EXISTS last_poll_at_utc TIMESTAMPTZ NULL;
ALTER TABLE ifood_connections ADD COLUMN IF NOT EXISTS last_poll_ok_at_utc TIMESTAMPTZ NULL;
-- Ultima conferencia real no iFood (GET merchant): AUTHORIZED, NOT_AUTHORIZED, APP_CREDENTIALS, RATE_LIMIT, UNAVAILABLE.
ALTER TABLE ifood_connections ADD COLUMN IF NOT EXISTS remote_check_code TEXT NULL;
ALTER TABLE ifood_connections ADD COLUMN IF NOT EXISTS remote_checked_at_utc TIMESTAMPTZ NULL;

DO $$
BEGIN
    IF NOT EXISTS (SELECT 1 FROM pg_constraint WHERE conname = 'ck_ifood_connections_poll_state' AND conrelid = 'ifood_connections'::regclass) THEN
        ALTER TABLE ifood_connections ADD CONSTRAINT ck_ifood_connections_poll_state
            CHECK (poll_state IS NULL OR poll_state IN ('ok','forbidden','error'));
    END IF;
END $$;

-- merchant_id deixa de ser chave sozinho. As FKs dos pedidos/eventos apontam para o par (merchant, loja),
-- que continua unico; o historico de uma loja antiga permanece intacto quando o merchant muda de loja.
DO $$
BEGIN
    IF EXISTS (SELECT 1 FROM pg_constraint WHERE conrelid = 'ifood_connections'::regclass AND contype = 'p'
               AND conkey = ARRAY[(SELECT attnum FROM pg_attribute WHERE attrelid = 'ifood_connections'::regclass AND attname = 'merchant_id')]) THEN
        ALTER TABLE ifood_connections DROP CONSTRAINT ifood_connections_pkey;
        ALTER TABLE ifood_connections ADD CONSTRAINT ifood_connections_pkey PRIMARY KEY (merchant_id, estabelecimento_id);
    END IF;
END $$;

-- Um merchant so pode estar ativo em UMA loja por vez.
CREATE UNIQUE INDEX IF NOT EXISTS ux_ifood_connections_active_merchant
    ON ifood_connections (merchant_id) WHERE revoked_at_utc IS NULL;

-- Uma loja so tem UMA conexao ativa. Criado apenas se os dados ja respeitam a regra (o codigo tambem a impoe).
DO $$
BEGIN
    IF EXISTS (SELECT 1 FROM pg_indexes WHERE indexname = 'ux_ifood_connections_active_store') THEN
        RETURN;
    END IF;
    IF EXISTS (SELECT 1 FROM ifood_connections WHERE revoked_at_utc IS NULL GROUP BY estabelecimento_id HAVING COUNT(*) > 1) THEN
        RAISE NOTICE 'ifood_connections: ha lojas com mais de uma conexao ativa; revogue as extras e rode esta migration de novo para criar ux_ifood_connections_active_store';
    ELSE
        CREATE UNIQUE INDEX ux_ifood_connections_active_store ON ifood_connections (estabelecimento_id) WHERE revoked_at_utc IS NULL;
    END IF;
END $$;

-- Auditoria: quem conectou, autorizou, transferiu, desconectou ou removeu. Sem FK de proposito:
-- o historico sobrevive mesmo a uma remocao de conexao nunca usada.
CREATE TABLE IF NOT EXISTS ifood_connection_audit (
    id BIGSERIAL PRIMARY KEY,
    merchant_id UUID NOT NULL,
    estabelecimento_id UUID NULL,
    action TEXT NOT NULL CHECK (btrim(action) <> ''),
    actor TEXT NULL,
    detail TEXT NULL,
    created_at_utc TIMESTAMPTZ NOT NULL DEFAULT NOW()
);
CREATE INDEX IF NOT EXISTS ix_ifood_connection_audit_merchant ON ifood_connection_audit (merchant_id, created_at_utc DESC);
CREATE INDEX IF NOT EXISTS ix_ifood_connection_audit_store ON ifood_connection_audit (estabelecimento_id, created_at_utc DESC);

INSERT INTO delivery_tracking_schema_versions(version) VALUES ('20261027_01_ifood_connection_lifecycle') ON CONFLICT DO NOTHING;
COMMIT;
