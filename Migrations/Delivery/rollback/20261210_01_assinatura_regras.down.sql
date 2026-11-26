-- Desfaz a 20261210_01_assinatura_regras. Voltar o servidor ANTES de rodar.
-- Perde: datas de expiração, vínculo das dívidas agrupadas, devoluções registradas e a marca de cortesia.
-- Os avisos reescritos não voltam ao texto antigo. Se já houver extra de cortesia a R$ 0, a regra antiga de preço não é recriada.
BEGIN;

DO $$
BEGIN
    IF to_regclass('assinatura_suspensa_antes_20261210') IS NOT NULL THEN
        UPDATE assinatura a SET financeiro = 'suspensa', suspensa_em = s.suspensa_em
          FROM assinatura_suspensa_antes_20261210 s WHERE s.id_assinatura = a.id AND a.financeiro = 'em_atraso';
        DROP TABLE assinatura_suspensa_antes_20261210;
    END IF;
END $$;

ALTER TABLE compra_extra DROP CONSTRAINT IF EXISTS ck_compra_extra_valores;
DO $$
BEGIN
    IF NOT EXISTS (SELECT 1 FROM compra_extra WHERE preco_unitario <= 0 OR valor_total <= 0) THEN
        ALTER TABLE compra_extra ADD CONSTRAINT compra_extra_preco_unitario_check CHECK (preco_unitario > 0);
        ALTER TABLE compra_extra ADD CONSTRAINT compra_extra_valor_total_check CHECK (valor_total > 0);
    END IF;
END $$;
ALTER TABLE compra_extra DROP COLUMN IF EXISTS cortesia;
ALTER TABLE compra_extra DROP COLUMN IF EXISTS dias;

DROP INDEX IF EXISTS ix_comercial_cobranca_agrupada;
DROP INDEX IF EXISTS ix_comercial_cobranca_expira;
ALTER TABLE comercial_cobranca DROP COLUMN IF EXISTS expira_em;
ALTER TABLE comercial_cobranca DROP COLUMN IF EXISTS divida_valor;
ALTER TABLE comercial_cobranca DROP COLUMN IF EXISTS divida_desde;
ALTER TABLE comercial_cobranca DROP COLUMN IF EXISTS agrupada_em;
ALTER TABLE comercial_cobranca DROP COLUMN IF EXISTS excedente_em;
ALTER TABLE comercial_cobranca DROP COLUMN IF EXISTS excedente_devolvido;
ALTER TABLE comercial_cobranca DROP COLUMN IF EXISTS excedente_devolvido_em;
ALTER TABLE comercial_cobranca DROP COLUMN IF EXISTS excedente_devolvido_por;
ALTER TABLE assinatura DROP COLUMN IF EXISTS ativada_em;

DELETE FROM delivery_tracking_schema_versions WHERE version = '20261210_01_assinatura_regras';

COMMIT;
