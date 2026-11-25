-- Verificacao somente leitura do webhook InfinitePay.
DO $$
BEGIN
    IF to_regclass('infinitepay_webhook_evento') IS NULL OR to_regclass('infinitepay_pendencia') IS NULL THEN
        RAISE EXCEPTION 'Faltam tabelas do webhook InfinitePay';
    END IF;
    IF EXISTS (SELECT 1 FROM infinitepay_webhook_evento e JOIN infinitepay_checkout c ON c.order_nsu = e.order_nsu
                WHERE c.id_intent <> e.id_intent OR c.id_estabelecimento <> e.id_estabelecimento) THEN
        RAISE EXCEPTION 'Aviso InfinitePay ligado a intencao ou loja diferente do checkout';
    END IF;
    IF EXISTS (SELECT 1 FROM infinitepay_checkout WHERE confirmado_em IS NOT NULL
                AND (transaction_nsu IS NULL OR invoice_slug IS NULL OR amount_confirmado_centavos IS DISTINCT FROM valor_centavos)) THEN
        RAISE EXCEPTION 'Checkout confirmado sem identificadores ou com valor diferente do cobrado';
    END IF;
    IF EXISTS (SELECT 1 FROM infinitepay_checkout c JOIN pagamento_intent i ON i.id = c.id_intent
                WHERE c.confirmado_em IS NOT NULL AND i.status NOT IN ('pago', 'estornado_parcial', 'estornado', 'contestado')) THEN
        RAISE EXCEPTION 'Checkout confirmado com intencao nao paga';
    END IF;
END $$;
