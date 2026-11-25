-- Gestao, Caixa: pagamento antes da entrega entra no livro no ato, como adiantamento de cliente.
-- Conta recebivel "Adiantamentos de clientes" (subtipo novo): fica negativa enquanto o pedido nao conclui. A venda tira o valor dela na
-- conclusao; o cancelamento estorna. A conta e criada pelo servico na primeira vez que a loja recebe antes de entregar.
-- Pedidos ja pagos antes desta migracao seguem a regra antiga na conclusao (nao ha adiantamento a abater).
-- Requer 20261121_01_gestao_caixa_acertos.sql. Aditiva e idempotente.
BEGIN;

ALTER TABLE gestao_contas DROP CONSTRAINT IF EXISTS gestao_contas_subtipo_check;
ALTER TABLE gestao_contas ADD CONSTRAINT gestao_contas_subtipo_check
    CHECK (subtipo IN ('ifood', 'maquininha', 'motoboys', 'adiantamentos', 'outro'));

COMMIT;
