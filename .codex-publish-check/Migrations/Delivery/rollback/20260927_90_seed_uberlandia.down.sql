-- Desfaz o seed de demonstracao (Uberlandia) do estabelecimento registrado no ledger.
-- Remove apenas o que o seed criou (ids deterministicos, origem_ref 'seed-uberlandia-%', motoboys 3499123010x).
BEGIN;
DO $down$
DECLARE
  v_est uuid;
  v_key text;
BEGIN
  SELECT substring(version FROM 'seed_demo_alvo:(.*)$')::uuid INTO v_est
    FROM delivery_tracking_schema_versions WHERE version LIKE 'seed_demo_alvo:%' LIMIT 1;
  IF v_est IS NULL THEN RAISE NOTICE 'Nenhum seed registrado no ledger.'; RETURN; END IF;

  -- Fase 5: mensagens ao motoboy (apontam para pedido/motoboy do seed), respostas rapidas e config.
  IF to_regclass('delivery_motoboy_message') IS NOT NULL THEN
    DELETE FROM delivery_motoboy_message WHERE estabelecimento_id = v_est
       AND motoboy_id IN (SELECT id FROM motoboy WHERE id_estabelecimento = v_est AND is_simulated AND telefone LIKE '3499123010%');
    DELETE FROM atendimento_respostas_rapidas WHERE estabelecimento_id = v_est
       AND id = ANY (ARRAY[md5('zippy-seed-uberlandia:' || v_est::text || ':resp:${r[0]}')::uuid, md5('zippy-seed-uberlandia:' || v_est::text || ':resp:${r[0]}')::uuid, md5('zippy-seed-uberlandia:' || v_est::text || ':resp:${r[0]}')::uuid, md5('zippy-seed-uberlandia:' || v_est::text || ':resp:${r[0]}')::uuid, md5('zippy-seed-uberlandia:' || v_est::text || ':resp:${r[0]}')::uuid, md5('zippy-seed-uberlandia:' || v_est::text || ':resp:${r[0]}')::uuid]);
    DELETE FROM estabelecimento_atendimento_config WHERE estabelecimento_id = v_est
       AND saudacao_humano LIKE 'Ola! Aqui e do Sabor de Uberlandia%';
  END IF;

  DELETE FROM delivery_route_stops WHERE estabelecimento_id = v_est
     AND pedido_id IN (SELECT id FROM pedido WHERE id_estabelecimento = v_est AND origem_ref LIKE 'seed-uberlandia-%');
  DELETE FROM pedido_item WHERE pedido_id IN (SELECT id FROM pedido WHERE id_estabelecimento = v_est AND origem_ref LIKE 'seed-uberlandia-%');
  DELETE FROM pedido WHERE id_estabelecimento = v_est AND origem_ref LIKE 'seed-uberlandia-%';

  DELETE FROM mensagens WHERE id_provedor LIKE 'seed-uberlandia-%'
     AND id_conversa IN (SELECT id FROM conversas WHERE id_estabelecimento = v_est);
  FOREACH v_key IN ARRAY ARRAY['conv:maria', 'conv:joao', 'conv:carlos', 'conv:juliana', 'conv:luciana', 'conv:thiago', 'conv:ana', 'conv:fernanda', 'conv:ricardo', 'conv:paulo', 'conv:patricia'] LOOP
    DELETE FROM conversas WHERE id_estabelecimento = v_est AND id = md5('zippy-seed-uberlandia:' || v_est::text || ':' || v_key)::uuid;
  END LOOP;
  FOREACH v_key IN ARRAY ARRAY['cli:maria', 'cli:joao', 'cli:ana', 'cli:carlos', 'cli:fernanda', 'cli:ricardo', 'cli:juliana', 'cli:paulo', 'cli:luciana', 'cli:marcos', 'cli:patricia', 'cli:thiago'] LOOP
    DELETE FROM clientes WHERE id_estabelecimento = v_est AND id = md5('zippy-seed-uberlandia:' || v_est::text || ':' || v_key)::uuid;
  END LOOP;

  DELETE FROM motoboy_estabelecimento WHERE estabelecimento_id = v_est
     AND motoboy_id IN (SELECT id FROM motoboy WHERE id_estabelecimento = v_est AND is_simulated AND telefone LIKE '3499123010%');
  DELETE FROM motoboy WHERE id_estabelecimento = v_est AND is_simulated AND telefone LIKE '3499123010%';

  FOREACH v_key IN ARRAY ARRAY['prod:pf-tropeiro', 'prod:frango-quiabo', 'prod:feijoada-mineira', 'prod:x-burguer', 'prod:x-bacon', 'prod:x-salada', 'prod:pao-queijo', 'prod:mandioca-frita', 'prod:batata-frita', 'prod:guarana-lata', 'prod:coca-2l', 'prod:suco-caju', 'prod:doce-leite', 'prod:pudim'] LOOP
    DELETE FROM cardapio_produto_grupo WHERE id_produto = md5('zippy-seed-uberlandia:' || v_est::text || ':' || v_key)::uuid;
    DELETE FROM cardapio_produto_atendimento WHERE produto_id = md5('zippy-seed-uberlandia:' || v_est::text || ':' || v_key)::uuid;
    DELETE FROM cardapio_produto WHERE id_estabelecimento = v_est AND id = md5('zippy-seed-uberlandia:' || v_est::text || ':' || v_key)::uuid;
  END LOOP;
  FOREACH v_key IN ARRAY ARRAY['adi:bacon', 'adi:cheddar', 'adi:ovo', 'adi:queijo'] LOOP
    DELETE FROM cardapio_grupo_adicional_item WHERE id = md5('zippy-seed-uberlandia:' || v_est::text || ':' || v_key)::uuid;
  END LOOP;
  FOREACH v_key IN ARRAY ARRAY['grp:extras-lanche'] LOOP
    DELETE FROM cardapio_grupo_adicional WHERE id_estabelecimento = v_est AND id = md5('zippy-seed-uberlandia:' || v_est::text || ':' || v_key)::uuid;
  END LOOP;
  FOREACH v_key IN ARRAY ARRAY['cat:pratos', 'cat:lanches', 'cat:porcoes', 'cat:bebidas', 'cat:sobremesas'] LOOP
    DELETE FROM cardapio_categoria WHERE id_estabelecimento = v_est AND id = md5('zippy-seed-uberlandia:' || v_est::text || ':' || v_key)::uuid;
  END LOOP;

  DELETE FROM delivery_tracking_schema_versions WHERE version IN ('20260927_90_seed_uberlandia', 'seed_demo_alvo:' || v_est::text, 'seed_demo_sem_alvo')
     OR version LIKE 'seed_demo_erro:%';
END $down$;
COMMIT;
