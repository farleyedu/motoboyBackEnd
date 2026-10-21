BEGIN;

ALTER TABLE motoboy_estabelecimento
    ADD COLUMN IF NOT EXISTS disabled_by_user_id INTEGER NULL REFERENCES usuario(id),
    ADD COLUMN IF NOT EXISTS disabled_reason TEXT NULL;

CREATE TABLE IF NOT EXISTS motoboy_vinculo_encerramentos (
    id BIGSERIAL PRIMARY KEY,
    estabelecimento_id UUID NOT NULL REFERENCES estabelecimentos(id),
    motoboy_id INTEGER NOT NULL REFERENCES motoboy(id),
    encerrado_por_usuario_id INTEGER NOT NULL REFERENCES usuario(id),
    encerrado_em_utc TIMESTAMPTZ NOT NULL DEFAULT NOW(),
    motivo TEXT NOT NULL
);

COMMIT;
