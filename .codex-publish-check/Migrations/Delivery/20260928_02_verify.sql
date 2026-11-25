-- Verificacao da Fase 6 (avisos de rastreio). Somente leitura; nao e aplicada automaticamente.

-- 1) Objetos criados (deve listar 2 tabelas e 3+1+6 colunas).
SELECT to_regclass('pedido_notificacao') AS notificacao, to_regclass('pedido_rastreio_token') AS token;
SELECT table_name, column_name FROM information_schema.columns
 WHERE table_schema = current_schema()
   AND ((table_name = 'pedido' AND column_name LIKE 'rastreio_opt_in%')
     OR (table_name = 'motoboy' AND column_name = 'compartilhar_localizacao_cliente')
     OR (table_name = 'delivery_settings' AND column_name LIKE 'notify_%'))
 ORDER BY table_name, column_name;

-- 2) Aviso de pedido sem opt-in (o servico nunca deveria criar; deve voltar vazio).
SELECT n.pedido_id, n.tipo FROM pedido_notificacao n
  JOIN pedido p ON p.id = n.pedido_id WHERE p.rastreio_opt_in = FALSE AND n.status = 'enviada';

-- 3) Aviso "motoboy chegando" enviado com motoboy que nao autorizou a localizacao (deve voltar vazio).
SELECT n.pedido_id FROM pedido_notificacao n
  JOIN pedido p ON p.id = n.pedido_id
  JOIN motoboy m ON m.id = p.motoboy_responsavel
 WHERE n.tipo = 'motoboy_chegando' AND n.status = 'enviada' AND m.compartilhar_localizacao_cliente = FALSE;

-- 4) Duplicados por tipo (a UNIQUE ja impede; deve voltar vazio).
SELECT pedido_id, tipo, COUNT(*) FROM pedido_notificacao GROUP BY pedido_id, tipo HAVING COUNT(*) > 1;

SELECT version, applied_at_utc FROM delivery_tracking_schema_versions WHERE version = '20260928_01_avisos_rastreio';
