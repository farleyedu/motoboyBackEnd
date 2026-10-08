DO $$ BEGIN
    IF to_regclass('delivery_chat_message') IS NULL OR to_regclass('delivery_chat_attachment') IS NULL
       OR to_regclass('delivery_chat_read') IS NULL OR to_regclass('delivery_chat_reaction') IS NULL THEN
       RAISE EXCEPTION 'Migration de comunicacao incompleta';
    END IF;
    IF EXISTS(SELECT 1 FROM delivery_motoboy_message x WHERE NOT EXISTS(SELECT 1 FROM delivery_chat_message c WHERE c.channel='store' AND c.legacy_id=x.id))
       OR EXISTS(SELECT 1 FROM motoboy_group_message x WHERE NOT EXISTS(SELECT 1 FROM delivery_chat_message c WHERE c.channel='group' AND c.legacy_id=x.id)) THEN
       RAISE EXCEPTION 'Historico legado incompleto';
    END IF;
END $$;
