-- Verificacao do schema do cardapio. Somente leitura; nao e aplicada automaticamente.

SELECT to_regclass('cardapio_categoria')          AS tabela_categoria,
       to_regclass('cardapio_produto')             AS tabela_produto,
       to_regclass('cardapio_grupo_adicional')      AS tabela_grupo_adicional,
       to_regclass('cardapio_grupo_adicional_item') AS tabela_grupo_adicional_item,
       to_regclass('cardapio_produto_grupo')        AS tabela_produto_grupo,
       to_regclass('cardapio_web_config')           AS tabela_web_config,
       to_regclass('cardapio_pedido_publico')       AS tabela_pedido_publico;

SELECT conname FROM pg_constraint
 WHERE conname IN ('ux_cardapio_categoria_estab_slug', 'ux_cardapio_produto_estab_slug', 'ux_cardapio_pedido_publico_codigo')
    OR conname IN (SELECT indexrelid::regclass::text FROM pg_index WHERE indexrelid::regclass::text LIKE 'ux_cardapio%');

SELECT indexname FROM pg_indexes
 WHERE indexname IN ('ux_cardapio_categoria_estab_slug', 'ux_cardapio_produto_estab_slug', 'ux_cardapio_pedido_publico_codigo');

SELECT version, applied_at_utc FROM delivery_tracking_schema_versions
 WHERE version = '20261001_01_cardapio_schema';
