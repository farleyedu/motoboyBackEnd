-- Desfaz o cadastro estruturado do motoboy (Fase A). Destrutivo: os dados preenchidos nesses
-- campos (CPF, endereco, CNH, dados bancarios, status_cadastro) sao perdidos.
BEGIN;
DROP INDEX IF EXISTS ux_motoboy_cpf;
ALTER TABLE motoboy
    DROP CONSTRAINT IF EXISTS ck_motoboy_tipo_veiculo,
    DROP CONSTRAINT IF EXISTS ck_motoboy_pix_tipo,
    DROP CONSTRAINT IF EXISTS ck_motoboy_status_cadastro;
ALTER TABLE motoboy
    DROP COLUMN IF EXISTS cpf,
    DROP COLUMN IF EXISTS data_nascimento,
    DROP COLUMN IF EXISTS cep,
    DROP COLUMN IF EXISTS logradouro,
    DROP COLUMN IF EXISTS numero,
    DROP COLUMN IF EXISTS complemento,
    DROP COLUMN IF EXISTS bairro,
    DROP COLUMN IF EXISTS cidade,
    DROP COLUMN IF EXISTS uf,
    DROP COLUMN IF EXISTS tipo_veiculo,
    DROP COLUMN IF EXISTS cnh_numero,
    DROP COLUMN IF EXISTS cnh_categoria,
    DROP COLUMN IF EXISTS cnh_validade,
    DROP COLUMN IF EXISTS pix_tipo,
    DROP COLUMN IF EXISTS pix_chave,
    DROP COLUMN IF EXISTS banco_nome,
    DROP COLUMN IF EXISTS banco_agencia,
    DROP COLUMN IF EXISTS banco_conta,
    DROP COLUMN IF EXISTS status_cadastro;
DELETE FROM delivery_tracking_schema_versions WHERE version = '20261006_01_motoboy_perfil';
COMMIT;
