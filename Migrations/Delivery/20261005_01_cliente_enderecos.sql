BEGIN;

-- ---------------------------------------------------------------------------
-- Enderecos do cliente: um cliente pode ter varios enderecos (casa, trabalho...) e o atendente escolhe um ao montar o
-- pedido. Tabela NOVA, ligada a CLIENTES (id UUID). Aditiva e idempotente: nada que existe e alterado.
--  - ativo = FALSE e a exclusao logica (pedidos antigos continuam com o endereco que usaram, que fica gravado neles).
--  - principal: no maximo UM endereco ativo por cliente (indice unico parcial); e o sugerido por padrao.
--  - as colunas de endereco em CLIENTES seguem como estao (compatibilidade); aqui o endereco existente delas vira o
--    primeiro endereco principal do cliente (backfill abaixo, so para quem ainda nao tem nenhum).
-- ---------------------------------------------------------------------------

CREATE TABLE IF NOT EXISTS cliente_enderecos (
    id UUID PRIMARY KEY DEFAULT gen_random_uuid(),
    id_cliente UUID NOT NULL REFERENCES clientes (id) ON DELETE CASCADE,
    apelido TEXT NULL,
    cep TEXT NULL,
    logradouro TEXT NOT NULL,
    numero TEXT NOT NULL,
    complemento TEXT NULL,
    bairro TEXT NOT NULL,
    cidade TEXT NOT NULL,
    uf TEXT NULL,
    referencia TEXT NULL,
    latitude DOUBLE PRECISION NULL,
    longitude DOUBLE PRECISION NULL,
    principal BOOLEAN NOT NULL DEFAULT FALSE,
    ativo BOOLEAN NOT NULL DEFAULT TRUE,
    created_at_utc TIMESTAMPTZ NOT NULL DEFAULT NOW(),
    updated_at_utc TIMESTAMPTZ NOT NULL DEFAULT NOW()
);

CREATE INDEX IF NOT EXISTS ix_cliente_enderecos_cliente
    ON cliente_enderecos (id_cliente)
    WHERE ativo;

CREATE UNIQUE INDEX IF NOT EXISTS ux_cliente_enderecos_principal
    ON cliente_enderecos (id_cliente)
    WHERE principal AND ativo;

-- Endereco ja cadastrado direto no cliente vira o principal dele (so completo: com rua, numero, bairro e cidade).
INSERT INTO cliente_enderecos (id_cliente, apelido, cep, logradouro, numero, complemento, bairro, cidade, uf, latitude, longitude, principal)
SELECT c.id, 'Principal', c.cep, c.logradouro, c.numero, c.complemento, c.bairro, c.cidade, c.uf, c.latitude, c.longitude, TRUE
  FROM clientes c
 WHERE COALESCE(btrim(c.logradouro), '') <> ''
   AND COALESCE(btrim(c.numero), '') <> ''
   AND COALESCE(btrim(c.bairro), '') <> ''
   AND COALESCE(btrim(c.cidade), '') <> ''
   AND NOT EXISTS (SELECT 1 FROM cliente_enderecos e WHERE e.id_cliente = c.id);

INSERT INTO delivery_tracking_schema_versions (version)
VALUES ('20261005_01_cliente_enderecos')
ON CONFLICT (version) DO NOTHING;

COMMIT;
