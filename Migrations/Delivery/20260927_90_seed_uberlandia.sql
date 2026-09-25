-- ---------------------------------------------------------------------------
-- SEED DE DEMONSTRACAO (Uberlandia/MG): cardapio, clientes, conversas, mensagens, motoboys e
-- pedidos de UM estabelecimento que ja existe. Nao cria estabelecimento, empresa nem usuario.
-- Todos os dados sao ficticios (nomes, telefones +55 34 99123-xxxx, enderecos e coordenadas
-- aproximadas dos bairros).
--
-- COMO ESCOLHE O ALVO (e por que e seguro rodar em qualquer ambiente):
--  1) so semeia estabelecimento ATIVO, com o modulo DELIVERY e completamente VAZIO
--     (nenhum pedido, conversa ou cliente): um estabelecimento que opera de verdade nunca e alterado;
--  2) para escolher o alvo, insira ANTES da subida:
--       INSERT INTO delivery_tracking_schema_versions (version) VALUES ('seed_demo_alvo:<uuid do estabelecimento>');
--  3) para NUNCA semear (ex.: producao):
--       INSERT INTO delivery_tracking_schema_versions (version) VALUES ('seed_demo_desligado');
--  4) o resultado fica registrado no ledger: 'seed_demo_alvo:<uuid>' (aplicado), 'seed_demo_sem_alvo'
--     (nenhum estabelecimento elegivel) ou 'seed_demo_erro: <mensagem>' (falhou e sera tentado de novo).
--  Para semear de novo depois: apague as linhas '20260927_90_seed_uberlandia' e 'seed_demo_%' do ledger.
-- Idempotente (ids deterministicos, ON CONFLICT/NOT EXISTS) e isolado numa subtransacao: se algo
-- falhar, o restante das migrations continua e nada do seed fica pela metade.
-- Desfazer: rollback/20260927_90_seed_uberlandia.down.sql.
-- ---------------------------------------------------------------------------

BEGIN;

-- Id deterministico por (estabelecimento, chave): permite repetir o seed e desfaze-lo.
CREATE OR REPLACE FUNCTION pg_temp.sid(p_est uuid, p_key text) RETURNS uuid
LANGUAGE sql IMMUTABLE AS $f$ SELECT md5('zippy-seed-uberlandia:' || p_est::text || ':' || p_key)::uuid $f$;

-- Literal SQL de um valor para a coluna legada de pedido, no tipo REAL da coluna
-- (as colunas de horario/valor/coordenada do legado variam entre text, numeric, date, time...).
CREATE OR REPLACE FUNCTION pg_temp.seed_lit(p_col text, p_val text) RETURNS text
LANGUAGE plpgsql AS $f$
DECLARE t text;
BEGIN
  IF p_val IS NULL THEN RETURN 'NULL'; END IF;
  SELECT c.data_type INTO t FROM information_schema.columns c
   WHERE c.table_schema = current_schema() AND c.table_name = 'pedido' AND c.column_name = p_col;
  IF t IS NULL THEN RAISE EXCEPTION 'coluna pedido.% nao encontrada', p_col; END IF;
  IF t IN ('character varying', 'character', 'text') THEN RETURN quote_literal(p_val); END IF;
  RETURN format('%L::%s', p_val, t);
END $f$;

CREATE OR REPLACE FUNCTION pg_temp.seed_pedido(p_est uuid, p jsonb) RETURNS int
LANGUAGE plpgsql AS $f$
DECLARE
  v_id int; v_created timestamp; v_cols text[] := '{}'; v_vals text[] := '{}';
  k text; it jsonb; v_ord int := 0; v_moto int; v_cli uuid; v_conv uuid; v_local timestamp;
BEGIN
  SELECT x.id INTO v_id FROM pedido x
   WHERE x.id_estabelecimento = p_est AND x.origem = p->>'origem' AND x.origem_ref = p->>'ref';
  IF v_id IS NOT NULL THEN RETURN v_id; END IF;

  v_local := NOW() AT TIME ZONE 'America/Sao_Paulo';
  v_created := v_local - make_interval(mins => (p->>'min_ago')::int);

  FOREACH k IN ARRAY ARRAY['nome_cliente','telefone_cliente','endereco_entrega','region','latitude','longitude',
      'entrega_rua','entrega_numero','entrega_bairro','entrega_cidade','entrega_estado','entrega_cep',
      'tipo_pagamento','troco','observacoes','status_pagamento','codigo_entrega','items','value'] LOOP
    IF jsonb_exists(p, k) THEN v_cols := v_cols || k; v_vals := v_vals || pg_temp.seed_lit(k, p->>k); END IF;
  END LOOP;

  v_cols := v_cols || 'data_pedido';       v_vals := v_vals || pg_temp.seed_lit('data_pedido', to_char(v_created, 'YYYY-MM-DD HH24:MI:SS'));
  v_cols := v_cols || 'horario_pedido';    v_vals := v_vals || pg_temp.seed_lit('horario_pedido', to_char(v_created, 'YYYY-MM-DD HH24:MI:SS'));
  v_cols := v_cols || 'previsao_entrega';  v_vals := v_vals || pg_temp.seed_lit('previsao_entrega', to_char(v_created + make_interval(mins => (p->>'prev_min')::int), 'YYYY-MM-DD HH24:MI:SS'));
  IF jsonb_exists(p, 'saida_after') THEN
    v_cols := v_cols || 'horario_saida';   v_vals := v_vals || pg_temp.seed_lit('horario_saida', to_char(v_created + make_interval(mins => (p->>'saida_after')::int), 'YYYY-MM-DD HH24:MI:SS'));
  END IF;
  IF jsonb_exists(p, 'entrega_after') THEN
    v_cols := v_cols || 'horario_entrega'; v_vals := v_vals || pg_temp.seed_lit('horario_entrega', to_char(v_created + make_interval(mins => (p->>'entrega_after')::int), 'YYYY-MM-DD HH24:MI:SS'));
  END IF;

  SELECT c.id INTO v_cli FROM clientes c WHERE c.id_estabelecimento = p_est AND c.telefone_e164 = p->>'telefone_cliente' LIMIT 1;
  IF p->>'conv' IS NOT NULL THEN v_conv := pg_temp.sid(p_est, 'conv:' || (p->>'conv')); END IF;
  IF p->>'moto' IS NOT NULL THEN
    SELECT m.id INTO v_moto FROM motoboy m WHERE m.id_estabelecimento = p_est AND m.nome = p->>'moto' AND m.is_simulated ORDER BY m.id LIMIT 1;
  END IF;

  EXECUTE format(
    'INSERT INTO pedido (%s, status_pedido, id_estabelecimento, motoboy_responsavel, origem, origem_ref, conversa_id, cliente_id, subtotal, taxa_entrega, desconto) ' ||
    'VALUES (%s, %s, %L, %L, %L, %L, %L, %L, %s, %s, 0) RETURNING id',
    array_to_string(v_cols, ', '), array_to_string(v_vals, ', '), (p->>'status')::int, p_est, v_moto,
    p->>'origem', p->>'ref', v_conv, v_cli, p->>'subtotal', p->>'taxa') INTO v_id;

  FOR it IN SELECT * FROM jsonb_array_elements(p->'itens') LOOP
    v_ord := v_ord + 1;
    INSERT INTO pedido_item (pedido_id, produto_id, nome, quantidade, preco_unitario, observacao, adicionais, ordem)
    VALUES (v_id, pg_temp.sid(p_est, 'prod:' || (it->>'prod')), it->>'nome', (it->>'qtd')::int, (it->>'preco')::numeric, it->>'obs',
      COALESCE((SELECT jsonb_agg(jsonb_build_object('id', pg_temp.sid(p_est, 'adi:' || (a->>'key')), 'nome', a->>'nome', 'preco', (a->>'preco')::numeric))
                  FROM jsonb_array_elements(it->'adicionais') a), '[]'::jsonb), v_ord);
  END LOOP;

  -- Entregas concluidas entram no historico de paradas do motoboy (metricas do dia).
  IF (p->>'status')::int = 3 AND v_moto IS NOT NULL THEN
    INSERT INTO delivery_route_stops (estabelecimento_id, motoboy_id, pedido_id, position, stop_status,
        assigned_at_utc, started_at_utc, picked_up_at_utc, arrived_at_utc, completed_at_utc, completed_by, updated_at_utc)
    VALUES (p_est, v_moto, v_id, 1, 'completed',
        NOW() - make_interval(mins => (p->>'min_ago')::int - 5),
        NOW() - make_interval(mins => (p->>'min_ago')::int - 20),
        NOW() - make_interval(mins => (p->>'min_ago')::int - 20),
        NOW() - make_interval(mins => (p->>'min_ago')::int - 48),
        NOW() - make_interval(mins => (p->>'min_ago')::int - 50), 'motoboy',
        NOW() - make_interval(mins => (p->>'min_ago')::int - 50));
  END IF;
  RETURN v_id;
END $f$;

-- Estabelecimento sem NENHUM dado real: sem pedido, conversa ou cliente que nao seja do proprio seed.
CREATE OR REPLACE FUNCTION pg_temp.seed_alvo_ok(p_est uuid) RETURNS boolean
LANGUAGE plpgsql AS $f$
BEGIN
  RETURN NOT EXISTS (SELECT 1 FROM pedido x WHERE x.id_estabelecimento = p_est AND (x.origem_ref IS NULL OR x.origem_ref NOT LIKE 'seed-uberlandia-%'))
     AND NOT EXISTS (SELECT 1 FROM clientes x WHERE x.id_estabelecimento = p_est AND x.telefone_e164 NOT LIKE '+5534991230%')
     AND NOT EXISTS (SELECT 1 FROM conversas x WHERE x.id_estabelecimento = p_est
                      AND x.id NOT IN (SELECT pg_temp.sid(p_est, 'conv:' || k) FROM unnest(ARRAY['maria', 'joao', 'carlos', 'juliana', 'luciana', 'thiago', 'ana', 'fernanda', 'ricardo', 'paulo', 'patricia']) AS k));
END $f$;

DO $seed$
DECLARE
  v_est uuid;
  v_alvo text;
