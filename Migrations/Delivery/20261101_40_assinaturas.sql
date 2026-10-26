-- Assinaturas da Zippy pelo Mercado Pago (sessao 4). Contrato em docs/pagamentos/CONTRATOS.md (§2.19-2.25, §5.3, §8.4).
--   * assinatura                -> uma viva por loja (pendente|ativa). Plano, ciclo vigente, renovacao, mudanca agendada, cancelamento.
--   * assinatura_ciclo          -> cada periodo pago/a pagar, com o plano e o PRECO DA EPOCA (mes-calendario ou 12 meses).
--   * comercial_cobranca        -> o que a loja deve a Zippy: ciclo, diferenca de upgrade ou compra avulsa. pagamento_intent.origem_ref_id = id daqui.
--   * compra_extra              -> recurso avulso comprado (preco da epoca), so ativa depois de pago.
--   * solicitacao_compra        -> funcionario pede, gerente aprova ou recusa.
--   * comercial_aviso           -> avisos de vencimento, atraso, suspensao e reativacao (um por cobranca e tipo).
--   * vw_assinatura_resumo      -> leitura estavel para a Gestao (sessao 2: GET /api/gestao/pagamentos/assinaturas).
-- Estado financeiro (assinatura.financeiro) e separado da situacao comercial (estabelecimentos.situacao_comercial).
-- Aditiva e idempotente. Rollback em rollback/20261101_40_assinaturas.down.sql. Exige 20261031_01 e 20261028_02.
BEGIN;

DO $$
BEGIN
    IF to_regclass('pagamento_intent') IS NULL OR to_regclass('pagamento_auditoria') IS NULL THEN
        RAISE EXCEPTION 'assinaturas requer 20261031_01_mercadopago_nucleo';
    END IF;
    IF to_regclass('produtos_avulsos') IS NULL THEN
        RAISE EXCEPTION 'assinaturas requer 20261028_02_planos_catalogo';
    END IF;
END $$;

