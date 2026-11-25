BEGIN;

-- A migration 20261005_03 expandiu os avisos, mas esqueceu o tipo usado pelo
-- fechamento automatico de conversas quando o pedido e entregue. Como a
-- migration anterior pode ja estar registrada como aplicada, esta correcao e
-- idempotente e garante o constraint correto nos ambientes existentes.
ALTER TABLE pedido_notificacao DROP CONSTRAINT IF EXISTS pedido_notificacao_tipo_check;
ALTER TABLE pedido_notificacao
    ADD CONSTRAINT pedido_notificacao_tipo_check
    CHECK (tipo IN ('saiu_da_loja', 'motoboy_chegando', 'pedido_enviado_loja', 'pedido_confirmado_loja', 'chegou',
                    'confirmacao_atendente', 'atendimento_fechado_entrega'));

INSERT INTO delivery_tracking_schema_versions (version)
VALUES ('20261006_06_corrige_tipo_notificacao_fechamento')
ON CONFLICT (version) DO NOTHING;

COMMIT;
