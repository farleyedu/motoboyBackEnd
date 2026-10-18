BEGIN;

CREATE TABLE IF NOT EXISTS atendimento_execucao (
    wa_chave TEXT PRIMARY KEY,
    conversa_id UUID NOT NULL REFERENCES conversas(id) ON DELETE CASCADE,
    decisao JSONB NOT NULL,
    concluida BOOLEAN NOT NULL DEFAULT FALSE,
    criado_em TIMESTAMPTZ NOT NULL DEFAULT NOW()
);
CREATE TABLE IF NOT EXISTS atendimento_execucao_acao (
    wa_chave TEXT NOT NULL REFERENCES atendimento_execucao(wa_chave) ON DELETE CASCADE,
    indice INTEGER NOT NULL,
    mensagem_id UUID NOT NULL,
    concluida BOOLEAN NOT NULL DEFAULT FALSE,
    PRIMARY KEY (wa_chave, indice)
);
CREATE INDEX IF NOT EXISTS ix_wa_evento_contato_ordem
    ON wa_evento (phone_number_id, (payload->'message'->>'from'), recebido_em, id)
    WHERE tipo = 'mensagem' AND estado IN ('pendente', 'processando');

INSERT INTO delivery_tracking_schema_versions(version) VALUES ('20261018_03_atendimento_execucao')
ON CONFLICT (version) DO NOTHING;
COMMIT;
