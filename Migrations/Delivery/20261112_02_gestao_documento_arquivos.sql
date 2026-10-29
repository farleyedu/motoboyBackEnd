-- Gestao, Integracao 6B: o arquivo original do documento de compra (foto do cupom ou XML), para a conferencia mostrar o
-- documento real. Um arquivo por documento, imutavel como o documento. Requer 20261106_01_gestao_documentos.sql.
BEGIN;

CREATE TABLE IF NOT EXISTS gestao_documento_arquivos (
    documento_id uuid PRIMARY KEY REFERENCES gestao_documentos(id),
    estabelecimento_id uuid NOT NULL REFERENCES estabelecimentos(id),
    media_type varchar(40) NOT NULL,
    conteudo bytea NOT NULL CHECK (octet_length(conteudo) BETWEEN 1 AND 10485760),
    criado_em timestamptz NOT NULL DEFAULT now()
);
CREATE INDEX IF NOT EXISTS ix_gestao_documento_arquivos_loja ON gestao_documento_arquivos (estabelecimento_id, documento_id);

DROP TRIGGER IF EXISTS gestao_documento_arquivos_imutavel ON gestao_documento_arquivos;
CREATE TRIGGER gestao_documento_arquivos_imutavel BEFORE UPDATE OR DELETE ON gestao_documento_arquivos
    FOR EACH ROW EXECUTE FUNCTION gestao_bloqueia_alteracao();

COMMIT;
