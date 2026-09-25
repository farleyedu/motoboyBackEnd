-- Desfaz 20260929_02_clientes_cadastro: remove SO as colunas do cadastro (perde esses dados).
-- A tabela clientes em si NAO e apagada: ela e do WhatsApp/automacao. O valor CLIENTES do enum
-- modulo_enum tambem fica (o Postgres nao remove valor de enum).
BEGIN;
DROP INDEX IF EXISTS ix_clientes_estab_nome;
ALTER TABLE clientes
    DROP COLUMN IF EXISTS email, DROP COLUMN IF EXISTS observacoes, DROP COLUMN IF EXISTS cep,
    DROP COLUMN IF EXISTS logradouro, DROP COLUMN IF EXISTS numero, DROP COLUMN IF EXISTS complemento,
    DROP COLUMN IF EXISTS bairro, DROP COLUMN IF EXISTS cidade, DROP COLUMN IF EXISTS uf,
    DROP COLUMN IF EXISTS latitude, DROP COLUMN IF EXISTS longitude, DROP COLUMN IF EXISTS ativo, DROP COLUMN IF EXISTS simulado,
    DROP COLUMN IF EXISTS criado_por_usuario_id;
DELETE FROM delivery_tracking_schema_versions WHERE version = '20260929_02_clientes_cadastro';
COMMIT;
