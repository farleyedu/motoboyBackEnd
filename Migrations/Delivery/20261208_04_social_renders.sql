-- Zippy Social R13: registro de cada vídeo gerado de um corte. O vídeo é gerado no navegador (WebCodecs) ou numa máquina com o
-- Zippy Worker (R14); o backend NUNCA processa vídeo, só registra o andamento. O MP4 só vai para o R2 quando a pessoa for
-- publicar (POST /api/social/renders/{id}/subir devolve a URL pré-assinada); até lá ele fica no PC de quem gerou.
-- Mesma row-level security das outras tabelas social_* (zippy.loja / zippy.sistema). Idempotente.
BEGIN;

CREATE TABLE IF NOT EXISTS social_renders (
    id              uuid PRIMARY KEY DEFAULT gen_random_uuid(),
    estabelecimento_id uuid NOT NULL REFERENCES estabelecimentos(id) ON DELETE CASCADE,
    corte_id        bigint NOT NULL REFERENCES social_cortes(id) ON DELETE CASCADE,
    receita_versao  integer NOT NULL CHECK (receita_versao >= 0),
    onde            text NOT NULL CHECK (onde IN ('navegador', 'maquina')),
    maquina_id      bigint,               -- social_maquinas entra no R14; sem FK até lá
    destino         text NOT NULL DEFAULT 'shorts',
    qualidade       text NOT NULL DEFAULT '1080p',
    status          text NOT NULL DEFAULT 'gerando' CHECK (status IN ('gerando', 'pronto', 'falhou', 'cancelado')),
    progresso       numeric(5,2) NOT NULL DEFAULT 0 CHECK (progresso >= 0 AND progresso <= 100),
    bytes           bigint CHECK (bytes IS NULL OR bytes > 0),
    duracao         numeric(10,2),
    gasto_s         numeric(10,2),        -- quanto tempo levou para gerar
    erro            text,
    nome_arquivo    text,
    r2_key          text,                 -- preenchido só quando sobe para publicar
    enviado_em      timestamptz,
    criado_por      integer,
    criado_em       timestamptz NOT NULL DEFAULT NOW(),
    atualizado_em   timestamptz NOT NULL DEFAULT NOW(),
    concluido_em    timestamptz,
    CONSTRAINT ck_social_renders_maquina CHECK (onde = 'navegador' OR maquina_id IS NOT NULL)
);
CREATE INDEX IF NOT EXISTS ix_social_renders_corte ON social_renders (corte_id, criado_em DESC);
CREATE INDEX IF NOT EXISTS ix_social_renders_loja ON social_renders (estabelecimento_id, criado_em DESC);

ALTER TABLE social_renders ENABLE ROW LEVEL SECURITY;
ALTER TABLE social_renders FORCE ROW LEVEL SECURITY;
DO $$
BEGIN
    IF NOT EXISTS (SELECT 1 FROM pg_policies WHERE tablename = 'social_renders' AND policyname = 'social_por_loja'
                    AND schemaname = current_schema()) THEN
        CREATE POLICY social_por_loja ON social_renders
            USING (COALESCE(current_setting('zippy.sistema', true), '') = 'sim'
                   OR estabelecimento_id::text = COALESCE(current_setting('zippy.loja', true), ''))
            WITH CHECK (COALESCE(current_setting('zippy.sistema', true), '') = 'sim'
                   OR estabelecimento_id::text = COALESCE(current_setting('zippy.loja', true), ''));
    END IF;
END $$;

INSERT INTO delivery_tracking_schema_versions (version)
VALUES ('20261208_04_social_renders')
ON CONFLICT (version) DO NOTHING;

COMMIT;
