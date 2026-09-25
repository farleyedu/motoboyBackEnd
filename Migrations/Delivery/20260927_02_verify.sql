-- Verificacao da Fase 4 (regras de rota). Somente leitura; nao e aplicada automaticamente.

-- 1) Colunas novas (deve listar 8 linhas).
SELECT table_name, column_name FROM information_schema.columns
 WHERE table_schema = current_schema()
   AND ((table_name = 'delivery_route_stops' AND column_name IN ('locked', 'locked_by_user_id', 'locked_at_utc'))
     OR (table_name = 'delivery_motoboy_route' AND column_name IN ('route_state', 'returning_since_utc'))
     OR (table_name = 'delivery_settings' AND column_name IN ('require_return_to_store', 'store_return_radius_m', 'auto_finish_route_on_return')))
 ORDER BY table_name, column_name;

-- 2) Parada travada que nao esta ativa (o lock some junto com a parada; deveria voltar vazio).
SELECT id, pedido_id, stop_status FROM delivery_route_stops
 WHERE locked AND stop_status NOT IN ('assigned', 'en_route');

-- 3) Rota 'returning' de motoboy que ainda tem parada ativa (inconsistente; deveria voltar vazio).
SELECT r.motoboy_id, r.estabelecimento_id
  FROM delivery_motoboy_route r
 WHERE r.route_state = 'returning'
   AND EXISTS (SELECT 1 FROM delivery_route_stops s
                WHERE s.motoboy_id = r.motoboy_id AND s.estabelecimento_id = r.estabelecimento_id
                  AND s.stop_status IN ('assigned', 'en_route'));

-- 4) Duas paradas na mesma posicao da fila ativa (o indice unico ja deveria impedir).
SELECT motoboy_id, estabelecimento_id, position, COUNT(*)
  FROM delivery_route_stops WHERE stop_status IN ('assigned', 'en_route')
 GROUP BY motoboy_id, estabelecimento_id, position HAVING COUNT(*) > 1;

SELECT version, applied_at_utc FROM delivery_tracking_schema_versions WHERE version LIKE '20260927%' ORDER BY version;
