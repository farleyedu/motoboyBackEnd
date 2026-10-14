BEGIN;

-- IF-13: configuração central do iFood editável por tela em Gestão, sem redeploy.
-- Linha única (id=1); client_secret nunca em texto puro (ver IfoodSecretProtector).
-- Esta tabela não liga nada sozinha: os workers continuam exigindo credenciais válidas
-- e, no caso do polling, pelo menos uma conexão autorizada em ifood_connections.
CREATE TABLE IF NOT EXISTS ifood_app_settings (
    id SMALLINT PRIMARY KEY CHECK (id = 1),
    client_id TEXT NOT NULL DEFAULT '',
    client_secret_cifrado TEXT NULL,
    enabled BOOLEAN NOT NULL DEFAULT FALSE,
    polling_enabled BOOLEAN NOT NULL DEFAULT FALSE,
    import_enabled BOOLEAN NOT NULL DEFAULT FALSE,
    projection_enabled BOOLEAN NOT NULL DEFAULT FALSE,
    commands_enabled BOOLEAN NOT NULL DEFAULT FALSE,
    exceptions_enabled BOOLEAN NOT NULL DEFAULT FALSE,
    updated_at_utc TIMESTAMPTZ NOT NULL DEFAULT NOW(),
    updated_by TEXT NULL
);

INSERT INTO ifood_app_settings(id) VALUES (1) ON CONFLICT DO NOTHING;

INSERT INTO delivery_tracking_schema_versions(version) VALUES ('20261014_02_ifood_runtime_settings') ON CONFLICT DO NOTHING;
COMMIT;
