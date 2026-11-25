BEGIN;

-- Assistente da Zippy: chaves das IAs cadastradas pela Gestão (CONTRATO-API §11). Uma por provedor, sempre CIFRADA (AES-GCM com
-- subchave HKDF de Atendimento:ChaveToken, prefixo "ak1."); o texto puro nunca chega ao banco. Variável de ambiente, se existir,
-- tem prioridade sobre esta tabela. A auditoria guarda quem cadastrou/trocou/apagou e o final, nunca o valor.
-- Requer usuario. Aditiva e idempotente.

CREATE TABLE IF NOT EXISTS assistente_chaves (
    provedor          TEXT PRIMARY KEY,
    cifrada           TEXT NOT NULL,
    final             VARCHAR(4) NOT NULL,
    salva_por         INTEGER NULL REFERENCES usuario (id),
    salva_em          TIMESTAMPTZ NOT NULL DEFAULT NOW(),
    ultimo_teste_em   TIMESTAMPTZ NULL,
    ultimo_teste_ok   BOOLEAN NULL,
    ultimo_teste_erro TEXT NULL
);
-- Guarda contra gravar texto puro por engano: só aceita o formato cifrado.
ALTER TABLE assistente_chaves DROP CONSTRAINT IF EXISTS ck_assistente_chaves_cifrada;
ALTER TABLE assistente_chaves ADD CONSTRAINT ck_assistente_chaves_cifrada CHECK (cifrada LIKE 'ak1.%');

CREATE TABLE IF NOT EXISTS assistente_chaves_auditoria (
    id        BIGSERIAL PRIMARY KEY,
    provedor  TEXT NOT NULL,
    acao      TEXT NOT NULL,
    final     VARCHAR(4) NULL,
    usuario   INTEGER NULL REFERENCES usuario (id),
    criado_em TIMESTAMPTZ NOT NULL DEFAULT NOW()
);
ALTER TABLE assistente_chaves_auditoria DROP CONSTRAINT IF EXISTS ck_assistente_chaves_auditoria_acao;
ALTER TABLE assistente_chaves_auditoria ADD CONSTRAINT ck_assistente_chaves_auditoria_acao CHECK (acao IN ('cadastrou', 'trocou', 'apagou'));
CREATE INDEX IF NOT EXISTS ix_assistente_chaves_auditoria_em ON assistente_chaves_auditoria (criado_em DESC);

INSERT INTO delivery_tracking_schema_versions (version)
VALUES ('20261205_11_assistente_chaves')
ON CONFLICT (version) DO NOTHING;

COMMIT;
