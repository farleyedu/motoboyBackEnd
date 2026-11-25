-- Situacao comercial do estabelecimento (plano docs/plano-modulos-pacotes-teste-limites.md, secao 3.1 do zippy-admin).
-- Separada da coluna "status" (cadastro ativo/inativo/trial), que ja e usada em varias consultas de login e selecao.
--   implantacao: configura tudo, mas delivery e integracoes ficam travados (ainda nao aplicado; etapa 7)
--   teste:       15 dias com todos os recursos, contados de operacao_iniciada_em (fim so informativo)
--   ativo:       plano contratado
--   bloqueado:   loga, mas a API responde 403 ESTABELECIMENTO_BLOQUEADO (SituacaoComercialMiddleware)
-- Toda mudanca e manual, feita pela Gestao. Lojas que ja existem ficam "ativo". Aditivo e idempotente.

ALTER TABLE estabelecimentos ADD COLUMN IF NOT EXISTS situacao_comercial TEXT NOT NULL DEFAULT 'ativo';
-- O ADD COLUMN preserva todas as lojas existentes como ativas. Novos cadastros nascem em implantacao.
ALTER TABLE estabelecimentos ALTER COLUMN situacao_comercial SET DEFAULT 'implantacao';
ALTER TABLE estabelecimentos ADD COLUMN IF NOT EXISTS operacao_iniciada_em TIMESTAMPTZ NULL;
ALTER TABLE estabelecimentos ADD COLUMN IF NOT EXISTS teste_termina_em TIMESTAMPTZ NULL;
ALTER TABLE estabelecimentos ADD COLUMN IF NOT EXISTS bloqueado_em TIMESTAMPTZ NULL;
ALTER TABLE estabelecimentos ADD COLUMN IF NOT EXISTS bloqueado_por INTEGER NULL;
ALTER TABLE estabelecimentos ADD COLUMN IF NOT EXISTS bloqueado_motivo TEXT NULL;
-- Chave do plano (partida, rota, turbo, frota) ou 'personalizado'; nulo = ainda nao escolhido.
ALTER TABLE estabelecimentos ADD COLUMN IF NOT EXISTS plano_comercial TEXT NULL;
-- Pacotes de 3 motoboys comprados; os limites de vagas/vinculos saem de uma regra unica (MotoboyLimites).
ALTER TABLE estabelecimentos ADD COLUMN IF NOT EXISTS pacotes_motoboy INTEGER NOT NULL DEFAULT 1;

DO $$
BEGIN
    IF NOT EXISTS (SELECT 1 FROM pg_constraint WHERE conname = 'ck_estabelecimentos_situacao_comercial') THEN
        ALTER TABLE estabelecimentos ADD CONSTRAINT ck_estabelecimentos_situacao_comercial
            CHECK (situacao_comercial IN ('implantacao', 'teste', 'ativo', 'bloqueado'));
    END IF;
    IF NOT EXISTS (SELECT 1 FROM pg_constraint WHERE conname = 'ck_estabelecimentos_pacotes_motoboy') THEN
        ALTER TABLE estabelecimentos ADD CONSTRAINT ck_estabelecimentos_pacotes_motoboy CHECK (pacotes_motoboy >= 1);
    END IF;
END $$;

-- Historico de cada mudanca (quem, quando, de onde para onde, por que).
CREATE TABLE IF NOT EXISTS estabelecimento_situacao_log (
    id               BIGSERIAL PRIMARY KEY,
    estabelecimento_id UUID NOT NULL REFERENCES estabelecimentos (id) ON DELETE CASCADE,
    acao             TEXT NOT NULL,
    situacao_antes   TEXT NULL,
    situacao_depois  TEXT NOT NULL,
    motivo           TEXT NULL,
    usuario_id       INTEGER NULL,
    detalhes         JSONB NULL,
    criado_em        TIMESTAMPTZ NOT NULL DEFAULT NOW()
);
CREATE INDEX IF NOT EXISTS ix_estabelecimento_situacao_log_estab ON estabelecimento_situacao_log (estabelecimento_id, criado_em DESC);

INSERT INTO delivery_tracking_schema_versions (version)
VALUES ('20261020_02_situacao_comercial')
ON CONFLICT (version) DO NOTHING;
