-- Verificacao da Fase 2 (zonas e operacao). Somente leitura; nao e aplicada automaticamente.

SELECT column_name FROM information_schema.columns
 WHERE table_schema = current_schema() AND table_name = 'estabelecimentos' AND column_name = 'entrega_gratis_acima_de';

SELECT to_regclass('delivery_zona') AS tabela_zona, to_regclass('estabelecimento_horario_especial') AS tabela_especial;

SELECT column_name FROM information_schema.columns
 WHERE table_schema = current_schema() AND table_name = 'delivery_settings'
   AND column_name IN ('auto_confirmar_pedidos','autoatribuir_motoboy','bloquear_pedidos_fora_horario','retirada_balcao_ativa','retirada_tempo_preparo_min')
 ORDER BY column_name;

SELECT version, applied_at_utc FROM delivery_tracking_schema_versions
 WHERE version = '20260928_04_zonas_operacao';
