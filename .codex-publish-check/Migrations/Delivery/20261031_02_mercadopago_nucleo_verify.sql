-- Confere 20261031_01_mercadopago_nucleo (somente leitura; nao e aplicado no boot).
DO $$
DECLARE
    tabela TEXT;
BEGIN
    FOREACH tabela IN ARRAY ARRAY['mp_aplicacao', 'mp_conta_zippy', 'mp_conta_loja', 'mp_oauth_tentativa', 'pagamento_preferencia_loja',
                                  'pagamento_intent', 'pagamento_tentativa', 'pagamento', 'cobranca_chat', 'cobranca_chat_evento',
                                  'pagamento_estorno', 'pagamento_disputa', 'mp_webhook_evento', 'pagamento_auditoria', 'pagamento_outbox'] LOOP
        IF to_regclass(tabela) IS NULL THEN
            RAISE EXCEPTION 'Falta a tabela %', tabela;
        END IF;
    END LOOP;
    IF NOT EXISTS (SELECT 1 FROM pg_constraint WHERE conname = 'ck_pagamento_intent_conta') THEN
        RAISE EXCEPTION 'Falta a regra origem -> conta recebedora';
    END IF;
    IF NOT EXISTS (SELECT 1 FROM pg_trigger WHERE tgname = 'tg_pagamento_estorno_limite') THEN
        RAISE EXCEPTION 'Falta a trava da soma dos estornos';
    END IF;
    IF NOT EXISTS (SELECT 1 FROM pg_trigger WHERE tgname = 'tg_pagamento_auditoria_imutavel') THEN
        RAISE EXCEPTION 'Auditoria financeira sem trava de imutabilidade';
    END IF;
    IF NOT EXISTS (SELECT 1 FROM pg_indexes WHERE indexname = 'ux_mp_conta_loja_viva') THEN
        RAISE EXCEPTION 'Falta a unicidade da conta viva por loja e ambiente';
    END IF;
    -- As tabelas historicas do Asaas continuam la (nunca apagar).
    IF to_regclass('checkout_pagamentos') IS NULL THEN
        RAISE EXCEPTION 'checkout_pagamentos (historico Asaas) sumiu';
    END IF;
END $$;
