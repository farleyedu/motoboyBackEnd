BEGIN;

-- ---------------------------------------------------------------------------
-- Grupo de motoboys por estabelecimento (Fase E do fluxo de motoboy): canal de chat/avisos entre o
-- atendente e TODOS os motoboys vinculados aquela loja, igual a um grupo de WhatsApp. So chat --
-- nao desapcha pedido (a atribuicao continua pelo mecanismo de oferta/fila que ja existe).
-- Aditiva e idempotente; nao toca em delivery_motoboy_message (1:1 atendente<->motoboy).
--  - Sem tabela de membership: quem esta "no grupo" e qualquer motoboy com motoboy_estabelecimento.ativo
--    naquela loja (igual a 1:1); nao exige sessao aberta para mandar, so para ver teus pedidos
-- ---------------------------------------------------------------------------

CREATE TABLE IF NOT EXISTS motoboy_group_message (
    id BIGSERIAL PRIMARY KEY,
    estabelecimento_id UUID NOT NULL REFERENCES estabelecimentos (id),
    sender_type TEXT NOT NULL CHECK (sender_type IN ('operator', 'motoboy')),
    motoboy_id INTEGER NULL REFERENCES motoboy (id),
    sent_by_user_id INTEGER NULL,
    body TEXT NOT NULL CHECK (char_length(body) BETWEEN 1 AND 500),
    created_at_utc TIMESTAMPTZ NOT NULL DEFAULT NOW()
);

CREATE INDEX IF NOT EXISTS ix_motoboy_group_message_estab
    ON motoboy_group_message (estabelecimento_id, created_at_utc DESC);

INSERT INTO delivery_tracking_schema_versions (version)
VALUES ('20261006_03_motoboy_grupo')
ON CONFLICT (version) DO NOTHING;

COMMIT;
