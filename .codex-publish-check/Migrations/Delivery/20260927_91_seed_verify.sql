-- Verificacao do seed de demonstracao (Uberlandia). Somente leitura; nao e aplicada automaticamente.

-- 1) Resultado do seed no ledger (alvo, sem_alvo ou erro).
SELECT version, applied_at_utc FROM delivery_tracking_schema_versions
 WHERE version LIKE 'seed_demo_%' OR version = '20260927_90_seed_uberlandia' ORDER BY applied_at_utc, version;

-- 2) Contagens do estabelecimento alvo (esperado: 5 categorias, 14 produtos, 12 clientes, 11 conversas,
--    3 motoboys, 14 pedidos e as mensagens das conversas).
WITH alvo AS (
  SELECT substring(version FROM 'seed_demo_alvo:(.*)$')::uuid AS id
    FROM delivery_tracking_schema_versions WHERE version LIKE 'seed_demo_alvo:%' LIMIT 1)
SELECT (SELECT COUNT(*) FROM cardapio_categoria c, alvo WHERE c.id_estabelecimento = alvo.id) AS categorias,
       (SELECT COUNT(*) FROM cardapio_produto c, alvo WHERE c.id_estabelecimento = alvo.id) AS produtos,
       (SELECT COUNT(*) FROM clientes c, alvo WHERE c.id_estabelecimento = alvo.id) AS clientes,
       (SELECT COUNT(*) FROM conversas c, alvo WHERE c.id_estabelecimento = alvo.id) AS conversas,
       (SELECT COUNT(*) FROM mensagens m JOIN conversas c ON c.id = m.id_conversa, alvo WHERE c.id_estabelecimento = alvo.id) AS mensagens,
       (SELECT COUNT(*) FROM motoboy m, alvo WHERE m.id_estabelecimento = alvo.id AND m.is_simulated AND m.telefone LIKE '3499123010%') AS motoboys,
       (SELECT COUNT(*) FROM pedido p, alvo WHERE p.id_estabelecimento = alvo.id AND p.origem_ref LIKE 'seed-uberlandia-%') AS pedidos;

-- 3) Pedido do seed sem itens (deveria voltar vazio).
SELECT p.id FROM pedido p LEFT JOIN pedido_item i ON i.pedido_id = p.id
 WHERE p.origem_ref LIKE 'seed-uberlandia-%' GROUP BY p.id HAVING COUNT(i.id) = 0;

-- 4) Conversa do seed sem mensagem (deveria voltar vazio).
SELECT c.id FROM conversas c LEFT JOIN mensagens m ON m.id_conversa = c.id
 WHERE c.id IN (SELECT id_conversa FROM mensagens WHERE id_provedor LIKE 'seed-uberlandia-%')
 GROUP BY c.id HAVING COUNT(m.id) = 0;
