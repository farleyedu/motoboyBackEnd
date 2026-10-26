-- Desfaz 20261101_20_pagamentos_gestao_oauth (so em banco de teste; perde politicas, verificacoes e liberacao de ambiente).
BEGIN;
DROP INDEX IF EXISTS ix_mp_oauth_tentativa_expira;
DROP INDEX IF EXISTS ix_mp_webhook_evento_status;
DROP TABLE IF EXISTS pagamento_politica;
DROP TABLE IF EXISTS mp_verificacao;
DROP TABLE IF EXISTS mp_ambiente_estado;
DELETE FROM delivery_tracking_schema_versions WHERE version = '20261101_20_pagamentos_gestao_oauth';
COMMIT;
