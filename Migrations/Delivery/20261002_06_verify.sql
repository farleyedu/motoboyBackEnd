-- Verificacao das tabelas que antes eram criadas pelo codigo. Somente leitura; nao e aplicada automaticamente.

SELECT table_name FROM information_schema.tables
 WHERE table_name IN ('checkout_asaas_customers', 'checkout_pagamentos', 'checkout_webhook_logs', 'empresa_webhook_auditoria')
 ORDER BY table_name;

SELECT column_name FROM information_schema.columns
 WHERE table_name = 'empresas' AND column_name IN ('ativo', 'pausada') ORDER BY column_name;

SELECT version, applied_at_utc FROM delivery_tracking_schema_versions
 WHERE version = '20261002_05_tabelas_do_codigo';
