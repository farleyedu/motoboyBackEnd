-- Verificacao somente leitura da cobranca do chat por link.
DO $$
BEGIN
    IF NOT EXISTS (SELECT 1 FROM information_schema.columns WHERE table_name = 'cobranca_chat' AND column_name = 'modalidade') THEN
        RAISE EXCEPTION 'Falta cobranca_chat.modalidade';
    END IF;
    IF NOT EXISTS (SELECT 1 FROM information_schema.columns WHERE table_name = 'cobranca_chat_config' AND column_name = 'link_pagamento_habilitado') THEN
        RAISE EXCEPTION 'Falta cobranca_chat_config.link_pagamento_habilitado';
    END IF;
    IF NOT EXISTS (SELECT 1 FROM pg_constraint WHERE conname = 'ck_pagamento_intent_conta'
                    AND pg_get_constraintdef(oid) LIKE '%infinitepay%chat%') THEN
        RAISE EXCEPTION 'A regra de conta da intencao nao aceita InfinitePay no chat';
    END IF;
    IF EXISTS (SELECT 1 FROM cobranca_chat c JOIN pagamento_intent i ON i.id = c.id_intent
                WHERE c.modalidade = 'link' AND i.provedor <> 'infinitepay') THEN
        RAISE EXCEPTION 'Cobranca por link ligada a intencao de outro provedor';
    END IF;
END $$;
