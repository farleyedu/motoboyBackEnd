BEGIN;

-- ---------------------------------------------------------------------------
-- Confirmacao do pedido do cardapio web pelo WhatsApp. O pedido publico (cardapio_pedido_publico)
-- vira um pre-pedido: o restaurante so o ve depois que o numero do cliente e comprovado.
--   aguardando_codigo -> o cliente ainda precisa mandar o codigo de 4 digitos (validade curta)
--   aguardando_aceite -> confirmado pelo WhatsApp; aparece para o atendente aceitar ou recusar
--   aceito / recusado -> decisao do restaurante (aceito cria o pedido real, origem cardapio_web)
--   expirado          -> o codigo venceu sem confirmacao
-- 'pendente' continua valendo para as linhas antigas. Tudo aditivo e idempotente.
-- ---------------------------------------------------------------------------

ALTER TABLE cardapio_pedido_publico
    ADD COLUMN IF NOT EXISTS codigo_confirmacao TEXT        NULL,
    ADD COLUMN IF NOT EXISTS codigo_expira_em   TIMESTAMPTZ NULL,
    ADD COLUMN IF NOT EXISTS canal_confirmacao  TEXT        NULL,
    ADD COLUMN IF NOT EXISTS telefone_contato   TEXT        NULL,
    ADD COLUMN IF NOT EXISTS id_conversa        UUID        NULL,
    ADD COLUMN IF NOT EXISTS confirmado_em      TIMESTAMPTZ NULL,
    ADD COLUMN IF NOT EXISTS aceito_em          TIMESTAMPTZ NULL,
    ADD COLUMN IF NOT EXISTS recusado_em        TIMESTAMPTZ NULL,
    ADD COLUMN IF NOT EXISTS motivo_recusa      TEXT        NULL,
    ADD COLUMN IF NOT EXISTS id_pedido          INTEGER     NULL;

ALTER TABLE cardapio_pedido_publico DROP CONSTRAINT IF EXISTS ck_cardapio_pedido_publico_status;
ALTER TABLE cardapio_pedido_publico
    ADD CONSTRAINT ck_cardapio_pedido_publico_status CHECK (status IN (
        'pendente', 'aguardando_codigo', 'aguardando_aceite', 'aceito', 'recusado', 'expirado'));

ALTER TABLE cardapio_pedido_publico DROP CONSTRAINT IF EXISTS ck_cardapio_pedido_publico_canal;
ALTER TABLE cardapio_pedido_publico
    ADD CONSTRAINT ck_cardapio_pedido_publico_canal CHECK (
        canal_confirmacao IS NULL OR canal_confirmacao IN ('janela_aberta', 'codigo'));

-- Um codigo de 4 digitos so pode estar ativo uma vez por estabelecimento: e o que liga a mensagem
-- recebida ao pre-pedido. Codigo vencido sai do indice assim que o status deixa de ser aguardando_codigo.
CREATE UNIQUE INDEX IF NOT EXISTS ux_cardapio_pedido_publico_codigo_ativo
    ON cardapio_pedido_publico (id_estabelecimento, codigo_confirmacao)
    WHERE status = 'aguardando_codigo';

-- Fila do atendente (so o que espera decisao).
CREATE INDEX IF NOT EXISTS ix_cardapio_pedido_publico_aguardando
    ON cardapio_pedido_publico (id_estabelecimento, confirmado_em)
    WHERE status = 'aguardando_aceite';

INSERT INTO delivery_tracking_schema_versions (version)
VALUES ('20261002_03_cardapio_confirmacao_whatsapp')
ON CONFLICT (version) DO NOTHING;

COMMIT;
