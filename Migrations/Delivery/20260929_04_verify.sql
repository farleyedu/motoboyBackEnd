-- Verificacao do modulo Clientes. Somente leitura; nao e aplicada automaticamente.

-- 1) Valor do enum e colunas novas (deve listar enum_ok = true e 14 colunas).
SELECT 'CLIENTES' = ANY (enum_range(NULL::modulo_enum)::text[]) AS enum_ok;
SELECT column_name FROM information_schema.columns
 WHERE table_schema = current_schema() AND table_name = 'clientes'
   AND column_name IN ('email','observacoes','cep','logradouro','numero','complemento','bairro','cidade','uf',
                       'latitude','longitude','ativo','simulado','criado_por_usuario_id')
 ORDER BY column_name;

-- 2) Mesmo telefone em dois clientes ATIVOS do mesmo estabelecimento (o cadastro impede; linhas antigas do
--    WhatsApp podem ter). Deveria voltar vazio.
SELECT id_estabelecimento, telefone_e164, COUNT(*)
  FROM clientes
 WHERE ativo = TRUE AND telefone_e164 IS NOT NULL
 GROUP BY 1, 2
HAVING COUNT(*) > 1;

-- 3) Estabelecimentos com Delivery/Pedidos sem o modulo CLIENTES (deveria voltar vazio apos o backfill).
SELECT id FROM estabelecimentos
 WHERE (modulos_ativos @> ARRAY['DELIVERY']::modulo_enum[] OR modulos_ativos @> ARRAY['PEDIDOS']::modulo_enum[])
   AND NOT (modulos_ativos @> ARRAY['CLIENTES']::modulo_enum[]);

SELECT version, applied_at_utc FROM delivery_tracking_schema_versions WHERE version LIKE '20260929_%' ORDER BY version;
