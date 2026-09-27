-- Desfaz 20260928_04_zonas_operacao.
BEGIN;
ALTER TABLE estabelecimentos DROP COLUMN IF EXISTS entrega_gratis_acima_de;
DROP TABLE IF EXISTS delivery_zona;
DROP TABLE IF EXISTS estabelecimento_horario_especial;
ALTER TABLE delivery_settings
    DROP COLUMN IF EXISTS auto_confirmar_pedidos,
    DROP COLUMN IF EXISTS autoatribuir_motoboy,
    DROP COLUMN IF EXISTS bloquear_pedidos_fora_horario,
    DROP COLUMN IF EXISTS retirada_balcao_ativa,
    DROP COLUMN IF EXISTS retirada_tempo_preparo_min;
DELETE FROM delivery_tracking_schema_versions WHERE version = '20260928_04_zonas_operacao';
COMMIT;
