BEGIN;

-- Desfaz 20261101_30. Pedidos online em andamento voltam a ser presenciais: confira antes se nao ha pagamento aberto.
DROP TABLE IF EXISTS cardapio_pedido_reembolso;

DROP INDEX IF EXISTS ix_cardapio_pedido_publico_pago_sem_confirmar;
DROP INDEX IF EXISTS ux_cardapio_pedido_publico_chave;
DROP INDEX IF EXISTS ix_cardapio_pedido_publico_aguardando;
CREATE INDEX IF NOT EXISTS ix_cardapio_pedido_publico_aguardando
    ON cardapio_pedido_publico (id_estabelecimento, confirmado_em)
    WHERE status = 'aguardando_aceite';

UPDATE cardapio_pedido_publico SET status = 'aguardando_aceite' WHERE status IN ('aguardando_pagamento', 'aceitando');
UPDATE cardapio_pedido_publico SET status = 'recusado' WHERE status = 'cancelado';

ALTER TABLE cardapio_pedido_publico DROP CONSTRAINT IF EXISTS ck_cardapio_pedido_publico_metodo_online;
ALTER TABLE cardapio_pedido_publico DROP CONSTRAINT IF EXISTS ck_cardapio_pedido_publico_status;
ALTER TABLE cardapio_pedido_publico
    ADD CONSTRAINT ck_cardapio_pedido_publico_status CHECK (status IN (
        'pendente', 'aguardando_codigo', 'aguardando_aceite', 'aceito', 'recusado', 'expirado'));

ALTER TABLE cardapio_pedido_publico
    DROP COLUMN IF EXISTS motivo_cancelamento,
    DROP COLUMN IF EXISTS cancelado_em,
    DROP COLUMN IF EXISTS pago_em,
    DROP COLUMN IF EXISTS id_intent_pago,
    DROP COLUMN IF EXISTS id_intent_atual,
    DROP COLUMN IF EXISTS chave_idempotencia,
    DROP COLUMN IF EXISTS metodo_online,
    DROP COLUMN IF EXISTS pagamento_online;

DELETE FROM delivery_tracking_schema_versions WHERE version = '20261101_30_cardapio_pagamento_online';

COMMIT;
