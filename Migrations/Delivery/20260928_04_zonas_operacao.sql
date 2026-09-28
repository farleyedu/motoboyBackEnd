BEGIN;

-- ---------------------------------------------------------------------------
-- Fase 2 da reconstrucao de Configuracoes (tela "Delivery e operacao").
-- Tudo aditivo; estabelecimento sem zona cadastrada continua usando so
-- taxa_entrega_fixa/taxa_entrega_por_km como hoje (OrderCoreRules.ComputeFee
-- cai pro calculo antigo quando nao ha zona ativa). Aplicada automaticamente
-- no boot (ApplyMigrationsOnStartup=true em producao).
-- ---------------------------------------------------------------------------

ALTER TABLE estabelecimentos
    ADD COLUMN IF NOT EXISTS entrega_gratis_acima_de NUMERIC(10,2) NULL;

-- Zona = faixa de raio (ate raio_ate_km, crescente) com taxa propria. A zona de um endereco e a de
-- MENOR raio_ate_km que ainda cobre a distancia calculada (mesma formula haversine do nucleo de
-- pedido). Zonas sem "ate_km" maior que o raio de entrega do estabelecimento nunca sao alcancadas.
CREATE TABLE IF NOT EXISTS delivery_zona (
    id UUID PRIMARY KEY,
    estabelecimento_id UUID NOT NULL REFERENCES estabelecimentos (id) ON DELETE CASCADE,
    nome VARCHAR(60) NOT NULL CHECK (length(trim(nome)) > 0),
    raio_ate_km NUMERIC(6,2) NOT NULL CHECK (raio_ate_km > 0),
    taxa NUMERIC(10,2) NOT NULL CHECK (taxa >= 0),
    cor VARCHAR(7) NULL,
    ordem INTEGER NOT NULL DEFAULT 0,
    ativo BOOLEAN NOT NULL DEFAULT TRUE,
    criado_em TIMESTAMPTZ NOT NULL DEFAULT NOW(),
    atualizado_em TIMESTAMPTZ NOT NULL DEFAULT NOW()
);
CREATE INDEX IF NOT EXISTS ix_delivery_zona_estab ON delivery_zona (estabelecimento_id, ativo, raio_ate_km);

ALTER TABLE delivery_settings
    ADD COLUMN IF NOT EXISTS auto_confirmar_pedidos BOOLEAN NOT NULL DEFAULT TRUE,
    -- Pedido pendente sem motoboy: tenta atribuir sozinho a um motoboy disponivel sem rota ativa
    -- (mesma acao que o atendente faria manualmente; nao decide ENTRE varios, so o caso obvio de 1).
    ADD COLUMN IF NOT EXISTS autoatribuir_motoboy BOOLEAN NOT NULL DEFAULT FALSE,
    ADD COLUMN IF NOT EXISTS bloquear_pedidos_fora_horario BOOLEAN NOT NULL DEFAULT FALSE,
    ADD COLUMN IF NOT EXISTS retirada_balcao_ativa BOOLEAN NOT NULL DEFAULT FALSE,
    ADD COLUMN IF NOT EXISTS retirada_tempo_preparo_min INTEGER NULL;

-- Excecao pontual ao horario semanal (feriado, evento). Uma linha por data; sobrepoe
-- estabelecimento_horario so naquele dia.
CREATE TABLE IF NOT EXISTS estabelecimento_horario_especial (
    id BIGSERIAL PRIMARY KEY,
    estabelecimento_id UUID NOT NULL REFERENCES estabelecimentos (id) ON DELETE CASCADE,
    data DATE NOT NULL,
    fechado BOOLEAN NOT NULL DEFAULT TRUE,
    abre_as TIME NULL,
    fecha_as TIME NULL,
    motivo VARCHAR(120) NULL,
    UNIQUE (estabelecimento_id, data)
);

INSERT INTO delivery_tracking_schema_versions (version)
VALUES ('20260928_04_zonas_operacao')
ON CONFLICT (version) DO NOTHING;

COMMIT;