CREATE TABLE IF NOT EXISTS assinatura (
    id                              UUID PRIMARY KEY,
    id_estabelecimento              UUID NOT NULL REFERENCES estabelecimentos (id) ON DELETE RESTRICT,
    plano_chave                     TEXT NOT NULL,
    periodicidade                   TEXT NOT NULL CHECK (periodicidade IN ('mensal', 'anual')),
    -- Preco do plano na contratacao (ou na ultima troca). O catalogo mudar depois nao altera o contrato.
    preco                           NUMERIC(10, 2) NOT NULL CHECK (preco >= 0),
    -- pendente: escolhida, primeiro ciclo ainda nao pago | ativa | encerrada (cancelada no fim do periodo ou nunca paga).
    status                          TEXT NOT NULL CHECK (status IN ('pendente', 'ativa', 'encerrada')),
    -- Estado financeiro: em_dia | em_atraso (dentro da tolerancia) | suspensa (operacoes novas travadas).
    financeiro                      TEXT NOT NULL DEFAULT 'em_dia' CHECK (financeiro IN ('em_dia', 'em_atraso', 'suspensa')),
    inadimplente_desde              TIMESTAMPTZ NULL,
    suspensa_em                     TIMESTAMPTZ NULL,
    -- Data local (America/Sao_Paulo) que da o dia do ciclo: 31/01 -> 28/02 (29 no bissexto) -> 31/03.
    ancora                          DATE NULL,
    -- Numero do ciclo em que a ancora comecou (troca de periodicidade reinicia a ancora).
    ancora_ciclo                    INTEGER NOT NULL DEFAULT 1,
    id_ciclo_atual                  UUID NULL,
    periodo_inicio                  TIMESTAMPTZ NULL,
    periodo_fim                     TIMESTAMPTZ NULL,
    metodo                          TEXT NULL CHECK (metodo IN ('pix', 'cartao')),
    -- So com autorizacao explicita (cartao no Mercado Pago, preapproval). Pix nunca renova sozinho: vira cobranca por ciclo.
    renovacao_automatica            BOOLEAN NOT NULL DEFAULT FALSE,
    renovacao_autorizada_em         TIMESTAMPTZ NULL,
    renovacao_autorizada_por        INTEGER NULL,
    mp_preapproval_id               TEXT NULL,
    mp_preapproval_status           TEXT NULL,
    preapproval_valor               NUMERIC(10, 2) NULL,
    cartao_final                    TEXT NULL CHECK (cartao_final IS NULL OR cartao_final ~ '^[0-9]{4}$'),
    cartao_bandeira                 TEXT NULL,
    -- Downgrade (ou troca para mensal) agendado para o fim do periodo, com o preco do catalogo no dia do pedido.
    mudanca_plano                   TEXT NULL,
    mudanca_periodicidade           TEXT NULL CHECK (mudanca_periodicidade IS NULL OR mudanca_periodicidade IN ('mensal', 'anual')),
    mudanca_preco                   NUMERIC(10, 2) NULL,
    mudanca_pedida_em               TIMESTAMPTZ NULL,
    mudanca_pedida_por              INTEGER NULL,
    -- Cancelamento: para as renovacoes e mantem o acesso ate periodo_fim. Sem estorno automatico.
    cancelamento_solicitado_em      TIMESTAMPTZ NULL,
    cancelamento_por                INTEGER NULL,
    cancelamento_motivo             TEXT NULL,
    encerrada_em                    TIMESTAMPTZ NULL,
    encerramento_motivo             TEXT NULL,
    contratada_por                  INTEGER NULL,
    idempotency_key                 TEXT NULL,
    versao                          INTEGER NOT NULL DEFAULT 1,
    created_at                      TIMESTAMPTZ NOT NULL DEFAULT NOW(),
    updated_at                      TIMESTAMPTZ NOT NULL DEFAULT NOW()
);
-- Nunca duas assinaturas vivas na mesma loja (upgrade troca o plano da mesma linha).
CREATE UNIQUE INDEX IF NOT EXISTS ux_assinatura_viva ON assinatura (id_estabelecimento) WHERE status IN ('pendente', 'ativa');
CREATE UNIQUE INDEX IF NOT EXISTS ux_assinatura_idem ON assinatura (id_estabelecimento, idempotency_key) WHERE idempotency_key IS NOT NULL;
CREATE UNIQUE INDEX IF NOT EXISTS ux_assinatura_preapproval ON assinatura (mp_preapproval_id) WHERE mp_preapproval_id IS NOT NULL;
CREATE INDEX IF NOT EXISTS ix_assinatura_loja ON assinatura (id_estabelecimento, created_at DESC);

CREATE TABLE IF NOT EXISTS assinatura_ciclo (
    id                  UUID PRIMARY KEY,
    id_assinatura       UUID NOT NULL REFERENCES assinatura (id) ON DELETE RESTRICT,
    id_estabelecimento  UUID NOT NULL REFERENCES estabelecimentos (id) ON DELETE RESTRICT,
    numero              INTEGER NOT NULL CHECK (numero >= 1),
    plano_chave         TEXT NOT NULL,
    periodicidade       TEXT NOT NULL CHECK (periodicidade IN ('mensal', 'anual')),
    preco               NUMERIC(10, 2) NOT NULL CHECK (preco >= 0),
    inicio              TIMESTAMPTZ NOT NULL,
    fim                 TIMESTAMPTZ NOT NULL,
    status              TEXT NOT NULL DEFAULT 'a_pagar' CHECK (status IN ('a_pagar', 'pago', 'cancelado')),
    -- Quando o plano deste ciclo foi aplicado na loja (modulos e motoboys).
    aplicado_em         TIMESTAMPTZ NULL,
    created_at          TIMESTAMPTZ NOT NULL DEFAULT NOW(),
    updated_at          TIMESTAMPTZ NOT NULL DEFAULT NOW(),
    CONSTRAINT ck_assinatura_ciclo_datas CHECK (fim > inicio),
    CONSTRAINT ux_assinatura_ciclo_numero UNIQUE (id_assinatura, numero)
);

