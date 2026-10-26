BEGIN;

-- Desfaz 20261101_50_cobranca_chat_atendente. Cobrancas em 'aguardando_aprovacao' viram 'cancelado' para o CHECK antigo voltar.
DROP TABLE IF EXISTS cobranca_chat_tarefa;
DROP TABLE IF EXISTS cobranca_chat_aprovacao;
DROP TABLE IF EXISTS cobranca_chat_config;

DROP INDEX IF EXISTS ix_cobranca_chat_pedido;
DROP INDEX IF EXISTS ix_cobranca_chat_reemitida;
DROP INDEX IF EXISTS ix_cobranca_chat_pendentes;
DROP INDEX IF EXISTS ux_cobranca_chat_aberta_pre_pedido_loja;
DROP INDEX IF EXISTS ux_cobranca_chat_aberta_pedido_loja;

UPDATE cobranca_chat SET status = 'cancelado', cancelado_em = COALESCE(cancelado_em, NOW()) WHERE status = 'aguardando_aprovacao';
ALTER TABLE cobranca_chat DROP CONSTRAINT IF EXISTS ck_cobranca_chat_status;
ALTER TABLE cobranca_chat ADD CONSTRAINT cobranca_chat_status_check CHECK (status IN (
    'criando', 'aguardando', 'enviado', 'pago', 'expirado', 'cancelado', 'reembolso_solicitado', 'reembolsado', 'recusado'));
ALTER TABLE cobranca_chat DROP CONSTRAINT IF EXISTS ck_cobranca_chat_moeda;
ALTER TABLE cobranca_chat
    DROP COLUMN IF EXISTS ultimo_erro,
    DROP COLUMN IF EXISTS link_token_cifrado,
    DROP COLUMN IF EXISTS cliente_id,
    DROP COLUMN IF EXISTS moeda;

COMMIT;
