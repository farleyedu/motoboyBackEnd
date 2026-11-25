BEGIN;

-- So volta enquanto nenhuma loja conectou pela Meta: apagar as tabelas perderia o token de integracao e o vinculo dos numeros
-- (a loja teria de autorizar de novo). Com canais 'autorizado' em uso, desconecte-os pela API antes.
DO $$
BEGIN
    IF EXISTS (SELECT 1 FROM canal_whatsapp WHERE origem = 'autorizado') THEN
        RAISE EXCEPTION 'Ha canais com origem autorizado: desconecte-os antes de reverter 20261030_01_whatsapp_conexao_meta';
    END IF;
    IF EXISTS (SELECT 1 FROM wa_evento WHERE tipo IN ('conta', 'eco')) THEN
        RAISE EXCEPTION 'Ha eventos conta/eco em wa_evento: apague-os (ou aguarde a retencao) antes de reverter';
    END IF;
END $$;

ALTER TABLE wa_evento DROP CONSTRAINT IF EXISTS ck_wa_evento_tipo;
ALTER TABLE wa_evento ADD CONSTRAINT ck_wa_evento_tipo CHECK (tipo IN ('mensagem', 'status'));

ALTER TABLE canal_whatsapp DROP CONSTRAINT IF EXISTS ck_canal_whatsapp_registro_estado;
ALTER TABLE canal_whatsapp DROP CONSTRAINT IF EXISTS ck_canal_whatsapp_webhook_origem;
ALTER TABLE canal_whatsapp ADD CONSTRAINT ck_canal_whatsapp_webhook_origem
    CHECK (origem = 'proprio' OR (webhook_estado = 'nao_aplicavel' AND webhook_chave IS NULL));
ALTER TABLE canal_whatsapp DROP CONSTRAINT IF EXISTS ck_canal_whatsapp_origem;
ALTER TABLE canal_whatsapp ADD CONSTRAINT ck_canal_whatsapp_origem CHECK (origem IN ('zippy', 'proprio', 'central'));

DROP INDEX IF EXISTS ix_canal_whatsapp_waba;
DROP INDEX IF EXISTS ix_canal_whatsapp_autorizacao;
ALTER TABLE canal_whatsapp DROP COLUMN IF EXISTS desconectado_em;
ALTER TABLE canal_whatsapp DROP COLUMN IF EXISTS coexistencia_sincronizada_em;
ALTER TABLE canal_whatsapp DROP COLUMN IF EXISTS inscricao_waba_em;
ALTER TABLE canal_whatsapp DROP COLUMN IF EXISTS estado_meta;
ALTER TABLE canal_whatsapp DROP COLUMN IF EXISTS registro_tentado_em;
ALTER TABLE canal_whatsapp DROP COLUMN IF EXISTS registro_tentativas;
ALTER TABLE canal_whatsapp DROP COLUMN IF EXISTS registro_pin_cifrado;
ALTER TABLE canal_whatsapp DROP COLUMN IF EXISTS registro_estado;
ALTER TABLE canal_whatsapp DROP COLUMN IF EXISTS coexistencia;
ALTER TABLE canal_whatsapp DROP COLUMN IF EXISTS id_autorizacao_meta;

DROP TABLE IF EXISTS whatsapp_conexao_tentativa;
DROP TABLE IF EXISTS whatsapp_autorizacao_meta;

DELETE FROM delivery_tracking_schema_versions WHERE version = '20261030_01_whatsapp_conexao_meta';
COMMIT;