CREATE TABLE IF NOT EXISTS compra_extra (
    id                  UUID PRIMARY KEY,
    id_estabelecimento  UUID NOT NULL REFERENCES estabelecimentos (id) ON DELETE RESTRICT,
    produto_avulso_id   BIGINT NOT NULL REFERENCES produtos_avulsos (id) ON DELETE RESTRICT,
    -- Retrato do produto e do preco na hora da compra.
    nome                TEXT NOT NULL,
    preco_unitario      NUMERIC(10, 2) NOT NULL CHECK (preco_unitario > 0),
    quantidade          INTEGER NOT NULL CHECK (quantidade BETWEEN 1 AND 20),
    valor_total         NUMERIC(12, 2) NOT NULL CHECK (valor_total > 0),
    motoboys            INTEGER NOT NULL DEFAULT 0 CHECK (motoboys >= 0),
    modulos_ativos      TEXT[] NOT NULL DEFAULT ARRAY[]::TEXT[],
    status              TEXT NOT NULL DEFAULT 'aguardando_pagamento' CHECK (status IN ('aguardando_pagamento', 'ativa', 'expirada', 'cancelada')),
    vigencia_inicio     TIMESTAMPTZ NULL,
    vigencia_fim        TIMESTAMPTZ NULL,
    id_solicitacao      UUID NULL,
    criado_por          INTEGER NULL,
    idempotency_key     TEXT NOT NULL,
    ativada_em          TIMESTAMPTZ NULL,
    cancelada_em        TIMESTAMPTZ NULL,
    versao              INTEGER NOT NULL DEFAULT 1,
    created_at          TIMESTAMPTZ NOT NULL DEFAULT NOW(),
    updated_at          TIMESTAMPTZ NOT NULL DEFAULT NOW(),
    CONSTRAINT ux_compra_extra_idem UNIQUE (id_estabelecimento, idempotency_key)
);
CREATE INDEX IF NOT EXISTS ix_compra_extra_loja ON compra_extra (id_estabelecimento, status, vigencia_fim);

CREATE TABLE IF NOT EXISTS comercial_cobranca (
    id                      UUID PRIMARY KEY,
    id_estabelecimento      UUID NOT NULL REFERENCES estabelecimentos (id) ON DELETE RESTRICT,
    tipo                    TEXT NOT NULL CHECK (tipo IN ('ciclo', 'upgrade', 'compra_extra')),
    id_assinatura           UUID NULL REFERENCES assinatura (id) ON DELETE RESTRICT,
    id_ciclo                UUID NULL REFERENCES assinatura_ciclo (id) ON DELETE RESTRICT,
    id_compra               UUID NULL REFERENCES compra_extra (id) ON DELETE RESTRICT,
    descricao               TEXT NOT NULL CHECK (length(descricao) BETWEEN 1 AND 140),
    valor                   NUMERIC(12, 2) NOT NULL CHECK (valor > 0),
    vencimento              TIMESTAMPTZ NOT NULL,
    -- aberta | processando (cartao recorrente com o Mercado Pago tentando) | paga | cancelada
    status                  TEXT NOT NULL DEFAULT 'aberta' CHECK (status IN ('aberta', 'processando', 'paga', 'cancelada')),
    meio                    TEXT NULL CHECK (meio IS NULL OR meio IN ('pix', 'cartao', 'cartao_recorrente', 'manual')),
    -- Pix/cartao vencido ou cancelado = nova intencao (nova chave): assinatura:{id}:{emissao}.
    emissao                 INTEGER NOT NULL DEFAULT 0,
    id_intent_atual         UUID NULL REFERENCES pagamento_intent (id) ON DELETE RESTRICT,
    mp_payment_id           BIGINT NULL,
    mp_authorized_payment_id BIGINT NULL,
    pago_em                 TIMESTAMPTZ NULL,
    -- mercadopago (Pix/cartao avulso) | mp_recorrente (preapproval) | manual (baixa da Gestao)
    pago_via                TEXT NULL CHECK (pago_via IS NULL OR pago_via IN ('mercadopago', 'mp_recorrente', 'manual')),
    valor_pago              NUMERIC(12, 2) NOT NULL DEFAULT 0,
    -- Pagamento que chegou depois de a cobranca ja estar paga/cancelada (Pix tardio, cartao duplicado): a Gestao devolve.
    valor_excedente         NUMERIC(12, 2) NOT NULL DEFAULT 0,
    detalhe                 JSONB NULL,
    cancelada_em            TIMESTAMPTZ NULL,
    cancelada_por           INTEGER NULL,
    cancelamento_motivo     TEXT NULL,
    idempotency_key         TEXT NOT NULL,
    versao                  INTEGER NOT NULL DEFAULT 1,
    created_at              TIMESTAMPTZ NOT NULL DEFAULT NOW(),
    updated_at              TIMESTAMPTZ NOT NULL DEFAULT NOW(),
    CONSTRAINT ux_comercial_cobranca_idem UNIQUE (id_estabelecimento, idempotency_key),
    CONSTRAINT ck_comercial_cobranca_ref CHECK (
        (tipo = 'ciclo' AND id_ciclo IS NOT NULL AND id_assinatura IS NOT NULL) OR
        (tipo = 'upgrade' AND id_assinatura IS NOT NULL) OR
        (tipo = 'compra_extra' AND id_compra IS NOT NULL))
);
-- Uma divida por ciclo; uma diferenca de upgrade aberta por assinatura; uma cobranca por compra.
CREATE UNIQUE INDEX IF NOT EXISTS ux_comercial_cobranca_ciclo ON comercial_cobranca (id_ciclo) WHERE tipo = 'ciclo' AND status <> 'cancelada';
CREATE UNIQUE INDEX IF NOT EXISTS ux_comercial_cobranca_upgrade ON comercial_cobranca (id_assinatura) WHERE tipo = 'upgrade' AND status IN ('aberta', 'processando');
CREATE UNIQUE INDEX IF NOT EXISTS ux_comercial_cobranca_compra ON comercial_cobranca (id_compra) WHERE tipo = 'compra_extra';
CREATE INDEX IF NOT EXISTS ix_comercial_cobranca_loja ON comercial_cobranca (id_estabelecimento, vencimento DESC);
CREATE INDEX IF NOT EXISTS ix_comercial_cobranca_abertas ON comercial_cobranca (vencimento) WHERE status IN ('aberta', 'processando');

