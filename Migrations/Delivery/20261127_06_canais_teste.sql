-- Multiplos numeros de teste da Zippy. Cada numero de teste e um canal_whatsapp com teste = TRUE e atende UMA loja por vez
-- (id_estabelecimento NOT NULL = destino obrigatorio); "central" passa a significar PRINCIPAL (no maximo um, indice parcial
-- ux_canal_whatsapp_central da 20261020_01 mantido, entao o contrato antigo continua valendo). Numero e Phone Number ID seguem unicos
-- pelos indices de 20261002_07. Preserva id, token, destino e auditoria do central atual; nada e apagado. Aditiva e idempotente.
-- finalidade: 'geral' (loja de teste comum) ou 'gestao' (destino = a loja real de Gestao, escolhida pelo operador por ID);
-- o ingressor da fila (07) le esta coluna, a logica de roteamento nao fica aqui.
BEGIN;

ALTER TABLE canal_whatsapp ADD COLUMN IF NOT EXISTS teste BOOLEAN NOT NULL DEFAULT FALSE;
ALTER TABLE canal_whatsapp ADD COLUMN IF NOT EXISTS finalidade_teste TEXT NOT NULL DEFAULT 'geral';

-- O central atual (ou qualquer canal de origem central) vira numero de teste. O principal continua sendo o mesmo registro.
UPDATE canal_whatsapp SET teste = TRUE WHERE (central OR origem = 'central') AND NOT teste;

DO $$
BEGIN
    IF NOT EXISTS (SELECT 1 FROM pg_constraint WHERE conname = 'ck_canal_whatsapp_finalidade_teste' AND conrelid = 'canal_whatsapp'::regclass) THEN
        ALTER TABLE canal_whatsapp ADD CONSTRAINT ck_canal_whatsapp_finalidade_teste CHECK (finalidade_teste IN ('geral', 'gestao'));
    END IF;
    -- Principal so existe entre os numeros de teste.
    IF NOT EXISTS (SELECT 1 FROM pg_constraint WHERE conname = 'ck_canal_whatsapp_central_teste' AND conrelid = 'canal_whatsapp'::regclass) THEN
        ALTER TABLE canal_whatsapp ADD CONSTRAINT ck_canal_whatsapp_central_teste CHECK (NOT central OR teste);
    END IF;
    -- Numero de teste nunca e numero de cliente (proprio/autorizado).
    IF NOT EXISTS (SELECT 1 FROM pg_constraint WHERE conname = 'ck_canal_whatsapp_teste_origem' AND conrelid = 'canal_whatsapp'::regclass) THEN
        ALTER TABLE canal_whatsapp ADD CONSTRAINT ck_canal_whatsapp_teste_origem CHECK (NOT teste OR origem IN ('central', 'zippy'));
    END IF;
END $$;

CREATE INDEX IF NOT EXISTS ix_canal_whatsapp_teste ON canal_whatsapp (created_at_utc) WHERE teste;

COMMIT;
