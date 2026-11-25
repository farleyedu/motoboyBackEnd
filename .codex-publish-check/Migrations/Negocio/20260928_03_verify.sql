-- Verificacao da Fase 1 (identidade/horarios). Somente leitura; nao e aplicada automaticamente.

SELECT column_name FROM information_schema.columns
 WHERE table_schema = current_schema() AND table_name = 'estabelecimentos'
   AND column_name IN ('site_url','favicon_url','cor_primaria','cor_secundaria','tipografia','instagram_url','facebook_url')
 ORDER BY column_name;

SELECT to_regclass('estabelecimento_horario') AS tabela_horario;

SELECT dia_semana, fechado, abre_as, fecha_as FROM estabelecimento_horario
 WHERE estabelecimento_id = '97c5d396-a42c-47de-8f21-a38c3f79d118'
 ORDER BY dia_semana;

SELECT nome_fantasia, cnpj_loja, telefone, site_url, cor_primaria, cor_secundaria
  FROM estabelecimentos WHERE id = '97c5d396-a42c-47de-8f21-a38c3f79d118';

SELECT version, applied_at_utc FROM delivery_tracking_schema_versions
 WHERE version LIKE '20260928_0%' ORDER BY version;
