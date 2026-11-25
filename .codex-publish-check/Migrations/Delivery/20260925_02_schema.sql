BEGIN;

-- ---------------------------------------------------------------------------
-- Fase 2 (nucleo de pedido): origem, referencia externa, vinculo com conversa/cliente,
-- totais calculados pelo servidor e itens estruturados com preco congelado.
-- Tudo aditivo e idempotente: nada legado e removido nem alterado. A coluna legada
-- pedido.items continua sendo gravada (derivada dos itens) ate a retirada do legado.
-- ---------------------------------------------------------------------------

ALTER TABLE pedido ADD COLUMN IF NOT EXISTS origem       TEXT NOT NULL DEFAULT 'atendente';
ALTER TABLE pedido ADD COLUMN IF NOT EXISTS origem_ref   TEXT NULL;
ALTER TABLE pedido ADD COLUMN IF NOT EXISTS conversa_id  UUID NULL;
ALTER TABLE pedido ADD COLUMN IF NOT EXISTS cliente_id   UUID NULL;
-- "value" continua sendo o TOTAL do pedido (subtotal + taxa - desconto), agora calculado no servidor.
ALTER TABLE pedido ADD COLUMN IF NOT EXISTS subtotal     NUMERIC(12, 2) NULL;
ALTER TABLE pedido ADD COLUMN IF NOT EXISTS taxa_entrega NUMERIC(12, 2) NULL;
ALTER TABLE pedido ADD COLUMN IF NOT EXISTS desconto     NUMERIC(12, 2) NULL;

CREATE TABLE IF NOT EXISTS pedido_item (
    id             BIGSERIAL PRIMARY KEY,
    pedido_id      INTEGER NOT NULL REFERENCES pedido (id) ON DELETE CASCADE,
    -- Nulo para linha avulsa (sem produto do cardapio).
    produto_id     UUID NULL,
    -- Nome e preco sao COPIA do cardapio no momento do pedido: mudar o cardapio depois
    -- nao altera pedidos ja feitos.
    nome           TEXT NOT NULL,
    quantidade     INTEGER NOT NULL CHECK (quantidade > 0),
    preco_unitario NUMERIC(12, 2) NOT NULL CHECK (preco_unitario >= 0),
    observacao     TEXT NULL,
    -- [{"id":"<uuid>","nome":"...","preco":1.50}] copiado do cardapio.
    adicionais     JSONB NOT NULL DEFAULT '[]'::jsonb,
    ordem          INTEGER NOT NULL DEFAULT 0
);

INSERT INTO delivery_tracking_schema_versions (version)
VALUES ('20260925_02_schema')
ON CONFLICT (version) DO NOTHING;

COMMIT;
