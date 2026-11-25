BEGIN;

-- ---------------------------------------------------------------------------
-- Mercado Pago: qual ambiente (sandbox | producao) o servidor usa passa a ser escolhido na Gestao, sem redeploy.
--   mp_ambiente_ativo  linha unica (id = 1). Sem linha: vale Payments:MercadoPago:Ambiente da configuracao.
-- As credenciais dos dois ambientes continuam em mp_aplicacao / mp_conta_zippy (uma linha por ambiente).
-- Requer 20261031_01 (nucleo Mercado Pago). Aditiva e idempotente; rollback em rollback/20261103_70_mercadopago_ambiente_ativo.down.sql.
-- ---------------------------------------------------------------------------

CREATE TABLE IF NOT EXISTS mp_ambiente_ativo (
    id              SMALLINT PRIMARY KEY DEFAULT 1 CHECK (id = 1),
    ambiente        TEXT NOT NULL CHECK (ambiente IN ('sandbox', 'producao')),
    atualizado_por  INTEGER NULL,
    updated_at      TIMESTAMPTZ NOT NULL DEFAULT NOW()
);

COMMIT;
