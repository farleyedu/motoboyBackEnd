BEGIN;

-- ---------------------------------------------------------------------------
-- Cadastro estruturado do motoboy (Fase A do fluxo de motoboy). Aditiva e idempotente: nada que
-- existe e alterado. Campos pertencem a linha CANONICA de motoboy (canonical_motoboy_id) -- uma
-- pessoa, um cadastro, N vinculos em motoboy_estabelecimento.
--  - status_cadastro e administrativo (ativo/inativo/bloqueado), SEPARADO de motoboy.status (presenca
--    online/offline/delivering, legado). Nao confundir os dois.
--  - cpf tem indice unico parcial (so quando preenchido) pra nao duplicar pessoa, sem travar motoboy
--    de teste do simulador (que pode nao ter CPF).
--  - sem foto de documento, aprovacao manual, avaliacao/nota ou metricas: fica para depois.
-- ---------------------------------------------------------------------------

ALTER TABLE motoboy
    ADD COLUMN IF NOT EXISTS cpf TEXT NULL,
    ADD COLUMN IF NOT EXISTS data_nascimento DATE NULL,
    ADD COLUMN IF NOT EXISTS cep TEXT NULL,
    ADD COLUMN IF NOT EXISTS logradouro TEXT NULL,
    ADD COLUMN IF NOT EXISTS numero TEXT NULL,
    ADD COLUMN IF NOT EXISTS complemento TEXT NULL,
    ADD COLUMN IF NOT EXISTS bairro TEXT NULL,
    ADD COLUMN IF NOT EXISTS cidade TEXT NULL,
    ADD COLUMN IF NOT EXISTS uf TEXT NULL,
    ADD COLUMN IF NOT EXISTS tipo_veiculo TEXT NULL,
    ADD COLUMN IF NOT EXISTS cnh_numero TEXT NULL,
    ADD COLUMN IF NOT EXISTS cnh_categoria TEXT NULL,
    ADD COLUMN IF NOT EXISTS cnh_validade DATE NULL,
    ADD COLUMN IF NOT EXISTS pix_tipo TEXT NULL,
    ADD COLUMN IF NOT EXISTS pix_chave TEXT NULL,
    ADD COLUMN IF NOT EXISTS banco_nome TEXT NULL,
    ADD COLUMN IF NOT EXISTS banco_agencia TEXT NULL,
    ADD COLUMN IF NOT EXISTS banco_conta TEXT NULL,
    ADD COLUMN IF NOT EXISTS status_cadastro TEXT NOT NULL DEFAULT 'ativo';

ALTER TABLE motoboy
    DROP CONSTRAINT IF EXISTS ck_motoboy_tipo_veiculo,
    DROP CONSTRAINT IF EXISTS ck_motoboy_pix_tipo,
    DROP CONSTRAINT IF EXISTS ck_motoboy_status_cadastro;

ALTER TABLE motoboy
    ADD CONSTRAINT ck_motoboy_tipo_veiculo
        CHECK (tipo_veiculo IS NULL OR tipo_veiculo IN ('moto', 'bicicleta', 'carro', 'a_pe')),
    ADD CONSTRAINT ck_motoboy_pix_tipo
        CHECK (pix_tipo IS NULL OR pix_tipo IN ('cpf', 'cnpj', 'email', 'telefone', 'aleatoria')),
    ADD CONSTRAINT ck_motoboy_status_cadastro
        CHECK (status_cadastro IN ('ativo', 'inativo', 'bloqueado'));

CREATE UNIQUE INDEX IF NOT EXISTS ux_motoboy_cpf
    ON motoboy (cpf)
    WHERE cpf IS NOT NULL;

INSERT INTO delivery_tracking_schema_versions (version)
VALUES ('20261006_01_motoboy_perfil')
ON CONFLICT (version) DO NOTHING;

COMMIT;
