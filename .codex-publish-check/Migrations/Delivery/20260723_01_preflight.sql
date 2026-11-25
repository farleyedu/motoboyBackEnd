-- Delivery Parte 2 - diagnostico somente leitura.
-- Execute antes das migrations mutaveis e salve o resultado para auditoria.

-- Pedidos sem estabelecimento (nao entram no mapa nem na fila ate serem corrigidos).
SELECT COUNT(*) AS pedidos_sem_estabelecimento
  FROM pedido
 WHERE id_estabelecimento IS NULL;

-- id_ifood duplicado: hoje nao ha protecao no banco contra isso.
SELECT id_ifood, COUNT(*) AS ocorrencias, ARRAY_AGG(id ORDER BY id) AS pedido_ids
  FROM pedido
 WHERE id_ifood IS NOT NULL
 GROUP BY id_ifood
HAVING COUNT(*) > 1
 ORDER BY ocorrencias DESC;

-- Distribuicao atual de status_pedido (para saber se o valor 5 ja esta em uso
-- com outro significado antes de reaproveita-lo para "Atribuido").
SELECT status_pedido, COUNT(*) AS total
  FROM pedido
 GROUP BY status_pedido
 ORDER BY status_pedido;

-- Pedidos com motoboy_responsavel setado mas sem pedido correspondente na tabela motoboy
-- (haviam sido criados antes de motoboy_responsavel virar FK confiavel).
SELECT p.id, p.motoboy_responsavel
  FROM pedido p
  LEFT JOIN motoboy m ON m.id = p.motoboy_responsavel
 WHERE p.motoboy_responsavel IS NOT NULL
   AND m.id IS NULL;
