-- Verificacao da fila de eventos do webhook. Somente leitura; nao e aplicada automaticamente.

SELECT estado, tipo, COUNT(*) AS total FROM wa_evento GROUP BY estado, tipo ORDER BY estado, tipo;

-- Eventos que nao foram processados e por que (canal desconhecido, assinatura, erro...).
SELECT recebido_em, tipo, estado, tentativas, phone_number_id, motivo
  FROM wa_evento
 WHERE estado IN ('ignorado', 'erro')
 ORDER BY recebido_em DESC
 LIMIT 50;

-- Fila parada: pendentes ha mais de 2 minutos.
SELECT COUNT(*) AS atrasados FROM wa_evento
 WHERE estado IN ('pendente', 'processando') AND proxima_tentativa_em < NOW() - INTERVAL '2 minutes';

SELECT version, applied_at_utc FROM delivery_tracking_schema_versions WHERE version = '20261002_09_wa_evento';