CREATE TABLE IF NOT EXISTS solicitacao_compra (
    id                  UUID PRIMARY KEY,
    id_estabelecimento  UUID NOT NULL REFERENCES estabelecimentos (id) ON DELETE RESTRICT,
    produto_avulso_id   BIGINT NOT NULL REFERENCES produtos_avulsos (id) ON DELETE RESTRICT,
    quantidade          INTEGER NOT NULL CHECK (quantidade BETWEEN 1 AND 20),
    motivo              TEXT NULL CHECK (motivo IS NULL OR length(motivo) <= 300),
    solicitante_id      INTEGER NOT NULL,
    status              TEXT NOT NULL DEFAULT 'pendente' CHECK (status IN ('pendente', 'aprovada', 'recusada', 'cancelada')),
    decidido_por        INTEGER NULL,
    decidido_em         TIMESTAMPTZ NULL,
    decisao_motivo      TEXT NULL CHECK (decisao_motivo IS NULL OR length(decisao_motivo) <= 300),
    id_compra           UUID NULL REFERENCES compra_extra (id) ON DELETE RESTRICT,
    created_at          TIMESTAMPTZ NOT NULL DEFAULT NOW(),
    updated_at          TIMESTAMPTZ NOT NULL DEFAULT NOW()
);
-- O mesmo funcionario nao abre dois pedidos iguais pendentes (duplo clique).
CREATE UNIQUE INDEX IF NOT EXISTS ux_solicitacao_compra_pendente
    ON solicitacao_compra (id_estabelecimento, produto_avulso_id, solicitante_id) WHERE status = 'pendente';
CREATE INDEX IF NOT EXISTS ix_solicitacao_compra_loja ON solicitacao_compra (id_estabelecimento, status, created_at DESC);

CREATE TABLE IF NOT EXISTS comercial_aviso (
    id                  BIGSERIAL PRIMARY KEY,
    id_estabelecimento  UUID NOT NULL REFERENCES estabelecimentos (id) ON DELETE CASCADE,
    id_cobranca         UUID NULL REFERENCES comercial_cobranca (id) ON DELETE CASCADE,
    -- fatura_gerada | antes_vencimento | vencimento | atraso | antes_bloqueio | suspensa | reativada | renovacao_recusada | ...
    tipo                TEXT NOT NULL,
    -- Diferencia avisos do mesmo tipo (ex.: atraso:3, antes_vencimento:1). Unico por cobranca.
    chave               TEXT NOT NULL,
    mensagem            TEXT NOT NULL,
    criado_em           TIMESTAMPTZ NOT NULL DEFAULT NOW(),
    lido_em             TIMESTAMPTZ NULL
);
CREATE UNIQUE INDEX IF NOT EXISTS ux_comercial_aviso_chave ON comercial_aviso (id_estabelecimento, chave);
CREATE INDEX IF NOT EXISTS ix_comercial_aviso_loja ON comercial_aviso (id_estabelecimento, criado_em DESC);

