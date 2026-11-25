DO $$
BEGIN
    IF to_regclass('ifood_produto_vinculo') IS NULL THEN
        RAISE EXCEPTION 'Falta a migration 20261024_01_ifood_produto_vinculo';
    END IF;
    IF NOT EXISTS (SELECT 1 FROM pg_constraint WHERE conname = 'ux_ifood_produto_vinculo_chave' AND conrelid = 'ifood_produto_vinculo'::regclass) THEN
        RAISE EXCEPTION 'Falta a chave unica (estabelecimento, merchant, tipo, chave) do vinculo iFood';
    END IF;
    IF NOT EXISTS (SELECT 1 FROM pg_constraint WHERE conname = 'fk_ifood_produto_vinculo_conexao' AND conrelid = 'ifood_produto_vinculo'::regclass) THEN
        RAISE EXCEPTION 'Falta a FK composta do vinculo para ifood_connections';
    END IF;
    IF NOT EXISTS (SELECT 1 FROM pg_constraint WHERE conname = 'fk_ifood_produto_vinculo_produto' AND conrelid = 'ifood_produto_vinculo'::regclass) THEN
        RAISE EXCEPTION 'Falta a FK composta do vinculo para cardapio_produto (mesma loja)';
    END IF;
    IF NOT EXISTS (SELECT 1 FROM pg_constraint WHERE conname = 'ck_ifood_produto_vinculo_confirmado' AND conrelid = 'ifood_produto_vinculo'::regclass) THEN
        RAISE EXCEPTION 'Falta o CHECK: confirmado exige produto e origem';
    END IF;
    IF EXISTS (SELECT 1 FROM ifood_produto_vinculo WHERE estado = 'confirmado' AND (produto_id IS NULL OR origem IS NULL)) THEN
        RAISE EXCEPTION 'Vinculo confirmado sem produto ou origem';
    END IF;
    IF EXISTS (SELECT 1 FROM ifood_produto_vinculo v JOIN cardapio_produto p ON p.id = v.produto_id
               WHERE p.id_estabelecimento <> v.estabelecimento_id) THEN
        RAISE EXCEPTION 'Vinculo aponta para produto de outra loja';
    END IF;
END $$;
SELECT version FROM delivery_tracking_schema_versions WHERE version = '20261024_01_ifood_produto_vinculo';
