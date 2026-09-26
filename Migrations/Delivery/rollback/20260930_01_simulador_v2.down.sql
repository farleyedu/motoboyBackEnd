-- Desfaz 20260930_01_simulador_v2: apaga eventos/sessoes do simulador e as colunas novas (perde esses dados).
BEGIN;
DROP TABLE IF EXISTS simulador_sessao;
DROP TABLE IF EXISTS simulador_evento;
ALTER TABLE clientes
    DROP COLUMN IF EXISTS avatar, DROP COLUMN IF EXISTS cpf, DROP COLUMN IF EXISTS data_nascimento,
    DROP COLUMN IF EXISTS referencia, DROP COLUMN IF EXISTS canal_preferido, DROP COLUMN IF EXISTS origem,
    DROP COLUMN IF EXISTS tags, DROP COLUMN IF EXISTS consentimento_whatsapp;
ALTER TABLE pedido DROP COLUMN IF EXISTS confirmado_em_utc, DROP COLUMN IF EXISTS preparo_em_utc, DROP COLUMN IF EXISTS canal;
DELETE FROM delivery_tracking_schema_versions WHERE version = '20260930_01_simulador_v2';
COMMIT;
