-- Primeiro acesso por link de convite e redefinicao de senha (sem envio de e-mail: a Gestao copia o link e manda por WhatsApp).
-- usuario_convites guarda so o hash (SHA-256) do token. tipo: convite (primeiro acesso) | redefinicao (link gerado pela Gestao)
-- | solicitacao (o proprio usuario pediu em "Esqueci minha senha"; nao tem link, so avisa a Gestao). Idempotente.
BEGIN;

-- TRUE enquanto a conta so tem a senha aleatoria do cadastro e o responsavel ainda nao abriu o convite.
ALTER TABLE usuario ADD COLUMN IF NOT EXISTS senha_pendente BOOLEAN NOT NULL DEFAULT FALSE;

CREATE TABLE IF NOT EXISTS usuario_convites (
    id UUID PRIMARY KEY DEFAULT gen_random_uuid(),
    id_usuario INTEGER NOT NULL REFERENCES usuario(id) ON DELETE CASCADE,
    tipo TEXT NOT NULL CHECK (tipo IN ('convite', 'redefinicao', 'solicitacao')),
    token_hash TEXT NOT NULL UNIQUE,
    expira_em TIMESTAMPTZ NOT NULL,
    usado_em TIMESTAMPTZ NULL,
    criado_por INTEGER NULL,
    criado_em TIMESTAMPTZ NOT NULL DEFAULT NOW()
);
CREATE INDEX IF NOT EXISTS ix_usuario_convites_usuario ON usuario_convites (id_usuario, criado_em DESC);

INSERT INTO delivery_tracking_schema_versions (version)
VALUES ('20261023_01_convite_acesso')
ON CONFLICT (version) DO NOTHING;

COMMIT;
