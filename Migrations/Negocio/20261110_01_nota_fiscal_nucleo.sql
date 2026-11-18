-- Nota fiscal (NFC-e), prompt 1: conexao com o provedor, regras de emissao e dados fiscais dos produtos.
-- Aplicada no boot pelo DeliveryMigrationHostedService (varre Migrations/Negocio). Aditiva e idempotente.
-- A chave do provedor fica cifrada (ITokenProtector, AES-GCM) e nunca sai do backend.
BEGIN;

CREATE TABLE IF NOT EXISTS nf_conexao (
    estabelecimento_id        UUID PRIMARY KEY REFERENCES estabelecimentos (id) ON DELETE CASCADE,
    provedor                  TEXT NOT NULL DEFAULT 'focus',
    ambiente                  TEXT NOT NULL DEFAULT 'homologacao' CHECK (ambiente IN ('homologacao', 'producao')),
    status                    TEXT NOT NULL DEFAULT 'nao_conectado' CHECK (status IN ('nao_conectado', 'conectado', 'erro')),
    modo_conta                TEXT NOT NULL DEFAULT 'da_loja' CHECK (modo_conta IN ('da_loja', 'do_zippy')),
    empresa_provedor_id       TEXT NULL,
    chave_cifrada             TEXT NULL,
    regime                    TEXT NULL CHECK (regime IS NULL OR regime IN ('mei', 'simples', 'presumido', 'real')),
    inscricao_estadual        TEXT NULL,
    confirmou_certificado     BOOLEAN NOT NULL DEFAULT FALSE,
    confirmou_csc             BOOLEAN NOT NULL DEFAULT FALSE,
    ultimo_teste_em           TIMESTAMPTZ NULL,
    mensagem                  TEXT NULL,
    webhook_segredo_cifrado   TEXT NULL,
    atualizado_por            INTEGER NULL,
    atualizado_em             TIMESTAMPTZ NOT NULL DEFAULT NOW()
);

CREATE TABLE IF NOT EXISTS nf_regras (
    estabelecimento_id  UUID PRIMARY KEY REFERENCES estabelecimentos (id) ON DELETE CASCADE,
    modo                TEXT NOT NULL DEFAULT 'manual' CHECK (modo IN ('manual', 'automatico', 'so_com_cpf')),
    canais              JSONB NOT NULL DEFAULT '{"cardapio":true,"balcao":true,"mesa":true,"ifood":true}'::jsonb,
    formas_pagamento    JSONB NOT NULL DEFAULT '{"dinheiro":true,"pix":true,"debito":true,"credito":true,"online":true}'::jsonb,
    espera_minutos      INTEGER NOT NULL DEFAULT 5 CHECK (espera_minutos BETWEEN 0 AND 60),
    atualizado_por      INTEGER NULL,
    atualizado_em       TIMESTAMPTZ NOT NULL DEFAULT NOW()
);

-- produto_id e o id de cardapio_produto. Sem FK de proposito: produto apagado nao pode travar nem apagar o historico fiscal.
CREATE TABLE IF NOT EXISTS nf_produto_fiscal (
    produto_id          UUID PRIMARY KEY,
    estabelecimento_id  UUID NOT NULL REFERENCES estabelecimentos (id) ON DELETE CASCADE,
    ncm                 TEXT NULL,
    cfop                TEXT NULL,
    csosn               TEXT NULL,
    cst                 TEXT NULL,
    origem              SMALLINT NULL CHECK (origem IS NULL OR origem BETWEEN 0 AND 8),
    unidade             TEXT NULL,
    atualizado_em       TIMESTAMPTZ NOT NULL DEFAULT NOW()
);

CREATE INDEX IF NOT EXISTS ix_nf_produto_fiscal_estabelecimento ON nf_produto_fiscal (estabelecimento_id);

INSERT INTO delivery_tracking_schema_versions (version)
VALUES ('20261110_01_nota_fiscal_nucleo')
ON CONFLICT (version) DO NOTHING;

COMMIT;
