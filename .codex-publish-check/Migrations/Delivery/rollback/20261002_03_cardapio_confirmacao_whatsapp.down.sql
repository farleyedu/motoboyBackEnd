BEGIN;

-- Antes de reverter, aceite ou recuse os pre-pedidos que ainda estao em aguardando_codigo/aguardando_aceite:
-- sem as colunas novas o codigo antigo nao sabe tratar esses status.
UPDATE cardapio_pedido_publico SET status = 'pendente'
 WHERE status IN ('aguardando_codigo', 'aguardando_aceite', 'aceito', 'recusado', 'expirado');

DROP INDEX IF EXISTS ix_cardapio_pedido_publico_aguardando;
DROP INDEX IF EXISTS ux_cardapio_pedido_publico_codigo_ativo;

ALTER TABLE cardapio_pedido_publico DROP CONSTRAINT IF EXISTS ck_cardapio_pedido_publico_canal;
ALTER TABLE cardapio_pedido_publico DROP CONSTRAINT IF EXISTS ck_cardapio_pedido_publico_status;

ALTER TABLE cardapio_pedido_publico
    DROP COLUMN IF EXISTS id_pedido,
    DROP COLUMN IF EXISTS motivo_recusa,
    DROP COLUMN IF EXISTS recusado_em,
    DROP COLUMN IF EXISTS aceito_em,
    DROP COLUMN IF EXISTS confirmado_em,
    DROP COLUMN IF EXISTS id_conversa,
    DROP COLUMN IF EXISTS telefone_contato,
    DROP COLUMN IF EXISTS canal_confirmacao,
    DROP COLUMN IF EXISTS codigo_expira_em,
    DROP COLUMN IF EXISTS codigo_confirmacao;

DELETE FROM delivery_tracking_schema_versions WHERE version = '20261002_03_cardapio_confirmacao_whatsapp';

COMMIT;
