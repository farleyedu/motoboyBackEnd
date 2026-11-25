-- Gestao, Logica E1: documentos de compra lidos (XML de NF-e/NFC-e, foto de cupom). docs/zippy-gestao/CONTRATOS.md.
-- O documento e o que foi lido, como foi lido; nada aqui mexe no financeiro nem no estoque (isso e a compra e a conferencia).
-- Duplicidade: a mesma chave de acesso, o mesmo arquivo (hash) ou o mesmo CNPJ+serie+numero nao entram duas vezes na loja.
-- Imutavel (gatilho da Fundacao). Requer 20261105_01_gestao_fundacao.sql. Aditiva e idempotente.
BEGIN;

CREATE TABLE IF NOT EXISTS gestao_documentos (
    id uuid PRIMARY KEY,
    estabelecimento_id uuid NOT NULL REFERENCES estabelecimentos(id),
    origem varchar(10) NOT NULL CHECK (origem IN ('xml', 'foto', 'manual')),
    chave_acesso varchar(44) CHECK (chave_acesso ~ '^[0-9]{44}$'),
    hash_arquivo varchar(64),
    impressao varchar(120),
    semelhanca varchar(200),
    fornecedor_cnpj varchar(14),
    fornecedor_nome varchar(160),
    data_emissao timestamptz,
    numero varchar(20),
    serie varchar(5),
    total numeric(15,2) NOT NULL DEFAULT 0,
    leitura jsonb NOT NULL,
    criado_por integer NOT NULL REFERENCES usuario(id),
    criado_em timestamptz NOT NULL DEFAULT now()
);
CREATE UNIQUE INDEX IF NOT EXISTS ux_gestao_documentos_chave ON gestao_documentos (estabelecimento_id, chave_acesso) WHERE chave_acesso IS NOT NULL;
CREATE UNIQUE INDEX IF NOT EXISTS ux_gestao_documentos_hash ON gestao_documentos (estabelecimento_id, hash_arquivo) WHERE hash_arquivo IS NOT NULL;
CREATE UNIQUE INDEX IF NOT EXISTS ux_gestao_documentos_impressao ON gestao_documentos (estabelecimento_id, impressao) WHERE impressao IS NOT NULL;
CREATE INDEX IF NOT EXISTS ix_gestao_documentos_semelhanca ON gestao_documentos (estabelecimento_id, semelhanca) WHERE semelhanca IS NOT NULL;
CREATE INDEX IF NOT EXISTS ix_gestao_documentos_loja_data ON gestao_documentos (estabelecimento_id, criado_em DESC);

DROP TRIGGER IF EXISTS gestao_documentos_imutavel ON gestao_documentos;
CREATE TRIGGER gestao_documentos_imutavel BEFORE UPDATE OR DELETE ON gestao_documentos
    FOR EACH ROW EXECUTE FUNCTION gestao_bloqueia_alteracao();
DROP TRIGGER IF EXISTS gestao_documentos_sem_truncate ON gestao_documentos;
CREATE TRIGGER gestao_documentos_sem_truncate BEFORE TRUNCATE ON gestao_documentos
    FOR EACH STATEMENT EXECUTE FUNCTION gestao_bloqueia_alteracao();

COMMIT;
