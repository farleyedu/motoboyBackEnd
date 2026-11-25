-- Verificacao somente leitura do checkout InfinitePay.
DO $$
BEGIN
    IF to_regclass('infinitepay_checkout') IS NULL THEN
        RAISE EXCEPTION 'Falta a tabela infinitepay_checkout';
    END IF;
    IF NOT EXISTS (SELECT 1 FROM pg_constraint WHERE conname = 'fk_infinitepay_checkout_versao_loja') THEN
        RAISE EXCEPTION 'Falta isolamento de loja no checkout InfinitePay';
    END IF;
    IF EXISTS (SELECT 1 FROM infinitepay_checkout c JOIN pagamento_intent i ON i.id = c.id_intent
                WHERE i.provedor <> 'infinitepay' OR i.id_estabelecimento <> c.id_estabelecimento
                   OR i.id_infinitepay_conta_versao IS DISTINCT FROM c.id_conta_versao OR i.external_reference <> c.order_nsu) THEN
        RAISE EXCEPTION 'Checkout InfinitePay incoerente com a intencao';
    END IF;
END $$;
