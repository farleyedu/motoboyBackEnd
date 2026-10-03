-- Verificacao do canal nas conversas. Somente leitura; nao e aplicada automaticamente.

SELECT COUNT(*) FILTER (WHERE id_canal IS NOT NULL) AS com_canal, COUNT(*) FILTER (WHERE id_canal IS NULL) AS sem_canal
  FROM conversas;

SELECT c.numero_e164, COUNT(*) AS conversas
  FROM conversas v JOIN canal_whatsapp c ON c.id = v.id_canal
 GROUP BY c.numero_e164 ORDER BY conversas DESC;

SELECT version, applied_at_utc FROM delivery_tracking_schema_versions WHERE version = '20261002_11_conversas_canal';
