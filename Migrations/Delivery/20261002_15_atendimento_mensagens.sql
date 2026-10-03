BEGIN;

-- ---------------------------------------------------------------------------
-- Textos do atendimento automatico configuraveis pelo dono da loja (plano etapa 5). A saudacao e a mensagem de fora do
-- horario continuam nas colunas que ja existiam; os demais textos ficam num JSON por chave:
--   menu, cardapio, atendente, agendamento, semServico
-- Variaveis aceitas nos textos: {loja} e {link}. Vazio = vale o texto padrao do sistema. Aditivo e idempotente.
-- ---------------------------------------------------------------------------

ALTER TABLE estabelecimento_atendimento_config
    ADD COLUMN IF NOT EXISTS mensagens JSONB NOT NULL DEFAULT '{}'::jsonb;

INSERT INTO delivery_tracking_schema_versions (version)
VALUES ('20261002_15_atendimento_mensagens')
ON CONFLICT (version) DO NOTHING;

COMMIT;
