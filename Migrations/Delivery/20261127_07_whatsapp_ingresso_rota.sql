-- Ingresso imutavel da mensagem do WhatsApp (docs/crm-inteligente/02-backend-whatsapp-ia.md, zippy-admin, A3).
-- O destino (canal + loja) passa a ser capturado no INGRESSO e gravado no proprio wa_evento; o worker so confere e nunca recalcula
-- pelo phone_number_id. Assim, um numero de teste que troca de loja (A -> B) com a fila pendente nunca entrega a mensagem de A em B.
--   rota: 'operacional' (restaurante: fluxo de sempre), 'gestao' (numero de teste com finalidade gestao) ou 'sem_canal'
--         (nenhum numero ativo tinha este Phone Number ID quando a mensagem chegou).
--   rota_snapshot: o canal naquele instante (origem, teste, finalidade, principal, WABA, Phone Number ID).
--   remetente/tipo_mensagem/midia_*/mensagem_em: o minimo para rastrear e para a Parte B achar a midia sem reler o payload.
--   categoria_remetente: so na loja Gestao (funcionario_verificado, contato_crm, desconhecido, ambiguo), gravada ao processar.
--   estado 'bloqueado': a rota falhou na conferencia de seguranca (destino alterado, acesso revogado, WABA errada...). Nao e
--         "processado": fica com rota_codigo (403/409/422) e motivo para diagnostico e reprocessamento manual.
-- Aditiva e idempotente (roda a cada boot). Eventos pendentes antigos recebem o destino atual do numero (o mesmo que o worker
-- antigo usaria), uma unica vez.
BEGIN;

ALTER TABLE wa_evento ADD COLUMN IF NOT EXISTS rota TEXT NULL;
ALTER TABLE wa_evento ADD COLUMN IF NOT EXISTS rota_capturada_em TIMESTAMPTZ NULL;
ALTER TABLE wa_evento ADD COLUMN IF NOT EXISTS id_canal_destino UUID NULL;
ALTER TABLE wa_evento ADD COLUMN IF NOT EXISTS id_estabelecimento_destino UUID NULL;
ALTER TABLE wa_evento ADD COLUMN IF NOT EXISTS rota_snapshot JSONB NULL;
ALTER TABLE wa_evento ADD COLUMN IF NOT EXISTS remetente TEXT NULL;
ALTER TABLE wa_evento ADD COLUMN IF NOT EXISTS tipo_mensagem TEXT NULL;
ALTER TABLE wa_evento ADD COLUMN IF NOT EXISTS midia_id TEXT NULL;
ALTER TABLE wa_evento ADD COLUMN IF NOT EXISTS midia_mime TEXT NULL;
ALTER TABLE wa_evento ADD COLUMN IF NOT EXISTS mensagem_em TIMESTAMPTZ NULL;
ALTER TABLE wa_evento ADD COLUMN IF NOT EXISTS categoria_remetente TEXT NULL;
ALTER TABLE wa_evento ADD COLUMN IF NOT EXISTS rota_codigo INTEGER NULL;

DO $$
DECLARE
    restricao TEXT;
BEGIN
    IF NOT EXISTS (SELECT 1 FROM pg_constraint WHERE conname = 'ck_wa_evento_rota' AND conrelid = 'wa_evento'::regclass) THEN
        ALTER TABLE wa_evento ADD CONSTRAINT ck_wa_evento_rota CHECK (rota IS NULL OR rota IN ('operacional', 'gestao', 'sem_canal'));
    END IF;
    -- Rota capturada com canal sempre tem a loja junto (nunca meio destino).
    IF NOT EXISTS (SELECT 1 FROM pg_constraint WHERE conname = 'ck_wa_evento_destino' AND conrelid = 'wa_evento'::regclass) THEN
        ALTER TABLE wa_evento ADD CONSTRAINT ck_wa_evento_destino CHECK (
            rota IS NULL OR rota = 'sem_canal'
            OR (id_canal_destino IS NOT NULL AND id_estabelecimento_destino IS NOT NULL AND rota_capturada_em IS NOT NULL));
    END IF;
    IF NOT EXISTS (SELECT 1 FROM pg_constraint WHERE conname = 'ck_wa_evento_categoria' AND conrelid = 'wa_evento'::regclass) THEN
        ALTER TABLE wa_evento ADD CONSTRAINT ck_wa_evento_categoria CHECK (
            categoria_remetente IS NULL OR categoria_remetente IN ('funcionario_verificado', 'contato_crm', 'desconhecido', 'ambiguo'));
    END IF;

    -- estado: o CHECK foi criado sem nome em 20261002_09; troca pelo nomeado com 'bloqueado'.
    FOR restricao IN
        SELECT conname FROM pg_constraint
         WHERE conrelid = 'wa_evento'::regclass AND contype = 'c' AND pg_get_constraintdef(oid) LIKE '%estado%'
           AND conname <> 'ck_wa_evento_estado'
    LOOP
        EXECUTE format('ALTER TABLE wa_evento DROP CONSTRAINT %I', restricao);
    END LOOP;
    IF NOT EXISTS (SELECT 1 FROM pg_constraint WHERE conname = 'ck_wa_evento_estado' AND conrelid = 'wa_evento'::regclass) THEN
        ALTER TABLE wa_evento ADD CONSTRAINT ck_wa_evento_estado
            CHECK (estado IN ('pendente', 'processando', 'processado', 'ignorado', 'erro', 'bloqueado'));
    END IF;
END $$;

-- Pendentes de antes desta versao: destino = dono atual do numero (o que o worker antigo usaria), capturado agora e nunca mais.
UPDATE wa_evento e
   SET rota = CASE WHEN c.id IS NULL THEN 'sem_canal'
                   WHEN (c.teste OR c.central) AND c.finalidade_teste = 'gestao' THEN 'gestao'
                   ELSE 'operacional' END,
       rota_capturada_em = NOW(),
       id_canal_destino = c.id,
       id_estabelecimento_destino = c.id_estabelecimento,
       rota_snapshot = CASE WHEN c.id IS NULL THEN jsonb_build_object('backfill', TRUE)
                            ELSE jsonb_build_object('backfill', TRUE, 'origem', c.origem, 'teste', c.teste OR c.central,
                                                    'finalidade', c.finalidade_teste, 'principal', c.central,
                                                    'waba', c.waba_id, 'phone_number_id', c.phone_number_id) END,
       remetente = COALESCE(e.remetente, e.payload->'message'->>'from'),
       tipo_mensagem = COALESCE(e.tipo_mensagem, e.payload->'message'->>'type')
  FROM wa_evento alvo
  LEFT JOIN canal_whatsapp c ON c.phone_number_id = alvo.phone_number_id AND c.status <> 'inativo'
 WHERE e.id = alvo.id
   AND e.tipo = 'mensagem' AND e.estado IN ('pendente', 'processando') AND e.rota IS NULL;

-- Diagnostico por loja de destino e por mensagem bloqueada.
CREATE INDEX IF NOT EXISTS ix_wa_evento_destino ON wa_evento (id_estabelecimento_destino, recebido_em DESC) WHERE tipo = 'mensagem';
CREATE INDEX IF NOT EXISTS ix_wa_evento_bloqueado ON wa_evento (recebido_em DESC) WHERE estado = 'bloqueado';

COMMIT;
