-- Acompanhamento: saude (docs/crm-inteligente/01-backend-base-crm.md, zippy-admin, regras de saude e fim do teste).
--   * acompanhamento_saude_regra: os limites do calculo, um por chave. Valor nulo = regra desligada (so escalonar_sem_pagamento_dias,
--     que nasce desligada ate o proprietario definir N; nenhum valor e assumido).
--   * acompanhamento_saude_regra_versao: cada mudanca de limite (antes/depois, quem, por que). A versao das regras e o id mais recente.
--   * acompanhamento: motivos e versoes da ultima faixa gravada pelo worker (o pausa_ate e a fase 'pausa' ja existem na migracao _01
--     e eliminam o falso positivo por inatividade).
--   * acompanhamento_teste_aviso: marcos do fim do teste (faltam_4 ... faltam_0, terminou, escalonar), um por teste, sem repetir.
-- Aditiva e idempotente (roda a cada boot). Nao toca em tabelas de pagamento.
BEGIN;

CREATE TABLE IF NOT EXISTS acompanhamento_saude_regra (
    chave          TEXT PRIMARY KEY,
    valor          INTEGER NULL CHECK (valor IS NULL OR valor >= 0),
    atualizado_em  TIMESTAMPTZ NOT NULL DEFAULT NOW(),
    atualizado_por INTEGER NULL
);

CREATE TABLE IF NOT EXISTS acompanhamento_saude_regra_versao (
    id         SERIAL PRIMARY KEY,
    em         TIMESTAMPTZ NOT NULL DEFAULT NOW(),
    usuario_id INTEGER NULL,
    motivo     TEXT NULL,
    antes      JSONB NOT NULL DEFAULT '{}'::jsonb,
    depois     JSONB NOT NULL DEFAULT '{}'::jsonb
);

-- Padroes do documento. DO NOTHING: nunca sobrescreve o que a equipe ja mudou.
INSERT INTO acompanhamento_saude_regra (chave, valor) VALUES
    ('queda_percentual', 40),
    ('queda_base_semanal_minima', 10),
    ('dias_sem_pedido', 7),
    ('vencimento_dias', 10),
    ('resposta_pendente_dias', 1),
    ('dias_sem_passo', 5),
    ('implantacao_travada_dias', 14),
    ('proposta_vence_em_dias', 3),
    ('teste_uso_minimo_pedidos', 10),
    ('teste_uso_apos_dias', 7),
    ('teste_atencao_ate_dias', 4),
    ('teste_ajuda_ate_dias', 2),
    ('escalonar_sem_pagamento_dias', NULL)
ON CONFLICT (chave) DO NOTHING;

DO $$
BEGIN
    IF to_regclass('acompanhamento') IS NOT NULL THEN
        ALTER TABLE acompanhamento ADD COLUMN IF NOT EXISTS faixa_motivos JSONB NOT NULL DEFAULT '[]'::jsonb;
        ALTER TABLE acompanhamento ADD COLUMN IF NOT EXISTS faixa_calculo INTEGER NULL;
        ALTER TABLE acompanhamento ADD COLUMN IF NOT EXISTS faixa_limites INTEGER NULL;
    END IF;
END $$;

CREATE TABLE IF NOT EXISTS acompanhamento_teste_aviso (
    id                 BIGSERIAL PRIMARY KEY,
    estabelecimento_id UUID NOT NULL,
    teste_termina_em   DATE NOT NULL,
    marco              TEXT NOT NULL,
    faixa              TEXT NOT NULL CHECK (faixa IN ('bem', 'atencao', 'precisa_ajuda')),
    texto              TEXT NOT NULL,
    criado_em          TIMESTAMPTZ NOT NULL DEFAULT NOW(),
    CONSTRAINT ux_acompanhamento_teste_aviso UNIQUE (estabelecimento_id, teste_termina_em, marco)
);

DO $$
BEGIN
    IF to_regclass('estabelecimentos') IS NOT NULL
       AND NOT EXISTS (SELECT 1 FROM pg_constraint WHERE conname = 'fk_acompanhamento_teste_aviso_loja') THEN
        ALTER TABLE acompanhamento_teste_aviso ADD CONSTRAINT fk_acompanhamento_teste_aviso_loja
            FOREIGN KEY (estabelecimento_id) REFERENCES estabelecimentos (id) ON DELETE CASCADE;
    END IF;
END $$;

COMMIT;
