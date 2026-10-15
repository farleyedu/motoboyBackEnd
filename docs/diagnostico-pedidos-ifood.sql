-- Somente leitura. Número visível não é o ID interno usado nos endpoints.
SELECT l.pedido_id,l.display_number,l.estabelecimento_id,s.remote_status,s.preparation_state,
       s.synchronization_state,s.data_is_current,i.id AS inbox_id,i.event_code,i.state,
       i.last_error_code,h.outcome,h.reason_code,i.received_at_utc
FROM ifood_order_links l
JOIN ifood_order_snapshots s USING(merchant_id,external_order_id,estabelecimento_id)
LEFT JOIN ifood_events_inbox i USING(merchant_id,external_order_id,estabelecimento_id)
LEFT JOIN ifood_order_event_history h ON h.inbox_id=i.id
WHERE l.display_number IN ('6485','8999')
  AND l.merchant_id='7c0ef690-b675-4cef-9819-7085fcbbbcf8'::uuid
ORDER BY l.display_number,COALESCE(i.occurred_at_utc,i.received_at_utc),i.id;
