BEGIN;

-- ---------------------------------------------------------------------------
-- Cadastro de clientes: estende a tabela CLIENTES que ja existe (criada pelo WhatsApp com nome +
-- telefone_e164 e ligada as conversas/leads por id_cliente). NAO cria tabela nova: o cliente
-- cadastrado e o mesmo que chega pelo chat. So colunas novas, todas opcionais ou com padrao, entao
-- as linhas e os fluxos de automacao atuais seguem iguais. Idempotente.
-- Exclusao e logica (ativo = FALSE): conversas e pedidos antigos continuam apontando para a linha.
-- ---------------------------------------------------------------------------

ALTER TABLE clientes
    ADD COLUMN IF NOT EXISTS email TEXT NULL,
    ADD COLUMN IF NOT EXISTS observacoes TEXT NULL,
    ADD COLUMN IF NOT EXISTS cep TEXT NULL,
    ADD COLUMN IF NOT EXISTS logradouro TEXT NULL,
    ADD COLUMN IF NOT EXISTS numero TEXT NULL,
    ADD COLUMN IF NOT EXISTS complemento TEXT NULL,
    ADD COLUMN IF NOT EXISTS bairro TEXT NULL,
    ADD COLUMN IF NOT EXISTS cidade TEXT NULL,
    ADD COLUMN IF NOT EXISTS uf TEXT NULL,
    ADD COLUMN IF NOT EXISTS latitude DOUBLE PRECISION NULL,
    ADD COLUMN IF NOT EXISTS longitude DOUBLE PRECISION NULL,
    ADD COLUMN IF NOT EXISTS ativo BOOLEAN NOT NULL DEFAULT TRUE,
    -- Cliente de TESTE: so ele pode ser simulado (mensagens pelo webhook) e o envio real de WhatsApp para ele e suprimido.
    ADD COLUMN IF NOT EXISTS simulado BOOLEAN NOT NULL DEFAULT FALSE,
    ADD COLUMN IF NOT EXISTS criado_por_usuario_id INTEGER NULL;

CREATE INDEX IF NOT EXISTS ix_clientes_estab_nome
    ON clientes (id_estabelecimento, lower(COALESCE(nome, '')));

INSERT INTO delivery_tracking_schema_versions (version)
VALUES ('20260929_02_clientes_cadastro')
ON CONFLICT (version) DO NOTHING;

COMMIT;
