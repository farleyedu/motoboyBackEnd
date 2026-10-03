BEGIN;

-- ---------------------------------------------------------------------------
-- Fila de eventos do webhook do WhatsApp, no Postgres (plano: zippy-admin/docs/plano-atendimento-whatsapp.md, etapa 2).
--
-- Antes o webhook respondia 200 a Meta e entregava a mensagem a uma fila EM MEMORIA: um reinicio ou deploy nesse
-- intervalo perdia a mensagem. Agora o evento e gravado ANTES de responder; um worker o processa depois e, se falhar,
-- tenta de novo com espera crescente. A chave (tipo, chave) torna a gravacao idempotente: a Meta reenvia eventos.
--   tipo = 'mensagem' -> chave = id da mensagem da Meta (wamid)
--   tipo = 'status'   -> chave = wamid + ':' + status (sent, delivered, read, failed)
-- ---------------------------------------------------------------------------

CREATE TABLE IF NOT EXISTS wa_evento (
    id UUID PRIMARY KEY,
    tipo TEXT NOT NULL CHECK (tipo IN ('mensagem', 'status')),
    chave TEXT NOT NULL,
    phone_number_id TEXT NULL,
    display_phone TEXT NULL,
    id_canal UUID NULL,
    payload JSONB NOT NULL,
    estado TEXT NOT NULL DEFAULT 'pendente' CHECK (estado IN ('pendente', 'processando', 'processado', 'ignorado', 'erro')),
    tentativas INTEGER NOT NULL DEFAULT 0,
    motivo TEXT NULL,
    proxima_tentativa_em TIMESTAMPTZ NOT NULL DEFAULT NOW(),
    recebido_em TIMESTAMPTZ NOT NULL DEFAULT NOW(),
    iniciado_em TIMESTAMPTZ NULL,
    processado_em TIMESTAMPTZ NULL,
    CONSTRAINT ux_wa_evento_tipo_chave UNIQUE (tipo, chave)
);

-- O worker so olha o que ainda nao terminou; o indice parcial mantem a consulta barata mesmo com historico grande.
CREATE INDEX IF NOT EXISTS ix_wa_evento_fila
    ON wa_evento (proxima_tentativa_em)
    WHERE estado IN ('pendente', 'processando');

CREATE INDEX IF NOT EXISTS ix_wa_evento_recebido ON wa_evento (recebido_em DESC);

INSERT INTO delivery_tracking_schema_versions (version)
VALUES ('20261002_09_wa_evento')
ON CONFLICT (version) DO NOTHING;

COMMIT;
