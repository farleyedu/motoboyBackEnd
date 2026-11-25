-- Janela nova so passa a valer no proximo dia operacional; vagas usam a mesma borda que o mapa.
ALTER TABLE delivery_settings ADD COLUMN IF NOT EXISTS order_window_pending JSONB NULL;
ALTER TABLE delivery_settings ADD COLUMN IF NOT EXISTS order_window_effective_from_utc TIMESTAMPTZ NULL;
ALTER TABLE delivery_settings ADD COLUMN IF NOT EXISTS order_window_last_change_utc TIMESTAMPTZ NULL;

CREATE TABLE IF NOT EXISTS motoboy_vaga_periodo (
    estabelecimento_id UUID NOT NULL REFERENCES estabelecimentos(id) ON DELETE CASCADE,
    periodo_inicio_utc TIMESTAMPTZ NOT NULL,
    motoboy_id INTEGER NOT NULL,
    ocupada_em TIMESTAMPTZ NOT NULL DEFAULT NOW(),
    pedido_id INTEGER NULL,
    PRIMARY KEY (estabelecimento_id, periodo_inicio_utc, motoboy_id)
);
CREATE INDEX IF NOT EXISTS ix_motoboy_vaga_periodo_loja_periodo
    ON motoboy_vaga_periodo (estabelecimento_id, periodo_inicio_utc);

INSERT INTO delivery_tracking_schema_versions (version)
VALUES ('20261021_02_dia_operacional_vagas')
ON CONFLICT (version) DO NOTHING;