BEGIN
  IF EXISTS (SELECT 1 FROM delivery_tracking_schema_versions WHERE version = 'seed_demo_desligado') THEN
    INSERT INTO delivery_tracking_schema_versions (version) VALUES ('20260927_90_seed_uberlandia') ON CONFLICT (version) DO NOTHING;
    RETURN;
  END IF;

  BEGIN
    SELECT substring(version FROM 'seed_demo_alvo:(.*)$') INTO v_alvo
      FROM delivery_tracking_schema_versions WHERE version LIKE 'seed_demo_alvo:%' ORDER BY applied_at_utc LIMIT 1;

    IF v_alvo IS NOT NULL THEN
      v_est := v_alvo::uuid;
      IF NOT EXISTS (SELECT 1 FROM estabelecimentos e WHERE e.id = v_est) THEN v_est := NULL; END IF;
    ELSE
      SELECT e.id INTO v_est
        FROM estabelecimentos e
       WHERE e.ativo = TRUE
         AND 'DELIVERY' = ANY (e.modulos_ativos::text[])
         AND pg_temp.seed_alvo_ok(e.id)
       ORDER BY e.created_at, e.id
       LIMIT 1;
    END IF;

    -- Alvo escolhido a mao tambem precisa estar sem dados reais.
    IF v_est IS NOT NULL AND NOT pg_temp.seed_alvo_ok(v_est) THEN
      v_est := NULL;
    END IF;

    IF v_est IS NULL THEN
      INSERT INTO delivery_tracking_schema_versions (version) VALUES ('seed_demo_sem_alvo') ON CONFLICT (version) DO NOTHING;
      INSERT INTO delivery_tracking_schema_versions (version) VALUES ('20260927_90_seed_uberlandia') ON CONFLICT (version) DO NOTHING;
      RETURN;
    END IF;

  INSERT INTO cardapio_categoria (id, id_estabelecimento, nome, slug, emoji, descricao, ordem, ativo, created_at, updated_at)
  VALUES (pg_temp.sid(v_est, 'cat:pratos'), v_est, 'Pratos', 'pratos', '🍛', 'Comida mineira caprichada, do jeito de Uberlandia.', 1, TRUE, NOW(), NOW())
  ON CONFLICT DO NOTHING;
  INSERT INTO cardapio_categoria (id, id_estabelecimento, nome, slug, emoji, descricao, ordem, ativo, created_at, updated_at)
  VALUES (pg_temp.sid(v_est, 'cat:lanches'), v_est, 'Lanches', 'lanches', '🍔', 'Hamburgueres e lanches na chapa.', 2, TRUE, NOW(), NOW())
  ON CONFLICT DO NOTHING;
  INSERT INTO cardapio_categoria (id, id_estabelecimento, nome, slug, emoji, descricao, ordem, ativo, created_at, updated_at)
  VALUES (pg_temp.sid(v_est, 'cat:porcoes'), v_est, 'Porcoes', 'porcoes', '🍟', 'Para dividir na mesa ou no sofa.', 3, TRUE, NOW(), NOW())
  ON CONFLICT DO NOTHING;
  INSERT INTO cardapio_categoria (id, id_estabelecimento, nome, slug, emoji, descricao, ordem, ativo, created_at, updated_at)
  VALUES (pg_temp.sid(v_est, 'cat:bebidas'), v_est, 'Bebidas', 'bebidas', '🥤', 'Geladas, direto da geladeira.', 4, TRUE, NOW(), NOW())
  ON CONFLICT DO NOTHING;
  INSERT INTO cardapio_categoria (id, id_estabelecimento, nome, slug, emoji, descricao, ordem, ativo, created_at, updated_at)
  VALUES (pg_temp.sid(v_est, 'cat:sobremesas'), v_est, 'Sobremesas', 'sobremesas', '🍮', 'Para fechar com doce.', 5, TRUE, NOW(), NOW())
  ON CONFLICT DO NOTHING;
  INSERT INTO cardapio_produto (id, id_estabelecimento, categoria_id, nome, slug, emoji, descricao, descricao_curta, preco_base, is_club, eco_friendly, ordem, ativo, destaque, disponivel, publico_web, created_at, updated_at)
  VALUES (pg_temp.sid(v_est, 'prod:pf-tropeiro'), v_est, pg_temp.sid(v_est, 'cat:pratos'), 'PF Tropeiro', 'pf-tropeiro', '🍛', 'Arroz, feijao tropeiro, bife acebolado, ovo e couve.', 'Arroz, feijao tropeiro, bife acebolado, ovo e couve.', 32.00, FALSE, FALSE, 1, TRUE, true, TRUE, TRUE, NOW(), NOW())
  ON CONFLICT DO NOTHING;
  INSERT INTO cardapio_produto (id, id_estabelecimento, categoria_id, nome, slug, emoji, descricao, descricao_curta, preco_base, is_club, eco_friendly, ordem, ativo, destaque, disponivel, publico_web, created_at, updated_at)
  VALUES (pg_temp.sid(v_est, 'prod:frango-quiabo'), v_est, pg_temp.sid(v_est, 'cat:pratos'), 'Frango com Quiabo', 'frango-quiabo', '🍗', 'Frango caipira com quiabo, angu e arroz.', 'Frango caipira com quiabo, angu e arroz.', 36.00, FALSE, FALSE, 2, TRUE, true, TRUE, TRUE, NOW(), NOW())
  ON CONFLICT DO NOTHING;
  INSERT INTO cardapio_produto (id, id_estabelecimento, categoria_id, nome, slug, emoji, descricao, descricao_curta, preco_base, is_club, eco_friendly, ordem, ativo, destaque, disponivel, publico_web, created_at, updated_at)
  VALUES (pg_temp.sid(v_est, 'prod:feijoada-mineira'), v_est, pg_temp.sid(v_est, 'cat:pratos'), 'Feijoada Mineira (individual)', 'feijoada-mineira', '🥘', 'Feijoada com couve, torresmo, farofa e laranja.', 'Feijoada com couve, torresmo, farofa e laranja.', 42.00, FALSE, FALSE, 3, TRUE, false, TRUE, TRUE, NOW(), NOW())
  ON CONFLICT DO NOTHING;
  INSERT INTO cardapio_produto (id, id_estabelecimento, categoria_id, nome, slug, emoji, descricao, descricao_curta, preco_base, is_club, eco_friendly, ordem, ativo, destaque, disponivel, publico_web, created_at, updated_at)
  VALUES (pg_temp.sid(v_est, 'prod:x-burguer'), v_est, pg_temp.sid(v_est, 'cat:lanches'), 'X-Burguer', 'x-burguer', '🍔', 'Pao, hamburguer 150 g, queijo e maionese da casa.', 'Pao, hamburguer 150 g, queijo e maionese da casa.', 24.00, FALSE, FALSE, 4, TRUE, false, TRUE, TRUE, NOW(), NOW())
  ON CONFLICT DO NOTHING;
  INSERT INTO cardapio_produto (id, id_estabelecimento, categoria_id, nome, slug, emoji, descricao, descricao_curta, preco_base, is_club, eco_friendly, ordem, ativo, destaque, disponivel, publico_web, created_at, updated_at)
  VALUES (pg_temp.sid(v_est, 'prod:x-bacon'), v_est, pg_temp.sid(v_est, 'cat:lanches'), 'X-Bacon', 'x-bacon', '🥓', 'Pao, hamburguer 150 g, queijo, bacon crocante e cheddar.', 'Pao, hamburguer 150 g, queijo, bacon crocante e cheddar.', 29.00, FALSE, FALSE, 5, TRUE, true, TRUE, TRUE, NOW(), NOW())
  ON CONFLICT DO NOTHING;
  INSERT INTO cardapio_produto (id, id_estabelecimento, categoria_id, nome, slug, emoji, descricao, descricao_curta, preco_base, is_club, eco_friendly, ordem, ativo, destaque, disponivel, publico_web, created_at, updated_at)
  VALUES (pg_temp.sid(v_est, 'prod:x-salada'), v_est, pg_temp.sid(v_est, 'cat:lanches'), 'X-Salada', 'x-salada', '🥬', 'Pao, hamburguer 150 g, queijo, alface, tomate e milho.', 'Pao, hamburguer 150 g, queijo, alface, tomate e milho.', 26.00, FALSE, FALSE, 6, TRUE, false, TRUE, TRUE, NOW(), NOW())
  ON CONFLICT DO NOTHING;
  INSERT INTO cardapio_produto (id, id_estabelecimento, categoria_id, nome, slug, emoji, descricao, descricao_curta, preco_base, is_club, eco_friendly, ordem, ativo, destaque, disponivel, publico_web, created_at, updated_at)
  VALUES (pg_temp.sid(v_est, 'prod:pao-queijo'), v_est, pg_temp.sid(v_est, 'cat:porcoes'), 'Porcao de Pao de Queijo', 'pao-queijo', '🧀', '12 unidades, quentinhas, com geleia de pimenta.', '12 unidades, quentinhas, com geleia de pimenta.', 22.00, FALSE, FALSE, 7, TRUE, true, TRUE, TRUE, NOW(), NOW())
  ON CONFLICT DO NOTHING;
  INSERT INTO cardapio_produto (id, id_estabelecimento, categoria_id, nome, slug, emoji, descricao, descricao_curta, preco_base, is_club, eco_friendly, ordem, ativo, destaque, disponivel, publico_web, created_at, updated_at)
  VALUES (pg_temp.sid(v_est, 'prod:mandioca-frita'), v_est, pg_temp.sid(v_est, 'cat:porcoes'), 'Mandioca Frita', 'mandioca-frita', '🍠', 'Porcao com mandioca frita e molho de alho.', 'Porcao com mandioca frita e molho de alho.', 25.00, FALSE, FALSE, 8, TRUE, false, TRUE, TRUE, NOW(), NOW())
  ON CONFLICT DO NOTHING;
  INSERT INTO cardapio_produto (id, id_estabelecimento, categoria_id, nome, slug, emoji, descricao, descricao_curta, preco_base, is_club, eco_friendly, ordem, ativo, destaque, disponivel, publico_web, created_at, updated_at)
  VALUES (pg_temp.sid(v_est, 'prod:batata-frita'), v_est, pg_temp.sid(v_est, 'cat:porcoes'), 'Batata Frita', 'batata-frita', '🍟', 'Porcao grande com cheddar e bacon opcional.', 'Porcao grande com cheddar e bacon opcional.', 27.00, FALSE, FALSE, 9, TRUE, false, TRUE, TRUE, NOW(), NOW())
  ON CONFLICT DO NOTHING;
  INSERT INTO cardapio_produto (id, id_estabelecimento, categoria_id, nome, slug, emoji, descricao, descricao_curta, preco_base, is_club, eco_friendly, ordem, ativo, destaque, disponivel, publico_web, created_at, updated_at)
  VALUES (pg_temp.sid(v_est, 'prod:guarana-lata'), v_est, pg_temp.sid(v_est, 'cat:bebidas'), 'Guarana Lata 350 ml', 'guarana-lata', '🥤', 'Gelado.', 'Gelado.', 6.50, FALSE, FALSE, 10, TRUE, false, TRUE, TRUE, NOW(), NOW())
  ON CONFLICT DO NOTHING;
  INSERT INTO cardapio_produto (id, id_estabelecimento, categoria_id, nome, slug, emoji, descricao, descricao_curta, preco_base, is_club, eco_friendly, ordem, ativo, destaque, disponivel, publico_web, created_at, updated_at)
  VALUES (pg_temp.sid(v_est, 'prod:coca-2l'), v_est, pg_temp.sid(v_est, 'cat:bebidas'), 'Refrigerante 2 L', 'coca-2l', '🧃', 'Coca-Cola, Guarana ou Sprite (perguntar).', 'Coca-Cola, Guarana ou Sprite (perguntar).', 14.00, FALSE, FALSE, 11, TRUE, false, TRUE, TRUE, NOW(), NOW())
  ON CONFLICT DO NOTHING;
  INSERT INTO cardapio_produto (id, id_estabelecimento, categoria_id, nome, slug, emoji, descricao, descricao_curta, preco_base, is_club, eco_friendly, ordem, ativo, destaque, disponivel, publico_web, created_at, updated_at)
  VALUES (pg_temp.sid(v_est, 'prod:suco-caju'), v_est, pg_temp.sid(v_est, 'cat:bebidas'), 'Suco de Caju 500 ml', 'suco-caju', '🍹', 'Suco natural, com ou sem acucar.', 'Suco natural, com ou sem acucar.', 10.00, FALSE, FALSE, 12, TRUE, false, TRUE, TRUE, NOW(), NOW())
  ON CONFLICT DO NOTHING;
  INSERT INTO cardapio_produto (id, id_estabelecimento, categoria_id, nome, slug, emoji, descricao, descricao_curta, preco_base, is_club, eco_friendly, ordem, ativo, destaque, disponivel, publico_web, created_at, updated_at)
  VALUES (pg_temp.sid(v_est, 'prod:doce-leite'), v_est, pg_temp.sid(v_est, 'cat:sobremesas'), 'Doce de Leite Caseiro', 'doce-leite', '🍮', 'Pote 200 g com queijo minas.', 'Pote 200 g com queijo minas.', 14.00, FALSE, FALSE, 13, TRUE, true, TRUE, TRUE, NOW(), NOW())
  ON CONFLICT DO NOTHING;
  INSERT INTO cardapio_produto (id, id_estabelecimento, categoria_id, nome, slug, emoji, descricao, descricao_curta, preco_base, is_club, eco_friendly, ordem, ativo, destaque, disponivel, publico_web, created_at, updated_at)
  VALUES (pg_temp.sid(v_est, 'prod:pudim'), v_est, pg_temp.sid(v_est, 'cat:sobremesas'), 'Pudim de Leite', 'pudim', '🍰', 'Fatia generosa com calda de caramelo.', 'Fatia generosa com calda de caramelo.', 12.00, FALSE, FALSE, 14, TRUE, false, TRUE, TRUE, NOW(), NOW())
  ON CONFLICT DO NOTHING;
  INSERT INTO cardapio_grupo_adicional (id, id_estabelecimento, nome, tipo, descricao, min_selecionados, max_selecionados, ordem, ativo, created_at, updated_at)
  VALUES (pg_temp.sid(v_est, 'grp:extras-lanche'), v_est, 'Extras do lanche', 'adicional_global', NULL, 0, 3, 1, TRUE, NOW(), NOW())
  ON CONFLICT DO NOTHING;
  INSERT INTO cardapio_grupo_adicional_item (id, id_grupo, nome, descricao, preco, ordem, ativo, created_at, updated_at)
  VALUES (pg_temp.sid(v_est, 'adi:bacon'), pg_temp.sid(v_est, 'grp:extras-lanche'), 'Bacon extra', NULL, 4.00, 1, TRUE, NOW(), NOW())
  ON CONFLICT DO NOTHING;
  INSERT INTO cardapio_grupo_adicional_item (id, id_grupo, nome, descricao, preco, ordem, ativo, created_at, updated_at)
  VALUES (pg_temp.sid(v_est, 'adi:cheddar'), pg_temp.sid(v_est, 'grp:extras-lanche'), 'Cheddar extra', NULL, 3.50, 2, TRUE, NOW(), NOW())
  ON CONFLICT DO NOTHING;
  INSERT INTO cardapio_grupo_adicional_item (id, id_grupo, nome, descricao, preco, ordem, ativo, created_at, updated_at)
  VALUES (pg_temp.sid(v_est, 'adi:ovo'), pg_temp.sid(v_est, 'grp:extras-lanche'), 'Ovo', NULL, 2.50, 3, TRUE, NOW(), NOW())
  ON CONFLICT DO NOTHING;
  INSERT INTO cardapio_grupo_adicional_item (id, id_grupo, nome, descricao, preco, ordem, ativo, created_at, updated_at)
  VALUES (pg_temp.sid(v_est, 'adi:queijo'), pg_temp.sid(v_est, 'grp:extras-lanche'), 'Queijo extra', NULL, 3.00, 4, TRUE, NOW(), NOW())
  ON CONFLICT DO NOTHING;
  INSERT INTO cardapio_produto_atendimento (produto_id, id_estabelecimento, apelidos, instrucoes, restricoes, tempo_extra_preparo_min)
  VALUES (pg_temp.sid(v_est, 'prod:pf-tropeiro'), v_est, ARRAY['pf', 'prato feito', 'tropeiro']::text[], 'Perguntar se quer o bife bem passado.', NULL, 5)
  ON CONFLICT DO NOTHING;
  INSERT INTO cardapio_produto_atendimento (produto_id, id_estabelecimento, apelidos, instrucoes, restricoes, tempo_extra_preparo_min)
  VALUES (pg_temp.sid(v_est, 'prod:frango-quiabo'), v_est, ARRAY['frango caipira', 'frango com angu']::text[], 'Servido com angu; avisar que o quiabo e do dia.', NULL, 10)
  ON CONFLICT DO NOTHING;
  INSERT INTO cardapio_produto_atendimento (produto_id, id_estabelecimento, apelidos, instrucoes, restricoes, tempo_extra_preparo_min)
  VALUES (pg_temp.sid(v_est, 'prod:feijoada-mineira'), v_est, ARRAY['feijoada']::text[], 'Disponivel so de sexta a domingo.', 'Contem carne de porco.', 10)
  ON CONFLICT DO NOTHING;
  INSERT INTO cardapio_produto_grupo (id_produto, id_grupo, ordem, created_at)
  SELECT pg_temp.sid(v_est, 'prod:x-burguer'), pg_temp.sid(v_est, 'grp:extras-lanche'), 1, NOW()
   WHERE NOT EXISTS (SELECT 1 FROM cardapio_produto_grupo x WHERE x.id_produto = pg_temp.sid(v_est, 'prod:x-burguer') AND x.id_grupo = pg_temp.sid(v_est, 'grp:extras-lanche'));
  INSERT INTO cardapio_produto_atendimento (produto_id, id_estabelecimento, apelidos, instrucoes, restricoes, tempo_extra_preparo_min)
  VALUES (pg_temp.sid(v_est, 'prod:x-burguer'), v_est, ARRAY['xis burguer', 'hamburguer simples']::text[], NULL, NULL, 0)
  ON CONFLICT DO NOTHING;
  INSERT INTO cardapio_produto_grupo (id_produto, id_grupo, ordem, created_at)
  SELECT pg_temp.sid(v_est, 'prod:x-bacon'), pg_temp.sid(v_est, 'grp:extras-lanche'), 1, NOW()
   WHERE NOT EXISTS (SELECT 1 FROM cardapio_produto_grupo x WHERE x.id_produto = pg_temp.sid(v_est, 'prod:x-bacon') AND x.id_grupo = pg_temp.sid(v_est, 'grp:extras-lanche'));
  INSERT INTO cardapio_produto_atendimento (produto_id, id_estabelecimento, apelidos, instrucoes, restricoes, tempo_extra_preparo_min)
  VALUES (pg_temp.sid(v_est, 'prod:x-bacon'), v_est, ARRAY['xis bacon', 'x bacon']::text[], 'Perguntar o ponto da carne.', NULL, 0)
  ON CONFLICT DO NOTHING;
  INSERT INTO cardapio_produto_grupo (id_produto, id_grupo, ordem, created_at)
  SELECT pg_temp.sid(v_est, 'prod:x-salada'), pg_temp.sid(v_est, 'grp:extras-lanche'), 1, NOW()
   WHERE NOT EXISTS (SELECT 1 FROM cardapio_produto_grupo x WHERE x.id_produto = pg_temp.sid(v_est, 'prod:x-salada') AND x.id_grupo = pg_temp.sid(v_est, 'grp:extras-lanche'));
  INSERT INTO cardapio_produto_atendimento (produto_id, id_estabelecimento, apelidos, instrucoes, restricoes, tempo_extra_preparo_min)
  VALUES (pg_temp.sid(v_est, 'prod:x-salada'), v_est, ARRAY['xis salada']::text[], 'Sem salada e so pedir.', NULL, 0)
  ON CONFLICT DO NOTHING;
  INSERT INTO cardapio_produto_atendimento (produto_id, id_estabelecimento, apelidos, instrucoes, restricoes, tempo_extra_preparo_min)
  VALUES (pg_temp.sid(v_est, 'prod:pao-queijo'), v_est, ARRAY['pao de queijo', 'porcao de pao de queijo']::text[], NULL, 'Contem gluten e leite.', 0)
  ON CONFLICT DO NOTHING;
  INSERT INTO cardapio_produto_atendimento (produto_id, id_estabelecimento, apelidos, instrucoes, restricoes, tempo_extra_preparo_min)
  VALUES (pg_temp.sid(v_est, 'prod:mandioca-frita'), v_est, ARRAY['aipim', 'macaxeira frita']::text[], NULL, NULL, 5)
  ON CONFLICT DO NOTHING;
  INSERT INTO cardapio_produto_grupo (id_produto, id_grupo, ordem, created_at)
  SELECT pg_temp.sid(v_est, 'prod:batata-frita'), pg_temp.sid(v_est, 'grp:extras-lanche'), 1, NOW()
   WHERE NOT EXISTS (SELECT 1 FROM cardapio_produto_grupo x WHERE x.id_produto = pg_temp.sid(v_est, 'prod:batata-frita') AND x.id_grupo = pg_temp.sid(v_est, 'grp:extras-lanche'));
  INSERT INTO cardapio_produto_atendimento (produto_id, id_estabelecimento, apelidos, instrucoes, restricoes, tempo_extra_preparo_min)
  VALUES (pg_temp.sid(v_est, 'prod:batata-frita'), v_est, ARRAY['fritas']::text[], NULL, NULL, 5)
  ON CONFLICT DO NOTHING;
  INSERT INTO cardapio_produto_atendimento (produto_id, id_estabelecimento, apelidos, instrucoes, restricoes, tempo_extra_preparo_min)
  VALUES (pg_temp.sid(v_est, 'prod:guarana-lata'), v_est, ARRAY['guarana']::text[], NULL, NULL, 0)
  ON CONFLICT DO NOTHING;
  INSERT INTO cardapio_produto_atendimento (produto_id, id_estabelecimento, apelidos, instrucoes, restricoes, tempo_extra_preparo_min)
  VALUES (pg_temp.sid(v_est, 'prod:coca-2l'), v_est, ARRAY['coca', 'refri 2 litros']::text[], 'Perguntar o sabor.', NULL, 0)
  ON CONFLICT DO NOTHING;
  INSERT INTO cardapio_produto_atendimento (produto_id, id_estabelecimento, apelidos, instrucoes, restricoes, tempo_extra_preparo_min)
  VALUES (pg_temp.sid(v_est, 'prod:suco-caju'), v_est, ARRAY['suco de caju']::text[], 'Perguntar se quer adocado.', NULL, 3)
  ON CONFLICT DO NOTHING;
  INSERT INTO cardapio_produto_atendimento (produto_id, id_estabelecimento, apelidos, instrucoes, restricoes, tempo_extra_preparo_min)
  VALUES (pg_temp.sid(v_est, 'prod:doce-leite'), v_est, ARRAY['doce de leite com queijo']::text[], NULL, 'Contem leite.', 0)
  ON CONFLICT DO NOTHING;
  INSERT INTO cardapio_produto_atendimento (produto_id, id_estabelecimento, apelidos, instrucoes, restricoes, tempo_extra_preparo_min)
  VALUES (pg_temp.sid(v_est, 'prod:pudim'), v_est, ARRAY['pudim']::text[], NULL, 'Contem leite e ovo.', 0)
  ON CONFLICT DO NOTHING;
  INSERT INTO clientes (id, id_estabelecimento, telefone_e164, nome, data_criacao, data_atualizacao)
  SELECT pg_temp.sid(v_est, 'cli:maria'), v_est, '+5534991230001', 'Maria Aparecida Souza', NOW() - interval '30 days', NOW() - interval '30 days'
   WHERE NOT EXISTS (SELECT 1 FROM clientes x WHERE x.id_estabelecimento = v_est AND x.telefone_e164 = '+5534991230001');
  INSERT INTO clientes (id, id_estabelecimento, telefone_e164, nome, data_criacao, data_atualizacao)
  SELECT pg_temp.sid(v_est, 'cli:joao'), v_est, '+5534991230002', 'Joao Pedro Almeida', NOW() - interval '30 days', NOW() - interval '30 days'
   WHERE NOT EXISTS (SELECT 1 FROM clientes x WHERE x.id_estabelecimento = v_est AND x.telefone_e164 = '+5534991230002');
  INSERT INTO clientes (id, id_estabelecimento, telefone_e164, nome, data_criacao, data_atualizacao)
  SELECT pg_temp.sid(v_est, 'cli:ana'), v_est, '+5534991230003', 'Ana Beatriz Ferreira', NOW() - interval '30 days', NOW() - interval '30 days'
   WHERE NOT EXISTS (SELECT 1 FROM clientes x WHERE x.id_estabelecimento = v_est AND x.telefone_e164 = '+5534991230003');
  INSERT INTO clientes (id, id_estabelecimento, telefone_e164, nome, data_criacao, data_atualizacao)
  SELECT pg_temp.sid(v_est, 'cli:carlos'), v_est, '+5534991230004', 'Carlos Eduardo Lima', NOW() - interval '30 days', NOW() - interval '30 days'
   WHERE NOT EXISTS (SELECT 1 FROM clientes x WHERE x.id_estabelecimento = v_est AND x.telefone_e164 = '+5534991230004');
  INSERT INTO clientes (id, id_estabelecimento, telefone_e164, nome, data_criacao, data_atualizacao)
  SELECT pg_temp.sid(v_est, 'cli:fernanda'), v_est, '+5534991230005', 'Fernanda Rocha', NOW() - interval '30 days', NOW() - interval '30 days'
   WHERE NOT EXISTS (SELECT 1 FROM clientes x WHERE x.id_estabelecimento = v_est AND x.telefone_e164 = '+5534991230005');
  INSERT INTO clientes (id, id_estabelecimento, telefone_e164, nome, data_criacao, data_atualizacao)
  SELECT pg_temp.sid(v_est, 'cli:ricardo'), v_est, '+5534991230006', 'Ricardo Martins', NOW() - interval '30 days', NOW() - interval '30 days'
   WHERE NOT EXISTS (SELECT 1 FROM clientes x WHERE x.id_estabelecimento = v_est AND x.telefone_e164 = '+5534991230006');
  INSERT INTO clientes (id, id_estabelecimento, telefone_e164, nome, data_criacao, data_atualizacao)
  SELECT pg_temp.sid(v_est, 'cli:juliana'), v_est, '+5534991230007', 'Juliana Costa', NOW() - interval '30 days', NOW() - interval '30 days'
   WHERE NOT EXISTS (SELECT 1 FROM clientes x WHERE x.id_estabelecimento = v_est AND x.telefone_e164 = '+5534991230007');
  INSERT INTO clientes (id, id_estabelecimento, telefone_e164, nome, data_criacao, data_atualizacao)
  SELECT pg_temp.sid(v_est, 'cli:paulo'), v_est, '+5534991230008', 'Paulo Henrique Nunes', NOW() - interval '30 days', NOW() - interval '30 days'
   WHERE NOT EXISTS (SELECT 1 FROM clientes x WHERE x.id_estabelecimento = v_est AND x.telefone_e164 = '+5534991230008');
  INSERT INTO clientes (id, id_estabelecimento, telefone_e164, nome, data_criacao, data_atualizacao)
  SELECT pg_temp.sid(v_est, 'cli:luciana'), v_est, '+5534991230009', 'Luciana Pereira', NOW() - interval '30 days', NOW() - interval '30 days'
   WHERE NOT EXISTS (SELECT 1 FROM clientes x WHERE x.id_estabelecimento = v_est AND x.telefone_e164 = '+5534991230009');
  INSERT INTO clientes (id, id_estabelecimento, telefone_e164, nome, data_criacao, data_atualizacao)
  SELECT pg_temp.sid(v_est, 'cli:marcos'), v_est, '+5534991230010', 'Marcos Vinicius Silva', NOW() - interval '30 days', NOW() - interval '30 days'
   WHERE NOT EXISTS (SELECT 1 FROM clientes x WHERE x.id_estabelecimento = v_est AND x.telefone_e164 = '+5534991230010');
  INSERT INTO clientes (id, id_estabelecimento, telefone_e164, nome, data_criacao, data_atualizacao)
  SELECT pg_temp.sid(v_est, 'cli:patricia'), v_est, '+5534991230011', 'Patricia Gomes', NOW() - interval '30 days', NOW() - interval '30 days'
   WHERE NOT EXISTS (SELECT 1 FROM clientes x WHERE x.id_estabelecimento = v_est AND x.telefone_e164 = '+5534991230011');
  INSERT INTO clientes (id, id_estabelecimento, telefone_e164, nome, data_criacao, data_atualizacao)
  SELECT pg_temp.sid(v_est, 'cli:thiago'), v_est, '+5534991230012', 'Thiago Barbosa', NOW() - interval '30 days', NOW() - interval '30 days'
   WHERE NOT EXISTS (SELECT 1 FROM clientes x WHERE x.id_estabelecimento = v_est AND x.telefone_e164 = '+5534991230012');
  INSERT INTO conversas (id, id_conversa_grupo, id_estabelecimento, id_cliente, canal, estado, status_atendimento, data_primeira_mensagem, data_ultima_mensagem, data_ultima_entrada, data_ultima_saida, janela_24h_inicio, janela_24h_fim, qtd_nao_lidas, motivo_fechamento, data_fechamento, data_criacao, data_atualizacao)
  VALUES (pg_temp.sid(v_est, 'conv:maria'), pg_temp.sid(v_est, 'conv:maria'), v_est, (SELECT id FROM clientes WHERE id_estabelecimento = v_est AND telefone_e164 = '+5534991230001'), 'whatsapp'::canal_chat_enum, 'fechado_agente'::estado_conversa_enum, 'encerrada_manual',
    NOW() - make_interval(mins => 320), NOW() - make_interval(mins => 265), NOW() - make_interval(mins => 265), NOW() - make_interval(mins => 310), NOW() - make_interval(mins => 320), NOW() - make_interval(mins => 265) + interval '24 hour', 0, 'Pedido concluido', NOW() - make_interval(mins => 265), NOW() - make_interval(mins => 320), NOW() - make_interval(mins => 265))
  ON CONFLICT (id) DO NOTHING;
  INSERT INTO mensagens (id, id_conversa, direcao, tipo, status, id_provedor, tentativas, criada_por, data_envio, data_entrega, data_leitura, data_criacao, conteudo)
  VALUES (pg_temp.sid(v_est, 'msg:maria:0'), pg_temp.sid(v_est, 'conv:maria'), 'entrada'::direcao_mensagem_enum, 'texto'::tipo_mensagem_enum, 'entregue'::status_mensagem_enum, 'seed-uberlandia-maria-0', 0, 'cliente', NOW() - make_interval(mins => 320), NOW() - make_interval(mins => 320), NULL, NOW() - make_interval(mins => 320), 'Boa tarde! Voces entregam no Fundinho?')
  ON CONFLICT DO NOTHING;
  INSERT INTO mensagens (id, id_conversa, direcao, tipo, status, id_provedor, tentativas, criada_por, data_envio, data_entrega, data_leitura, data_criacao, conteudo)
  VALUES (pg_temp.sid(v_est, 'msg:maria:1'), pg_temp.sid(v_est, 'conv:maria'), 'saida'::direcao_mensagem_enum, 'texto'::tipo_mensagem_enum, 'lida'::status_mensagem_enum, 'seed-uberlandia-maria-1', 0, 'agente', NOW() - make_interval(mins => 319), NOW() - make_interval(mins => 319), NOW() - make_interval(mins => 319), NOW() - make_interval(mins => 319), 'Boa tarde, Maria! Entregamos sim. O que vai ser hoje?')
  ON CONFLICT DO NOTHING;
  INSERT INTO mensagens (id, id_conversa, direcao, tipo, status, id_provedor, tentativas, criada_por, data_envio, data_entrega, data_leitura, data_criacao, conteudo)
  VALUES (pg_temp.sid(v_est, 'msg:maria:2'), pg_temp.sid(v_est, 'conv:maria'), 'entrada'::direcao_mensagem_enum, 'texto'::tipo_mensagem_enum, 'entregue'::status_mensagem_enum, 'seed-uberlandia-maria-2', 0, 'cliente', NOW() - make_interval(mins => 318), NOW() - make_interval(mins => 318), NULL, NOW() - make_interval(mins => 318), 'Queria um PF tropeiro e um suco de caju sem acucar')
  ON CONFLICT DO NOTHING;
  INSERT INTO mensagens (id, id_conversa, direcao, tipo, status, id_provedor, tentativas, criada_por, data_envio, data_entrega, data_leitura, data_criacao, conteudo)
  VALUES (pg_temp.sid(v_est, 'msg:maria:3'), pg_temp.sid(v_est, 'conv:maria'), 'saida'::direcao_mensagem_enum, 'texto'::tipo_mensagem_enum, 'lida'::status_mensagem_enum, 'seed-uberlandia-maria-3', 0, 'agente', NOW() - make_interval(mins => 316), NOW() - make_interval(mins => 316), NOW() - make_interval(mins => 316), NOW() - make_interval(mins => 316), 'Anotado! PF Tropeiro + Suco de Caju sem acucar. Entrega R$ 6,00, total R$ 48,00. Pago no PIX?')
  ON CONFLICT DO NOTHING;
  INSERT INTO mensagens (id, id_conversa, direcao, tipo, status, id_provedor, tentativas, criada_por, data_envio, data_entrega, data_leitura, data_criacao, conteudo)
  VALUES (pg_temp.sid(v_est, 'msg:maria:4'), pg_temp.sid(v_est, 'conv:maria'), 'entrada'::direcao_mensagem_enum, 'texto'::tipo_mensagem_enum, 'entregue'::status_mensagem_enum, 'seed-uberlandia-maria-4', 0, 'cliente', NOW() - make_interval(mins => 315), NOW() - make_interval(mins => 315), NULL, NOW() - make_interval(mins => 315), 'Isso, pode mandar a chave')
  ON CONFLICT DO NOTHING;
  INSERT INTO mensagens (id, id_conversa, direcao, tipo, status, id_provedor, tentativas, criada_por, data_envio, data_entrega, data_leitura, data_criacao, conteudo)
  VALUES (pg_temp.sid(v_est, 'msg:maria:5'), pg_temp.sid(v_est, 'conv:maria'), 'saida'::direcao_mensagem_enum, 'texto'::tipo_mensagem_enum, 'lida'::status_mensagem_enum, 'seed-uberlandia-maria-5', 0, 'agente', NOW() - make_interval(mins => 314), NOW() - make_interval(mins => 314), NOW() - make_interval(mins => 314), NOW() - make_interval(mins => 314), 'Chave PIX: contato@saboruberlandia.example. Assim que confirmar ja liberamos.')
  ON CONFLICT DO NOTHING;
  INSERT INTO mensagens (id, id_conversa, direcao, tipo, status, id_provedor, tentativas, criada_por, data_envio, data_entrega, data_leitura, data_criacao, conteudo)
  VALUES (pg_temp.sid(v_est, 'msg:maria:6'), pg_temp.sid(v_est, 'conv:maria'), 'entrada'::direcao_mensagem_enum, 'texto'::tipo_mensagem_enum, 'entregue'::status_mensagem_enum, 'seed-uberlandia-maria-6', 0, 'cliente', NOW() - make_interval(mins => 311), NOW() - make_interval(mins => 311), NULL, NOW() - make_interval(mins => 311), 'Paguei!')
  ON CONFLICT DO NOTHING;
  INSERT INTO mensagens (id, id_conversa, direcao, tipo, status, id_provedor, tentativas, criada_por, data_envio, data_entrega, data_leitura, data_criacao, conteudo)
  VALUES (pg_temp.sid(v_est, 'msg:maria:7'), pg_temp.sid(v_est, 'conv:maria'), 'saida'::direcao_mensagem_enum, 'texto'::tipo_mensagem_enum, 'lida'::status_mensagem_enum, 'seed-uberlandia-maria-7', 0, 'agente', NOW() - make_interval(mins => 310), NOW() - make_interval(mins => 310), NOW() - make_interval(mins => 310), NOW() - make_interval(mins => 310), 'Recebido, obrigado! Saindo para entrega em instantes.')
  ON CONFLICT DO NOTHING;
  INSERT INTO mensagens (id, id_conversa, direcao, tipo, status, id_provedor, tentativas, criada_por, data_envio, data_entrega, data_leitura, data_criacao, conteudo)
  VALUES (pg_temp.sid(v_est, 'msg:maria:8'), pg_temp.sid(v_est, 'conv:maria'), 'entrada'::direcao_mensagem_enum, 'texto'::tipo_mensagem_enum, 'entregue'::status_mensagem_enum, 'seed-uberlandia-maria-8', 0, 'cliente', NOW() - make_interval(mins => 265), NOW() - make_interval(mins => 265), NULL, NOW() - make_interval(mins => 265), 'Chegou tudo certinho, muito bom!')
  ON CONFLICT DO NOTHING;
  INSERT INTO conversas (id, id_conversa_grupo, id_estabelecimento, id_cliente, canal, estado, status_atendimento, data_primeira_mensagem, data_ultima_mensagem, data_ultima_entrada, data_ultima_saida, janela_24h_inicio, janela_24h_fim, qtd_nao_lidas, motivo_fechamento, data_fechamento, data_criacao, data_atualizacao)
  VALUES (pg_temp.sid(v_est, 'conv:joao'), pg_temp.sid(v_est, 'conv:joao'), v_est, (SELECT id FROM clientes WHERE id_estabelecimento = v_est AND telefone_e164 = '+5534991230002'), 'whatsapp'::canal_chat_enum, 'fechado_agente'::estado_conversa_enum, 'encerrada_manual',
    NOW() - make_interval(mins => 270), NOW() - make_interval(mins => 263), NOW() - make_interval(mins => 264), NOW() - make_interval(mins => 263), NOW() - make_interval(mins => 270), NOW() - make_interval(mins => 264) + interval '24 hour', 0, 'Pedido concluido', NOW() - make_interval(mins => 263), NOW() - make_interval(mins => 270), NOW() - make_interval(mins => 263))
  ON CONFLICT (id) DO NOTHING;
  INSERT INTO mensagens (id, id_conversa, direcao, tipo, status, id_provedor, tentativas, criada_por, data_envio, data_entrega, data_leitura, data_criacao, conteudo)
  VALUES (pg_temp.sid(v_est, 'msg:joao:0'), pg_temp.sid(v_est, 'conv:joao'), 'entrada'::direcao_mensagem_enum, 'texto'::tipo_mensagem_enum, 'entregue'::status_mensagem_enum, 'seed-uberlandia-joao-0', 0, 'cliente', NOW() - make_interval(mins => 270), NOW() - make_interval(mins => 270), NULL, NOW() - make_interval(mins => 270), 'Oi, tem X-Bacon?')
  ON CONFLICT DO NOTHING;
  INSERT INTO mensagens (id, id_conversa, direcao, tipo, status, id_provedor, tentativas, criada_por, data_envio, data_entrega, data_leitura, data_criacao, conteudo)
  VALUES (pg_temp.sid(v_est, 'msg:joao:1'), pg_temp.sid(v_est, 'conv:joao'), 'saida'::direcao_mensagem_enum, 'texto'::tipo_mensagem_enum, 'lida'::status_mensagem_enum, 'seed-uberlandia-joao-1', 0, 'agente', NOW() - make_interval(mins => 268), NOW() - make_interval(mins => 268), NOW() - make_interval(mins => 268), NOW() - make_interval(mins => 268), 'Temos! Quantos vai querer?')
  ON CONFLICT DO NOTHING;
  INSERT INTO mensagens (id, id_conversa, direcao, tipo, status, id_provedor, tentativas, criada_por, data_envio, data_entrega, data_leitura, data_criacao, conteudo)
  VALUES (pg_temp.sid(v_est, 'msg:joao:2'), pg_temp.sid(v_est, 'conv:joao'), 'entrada'::direcao_mensagem_enum, 'texto'::tipo_mensagem_enum, 'entregue'::status_mensagem_enum, 'seed-uberlandia-joao-2', 0, 'cliente', NOW() - make_interval(mins => 267), NOW() - make_interval(mins => 267), NULL, NOW() - make_interval(mins => 267), '2, um deles com ovo. E um refri de 2 litros de guarana')
  ON CONFLICT DO NOTHING;
  INSERT INTO mensagens (id, id_conversa, direcao, tipo, status, id_provedor, tentativas, criada_por, data_envio, data_entrega, data_leitura, data_criacao, conteudo)
  VALUES (pg_temp.sid(v_est, 'msg:joao:3'), pg_temp.sid(v_est, 'conv:joao'), 'saida'::direcao_mensagem_enum, 'texto'::tipo_mensagem_enum, 'lida'::status_mensagem_enum, 'seed-uberlandia-joao-3', 0, 'agente', NOW() - make_interval(mins => 265), NOW() - make_interval(mins => 265), NOW() - make_interval(mins => 265), NOW() - make_interval(mins => 265), 'Fechado: 2 X-Bacon (1 com ovo), 1 Guarana 2 L. Total R$ 79,50 com entrega. Cartao na entrega?')
  ON CONFLICT DO NOTHING;
  INSERT INTO mensagens (id, id_conversa, direcao, tipo, status, id_provedor, tentativas, criada_por, data_envio, data_entrega, data_leitura, data_criacao, conteudo)
  VALUES (pg_temp.sid(v_est, 'msg:joao:4'), pg_temp.sid(v_est, 'conv:joao'), 'entrada'::direcao_mensagem_enum, 'texto'::tipo_mensagem_enum, 'entregue'::status_mensagem_enum, 'seed-uberlandia-joao-4', 0, 'cliente', NOW() - make_interval(mins => 264), NOW() - make_interval(mins => 264), NULL, NOW() - make_interval(mins => 264), 'Cartao mesmo, moro na Santa Monica, Av. Joao Naves de Avila 1800')
  ON CONFLICT DO NOTHING;
  INSERT INTO mensagens (id, id_conversa, direcao, tipo, status, id_provedor, tentativas, criada_por, data_envio, data_entrega, data_leitura, data_criacao, conteudo)
  VALUES (pg_temp.sid(v_est, 'msg:joao:5'), pg_temp.sid(v_est, 'conv:joao'), 'saida'::direcao_mensagem_enum, 'texto'::tipo_mensagem_enum, 'lida'::status_mensagem_enum, 'seed-uberlandia-joao-5', 0, 'agente', NOW() - make_interval(mins => 263), NOW() - make_interval(mins => 263), NOW() - make_interval(mins => 263), NOW() - make_interval(mins => 263), 'Perfeito, pedido lancado!')
  ON CONFLICT DO NOTHING;
  INSERT INTO conversas (id, id_conversa_grupo, id_estabelecimento, id_cliente, canal, estado, status_atendimento, data_primeira_mensagem, data_ultima_mensagem, data_ultima_entrada, data_ultima_saida, janela_24h_inicio, janela_24h_fim, qtd_nao_lidas, motivo_fechamento, data_fechamento, data_criacao, data_atualizacao)
  VALUES (pg_temp.sid(v_est, 'conv:carlos'), pg_temp.sid(v_est, 'conv:carlos'), v_est, (SELECT id FROM clientes WHERE id_estabelecimento = v_est AND telefone_e164 = '+5534991230004'), 'whatsapp'::canal_chat_enum, 'fechado_agente'::estado_conversa_enum, 'encerrada_manual',
    NOW() - make_interval(mins => 1520), NOW() - make_interval(mins => 1514), NOW() - make_interval(mins => 1516), NOW() - make_interval(mins => 1514), NOW() - make_interval(mins => 1520), NOW() - make_interval(mins => 1516) + interval '24 hour', 0, 'Pedido concluido', NOW() - make_interval(mins => 1514), NOW() - make_interval(mins => 1520), NOW() - make_interval(mins => 1514))
  ON CONFLICT (id) DO NOTHING;
  INSERT INTO mensagens (id, id_conversa, direcao, tipo, status, id_provedor, tentativas, criada_por, data_envio, data_entrega, data_leitura, data_criacao, conteudo)
  VALUES (pg_temp.sid(v_est, 'msg:carlos:0'), pg_temp.sid(v_est, 'conv:carlos'), 'entrada'::direcao_mensagem_enum, 'texto'::tipo_mensagem_enum, 'entregue'::status_mensagem_enum, 'seed-uberlandia-carlos-0', 0, 'cliente', NOW() - make_interval(mins => 1520), NOW() - make_interval(mins => 1520), NULL, NOW() - make_interval(mins => 1520), 'Boa noite, a feijoada de hoje ainda tem?')
  ON CONFLICT DO NOTHING;
  INSERT INTO mensagens (id, id_conversa, direcao, tipo, status, id_provedor, tentativas, criada_por, data_envio, data_entrega, data_leitura, data_criacao, conteudo)
  VALUES (pg_temp.sid(v_est, 'msg:carlos:1'), pg_temp.sid(v_est, 'conv:carlos'), 'saida'::direcao_mensagem_enum, 'texto'::tipo_mensagem_enum, 'lida'::status_mensagem_enum, 'seed-uberlandia-carlos-1', 0, 'agente', NOW() - make_interval(mins => 1517), NOW() - make_interval(mins => 1517), NOW() - make_interval(mins => 1517), NOW() - make_interval(mins => 1517), 'Boa noite, Carlos! Tem sim. Serve bem uma pessoa cada.')
  ON CONFLICT DO NOTHING;
  INSERT INTO mensagens (id, id_conversa, direcao, tipo, status, id_provedor, tentativas, criada_por, data_envio, data_entrega, data_leitura, data_criacao, conteudo)
  VALUES (pg_temp.sid(v_est, 'msg:carlos:2'), pg_temp.sid(v_est, 'conv:carlos'), 'entrada'::direcao_mensagem_enum, 'texto'::tipo_mensagem_enum, 'entregue'::status_mensagem_enum, 'seed-uberlandia-carlos-2', 0, 'cliente', NOW() - make_interval(mins => 1516), NOW() - make_interval(mins => 1516), NULL, NOW() - make_interval(mins => 1516), 'Manda 2 feijoadas e 2 guaranas. Pago em dinheiro, troco para 100')
  ON CONFLICT DO NOTHING;
  INSERT INTO mensagens (id, id_conversa, direcao, tipo, status, id_provedor, tentativas, criada_por, data_envio, data_entrega, data_leitura, data_criacao, conteudo)
  VALUES (pg_temp.sid(v_est, 'msg:carlos:3'), pg_temp.sid(v_est, 'conv:carlos'), 'saida'::direcao_mensagem_enum, 'texto'::tipo_mensagem_enum, 'lida'::status_mensagem_enum, 'seed-uberlandia-carlos-3', 0, 'agente', NOW() - make_interval(mins => 1514), NOW() - make_interval(mins => 1514), NOW() - make_interval(mins => 1514), NOW() - make_interval(mins => 1514), 'Certo! Total R$ 104,00 com a entrega no Centro. Ja estamos preparando.')
  ON CONFLICT DO NOTHING;
  INSERT INTO conversas (id, id_conversa_grupo, id_estabelecimento, id_cliente, canal, estado, status_atendimento, data_primeira_mensagem, data_ultima_mensagem, data_ultima_entrada, data_ultima_saida, janela_24h_inicio, janela_24h_fim, qtd_nao_lidas, motivo_fechamento, data_fechamento, data_criacao, data_atualizacao)
  VALUES (pg_temp.sid(v_est, 'conv:juliana'), pg_temp.sid(v_est, 'conv:juliana'), v_est, (SELECT id FROM clientes WHERE id_estabelecimento = v_est AND telefone_e164 = '+5534991230007'), 'whatsapp'::canal_chat_enum, 'fechado_agente'::estado_conversa_enum, 'encerrada_manual',
    NOW() - make_interval(mins => 410), NOW() - make_interval(mins => 401), NOW() - make_interval(mins => 402), NOW() - make_interval(mins => 401), NOW() - make_interval(mins => 410), NOW() - make_interval(mins => 402) + interval '24 hour', 0, 'Cliente desistiu', NOW() - make_interval(mins => 401), NOW() - make_interval(mins => 410), NOW() - make_interval(mins => 401))
  ON CONFLICT (id) DO NOTHING;
  INSERT INTO mensagens (id, id_conversa, direcao, tipo, status, id_provedor, tentativas, criada_por, data_envio, data_entrega, data_leitura, data_criacao, conteudo)
  VALUES (pg_temp.sid(v_est, 'msg:juliana:0'), pg_temp.sid(v_est, 'conv:juliana'), 'entrada'::direcao_mensagem_enum, 'texto'::tipo_mensagem_enum, 'entregue'::status_mensagem_enum, 'seed-uberlandia-juliana-0', 0, 'cliente', NOW() - make_interval(mins => 410), NOW() - make_interval(mins => 410), NULL, NOW() - make_interval(mins => 410), 'Quero 3 x-burguer com queijo extra pro bairro Brasil')
  ON CONFLICT DO NOTHING;
  INSERT INTO mensagens (id, id_conversa, direcao, tipo, status, id_provedor, tentativas, criada_por, data_envio, data_entrega, data_leitura, data_criacao, conteudo)
  VALUES (pg_temp.sid(v_est, 'msg:juliana:1'), pg_temp.sid(v_est, 'conv:juliana'), 'saida'::direcao_mensagem_enum, 'texto'::tipo_mensagem_enum, 'lida'::status_mensagem_enum, 'seed-uberlandia-juliana-1', 0, 'agente', NOW() - make_interval(mins => 408), NOW() - make_interval(mins => 408), NOW() - make_interval(mins => 408), NOW() - make_interval(mins => 408), 'Claro! Fica R$ 91,50 com a entrega (R$ 9,00). Confirma?')
  ON CONFLICT DO NOTHING;
  INSERT INTO mensagens (id, id_conversa, direcao, tipo, status, id_provedor, tentativas, criada_por, data_envio, data_entrega, data_leitura, data_criacao, conteudo)
  VALUES (pg_temp.sid(v_est, 'msg:juliana:2'), pg_temp.sid(v_est, 'conv:juliana'), 'entrada'::direcao_mensagem_enum, 'texto'::tipo_mensagem_enum, 'entregue'::status_mensagem_enum, 'seed-uberlandia-juliana-2', 0, 'cliente', NOW() - make_interval(mins => 402), NOW() - make_interval(mins => 402), NULL, NOW() - make_interval(mins => 402), 'Ai, achei a taxa alta, vou deixar pra outro dia. Obrigada!')
  ON CONFLICT DO NOTHING;
  INSERT INTO mensagens (id, id_conversa, direcao, tipo, status, id_provedor, tentativas, criada_por, data_envio, data_entrega, data_leitura, data_criacao, conteudo)
  VALUES (pg_temp.sid(v_est, 'msg:juliana:3'), pg_temp.sid(v_est, 'conv:juliana'), 'saida'::direcao_mensagem_enum, 'texto'::tipo_mensagem_enum, 'lida'::status_mensagem_enum, 'seed-uberlandia-juliana-3', 0, 'agente', NOW() - make_interval(mins => 401), NOW() - make_interval(mins => 401), NOW() - make_interval(mins => 401), NOW() - make_interval(mins => 401), 'Sem problemas, Juliana! Cancelei o pedido. Quando quiser, e so chamar.')
  ON CONFLICT DO NOTHING;
  INSERT INTO conversas (id, id_conversa_grupo, id_estabelecimento, id_cliente, canal, estado, status_atendimento, data_primeira_mensagem, data_ultima_mensagem, data_ultima_entrada, data_ultima_saida, janela_24h_inicio, janela_24h_fim, qtd_nao_lidas, motivo_fechamento, data_fechamento, data_criacao, data_atualizacao)
  VALUES (pg_temp.sid(v_est, 'conv:luciana'), pg_temp.sid(v_est, 'conv:luciana'), v_est, (SELECT id FROM clientes WHERE id_estabelecimento = v_est AND telefone_e164 = '+5534991230009'), 'whatsapp'::canal_chat_enum, 'em_atendimento'::estado_conversa_enum, 'em_andamento',
    NOW() - make_interval(mins => 20), NOW() - make_interval(mins => 12), NOW() - make_interval(mins => 14), NOW() - make_interval(mins => 12), NOW() - make_interval(mins => 20), NOW() - make_interval(mins => 14) + interval '24 hour', 0, NULL, NULL, NOW() - make_interval(mins => 20), NOW() - make_interval(mins => 12))
  ON CONFLICT (id) DO NOTHING;
  INSERT INTO mensagens (id, id_conversa, direcao, tipo, status, id_provedor, tentativas, criada_por, data_envio, data_entrega, data_leitura, data_criacao, conteudo)
  VALUES (pg_temp.sid(v_est, 'msg:luciana:0'), pg_temp.sid(v_est, 'conv:luciana'), 'entrada'::direcao_mensagem_enum, 'texto'::tipo_mensagem_enum, 'entregue'::status_mensagem_enum, 'seed-uberlandia-luciana-0', 0, 'cliente', NOW() - make_interval(mins => 20), NOW() - make_interval(mins => 20), NULL, NOW() - make_interval(mins => 20), 'Boa noite! 2 PF tropeiro e 1 refri de 2 litros, por favor')
  ON CONFLICT DO NOTHING;
  INSERT INTO mensagens (id, id_conversa, direcao, tipo, status, id_provedor, tentativas, criada_por, data_envio, data_entrega, data_leitura, data_criacao, conteudo)
  VALUES (pg_temp.sid(v_est, 'msg:luciana:1'), pg_temp.sid(v_est, 'conv:luciana'), 'saida'::direcao_mensagem_enum, 'texto'::tipo_mensagem_enum, 'lida'::status_mensagem_enum, 'seed-uberlandia-luciana-1', 0, 'agente', NOW() - make_interval(mins => 18), NOW() - make_interval(mins => 18), NOW() - make_interval(mins => 18), NOW() - make_interval(mins => 18), 'Boa noite, Luciana! Qual sabor do refri?')
  ON CONFLICT DO NOTHING;
  INSERT INTO mensagens (id, id_conversa, direcao, tipo, status, id_provedor, tentativas, criada_por, data_envio, data_entrega, data_leitura, data_criacao, conteudo)
  VALUES (pg_temp.sid(v_est, 'msg:luciana:2'), pg_temp.sid(v_est, 'conv:luciana'), 'entrada'::direcao_mensagem_enum, 'texto'::tipo_mensagem_enum, 'entregue'::status_mensagem_enum, 'seed-uberlandia-luciana-2', 0, 'cliente', NOW() - make_interval(mins => 17), NOW() - make_interval(mins => 17), NULL, NOW() - make_interval(mins => 17), 'Coca-Cola. Entrega na Av. Floriano Peixoto 410, Centro')
  ON CONFLICT DO NOTHING;
  INSERT INTO mensagens (id, id_conversa, direcao, tipo, status, id_provedor, tentativas, criada_por, data_envio, data_entrega, data_leitura, data_criacao, conteudo)
  VALUES (pg_temp.sid(v_est, 'msg:luciana:3'), pg_temp.sid(v_est, 'conv:luciana'), 'saida'::direcao_mensagem_enum, 'texto'::tipo_mensagem_enum, 'lida'::status_mensagem_enum, 'seed-uberlandia-luciana-3', 0, 'agente', NOW() - make_interval(mins => 15), NOW() - make_interval(mins => 15), NOW() - make_interval(mins => 15), NOW() - make_interval(mins => 15), 'Anotado! Total R$ 83,00 (entrega R$ 5,00). Pagamento no PIX?')
  ON CONFLICT DO NOTHING;
  INSERT INTO mensagens (id, id_conversa, direcao, tipo, status, id_provedor, tentativas, criada_por, data_envio, data_entrega, data_leitura, data_criacao, conteudo)
  VALUES (pg_temp.sid(v_est, 'msg:luciana:4'), pg_temp.sid(v_est, 'conv:luciana'), 'entrada'::direcao_mensagem_enum, 'texto'::tipo_mensagem_enum, 'entregue'::status_mensagem_enum, 'seed-uberlandia-luciana-4', 0, 'cliente', NOW() - make_interval(mins => 14), NOW() - make_interval(mins => 14), NULL, NOW() - make_interval(mins => 14), 'PIX, ja faco a transferencia')
  ON CONFLICT DO NOTHING;
  INSERT INTO mensagens (id, id_conversa, direcao, tipo, status, id_provedor, tentativas, criada_por, data_envio, data_entrega, data_leitura, data_criacao, conteudo)
  VALUES (pg_temp.sid(v_est, 'msg:luciana:5'), pg_temp.sid(v_est, 'conv:luciana'), 'saida'::direcao_mensagem_enum, 'texto'::tipo_mensagem_enum, 'lida'::status_mensagem_enum, 'seed-uberlandia-luciana-5', 0, 'agente', NOW() - make_interval(mins => 12), NOW() - make_interval(mins => 12), NOW() - make_interval(mins => 12), NOW() - make_interval(mins => 12), 'Pedido lancado! Codigo de entrega: 4821. Previsao de 40 minutos.')
  ON CONFLICT DO NOTHING;
  INSERT INTO conversas (id, id_conversa_grupo, id_estabelecimento, id_cliente, canal, estado, status_atendimento, data_primeira_mensagem, data_ultima_mensagem, data_ultima_entrada, data_ultima_saida, janela_24h_inicio, janela_24h_fim, qtd_nao_lidas, motivo_fechamento, data_fechamento, data_criacao, data_atualizacao)
  VALUES (pg_temp.sid(v_est, 'conv:thiago'), pg_temp.sid(v_est, 'conv:thiago'), v_est, (SELECT id FROM clientes WHERE id_estabelecimento = v_est AND telefone_e164 = '+5534991230012'), 'whatsapp'::canal_chat_enum, 'em_atendimento'::estado_conversa_enum, 'aguardando_cliente',
    NOW() - make_interval(mins => 9), NOW() - make_interval(mins => 5), NOW() - make_interval(mins => 7), NOW() - make_interval(mins => 5), NOW() - make_interval(mins => 9), NOW() - make_interval(mins => 7) + interval '24 hour', 0, NULL, NULL, NOW() - make_interval(mins => 9), NOW() - make_interval(mins => 5))
  ON CONFLICT (id) DO NOTHING;
  INSERT INTO mensagens (id, id_conversa, direcao, tipo, status, id_provedor, tentativas, criada_por, data_envio, data_entrega, data_leitura, data_criacao, conteudo)
  VALUES (pg_temp.sid(v_est, 'msg:thiago:0'), pg_temp.sid(v_est, 'conv:thiago'), 'entrada'::direcao_mensagem_enum, 'texto'::tipo_mensagem_enum, 'entregue'::status_mensagem_enum, 'seed-uberlandia-thiago-0', 0, 'cliente', NOW() - make_interval(mins => 9), NOW() - make_interval(mins => 9), NULL, NOW() - make_interval(mins => 9), 'Oi, vocês entregam no Jardim Karaiba?')
  ON CONFLICT DO NOTHING;
  INSERT INTO mensagens (id, id_conversa, direcao, tipo, status, id_provedor, tentativas, criada_por, data_envio, data_entrega, data_leitura, data_criacao, conteudo)
  VALUES (pg_temp.sid(v_est, 'msg:thiago:1'), pg_temp.sid(v_est, 'conv:thiago'), 'saida'::direcao_mensagem_enum, 'texto'::tipo_mensagem_enum, 'lida'::status_mensagem_enum, 'seed-uberlandia-thiago-1', 0, 'agente', NOW() - make_interval(mins => 8), NOW() - make_interval(mins => 8), NOW() - make_interval(mins => 8), NOW() - make_interval(mins => 8), 'Oi, Thiago! Entregamos sim, taxa de R$ 9,00. O que vai querer?')
  ON CONFLICT DO NOTHING;
  INSERT INTO mensagens (id, id_conversa, direcao, tipo, status, id_provedor, tentativas, criada_por, data_envio, data_entrega, data_leitura, data_criacao, conteudo)
  VALUES (pg_temp.sid(v_est, 'msg:thiago:2'), pg_temp.sid(v_est, 'conv:thiago'), 'entrada'::direcao_mensagem_enum, 'texto'::tipo_mensagem_enum, 'entregue'::status_mensagem_enum, 'seed-uberlandia-thiago-2', 0, 'cliente', NOW() - make_interval(mins => 7), NOW() - make_interval(mins => 7), NULL, NOW() - make_interval(mins => 7), '2 x-salada com ovo e 2 guaranas lata. Dinheiro, troco pra 50')
  ON CONFLICT DO NOTHING;
  INSERT INTO mensagens (id, id_conversa, direcao, tipo, status, id_provedor, tentativas, criada_por, data_envio, data_entrega, data_leitura, data_criacao, conteudo)
  VALUES (pg_temp.sid(v_est, 'msg:thiago:3'), pg_temp.sid(v_est, 'conv:thiago'), 'saida'::direcao_mensagem_enum, 'texto'::tipo_mensagem_enum, 'lida'::status_mensagem_enum, 'seed-uberlandia-thiago-3', 0, 'agente', NOW() - make_interval(mins => 5), NOW() - make_interval(mins => 5), NOW() - make_interval(mins => 5), NOW() - make_interval(mins => 5), 'Pedido lancado! Portao azul, campainha, certo? Em uns 45 minutos chega.')
  ON CONFLICT DO NOTHING;
  INSERT INTO conversas (id, id_conversa_grupo, id_estabelecimento, id_cliente, canal, estado, status_atendimento, data_primeira_mensagem, data_ultima_mensagem, data_ultima_entrada, data_ultima_saida, janela_24h_inicio, janela_24h_fim, qtd_nao_lidas, motivo_fechamento, data_fechamento, data_criacao, data_atualizacao)
  VALUES (pg_temp.sid(v_est, 'conv:ana'), pg_temp.sid(v_est, 'conv:ana'), v_est, (SELECT id FROM clientes WHERE id_estabelecimento = v_est AND telefone_e164 = '+5534991230003'), 'whatsapp'::canal_chat_enum, 'em_atendimento'::estado_conversa_enum, 'em_andamento',
    NOW() - make_interval(mins => 14), NOW() - make_interval(mins => 7), NOW() - make_interval(mins => 7), NOW() - make_interval(mins => 8), NOW() - make_interval(mins => 14), NOW() - make_interval(mins => 7) + interval '24 hour', 1, NULL, NULL, NOW() - make_interval(mins => 14), NOW() - make_interval(mins => 7))
  ON CONFLICT (id) DO NOTHING;
  INSERT INTO mensagens (id, id_conversa, direcao, tipo, status, id_provedor, tentativas, criada_por, data_envio, data_entrega, data_leitura, data_criacao, conteudo)
  VALUES (pg_temp.sid(v_est, 'msg:ana:0'), pg_temp.sid(v_est, 'conv:ana'), 'entrada'::direcao_mensagem_enum, 'texto'::tipo_mensagem_enum, 'entregue'::status_mensagem_enum, 'seed-uberlandia-ana-0', 0, 'cliente', NOW() - make_interval(mins => 14), NOW() - make_interval(mins => 14), NULL, NOW() - make_interval(mins => 14), 'Oi! Queria porcao de pao de queijo e 2 sucos de caju')
  ON CONFLICT DO NOTHING;
  INSERT INTO mensagens (id, id_conversa, direcao, tipo, status, id_provedor, tentativas, criada_por, data_envio, data_entrega, data_leitura, data_criacao, conteudo)
  VALUES (pg_temp.sid(v_est, 'msg:ana:1'), pg_temp.sid(v_est, 'conv:ana'), 'saida'::direcao_mensagem_enum, 'texto'::tipo_mensagem_enum, 'lida'::status_mensagem_enum, 'seed-uberlandia-ana-1', 0, 'agente', NOW() - make_interval(mins => 12), NOW() - make_interval(mins => 12), NOW() - make_interval(mins => 12), NOW() - make_interval(mins => 12), 'Ola, Ana! Beleza. Continua no mesmo endereco do Tibery?')
  ON CONFLICT DO NOTHING;
  INSERT INTO mensagens (id, id_conversa, direcao, tipo, status, id_provedor, tentativas, criada_por, data_envio, data_entrega, data_leitura, data_criacao, conteudo)
  VALUES (pg_temp.sid(v_est, 'msg:ana:2'), pg_temp.sid(v_est, 'conv:ana'), 'entrada'::direcao_mensagem_enum, 'texto'::tipo_mensagem_enum, 'entregue'::status_mensagem_enum, 'seed-uberlandia-ana-2', 0, 'cliente', NOW() - make_interval(mins => 10), NOW() - make_interval(mins => 10), NULL, NOW() - make_interval(mins => 10), 'Mudei, agora e na Av. Rondon Pacheco, 2500 mesmo, apto 31')
  ON CONFLICT DO NOTHING;
  INSERT INTO mensagens (id, id_conversa, direcao, tipo, status, id_provedor, tentativas, criada_por, data_envio, data_entrega, data_leitura, data_criacao, conteudo)
  VALUES (pg_temp.sid(v_est, 'msg:ana:3'), pg_temp.sid(v_est, 'conv:ana'), 'saida'::direcao_mensagem_enum, 'texto'::tipo_mensagem_enum, 'lida'::status_mensagem_enum, 'seed-uberlandia-ana-3', 0, 'agente', NOW() - make_interval(mins => 8), NOW() - make_interval(mins => 8), NOW() - make_interval(mins => 8), NOW() - make_interval(mins => 8), 'Pode confirmar o CEP pra mim?')
  ON CONFLICT DO NOTHING;
  INSERT INTO mensagens (id, id_conversa, direcao, tipo, status, id_provedor, tentativas, criada_por, data_envio, data_entrega, data_leitura, data_criacao, conteudo)
  VALUES (pg_temp.sid(v_est, 'msg:ana:4'), pg_temp.sid(v_est, 'conv:ana'), 'entrada'::direcao_mensagem_enum, 'texto'::tipo_mensagem_enum, 'entregue'::status_mensagem_enum, 'seed-uberlandia-ana-4', 0, 'cliente', NOW() - make_interval(mins => 7), NOW() - make_interval(mins => 7), NULL, NOW() - make_interval(mins => 7), '38405-142! Vai dar quanto?')
  ON CONFLICT DO NOTHING;
  INSERT INTO conversas (id, id_conversa_grupo, id_estabelecimento, id_cliente, canal, estado, status_atendimento, data_primeira_mensagem, data_ultima_mensagem, data_ultima_entrada, data_ultima_saida, janela_24h_inicio, janela_24h_fim, qtd_nao_lidas, motivo_fechamento, data_fechamento, data_criacao, data_atualizacao)
  VALUES (pg_temp.sid(v_est, 'conv:fernanda'), pg_temp.sid(v_est, 'conv:fernanda'), v_est, (SELECT id FROM clientes WHERE id_estabelecimento = v_est AND telefone_e164 = '+5534991230005'), 'whatsapp'::canal_chat_enum, 'em_atendimento'::estado_conversa_enum, 'aguardando_interno',
    NOW() - make_interval(mins => 3), NOW() - make_interval(mins => 2), NOW() - make_interval(mins => 2), NULL, NOW() - make_interval(mins => 3), NOW() - make_interval(mins => 2) + interval '24 hour', 2, NULL, NULL, NOW() - make_interval(mins => 3), NOW() - make_interval(mins => 2))
  ON CONFLICT (id) DO NOTHING;
  INSERT INTO mensagens (id, id_conversa, direcao, tipo, status, id_provedor, tentativas, criada_por, data_envio, data_entrega, data_leitura, data_criacao, conteudo)
  VALUES (pg_temp.sid(v_est, 'msg:fernanda:0'), pg_temp.sid(v_est, 'conv:fernanda'), 'entrada'::direcao_mensagem_enum, 'texto'::tipo_mensagem_enum, 'entregue'::status_mensagem_enum, 'seed-uberlandia-fernanda-0', 0, 'cliente', NOW() - make_interval(mins => 3), NOW() - make_interval(mins => 3), NULL, NOW() - make_interval(mins => 3), 'Boa noite, ainda estao atendendo?')
  ON CONFLICT DO NOTHING;
  INSERT INTO mensagens (id, id_conversa, direcao, tipo, status, id_provedor, tentativas, criada_por, data_envio, data_entrega, data_leitura, data_criacao, conteudo)
  VALUES (pg_temp.sid(v_est, 'msg:fernanda:1'), pg_temp.sid(v_est, 'conv:fernanda'), 'entrada'::direcao_mensagem_enum, 'texto'::tipo_mensagem_enum, 'entregue'::status_mensagem_enum, 'seed-uberlandia-fernanda-1', 0, 'cliente', NOW() - make_interval(mins => 2), NOW() - make_interval(mins => 2), NULL, NOW() - make_interval(mins => 2), 'Queria saber o prazo de entrega para o Osvaldo Rezende')
  ON CONFLICT DO NOTHING;
  INSERT INTO conversas (id, id_conversa_grupo, id_estabelecimento, id_cliente, canal, estado, status_atendimento, data_primeira_mensagem, data_ultima_mensagem, data_ultima_entrada, data_ultima_saida, janela_24h_inicio, janela_24h_fim, qtd_nao_lidas, motivo_fechamento, data_fechamento, data_criacao, data_atualizacao)
  VALUES (pg_temp.sid(v_est, 'conv:ricardo'), pg_temp.sid(v_est, 'conv:ricardo'), v_est, (SELECT id FROM clientes WHERE id_estabelecimento = v_est AND telefone_e164 = '+5534991230006'), 'whatsapp'::canal_chat_enum, 'em_atendimento'::estado_conversa_enum, 'aguardando_interno',
    NOW() - make_interval(mins => 7), NOW() - make_interval(mins => 7), NOW() - make_interval(mins => 7), NULL, NOW() - make_interval(mins => 7), NOW() - make_interval(mins => 7) + interval '24 hour', 1, NULL, NULL, NOW() - make_interval(mins => 7), NOW() - make_interval(mins => 7))
  ON CONFLICT (id) DO NOTHING;
  INSERT INTO mensagens (id, id_conversa, direcao, tipo, status, id_provedor, tentativas, criada_por, data_envio, data_entrega, data_leitura, data_criacao, conteudo)
  VALUES (pg_temp.sid(v_est, 'msg:ricardo:0'), pg_temp.sid(v_est, 'conv:ricardo'), 'entrada'::direcao_mensagem_enum, 'texto'::tipo_mensagem_enum, 'entregue'::status_mensagem_enum, 'seed-uberlandia-ricardo-0', 0, 'cliente', NOW() - make_interval(mins => 7), NOW() - make_interval(mins => 7), NULL, NOW() - make_interval(mins => 7), 'Meu pedido do iFood atrasou, voces conseguem ver?')
  ON CONFLICT DO NOTHING;
  INSERT INTO conversas (id, id_conversa_grupo, id_estabelecimento, id_cliente, canal, estado, status_atendimento, data_primeira_mensagem, data_ultima_mensagem, data_ultima_entrada, data_ultima_saida, janela_24h_inicio, janela_24h_fim, qtd_nao_lidas, motivo_fechamento, data_fechamento, data_criacao, data_atualizacao)
  VALUES (pg_temp.sid(v_est, 'conv:paulo'), pg_temp.sid(v_est, 'conv:paulo'), v_est, (SELECT id FROM clientes WHERE id_estabelecimento = v_est AND telefone_e164 = '+5534991230008'), 'whatsapp'::canal_chat_enum, 'em_atendimento'::estado_conversa_enum, 'aguardando_interno',
    NOW() - make_interval(mins => 60), NOW() - make_interval(mins => 57), NOW() - make_interval(mins => 57), NULL, NOW() - make_interval(mins => 60), NOW() - make_interval(mins => 57) + interval '24 hour', 3, NULL, NULL, NOW() - make_interval(mins => 60), NOW() - make_interval(mins => 57))
  ON CONFLICT (id) DO NOTHING;
  INSERT INTO mensagens (id, id_conversa, direcao, tipo, status, id_provedor, tentativas, criada_por, data_envio, data_entrega, data_leitura, data_criacao, conteudo)
  VALUES (pg_temp.sid(v_est, 'msg:paulo:0'), pg_temp.sid(v_est, 'conv:paulo'), 'entrada'::direcao_mensagem_enum, 'texto'::tipo_mensagem_enum, 'entregue'::status_mensagem_enum, 'seed-uberlandia-paulo-0', 0, 'cliente', NOW() - make_interval(mins => 60), NOW() - make_interval(mins => 60), NULL, NOW() - make_interval(mins => 60), 'Olá, tem porcao de mandioca frita?')
  ON CONFLICT DO NOTHING;
  INSERT INTO mensagens (id, id_conversa, direcao, tipo, status, id_provedor, tentativas, criada_por, data_envio, data_entrega, data_leitura, data_criacao, conteudo)
  VALUES (pg_temp.sid(v_est, 'msg:paulo:1'), pg_temp.sid(v_est, 'conv:paulo'), 'entrada'::direcao_mensagem_enum, 'texto'::tipo_mensagem_enum, 'entregue'::status_mensagem_enum, 'seed-uberlandia-paulo-1', 0, 'cliente', NOW() - make_interval(mins => 58), NOW() - make_interval(mins => 58), NULL, NOW() - make_interval(mins => 58), 'Moro no Lidice, Rua Machado de Assis 77')
  ON CONFLICT DO NOTHING;
  INSERT INTO mensagens (id, id_conversa, direcao, tipo, status, id_provedor, tentativas, criada_por, data_envio, data_entrega, data_leitura, data_criacao, conteudo)
  VALUES (pg_temp.sid(v_est, 'msg:paulo:2'), pg_temp.sid(v_est, 'conv:paulo'), 'entrada'::direcao_mensagem_enum, 'texto'::tipo_mensagem_enum, 'entregue'::status_mensagem_enum, 'seed-uberlandia-paulo-2', 0, 'cliente', NOW() - make_interval(mins => 57), NOW() - make_interval(mins => 57), NULL, NOW() - make_interval(mins => 57), 'Alguem por ai?')
  ON CONFLICT DO NOTHING;
  INSERT INTO conversas (id, id_conversa_grupo, id_estabelecimento, id_cliente, canal, estado, status_atendimento, data_primeira_mensagem, data_ultima_mensagem, data_ultima_entrada, data_ultima_saida, janela_24h_inicio, janela_24h_fim, qtd_nao_lidas, motivo_fechamento, data_fechamento, data_criacao, data_atualizacao)
  VALUES (pg_temp.sid(v_est, 'conv:patricia'), pg_temp.sid(v_est, 'conv:patricia'), v_est, (SELECT id FROM clientes WHERE id_estabelecimento = v_est AND telefone_e164 = '+5534991230011'), 'whatsapp'::canal_chat_enum, 'aberto'::estado_conversa_enum, 'com_bot',
    NOW() - make_interval(mins => 200), NOW() - make_interval(mins => 199), NOW() - make_interval(mins => 200), NOW() - make_interval(mins => 199), NOW() - make_interval(mins => 200), NOW() - make_interval(mins => 200) + interval '24 hour', 0, NULL, NULL, NOW() - make_interval(mins => 200), NOW() - make_interval(mins => 199))
  ON CONFLICT (id) DO NOTHING;
  INSERT INTO mensagens (id, id_conversa, direcao, tipo, status, id_provedor, tentativas, criada_por, data_envio, data_entrega, data_leitura, data_criacao, conteudo)
  VALUES (pg_temp.sid(v_est, 'msg:patricia:0'), pg_temp.sid(v_est, 'conv:patricia'), 'entrada'::direcao_mensagem_enum, 'texto'::tipo_mensagem_enum, 'entregue'::status_mensagem_enum, 'seed-uberlandia-patricia-0', 0, 'cliente', NOW() - make_interval(mins => 200), NOW() - make_interval(mins => 200), NULL, NOW() - make_interval(mins => 200), 'Bom dia! Qual o horario de funcionamento?')
  ON CONFLICT DO NOTHING;
  INSERT INTO mensagens (id, id_conversa, direcao, tipo, status, id_provedor, tentativas, criada_por, data_envio, data_entrega, data_leitura, data_criacao, conteudo)
  VALUES (pg_temp.sid(v_est, 'msg:patricia:1'), pg_temp.sid(v_est, 'conv:patricia'), 'saida'::direcao_mensagem_enum, 'texto'::tipo_mensagem_enum, 'lida'::status_mensagem_enum, 'seed-uberlandia-patricia-1', 0, 'sistema', NOW() - make_interval(mins => 199), NOW() - make_interval(mins => 199), NOW() - make_interval(mins => 199), NOW() - make_interval(mins => 199), 'Bom dia! Funcionamos todos os dias, das 11h as 23h. Posso ajudar com um pedido?')
  ON CONFLICT DO NOTHING;
  INSERT INTO motoboy (nome, telefone, status, id_estabelecimento, is_simulated)
  SELECT 'Diego Santos', '34991230101', 2, v_est, TRUE
   WHERE NOT EXISTS (SELECT 1 FROM motoboy x WHERE x.id_estabelecimento = v_est AND x.nome = 'Diego Santos' AND x.telefone = '34991230101');
  INSERT INTO motoboy (nome, telefone, status, id_estabelecimento, is_simulated)
  SELECT 'Rafael Oliveira', '34991230102', 2, v_est, TRUE
   WHERE NOT EXISTS (SELECT 1 FROM motoboy x WHERE x.id_estabelecimento = v_est AND x.nome = 'Rafael Oliveira' AND x.telefone = '34991230102');
  INSERT INTO motoboy (nome, telefone, status, id_estabelecimento, is_simulated)
  SELECT 'Bruno Carvalho', '34991230103', 2, v_est, TRUE
   WHERE NOT EXISTS (SELECT 1 FROM motoboy x WHERE x.id_estabelecimento = v_est AND x.nome = 'Bruno Carvalho' AND x.telefone = '34991230103');
  UPDATE motoboy SET canonical_motoboy_id = id WHERE id_estabelecimento = v_est AND is_simulated AND canonical_motoboy_id IS NULL AND telefone LIKE '3499123010%';
  INSERT INTO motoboy_estabelecimento (motoboy_id, estabelecimento_id, ativo, simulator_enabled, created_at_utc, updated_at_utc)
  SELECT m.id, v_est, TRUE, TRUE, NOW(), NOW() FROM motoboy m
   WHERE m.id_estabelecimento = v_est AND m.is_simulated AND m.telefone LIKE '3499123010%'
     AND NOT EXISTS (SELECT 1 FROM motoboy_estabelecimento me WHERE me.motoboy_id = m.id AND me.estabelecimento_id = v_est);
  PERFORM pg_temp.seed_pedido(v_est, '{"ref":"seed-uberlandia-1","origem":"atendente","status":3,"cli":"maria","conv":"maria","moto":"Diego Santos","min_ago":300,"prev_min":45,"nome_cliente":"Maria Aparecida Souza","telefone_cliente":"+5534991230001","endereco_entrega":"Rua Tenente Virmondes, 350","region":"Fundinho","latitude":"-18.9243","longitude":"-48.2727","entrega_rua":"Rua Tenente Virmondes","entrega_numero":"350","entrega_bairro":"Fundinho","entrega_cidade":"Uberlandia","entrega_estado":"MG","entrega_cep":"38400-100","tipo_pagamento":"PIX","status_pagamento":"Pago","items":"[{\"nome\":\"PF Tropeiro\",\"quantidade\":1,\"preco\":32},{\"nome\":\"Suco de Caju 500 ml\",\"quantidade\":1,\"preco\":10}]","value":"48.00","subtotal":"42.00","taxa":"6.00","itens":[{"prod":"pf-tropeiro","nome":"PF Tropeiro","qtd":1,"preco":32,"obs":null,"adicionais":[]},{"prod":"suco-caju","nome":"Suco de Caju 500 ml","qtd":1,"preco":10,"obs":"Sem acucar","adicionais":[]}],"saida_after":20,"entrega_after":50}'::jsonb);
  PERFORM pg_temp.seed_pedido(v_est, '{"ref":"seed-uberlandia-2","origem":"atendente","status":3,"cli":"joao","conv":"joao","moto":"Rafael Oliveira","min_ago":250,"prev_min":45,"nome_cliente":"Joao Pedro Almeida","telefone_cliente":"+5534991230002","endereco_entrega":"Avenida Joao Naves de Avila, 1800","region":"Santa Monica","latitude":"-18.916","longitude":"-48.259","entrega_rua":"Avenida Joao Naves de Avila","entrega_numero":"1800","entrega_bairro":"Santa Monica","entrega_cidade":"Uberlandia","entrega_estado":"MG","entrega_cep":"38408-144","tipo_pagamento":"Cartao","status_pagamento":"Pago","items":"[{\"nome\":\"X-Bacon + Ovo\",\"quantidade\":2,\"preco\":31.5},{\"nome\":\"Refrigerante 2 L\",\"quantidade\":1,\"preco\":14}]","value":"83.00","subtotal":"77.00","taxa":"6.00","itens":[{"prod":"x-bacon","nome":"X-Bacon","qtd":2,"preco":29,"obs":null,"adicionais":[{"key":"ovo","nome":"Ovo","preco":2.5}]},{"prod":"coca-2l","nome":"Refrigerante 2 L","qtd":1,"preco":14,"obs":"Guarana","adicionais":[]}],"saida_after":20,"entrega_after":50}'::jsonb);
  PERFORM pg_temp.seed_pedido(v_est, '{"ref":"seed-uberlandia-3","origem":"cardapio_web","status":3,"cli":"ana","conv":null,"moto":"Diego Santos","min_ago":190,"prev_min":45,"nome_cliente":"Ana Beatriz Ferreira","telefone_cliente":"+5534991230003","endereco_entrega":"Avenida Rondon Pacheco, 2500","region":"Tibery","latitude":"-18.8946","longitude":"-48.2517","entrega_rua":"Avenida Rondon Pacheco","entrega_numero":"2500","entrega_bairro":"Tibery","entrega_cidade":"Uberlandia","entrega_estado":"MG","entrega_cep":"38405-142","tipo_pagamento":"Pago online","status_pagamento":"Pago","items":"[{\"nome\":\"Frango com Quiabo\",\"quantidade\":2,\"preco\":36},{\"nome\":\"Pudim de Leite\",\"quantidade\":2,\"preco\":12}]","value":"104.00","subtotal":"96.00","taxa":"8.00","itens":[{"prod":"frango-quiabo","nome":"Frango com Quiabo","qtd":2,"preco":36,"obs":null,"adicionais":[]},{"prod":"pudim","nome":"Pudim de Leite","qtd":2,"preco":12,"obs":null,"adicionais":[]}],"saida_after":20,"entrega_after":50}'::jsonb);
  PERFORM pg_temp.seed_pedido(v_est, '{"ref":"seed-uberlandia-4","origem":"atendente","status":3,"cli":"carlos","conv":"carlos","moto":"Rafael Oliveira","min_ago":1500,"prev_min":45,"nome_cliente":"Carlos Eduardo Lima","telefone_cliente":"+5534991230004","endereco_entrega":"Rua Bias Fortes, 145","region":"Centro","latitude":"-18.9186","longitude":"-48.2772","entrega_rua":"Rua Bias Fortes","entrega_numero":"145","entrega_bairro":"Centro","entrega_cidade":"Uberlandia","entrega_estado":"MG","entrega_cep":"38400-116","tipo_pagamento":"Dinheiro","status_pagamento":"Pago","items":"[{\"nome\":\"Feijoada Mineira (individual)\",\"quantidade\":2,\"preco\":42},{\"nome\":\"Guarana Lata 350 ml\",\"quantidade\":2,\"preco\":6.5}]","value":"102.00","subtotal":"97.00","taxa":"5.00","itens":[{"prod":"feijoada-mineira","nome":"Feijoada Mineira (individual)","qtd":2,"preco":42,"obs":null,"adicionais":[]},{"prod":"guarana-lata","nome":"Guarana Lata 350 ml","qtd":2,"preco":6.5,"obs":null,"adicionais":[]}],"troco":"100","saida_after":20,"entrega_after":50}'::jsonb);
  PERFORM pg_temp.seed_pedido(v_est, '{"ref":"seed-uberlandia-5","origem":"ifood","status":3,"cli":"fernanda","conv":null,"moto":"Bruno Carvalho","min_ago":1700,"prev_min":45,"nome_cliente":"Fernanda Rocha","telefone_cliente":"+5534991230005","endereco_entrega":"Avenida Segismundo Pereira, 900","region":"Osvaldo Rezende","latitude":"-18.9055","longitude":"-48.2716","entrega_rua":"Avenida Segismundo Pereira","entrega_numero":"900","entrega_bairro":"Osvaldo Rezende","entrega_cidade":"Uberlandia","entrega_estado":"MG","entrega_cep":"38400-500","tipo_pagamento":"Pago online","status_pagamento":"Pago","items":"[{\"nome\":\"X-Salada\",\"quantidade\":1,\"preco\":26},{\"nome\":\"Batata Frita + Cheddar extra\",\"quantidade\":1,\"preco\":30.5}]","value":"63.50","subtotal":"56.50","taxa":"7.00","itens":[{"prod":"x-salada","nome":"X-Salada","qtd":1,"preco":26,"obs":null,"adicionais":[]},{"prod":"batata-frita","nome":"Batata Frita","qtd":1,"preco":27,"obs":null,"adicionais":[{"key":"cheddar","nome":"Cheddar extra","preco":3.5}]}],"saida_after":20,"entrega_after":50}'::jsonb);
  PERFORM pg_temp.seed_pedido(v_est, '{"ref":"seed-uberlandia-6","origem":"ifood","status":3,"cli":"ricardo","conv":null,"moto":"Diego Santos","min_ago":2900,"prev_min":45,"nome_cliente":"Ricardo Martins","telefone_cliente":"+5534991230006","endereco_entrega":"Rua Duque de Caxias, 620","region":"Martins","latitude":"-18.9247","longitude":"-48.2802","entrega_rua":"Rua Duque de Caxias","entrega_numero":"620","entrega_bairro":"Martins","entrega_cidade":"Uberlandia","entrega_estado":"MG","entrega_cep":"38400-206","tipo_pagamento":"Pago online","status_pagamento":"Pago","items":"[{\"nome\":\"Porcao de Pao de Queijo\",\"quantidade\":2,\"preco\":22},{\"nome\":\"Suco de Caju 500 ml\",\"quantidade\":2,\"preco\":10}]","value":"70.00","subtotal":"64.00","taxa":"6.00","itens":[{"prod":"pao-queijo","nome":"Porcao de Pao de Queijo","qtd":2,"preco":22,"obs":null,"adicionais":[]},{"prod":"suco-caju","nome":"Suco de Caju 500 ml","qtd":2,"preco":10,"obs":null,"adicionais":[]}],"saida_after":20,"entrega_after":50}'::jsonb);
  PERFORM pg_temp.seed_pedido(v_est, '{"ref":"seed-uberlandia-7","origem":"atendente","status":4,"cli":"juliana","conv":"juliana","moto":null,"min_ago":400,"prev_min":45,"nome_cliente":"Juliana Costa","telefone_cliente":"+5534991230007","endereco_entrega":"Avenida Cesario Alvim, 3100","region":"Brasil","latitude":"-18.9111","longitude":"-48.2988","entrega_rua":"Avenida Cesario Alvim","entrega_numero":"3100","entrega_bairro":"Brasil","entrega_cidade":"Uberlandia","entrega_estado":"MG","entrega_cep":"38400-370","tipo_pagamento":"PIX","status_pagamento":"Pendente","items":"[{\"nome\":\"X-Burguer + Queijo extra\",\"quantidade\":3,\"preco\":27}]","value":"90.00","subtotal":"81.00","taxa":"9.00","itens":[{"prod":"x-burguer","nome":"X-Burguer","qtd":3,"preco":24,"obs":null,"adicionais":[{"key":"queijo","nome":"Queijo extra","preco":3}]}],"observacoes":"Cancelado: cliente desistiu do pedido."}'::jsonb);
  PERFORM pg_temp.seed_pedido(v_est, '{"ref":"seed-uberlandia-8","origem":"cardapio_web","status":4,"cli":"paulo","conv":null,"moto":null,"min_ago":1900,"prev_min":45,"nome_cliente":"Paulo Henrique Nunes","telefone_cliente":"+5534991230008","endereco_entrega":"Rua Machado de Assis, 77","region":"Lidice","latitude":"-18.932","longitude":"-48.283","entrega_rua":"Rua Machado de Assis","entrega_numero":"77","entrega_bairro":"Lidice","entrega_cidade":"Uberlandia","entrega_estado":"MG","entrega_cep":"38400-464","tipo_pagamento":"Cartao","status_pagamento":"Pendente","items":"[{\"nome\":\"Mandioca Frita\",\"quantidade\":1,\"preco\":25}]","value":"31.00","subtotal":"25.00","taxa":"6.00","itens":[{"prod":"mandioca-frita","nome":"Mandioca Frita","qtd":1,"preco":25,"obs":null,"adicionais":[]}],"observacoes":"Cancelado: endereco fora da area de entrega."}'::jsonb);
  PERFORM pg_temp.seed_pedido(v_est, '{"ref":"seed-uberlandia-9","origem":"atendente","status":1,"cli":"luciana","conv":"luciana","moto":null,"min_ago":12,"prev_min":45,"nome_cliente":"Luciana Pereira","telefone_cliente":"+5534991230009","endereco_entrega":"Avenida Floriano Peixoto, 410","region":"Centro","latitude":"-18.92","longitude":"-48.278","entrega_rua":"Avenida Floriano Peixoto","entrega_numero":"410","entrega_bairro":"Centro","entrega_cidade":"Uberlandia","entrega_estado":"MG","entrega_cep":"38400-114","tipo_pagamento":"PIX","status_pagamento":"Pendente","items":"[{\"nome\":\"PF Tropeiro\",\"quantidade\":2,\"preco\":32},{\"nome\":\"Refrigerante 2 L\",\"quantidade\":1,\"preco\":14}]","value":"83.00","subtotal":"78.00","taxa":"5.00","itens":[{"prod":"pf-tropeiro","nome":"PF Tropeiro","qtd":2,"preco":32,"obs":null,"adicionais":[]},{"prod":"coca-2l","nome":"Refrigerante 2 L","qtd":1,"preco":14,"obs":"Coca-Cola","adicionais":[]}],"codigo_entrega":"4821"}'::jsonb);
  PERFORM pg_temp.seed_pedido(v_est, '{"ref":"seed-uberlandia-10","origem":"cardapio_web","status":1,"cli":"marcos","conv":null,"moto":null,"min_ago":25,"prev_min":45,"nome_cliente":"Marcos Vinicius Silva","telefone_cliente":"+5534991230010","endereco_entrega":"Rua Ipiranga, 1220","region":"Morada da Colina","latitude":"-18.9385","longitude":"-48.236","entrega_rua":"Rua Ipiranga","entrega_numero":"1220","entrega_bairro":"Morada da Colina","entrega_cidade":"Uberlandia","entrega_estado":"MG","entrega_cep":"38411-106","tipo_pagamento":"Pago online","status_pagamento":"Pago","items":"[{\"nome\":\"X-Bacon + Bacon extra, Cheddar extra\",\"quantidade\":1,\"preco\":36.5},{\"nome\":\"Batata Frita\",\"quantidade\":1,\"preco\":27}]","value":"73.50","subtotal":"63.50","taxa":"10.00","itens":[{"prod":"x-bacon","nome":"X-Bacon","qtd":1,"preco":29,"obs":null,"adicionais":[{"key":"bacon","nome":"Bacon extra","preco":4},{"key":"cheddar","nome":"Cheddar extra","preco":3.5}]},{"prod":"batata-frita","nome":"Batata Frita","qtd":1,"preco":27,"obs":null,"adicionais":[]}]}'::jsonb);
  PERFORM pg_temp.seed_pedido(v_est, '{"ref":"seed-uberlandia-11","origem":"ifood","status":1,"cli":"patricia","conv":null,"moto":null,"min_ago":40,"prev_min":45,"nome_cliente":"Patricia Gomes","telefone_cliente":"+5534991230011","endereco_entrega":"Avenida Afonso Pena, 1500","region":"Centro","latitude":"-18.9165","longitude":"-48.2755","entrega_rua":"Avenida Afonso Pena","entrega_numero":"1500","entrega_bairro":"Centro","entrega_cidade":"Uberlandia","entrega_estado":"MG","entrega_cep":"38400-128","tipo_pagamento":"Pago online","status_pagamento":"Pago","items":"[{\"nome\":\"Frango com Quiabo\",\"quantidade\":1,\"preco\":36},{\"nome\":\"Doce de Leite Caseiro\",\"quantidade\":1,\"preco\":14}]","value":"56.00","subtotal":"50.00","taxa":"6.00","itens":[{"prod":"frango-quiabo","nome":"Frango com Quiabo","qtd":1,"preco":36,"obs":null,"adicionais":[]},{"prod":"doce-leite","nome":"Doce de Leite Caseiro","qtd":1,"preco":14,"obs":null,"adicionais":[]}]}'::jsonb);
  PERFORM pg_temp.seed_pedido(v_est, '{"ref":"seed-uberlandia-12","origem":"atendente","status":1,"cli":"thiago","conv":"thiago","moto":null,"min_ago":5,"prev_min":45,"nome_cliente":"Thiago Barbosa","telefone_cliente":"+5534991230012","endereco_entrega":"Rua dos Vinhedos, 95","region":"Jardim Karaiba","latitude":"-18.945","longitude":"-48.25","entrega_rua":"Rua dos Vinhedos","entrega_numero":"95","entrega_bairro":"Jardim Karaiba","entrega_cidade":"Uberlandia","entrega_estado":"MG","entrega_cep":"38411-186","tipo_pagamento":"Dinheiro","status_pagamento":"Pendente","items":"[{\"nome\":\"X-Salada + Ovo\",\"quantidade\":2,\"preco\":28.5},{\"nome\":\"Guarana Lata 350 ml\",\"quantidade\":2,\"preco\":6.5}]","value":"79.00","subtotal":"70.00","taxa":"9.00","itens":[{"prod":"x-salada","nome":"X-Salada","qtd":2,"preco":26,"obs":null,"adicionais":[{"key":"ovo","nome":"Ovo","preco":2.5}]},{"prod":"guarana-lata","nome":"Guarana Lata 350 ml","qtd":2,"preco":6.5,"obs":null,"adicionais":[]}],"troco":"50","observacoes":"Portao azul, tocar a campainha."}'::jsonb);
  PERFORM pg_temp.seed_pedido(v_est, '{"ref":"seed-uberlandia-13","origem":"atendente","status":6,"cli":"ana","conv":"ana","moto":null,"min_ago":8,"prev_min":45,"nome_cliente":"Ana Beatriz Ferreira","telefone_cliente":"+5534991230003","endereco_entrega":"Avenida Rondon Pacheco, 2500","region":"Tibery","latitude":"-18.8946","longitude":"-48.2517","entrega_rua":"Avenida Rondon Pacheco","entrega_numero":"2500","entrega_bairro":"Tibery","entrega_cidade":"Uberlandia","entrega_estado":"MG","entrega_cep":"38405-142","tipo_pagamento":"PIX","status_pagamento":"Pendente","items":"[{\"nome\":\"Porcao de Pao de Queijo\",\"quantidade\":1,\"preco\":22},{\"nome\":\"Suco de Caju 500 ml\",\"quantidade\":2,\"preco\":10}]","value":"50.00","subtotal":"42.00","taxa":"8.00","itens":[{"prod":"pao-queijo","nome":"Porcao de Pao de Queijo","qtd":1,"preco":22,"obs":null,"adicionais":[]},{"prod":"suco-caju","nome":"Suco de Caju 500 ml","qtd":2,"preco":10,"obs":null,"adicionais":[]}],"observacoes":"Rascunho: aguardando confirmacao do endereco."}'::jsonb);
  PERFORM pg_temp.seed_pedido(v_est, '{"ref":"seed-uberlandia-14","origem":"cardapio_web","status":1,"cli":"joao","conv":null,"moto":null,"min_ago":70,"prev_min":45,"nome_cliente":"Joao Pedro Almeida","telefone_cliente":"+5534991230002","endereco_entrega":"Avenida Joao Naves de Avila, 1800","region":"Santa Monica","latitude":"-18.916","longitude":"-48.259","entrega_rua":"Avenida Joao Naves de Avila","entrega_numero":"1800","entrega_bairro":"Santa Monica","entrega_cidade":"Uberlandia","entrega_estado":"MG","entrega_cep":"38408-144","tipo_pagamento":"Cartao","status_pagamento":"Pago","items":"[{\"nome\":\"Feijoada Mineira (individual)\",\"quantidade\":1,\"preco\":42},{\"nome\":\"Pudim de Leite\",\"quantidade\":1,\"preco\":12}]","value":"60.00","subtotal":"54.00","taxa":"6.00","itens":[{"prod":"feijoada-mineira","nome":"Feijoada Mineira (individual)","qtd":1,"preco":42,"obs":null,"adicionais":[]},{"prod":"pudim","nome":"Pudim de Leite","qtd":1,"preco":12,"obs":null,"adicionais":[]}]}'::jsonb);

    INSERT INTO delivery_tracking_schema_versions (version) VALUES ('seed_demo_alvo:' || v_est::text) ON CONFLICT (version) DO NOTHING;
    INSERT INTO delivery_tracking_schema_versions (version) VALUES ('20260927_90_seed_uberlandia') ON CONFLICT (version) DO NOTHING;
  EXCEPTION WHEN OTHERS THEN
    -- Subtransacao desfeita: nada do seed ficou. Registra o motivo e tenta de novo na proxima subida.
    INSERT INTO delivery_tracking_schema_versions (version)
    VALUES (left('seed_demo_erro: ' || SQLSTATE || ' ' || SQLERRM, 240)) ON CONFLICT (version) DO NOTHING;
  END;
END $seed$;

COMMIT;
