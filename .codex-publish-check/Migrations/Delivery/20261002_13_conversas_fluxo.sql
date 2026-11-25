BEGIN;

-- ---------------------------------------------------------------------------
-- Estado do atendimento automatico por conversa (motor de atendimento, plano etapa 4).
--   fluxo_estado  em que servico/passo a conversa esta (ex.: o cliente escolheu "Fazer pedido") e os dados do passo.
--   fluxo_chave   nome do fluxo ativo, para consulta rapida e para o log.
--   fluxo_versao  versao do formato do estado: uma conversa antiga continua entendida depois de o fluxo mudar.
-- Coluna propria (e nao contexto_estado) para nao se misturar com o estado dos fluxos antigos, que saem na limpeza.
-- Aditivo e idempotente.
-- ---------------------------------------------------------------------------

ALTER TABLE conversas ADD COLUMN IF NOT EXISTS fluxo_estado JSONB NULL;
ALTER TABLE conversas ADD COLUMN IF NOT EXISTS fluxo_chave TEXT NULL;
ALTER TABLE conversas ADD COLUMN IF NOT EXISTS fluxo_versao INTEGER NOT NULL DEFAULT 1;

INSERT INTO delivery_tracking_schema_versions (version)
VALUES ('20261002_13_conversas_fluxo')
ON CONFLICT (version) DO NOTHING;

COMMIT;
