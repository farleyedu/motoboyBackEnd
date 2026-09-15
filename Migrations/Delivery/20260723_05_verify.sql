-- Deve retornar zero em todas as consultas de inconsistencia.

-- Mais de uma parada 'en_route' para o mesmo motoboy.
SELECT motoboy_id, COUNT(*) AS en_route_count
  FROM delivery_route_stops
 WHERE stop_status = 'en_route'
 GROUP BY motoboy_id
HAVING COUNT(*) > 1;

-- Mesmo pedido em mais de uma parada ativa.
SELECT pedido_id, COUNT(*) AS active_stops
  FROM delivery_route_stops
 WHERE stop_status IN ('assigned', 'en_route')
 GROUP BY pedido_id
HAVING COUNT(*) > 1;

-- Posicao duplicada dentro da fila ativa do mesmo motoboy.
SELECT motoboy_id, position, COUNT(*) AS total
  FROM delivery_route_stops
 WHERE stop_status IN ('assigned', 'en_route')
 GROUP BY motoboy_id, position
HAVING COUNT(*) > 1;

-- Parada ativa cujo pedido nao esta com status_pedido coerente
-- (en_route deveria ser 2/EmRota; assigned deveria ser 5/Atribuido).
SELECT s.id AS stop_id, s.pedido_id, s.stop_status, p.status_pedido
  FROM delivery_route_stops s
  JOIN pedido p ON p.id = s.pedido_id
 WHERE (s.stop_status = 'en_route' AND p.status_pedido IS DISTINCT FROM 2)
    OR (s.stop_status = 'assigned' AND p.status_pedido IS DISTINCT FROM 5);

-- Parada ativa cujo pedido pertence a outro estabelecimento (nunca deveria acontecer).
SELECT s.id AS stop_id, s.estabelecimento_id AS stop_estabelecimento, p.id_estabelecimento AS pedido_estabelecimento
  FROM delivery_route_stops s
  JOIN pedido p ON p.id = s.pedido_id
 WHERE s.estabelecimento_id IS DISTINCT FROM p.id_estabelecimento;

-- id_ifood duplicado remanescente (a constraint da 04 deveria ter impedido novos).
SELECT id_ifood, COUNT(*) AS total
  FROM pedido
 WHERE id_ifood IS NOT NULL
 GROUP BY id_ifood
HAVING COUNT(*) > 1;

SELECT version, applied_at_utc
  FROM delivery_tracking_schema_versions
 WHERE version LIKE '20260723%'
 ORDER BY applied_at_utc, version;