-- Pacotes de motoboy vindos de compra avulsa: qualquer quantidade do produto (o lancamento manual da Gestao segue 3 ou 7 no codigo).
ALTER TABLE motoboy_pacotes ADD COLUMN IF NOT EXISTS id_compra_extra UUID NULL REFERENCES compra_extra (id) ON DELETE RESTRICT;
ALTER TABLE motoboy_pacotes DROP CONSTRAINT IF EXISTS motoboy_pacotes_quantidade_check;
DO $$
BEGIN
    IF NOT EXISTS (SELECT 1 FROM pg_constraint WHERE conname = 'ck_motoboy_pacotes_quantidade') THEN
        ALTER TABLE motoboy_pacotes ADD CONSTRAINT ck_motoboy_pacotes_quantidade CHECK (quantidade BETWEEN 1 AND 200);
    END IF;
END $$;
CREATE UNIQUE INDEX IF NOT EXISTS ux_motoboy_pacotes_compra ON motoboy_pacotes (id_compra_extra) WHERE id_compra_extra IS NOT NULL;

-- Leitura da Gestao (sessao 2). Colunas estaveis; acrescentar so no fim.
CREATE OR REPLACE VIEW vw_assinatura_resumo AS
SELECT a.id                         AS assinatura_id,
       a.id_estabelecimento,
       e.nome_fantasia              AS loja_nome,
       a.plano_chave,
       p.nome                       AS plano_nome,
       a.periodicidade,
       a.preco,
       a.status,
       a.financeiro,
       a.metodo,
       a.renovacao_automatica,
       a.periodo_inicio,
       a.periodo_fim,
       a.inadimplente_desde,
       a.suspensa_em,
       a.cancelamento_solicitado_em,
       a.mudanca_plano,
       prox.id                      AS proxima_cobranca_id,
       prox.vencimento              AS proxima_cobranca_vencimento,
       prox.valor                   AS proxima_cobranca_valor,
       COALESCE(ab.total, 0)::NUMERIC(12, 2) AS valor_em_aberto,
       COALESCE(ab.vencido, 0)::NUMERIC(12, 2) AS valor_vencido,
       COALESCE(pg.total, 0)::NUMERIC(12, 2) AS valor_pago_total,
       COALESCE(ab.excedente, 0)::NUMERIC(12, 2) AS valor_excedente,
       a.created_at,
       a.updated_at
  FROM assinatura a
  JOIN estabelecimentos e ON e.id = a.id_estabelecimento
  LEFT JOIN pacotes_comerciais p ON p.chave = a.plano_chave
  LEFT JOIN LATERAL (
        SELECT c.id, c.vencimento, c.valor FROM comercial_cobranca c
         WHERE c.id_assinatura = a.id AND c.status IN ('aberta', 'processando')
         ORDER BY c.vencimento LIMIT 1) prox ON TRUE
  LEFT JOIN LATERAL (
        SELECT SUM(c.valor) FILTER (WHERE c.status IN ('aberta', 'processando')) AS total,
               SUM(c.valor) FILTER (WHERE c.status IN ('aberta', 'processando') AND c.vencimento < NOW()) AS vencido,
               SUM(c.valor_excedente) AS excedente
          FROM comercial_cobranca c WHERE c.id_assinatura = a.id) ab ON TRUE
  LEFT JOIN LATERAL (
        SELECT SUM(c.valor_pago) AS total FROM comercial_cobranca c WHERE c.id_assinatura = a.id AND c.status = 'paga') pg ON TRUE;

INSERT INTO delivery_tracking_schema_versions (version)
VALUES ('20261101_40_assinaturas')
ON CONFLICT (version) DO NOTHING;

COMMIT;
