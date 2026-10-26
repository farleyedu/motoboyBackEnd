-- Gestao de Pagamentos da Zippy e conexao OAuth das lojas (sessao 2). Contrato em docs/pagamentos/CONTRATOS.md (§2.16-2.18, §8.1, §8.2).
--   * mp_ambiente_estado   -> por ambiente: liberado para uso e se os restaurantes ja podem conectar. Producao so libera com verificacao aprovada.
--   * mp_verificacao       -> cada "Verificar agora" da Gestao (resultado por etapa, sem segredo). A ultima aprovada destrava o ambiente.
--   * pagamento_politica   -> politicas financeiras da plataforma (tolerancia, avisos, alcadas), linha unica com valores padrao.
-- Aditiva e idempotente. Rollback em rollback/20261101_20_pagamentos_gestao_oauth.down.sql.
BEGIN;

DO $$
BEGIN
    IF to_regclass('mp_aplicacao') IS NULL OR to_regclass('mp_conta_loja') IS NULL OR to_regclass('pagamento_auditoria') IS NULL THEN
        RAISE EXCEPTION 'pagamentos_gestao_oauth requer 20261031_01_mercadopago_nucleo';
    END IF;
END $$;

CREATE TABLE IF NOT EXISTS mp_ambiente_estado (
    ambiente               TEXT PRIMARY KEY CHECK (ambiente IN ('sandbox', 'producao')),
    -- Ambiente liberado: a Gestao confirmou que aplicacao, recebedor Zippy, callback e webhook funcionam.
    habilitado             BOOLEAN NOT NULL DEFAULT FALSE,
    -- Restaurantes podem iniciar "Conectar Mercado Pago" (exige habilitado).
    lojas_podem_conectar   BOOLEAN NOT NULL DEFAULT FALSE,
    id_verificacao         UUID NULL,
    habilitado_por         INTEGER NULL,
    habilitado_em          TIMESTAMPTZ NULL,
    updated_at             TIMESTAMPTZ NOT NULL DEFAULT NOW(),
    CONSTRAINT ck_mp_ambiente_lojas CHECK (habilitado OR NOT lojas_podem_conectar)
);

CREATE TABLE IF NOT EXISTS mp_verificacao (
    id                     UUID PRIMARY KEY,
    ambiente               TEXT NOT NULL CHECK (ambiente IN ('sandbox', 'producao')),
    aprovada               BOOLEAN NOT NULL,
    -- [{ etapa, ok, codigo, mensagem }] - nunca token ou segredo.
    etapas                 JSONB NOT NULL DEFAULT '[]'::jsonb,
    executada_por          INTEGER NULL,
    created_at             TIMESTAMPTZ NOT NULL DEFAULT NOW()
);
CREATE INDEX IF NOT EXISTS ix_mp_verificacao_ambiente ON mp_verificacao (ambiente, created_at DESC);

CREATE TABLE IF NOT EXISTS pagamento_politica (
    id                          SMALLINT PRIMARY KEY DEFAULT 1 CHECK (id = 1),
    -- Dias de atraso da mensalidade antes de restringir a loja (a sessao 4 aplica).
    tolerancia_dias             INTEGER NOT NULL DEFAULT 7 CHECK (tolerancia_dias BETWEEN 0 AND 30),
    avisos_antes_dias           INTEGER[] NOT NULL DEFAULT '{3,1}',
    avisos_depois_dias          INTEGER[] NOT NULL DEFAULT '{1,3,6}',
    -- Estorno ate este valor: gerente da loja; acima: so dono (0 = qualquer valor exige dono).
    alcada_estorno_gerente      NUMERIC(12, 2) NOT NULL DEFAULT 200 CHECK (alcada_estorno_gerente >= 0),
    -- Estorno feito pela Gestao acima deste valor pede confirmacao digitada.
    alcada_estorno_confirmacao  NUMERIC(12, 2) NOT NULL DEFAULT 1000 CHECK (alcada_estorno_confirmacao >= 0),
    -- Aviso para a Gestao quando um webhook falha mais que isto em 24 h.
    alerta_falhas_webhook       INTEGER NOT NULL DEFAULT 5 CHECK (alerta_falhas_webhook BETWEEN 1 AND 1000),
    atualizado_por              INTEGER NULL,
    updated_at                  TIMESTAMPTZ NOT NULL DEFAULT NOW()
);
INSERT INTO pagamento_politica (id) VALUES (1) ON CONFLICT (id) DO NOTHING;

INSERT INTO mp_ambiente_estado (ambiente) VALUES ('sandbox'), ('producao') ON CONFLICT (ambiente) DO NOTHING;

-- Lista de webhooks e da Gestao por status/data.
CREATE INDEX IF NOT EXISTS ix_mp_webhook_evento_status ON mp_webhook_evento (ambiente, status, recebido_em DESC);
-- Uma tentativa OAuth so pode ser consumida uma vez: a busca do callback usa state_hash (unico) + estado.
CREATE INDEX IF NOT EXISTS ix_mp_oauth_tentativa_expira ON mp_oauth_tentativa (expira_em) WHERE estado = 'iniciada';

COMMIT;
