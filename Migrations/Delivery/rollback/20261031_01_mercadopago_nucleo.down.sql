-- Reverte 20261031_01_mercadopago_nucleo. So volta enquanto nenhum pagamento real passou pelo MP: apagar as tabelas perderia o
-- historico financeiro e os tokens das lojas (elas teriam de conectar de novo).
BEGIN;

DO $$
BEGIN
    IF EXISTS (SELECT 1 FROM pagamento WHERE ambiente = 'producao') THEN
        RAISE EXCEPTION 'Ha pagamentos de producao: nao reverta 20261031_01_mercadopago_nucleo';
    END IF;
END $$;

DROP TABLE IF EXISTS pagamento_outbox;
DROP TRIGGER IF EXISTS tg_pagamento_auditoria_imutavel ON pagamento_auditoria;
DROP TABLE IF EXISTS pagamento_auditoria;
DROP FUNCTION IF EXISTS fn_pagamento_auditoria_imutavel();
DROP TABLE IF EXISTS mp_webhook_evento;
DROP TABLE IF EXISTS pagamento_disputa;
DROP TABLE IF EXISTS pagamento_estorno;
DROP FUNCTION IF EXISTS fn_pagamento_estorno_limite();
DROP TABLE IF EXISTS cobranca_chat_evento;
DROP TABLE IF EXISTS cobranca_chat;
DROP TABLE IF EXISTS pagamento;
DROP TABLE IF EXISTS pagamento_tentativa;
DROP TABLE IF EXISTS pagamento_intent;
DROP TABLE IF EXISTS pagamento_preferencia_loja;
DROP TABLE IF EXISTS mp_oauth_tentativa;
DROP TABLE IF EXISTS mp_conta_loja;
DROP TABLE IF EXISTS mp_conta_zippy;
DROP TABLE IF EXISTS mp_aplicacao;

DELETE FROM delivery_tracking_schema_versions WHERE version = '20261031_01_mercadopago_nucleo';

COMMIT;
