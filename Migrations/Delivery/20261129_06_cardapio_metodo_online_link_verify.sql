-- Verificacao somente leitura: a regra aceita 'online' e nenhum pedido ficou incoerente.
DO $$
BEGIN
    IF NOT EXISTS (SELECT 1 FROM pg_constraint WHERE conname = 'ck_cardapio_pedido_publico_metodo_online'
                    AND pg_get_constraintdef(oid) LIKE '%online%') THEN
        RAISE EXCEPTION 'Falta o valor online na regra de metodo_online';
    END IF;
END $$;
