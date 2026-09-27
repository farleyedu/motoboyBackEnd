-- Fase 1 da reconstrucao de Configuracoes: identidade visual, contato publico, redes sociais e
-- horario de funcionamento (compartilhado pelas telas Negocio e Delivery). Nao e aplicada
-- automaticamente (so Migrations/Delivery tem hosted service); aplicar manualmente. Aditivo,
-- idempotente.
BEGIN;

ALTER TABLE estabelecimentos
    ADD COLUMN IF NOT EXISTS site_url TEXT NULL,
    ADD COLUMN IF NOT EXISTS favicon_url TEXT NULL,
    ADD COLUMN IF NOT EXISTS cor_primaria TEXT NULL,
    ADD COLUMN IF NOT EXISTS cor_secundaria TEXT NULL,
    ADD COLUMN IF NOT EXISTS tipografia TEXT NULL,
    ADD COLUMN IF NOT EXISTS instagram_url TEXT NULL,
    ADD COLUMN IF NOT EXISTS facebook_url TEXT NULL;

-- Um estabelecimento tem no maximo uma linha por dia da semana (0=segunda ... 6=domingo, mesmo
-- indice que a tela de Disponibilidade ja usa). fechado=true ignora abre_as/fecha_as.
CREATE TABLE IF NOT EXISTS estabelecimento_horario (
    id BIGSERIAL PRIMARY KEY,
    estabelecimento_id UUID NOT NULL REFERENCES estabelecimentos (id) ON DELETE CASCADE,
    dia_semana SMALLINT NOT NULL CHECK (dia_semana BETWEEN 0 AND 6),
    fechado BOOLEAN NOT NULL DEFAULT FALSE,
    abre_as TIME NULL,
    fecha_as TIME NULL,
    UNIQUE (estabelecimento_id, dia_semana)
);

INSERT INTO delivery_tracking_schema_versions (version)
VALUES ('20260928_01_identidade_horarios')
ON CONFLICT (version) DO NOTHING;

COMMIT;
