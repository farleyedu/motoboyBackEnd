-- Verificacao do estado do motor de atendimento. Somente leitura; nao e aplicada automaticamente.

SELECT fluxo_chave, COUNT(*) AS conversas FROM conversas WHERE fluxo_chave IS NOT NULL GROUP BY fluxo_chave ORDER BY conversas DESC;

SELECT version, applied_at_utc FROM delivery_tracking_schema_versions WHERE version = '20261002_13_conversas_fluxo';
