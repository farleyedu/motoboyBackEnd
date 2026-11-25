-- Verificacao somente leitura do estorno informado.
DO $$
BEGIN
    IF to_regclass('pagamento_estorno_informado') IS NULL THEN
        RAISE EXCEPTION 'Falta a tabela pagamento_estorno_informado';
    END IF;
    IF EXISTS (SELECT 1 FROM pagamento_estorno_informado e JOIN pagamento_intent i ON i.id = e.id_intent
                WHERE i.id_estabelecimento <> e.id_estabelecimento OR i.provedor <> e.provedor) THEN
        RAISE EXCEPTION 'Estorno informado ligado a loja ou provedor diferente da intencao';
    END IF;
    IF EXISTS (SELECT 1 FROM pagamento_estorno_informado e JOIN pagamento_intent i ON i.id = e.id_intent
                WHERE e.status = 'confirmado' AND i.valor_estornado < e.valor) THEN
        RAISE EXCEPTION 'Estorno confirmado sem refletir na intencao';
    END IF;
END $$;
