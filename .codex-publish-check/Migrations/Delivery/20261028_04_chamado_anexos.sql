-- Chamados: prints anexados as mensagens (abertura e respostas, da loja ou da Zippy). Os arquivos ficam no Cloudinary
-- (chamados/{estabelecimento}/{chamado}); aqui so a referencia. public_id guarda o identificador no Cloudinary para
-- limpeza futura. Aditiva e idempotente. Rollback em rollback/20261028_04_chamado_anexos.down.sql.
BEGIN;

CREATE TABLE IF NOT EXISTS chamado_anexo (
    id          BIGSERIAL PRIMARY KEY,
    mensagem_id BIGINT NOT NULL REFERENCES chamado_mensagem (id) ON DELETE CASCADE,
    url         TEXT NOT NULL,
    nome        TEXT NOT NULL,
    tipo        TEXT NOT NULL,
    bytes       INTEGER NOT NULL,
    public_id   TEXT NULL,
    criado_em   TIMESTAMPTZ NOT NULL DEFAULT NOW()
);
CREATE INDEX IF NOT EXISTS ix_chamado_anexo_mensagem ON chamado_anexo (mensagem_id);

COMMIT;
