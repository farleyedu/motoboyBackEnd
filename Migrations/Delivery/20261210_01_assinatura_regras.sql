-- Regras da assinatura (26/11/2026): nada bloqueia sozinho, dívida na volta, tentativas que expiram e ações da Gestão.
--   * assinatura.ativada_em        -> quando o plano passou a valer (receita nova e perdida no Dinheiro).
--   * comercial_cobranca.expira_em -> tentativa de upgrade ou extra (Pix de 1 hora). Nulo = nunca expira sozinha (inclusive as antigas).
--   * divida_valor/divida_desde    -> parte de dívida somada na cobrança de quem voltou a assinar. agrupada_em aponta para ela.
--   * excedente_*                  -> pagamento em dobro e quando a Gestão devolveu.
--   * compra_extra.cortesia/dias   -> extra dado pela Gestão e prazo escolhido por ela.
--   * Fim da suspensão automática: 'suspensa' vira 'em_atraso' (o valor antigo fica em assinatura_suspensa_antes_20261210).
-- Aditiva e idempotente. Aplicar ANTES de publicar o servidor. Rollback em rollback/20261210_01_assinatura_regras.down.sql.
BEGIN;

DO $$
BEGIN
    IF to_regclass('assinatura') IS NULL OR to_regclass('comercial_cobranca') IS NULL THEN
        RAISE EXCEPTION 'assinatura_regras requer 20261101_40_assinaturas';
    END IF;
END $$;

ALTER TABLE assinatura ADD COLUMN IF NOT EXISTS ativada_em TIMESTAMPTZ NULL;

ALTER TABLE comercial_cobranca ADD COLUMN IF NOT EXISTS expira_em TIMESTAMPTZ NULL;
ALTER TABLE comercial_cobranca ADD COLUMN IF NOT EXISTS divida_valor NUMERIC(12, 2) NOT NULL DEFAULT 0;
ALTER TABLE comercial_cobranca ADD COLUMN IF NOT EXISTS divida_desde TIMESTAMPTZ NULL;
ALTER TABLE comercial_cobranca ADD COLUMN IF NOT EXISTS agrupada_em UUID NULL;
ALTER TABLE comercial_cobranca ADD COLUMN IF NOT EXISTS excedente_em TIMESTAMPTZ NULL;
ALTER TABLE comercial_cobranca ADD COLUMN IF NOT EXISTS excedente_devolvido NUMERIC(12, 2) NOT NULL DEFAULT 0;
ALTER TABLE comercial_cobranca ADD COLUMN IF NOT EXISTS excedente_devolvido_em TIMESTAMPTZ NULL;
ALTER TABLE comercial_cobranca ADD COLUMN IF NOT EXISTS excedente_devolvido_por INTEGER NULL;
CREATE INDEX IF NOT EXISTS ix_comercial_cobranca_agrupada ON comercial_cobranca (agrupada_em) WHERE agrupada_em IS NOT NULL;
CREATE INDEX IF NOT EXISTS ix_comercial_cobranca_expira ON comercial_cobranca (expira_em) WHERE expira_em IS NOT NULL AND status IN ('aberta', 'processando');

ALTER TABLE compra_extra ADD COLUMN IF NOT EXISTS cortesia BOOLEAN NOT NULL DEFAULT FALSE;
ALTER TABLE compra_extra ADD COLUMN IF NOT EXISTS dias INTEGER NULL;
-- Cortesia da Gestão vale R$ 0: os preços passam a aceitar zero (a compra paga continua conferida no código).
ALTER TABLE compra_extra DROP CONSTRAINT IF EXISTS compra_extra_preco_unitario_check;
ALTER TABLE compra_extra DROP CONSTRAINT IF EXISTS compra_extra_valor_total_check;
DO $$
BEGIN
    IF NOT EXISTS (SELECT 1 FROM pg_constraint WHERE conname = 'ck_compra_extra_valores' AND conrelid = 'compra_extra'::regclass) THEN
        ALTER TABLE compra_extra ADD CONSTRAINT ck_compra_extra_valores
            CHECK (preco_unitario >= 0 AND valor_total >= 0 AND (cortesia OR (preco_unitario > 0 AND valor_total > 0)));
    END IF;
END $$;

-- Quem já pagou (ou está ativa) passou a valer no primeiro pagamento; sem pagamento registrado, na contratação.
UPDATE assinatura a
   SET ativada_em = COALESCE((SELECT MIN(c.pago_em) FROM comercial_cobranca c WHERE c.id_assinatura = a.id AND c.status = 'paga'), a.created_at)
 WHERE a.ativada_em IS NULL
   AND (a.status = 'ativa' OR EXISTS (SELECT 1 FROM comercial_cobranca c WHERE c.id_assinatura = a.id AND c.status = 'paga'));

CREATE TABLE IF NOT EXISTS assinatura_suspensa_antes_20261210 (
    id_assinatura UUID PRIMARY KEY,
    suspensa_em   TIMESTAMPTZ NULL
);
INSERT INTO assinatura_suspensa_antes_20261210 (id_assinatura, suspensa_em)
SELECT id, suspensa_em FROM assinatura WHERE financeiro = 'suspensa'
ON CONFLICT (id_assinatura) DO NOTHING;
UPDATE assinatura SET financeiro = 'em_atraso', suspensa_em = NULL, versao = versao + 1, updated_at = NOW() WHERE financeiro = 'suspensa';

-- Avisos antigos que ameaçavam suspender deixam de ameaçar (e ficam como lidos).
UPDATE comercial_aviso
   SET mensagem = 'Há um pagamento em aberto. Confira em Assinatura.', lido_em = COALESCE(lido_em, NOW())
 WHERE (tipo IN ('suspensa', 'antes_bloqueio', 'vencimento', 'atraso')
        AND (mensagem ILIKE '%suspen%' OR mensagem ILIKE '%param em%'));
UPDATE comercial_aviso SET mensagem = 'Sua assinatura terminou. O histórico continua aqui; assine de novo quando quiser.'
 WHERE tipo = 'encerrada' AND mensagem ILIKE '%contrate um plano para voltar%';

INSERT INTO delivery_tracking_schema_versions (version)
VALUES ('20261210_01_assinatura_regras')
ON CONFLICT (version) DO NOTHING;

COMMIT;
