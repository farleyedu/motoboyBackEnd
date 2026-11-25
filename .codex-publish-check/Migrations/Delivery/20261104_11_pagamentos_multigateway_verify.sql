-- Verificacao somente leitura da base multi-gateway.
DO $$
DECLARE
    tabela TEXT;
BEGIN
    FOREACH tabela IN ARRAY ARRAY['pagamento_provedor_loja', 'pagamento_preferencia_provedor',
                                  'infinitepay_conta_loja', 'infinitepay_conta_versao'] LOOP
        IF to_regclass(tabela) IS NULL THEN
            RAISE EXCEPTION 'Falta a tabela %', tabela;
        END IF;
    END LOOP;

    IF NOT EXISTS (SELECT 1 FROM information_schema.columns WHERE table_name = 'pagamento_intent' AND column_name = 'provedor')
       OR NOT EXISTS (SELECT 1 FROM information_schema.columns WHERE table_name = 'pagamento_intent' AND column_name = 'id_infinitepay_conta_versao') THEN
        RAISE EXCEPTION 'Falta o snapshot de provedor/conta na intencao';
    END IF;

    IF EXISTS (SELECT 1 FROM pagamento_intent WHERE provedor = 'mercadopago' AND id_infinitepay_conta_versao IS NOT NULL) THEN
        RAISE EXCEPTION 'Intencao Mercado Pago ligada a conta InfinitePay';
    END IF;

    IF NOT EXISTS (SELECT 1 FROM pg_constraint WHERE conname = 'fk_pagamento_intent_infinitepay_loja') THEN
        RAISE EXCEPTION 'Falta isolamento de loja no snapshot InfinitePay';
    END IF;

    IF EXISTS (SELECT id_conta FROM infinitepay_conta_versao WHERE ativa GROUP BY id_conta HAVING count(*) > 1) THEN
        RAISE EXCEPTION 'Mais de uma versao InfinitePay ativa para a mesma conta';
    END IF;

    IF EXISTS (
        SELECT 1 FROM infinitepay_conta_versao v
        JOIN infinitepay_conta_loja c ON c.id = v.id_conta
        WHERE v.infinite_tag_snapshot <> c.infinite_tag_normalizada AND v.ativa
    ) THEN
        RAISE EXCEPTION 'Versao ativa InfinitePay diverge do handle atual';
    END IF;
END $$;
