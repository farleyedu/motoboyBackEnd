-- Identidade por WhatsApp (docs/crm-inteligente/02-backend-whatsapp-ia.md, zippy-admin, A1):
--   * usuario_whatsapp_vinculo: um vinculo por usuario entre o telefone E.164 dele e a prova de posse do numero.
--     Estados: nao_verificado -> pendente -> verificado -> suspenso/revogado. O codigo nunca fica em claro: so o HMAC (preso ao
--     vinculo e ao telefone), apagado ao confirmar, expirar, suspender ou revogar. "versao" e a concorrencia otimista das tentativas.
--   * Um telefone so pode estar pendente ou verificado para um usuario por vez (indice unico parcial).
--   * usuario_whatsapp_auditoria: trilha de envio, confirmacao, suspensao e revogacao. So os 4 ultimos digitos; nunca codigo ou hash.
-- Aditiva e idempotente (roda a cada boot).
BEGIN;

CREATE TABLE IF NOT EXISTS usuario_whatsapp_vinculo (
    id                  UUID PRIMARY KEY,
    usuario_id          INTEGER NOT NULL UNIQUE REFERENCES usuario(id),
    telefone_e164       VARCHAR(16) NULL CHECK (telefone_e164 ~ '^\+[1-9][0-9]{7,14}$'),
    estado              TEXT NOT NULL DEFAULT 'nao_verificado'
                        CHECK (estado IN ('nao_verificado', 'pendente', 'verificado', 'suspenso', 'revogado')),
    codigo_hash         BYTEA NULL,
    codigo_expira_em    TIMESTAMPTZ NULL,
    tentativas          INTEGER NOT NULL DEFAULT 0 CHECK (tentativas >= 0),
    envios_janela       INTEGER NOT NULL DEFAULT 0 CHECK (envios_janela >= 0),
    janela_envio_inicio TIMESTAMPTZ NULL,
    ultimo_envio_em     TIMESTAMPTZ NULL,
    bloqueado_ate       TIMESTAMPTZ NULL,
    verificado_em       TIMESTAMPTZ NULL,
    revogado_em         TIMESTAMPTZ NULL,
    motivo              TEXT NULL,
    versao              BIGINT NOT NULL DEFAULT 1,
    criado_em           TIMESTAMPTZ NOT NULL DEFAULT NOW(),
    atualizado_em       TIMESTAMPTZ NOT NULL DEFAULT NOW(),
    -- Pendente exige codigo com prazo; verificado e o resto nunca guardam codigo.
    CONSTRAINT ck_usuario_whatsapp_vinculo_codigo CHECK (
        (estado = 'pendente' AND codigo_hash IS NOT NULL AND codigo_expira_em IS NOT NULL AND telefone_e164 IS NOT NULL)
        OR (estado <> 'pendente' AND codigo_hash IS NULL)
    ),
    CONSTRAINT ck_usuario_whatsapp_vinculo_verificado CHECK (estado <> 'verificado' OR (telefone_e164 IS NOT NULL AND verificado_em IS NOT NULL))
);

CREATE UNIQUE INDEX IF NOT EXISTS ux_usuario_whatsapp_vinculo_telefone_ativo
    ON usuario_whatsapp_vinculo (telefone_e164)
    WHERE estado IN ('pendente', 'verificado');

CREATE TABLE IF NOT EXISTS usuario_whatsapp_auditoria (
    id              BIGSERIAL PRIMARY KEY,
    usuario_id      INTEGER NOT NULL,
    vinculo_id      UUID NULL,
    evento          TEXT NOT NULL,
    ator_usuario_id INTEGER NULL,
    telefone_final  VARCHAR(4) NULL,
    detalhe         TEXT NULL,
    criado_em       TIMESTAMPTZ NOT NULL DEFAULT NOW()
);

CREATE INDEX IF NOT EXISTS ix_usuario_whatsapp_auditoria_usuario ON usuario_whatsapp_auditoria (usuario_id, criado_em DESC);

-- Resolucao do remetente pelo telefone atual do usuario.
CREATE INDEX IF NOT EXISTS ix_usuario_telefone_e164 ON usuario (telefone_e164) WHERE telefone_e164 IS NOT NULL;

COMMIT;
