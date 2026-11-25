-- Nucleo de pagamentos Mercado Pago (substitui o Asaas, que nunca foi usado em producao). Contrato completo em docs/pagamentos/CONTRATOS.md.
-- Tres contas que nunca se misturam:
--   * mp_aplicacao               -> a APLICACAO MP da Zippy (client id/secret, public key, segredo do webhook). So OAuth das lojas e webhook.
--   * mp_conta_zippy             -> a conta RECEBEDORA da Zippy (access token proprio): mensalidades e extras, receita da plataforma.
--   * mp_conta_loja              -> a conta recebedora de CADA restaurante, por OAuth: vendas do cardapio e cobrancas do chat.
-- Todo segredo e gravado cifrado pela aplicacao (AES-GCM, chave mestra PAGAMENTOS_CHAVE_MESTRA fora do banco) e nunca sai em GET.
-- sandbox e producao sao linhas separadas: um token de um ambiente nunca e usado no outro.
-- Assinaturas, ciclos, compras de extras, solicitacoes e politicas comerciais ficam para a sessao 4 (decisao em CONTRATOS.md):
-- aqui so o que elas consomem (pagamento_intent com origem assinatura/compra_extra e origem_ref_id).
-- Aditiva e idempotente; nao toca nas tabelas checkout_* do Asaas (historicas). Rollback em rollback/20261031_01_mercadopago_nucleo.down.sql.
BEGIN;

DO $$
BEGIN
    IF to_regclass('estabelecimentos') IS NULL OR to_regclass('conversas') IS NULL OR to_regclass('clientes') IS NULL
       OR to_regclass('cardapio_pedido_publico') IS NULL THEN
        RAISE EXCEPTION 'mercadopago_nucleo requer estabelecimentos, conversas, clientes e cardapio_pedido_publico';
    END IF;
END $$;

-- 1) Aplicacao MP da Zippy, uma linha por ambiente --------------------------------------------------------------------------
CREATE TABLE IF NOT EXISTS mp_aplicacao (
    id                     UUID PRIMARY KEY,
    ambiente               TEXT NOT NULL CHECK (ambiente IN ('sandbox', 'producao')),
    client_id              TEXT NOT NULL CHECK (client_id ~ '^[0-9]+$'),
    client_secret_cifrado  TEXT NOT NULL,
    public_key             TEXT NULL,
    webhook_secret_cifrado TEXT NULL,
    -- Destino do OAuth (precisa ser identico ao cadastrado no painel do MP).
    redirect_uri           TEXT NULL,
    ativo                  BOOLEAN NOT NULL DEFAULT TRUE,
    atualizado_por         INTEGER NULL,
    created_at             TIMESTAMPTZ NOT NULL DEFAULT NOW(),
    updated_at             TIMESTAMPTZ NOT NULL DEFAULT NOW(),
    CONSTRAINT ux_mp_aplicacao_ambiente UNIQUE (ambiente)
);

-- 2) Conta recebedora da Zippy, uma por ambiente -----------------------------------------------------------------------------
CREATE TABLE IF NOT EXISTS mp_conta_zippy (
    id                    UUID PRIMARY KEY,
    ambiente              TEXT NOT NULL CHECK (ambiente IN ('sandbox', 'producao')),
    -- user_id da conta no MP: e o collector_id esperado em todo pagamento recebido por ela.
    mp_user_id            BIGINT NOT NULL,
    mp_user_id_mascarado  TEXT NOT NULL,
    access_token_cifrado  TEXT NOT NULL,
    public_key            TEXT NULL,
    estado                TEXT NOT NULL DEFAULT 'ativa' CHECK (estado IN ('ativa', 'inativa', 'erro')),
    ultimo_erro           TEXT NULL,
    validada_em           TIMESTAMPTZ NULL,
    atualizado_por        INTEGER NULL,
    created_at            TIMESTAMPTZ NOT NULL DEFAULT NOW(),
    updated_at            TIMESTAMPTZ NOT NULL DEFAULT NOW(),
    CONSTRAINT ux_mp_conta_zippy_ambiente UNIQUE (ambiente)
);

-- 3) Contas OAuth das lojas --------------------------------------------------------------------------------------------------
CREATE TABLE IF NOT EXISTS mp_conta_loja (
    id                     UUID PRIMARY KEY,
    id_estabelecimento     UUID NOT NULL REFERENCES estabelecimentos (id) ON DELETE CASCADE,
    ambiente               TEXT NOT NULL CHECK (ambiente IN ('sandbox', 'producao')),
    mp_user_id             BIGINT NOT NULL,
    mp_user_id_mascarado   TEXT NOT NULL,
    access_token_cifrado   TEXT NULL,
    refresh_token_cifrado  TEXT NULL,
    public_key             TEXT NULL,
    escopos                TEXT[] NOT NULL DEFAULT '{}',
    live_mode              BOOLEAN NOT NULL DEFAULT FALSE,
    token_expira_em        TIMESTAMPTZ NULL,
    estado                 TEXT NOT NULL DEFAULT 'ativa'
        CHECK (estado IN ('ativa', 'expirada', 'revogada', 'desconectada', 'erro')),
    ultimo_erro            TEXT NULL,
    ultimo_refresh_em      TIMESTAMPTZ NULL,
    conectado_por          INTEGER NULL,
    conectado_em           TIMESTAMPTZ NOT NULL DEFAULT NOW(),
    desconectado_em        TIMESTAMPTZ NULL,
    desconectado_por       INTEGER NULL,
    created_at             TIMESTAMPTZ NOT NULL DEFAULT NOW(),
    updated_at             TIMESTAMPTZ NOT NULL DEFAULT NOW(),
    -- Conta usavel tem token; desconectada/revogada perde os tokens (a linha fica para o historico dos pagamentos).
    CONSTRAINT ck_mp_conta_loja_tokens CHECK (
        estado IN ('desconectada', 'revogada') OR (access_token_cifrado IS NOT NULL AND refresh_token_cifrado IS NOT NULL))
);

-- No maximo uma conta "viva" por loja e ambiente.
CREATE UNIQUE INDEX IF NOT EXISTS ux_mp_conta_loja_viva
    ON mp_conta_loja (id_estabelecimento, ambiente) WHERE estado IN ('ativa', 'expirada', 'erro');
CREATE INDEX IF NOT EXISTS ix_mp_conta_loja_user ON mp_conta_loja (ambiente, mp_user_id);
CREATE INDEX IF NOT EXISTS ix_mp_conta_loja_expira ON mp_conta_loja (token_expira_em) WHERE estado = 'ativa';

-- Cada "Conectar Mercado Pago" iniciado no painel (sessao 2): state contra CSRF e PKCE.
CREATE TABLE IF NOT EXISTS mp_oauth_tentativa (
    id                     UUID PRIMARY KEY,
    id_estabelecimento     UUID NOT NULL REFERENCES estabelecimentos (id) ON DELETE CASCADE,
    ambiente               TEXT NOT NULL CHECK (ambiente IN ('sandbox', 'producao')),
    usuario_id             INTEGER NOT NULL,
    -- SHA-256 do state entregue ao navegador (o state em si nunca e gravado).
    state_hash             TEXT NOT NULL,
    code_verifier_cifrado  TEXT NULL,
    estado                 TEXT NOT NULL DEFAULT 'iniciada'
        CHECK (estado IN ('iniciada', 'concluida', 'cancelada', 'expirada', 'erro')),
    erro_codigo            TEXT NULL,
    id_conta_loja          UUID NULL REFERENCES mp_conta_loja (id) ON DELETE SET NULL,
    expira_em              TIMESTAMPTZ NOT NULL,
    concluida_em           TIMESTAMPTZ NULL,
    created_at             TIMESTAMPTZ NOT NULL DEFAULT NOW(),
    CONSTRAINT ux_mp_oauth_tentativa_state UNIQUE (state_hash)
);
CREATE INDEX IF NOT EXISTS ix_mp_oauth_tentativa_loja ON mp_oauth_tentativa (id_estabelecimento, created_at DESC);

-- 4) Meios aceitos pela loja -------------------------------------------------------------------------------------------------
CREATE TABLE IF NOT EXISTS pagamento_preferencia_loja (
    id_estabelecimento     UUID PRIMARY KEY REFERENCES estabelecimentos (id) ON DELETE CASCADE,
    pix_ativo              BOOLEAN NOT NULL DEFAULT FALSE,
    cartao_ativo           BOOLEAN NOT NULL DEFAULT FALSE,
    -- Dinheiro/maquininha na entrega ou no balcao (nao passa pelo MP).
    presencial_ativo       BOOLEAN NOT NULL DEFAULT TRUE,
    pix_validade_minutos   INTEGER NOT NULL DEFAULT 30 CHECK (pix_validade_minutos BETWEEN 5 AND 1440),
    cartao_parcelas_max    INTEGER NOT NULL DEFAULT 1 CHECK (cartao_parcelas_max BETWEEN 1 AND 12),
    atualizado_por         INTEGER NULL,
    updated_at             TIMESTAMPTZ NOT NULL DEFAULT NOW()
);

-- 5) Intencao de pagamento: o "quanto, por que e para quem" -------------------------------------------------------------------
CREATE TABLE IF NOT EXISTS pagamento_intent (
    id                     UUID PRIMARY KEY,
    id_estabelecimento     UUID NOT NULL REFERENCES estabelecimentos (id) ON DELETE RESTRICT,
    ambiente               TEXT NOT NULL CHECK (ambiente IN ('sandbox', 'producao')),
    origem                 TEXT NOT NULL CHECK (origem IN ('cardapio', 'chat', 'assinatura', 'compra_extra')),
    -- Quem recebe. A regra origem -> conta tambem vale no banco (ck_pagamento_intent_conta).
    conta_tipo             TEXT NOT NULL CHECK (conta_tipo IN ('loja', 'zippy')),
    id_conta_loja          UUID NULL REFERENCES mp_conta_loja (id) ON DELETE RESTRICT,
    id_conta_zippy         UUID NULL REFERENCES mp_conta_zippy (id) ON DELETE RESTRICT,
    pedido_id              INTEGER NULL,
    pre_pedido_id          UUID NULL REFERENCES cardapio_pedido_publico (id) ON DELETE SET NULL,
    conversa_id            UUID NULL REFERENCES conversas (id) ON DELETE SET NULL,
    cliente_id             UUID NULL REFERENCES clientes (id) ON DELETE SET NULL,
    -- assinatura -> id do ciclo; compra_extra -> id da compra (tabelas da sessao 4).
    origem_ref_id          UUID NULL,
    valor                  NUMERIC(12, 2) NOT NULL CHECK (valor > 0),
    moeda                  TEXT NOT NULL DEFAULT 'BRL' CHECK (moeda = 'BRL'),
    descricao              TEXT NOT NULL,
    status                 TEXT NOT NULL DEFAULT 'criado'
        CHECK (status IN ('criado', 'aguardando_pagamento', 'processando', 'pago', 'recusado', 'expirado', 'cancelado',
                          'estornado_parcial', 'estornado', 'contestado')),
    versao                 INTEGER NOT NULL DEFAULT 1,
    -- Enviado ao MP como external_reference; o webhook so aceita pagamento que volta com ele.
    external_reference     TEXT NOT NULL,
    -- Chave do chamador (ex.: cardapio:<pre_pedido>:pix). Repetir a chamada devolve a mesma intencao.
    idempotency_key        TEXT NOT NULL,
    valor_pago             NUMERIC(12, 2) NOT NULL DEFAULT 0 CHECK (valor_pago >= 0),
    valor_estornado        NUMERIC(12, 2) NOT NULL DEFAULT 0 CHECK (valor_estornado >= 0 AND valor_estornado <= valor_pago),
    -- SHA-256 do token opaco do link publico da pagina Pix (o token so existe no link).
    token_publico_hash     TEXT NULL,
    expira_em              TIMESTAMPTZ NULL,
    pago_em                TIMESTAMPTZ NULL,
    criado_por             INTEGER NULL,
    created_at             TIMESTAMPTZ NOT NULL DEFAULT NOW(),
    updated_at             TIMESTAMPTZ NOT NULL DEFAULT NOW(),
    CONSTRAINT ux_pagamento_intent_external_reference UNIQUE (external_reference),
    CONSTRAINT ux_pagamento_intent_idempotency UNIQUE (id_estabelecimento, idempotency_key),
    CONSTRAINT ck_pagamento_intent_conta CHECK (
        (origem IN ('cardapio', 'chat') AND conta_tipo = 'loja' AND id_conta_loja IS NOT NULL AND id_conta_zippy IS NULL)
        OR (origem IN ('assinatura', 'compra_extra') AND conta_tipo = 'zippy' AND id_conta_zippy IS NOT NULL AND id_conta_loja IS NULL))
);

CREATE UNIQUE INDEX IF NOT EXISTS ux_pagamento_intent_token ON pagamento_intent (token_publico_hash) WHERE token_publico_hash IS NOT NULL;
CREATE INDEX IF NOT EXISTS ix_pagamento_intent_loja ON pagamento_intent (id_estabelecimento, created_at DESC);
CREATE INDEX IF NOT EXISTS ix_pagamento_intent_pre_pedido ON pagamento_intent (pre_pedido_id) WHERE pre_pedido_id IS NOT NULL;
CREATE INDEX IF NOT EXISTS ix_pagamento_intent_pedido ON pagamento_intent (id_estabelecimento, pedido_id) WHERE pedido_id IS NOT NULL;
CREATE INDEX IF NOT EXISTS ix_pagamento_intent_conversa ON pagamento_intent (conversa_id) WHERE conversa_id IS NOT NULL;
CREATE INDEX IF NOT EXISTS ix_pagamento_intent_aberta ON pagamento_intent (expira_em)
    WHERE status IN ('criado', 'aguardando_pagamento', 'processando');

-- 6) Tentativas: cada chamada de criacao ao MP (Pix gerado de novo, cartao tentado de novo) --------------------------------------
CREATE TABLE IF NOT EXISTS pagamento_tentativa (
    id                     UUID PRIMARY KEY,
    id_intent              UUID NOT NULL REFERENCES pagamento_intent (id) ON DELETE CASCADE,
    numero                 INTEGER NOT NULL CHECK (numero > 0),
    metodo                 TEXT NOT NULL CHECK (metodo IN ('pix', 'cartao')),
    -- Mandada no header X-Idempotency-Key: repetir a mesma tentativa nunca cria dois pagamentos no MP.
    idempotency_key        TEXT NOT NULL,
    status                 TEXT NOT NULL DEFAULT 'criando' CHECK (status IN ('criando', 'criada', 'falhou')),
    mp_payment_id          BIGINT NULL,
    erro_codigo            TEXT NULL,
    erro_mensagem          TEXT NULL,
    created_at             TIMESTAMPTZ NOT NULL DEFAULT NOW(),
    concluida_em           TIMESTAMPTZ NULL,
    CONSTRAINT ux_pagamento_tentativa_numero UNIQUE (id_intent, numero),
    CONSTRAINT ux_pagamento_tentativa_idempotency UNIQUE (idempotency_key)
);
CREATE INDEX IF NOT EXISTS ix_pagamento_tentativa_criando ON pagamento_tentativa (created_at) WHERE status = 'criando';

-- 7) Pagamentos como o MP os ve ----------------------------------------------------------------------------------------------
CREATE TABLE IF NOT EXISTS pagamento (
    id                     UUID PRIMARY KEY,
    id_intent              UUID NOT NULL REFERENCES pagamento_intent (id) ON DELETE RESTRICT,
    id_tentativa           UUID NULL REFERENCES pagamento_tentativa (id) ON DELETE SET NULL,
    id_estabelecimento     UUID NOT NULL REFERENCES estabelecimentos (id) ON DELETE RESTRICT,
    ambiente               TEXT NOT NULL CHECK (ambiente IN ('sandbox', 'producao')),
    mp_payment_id          BIGINT NOT NULL,
    -- Dono do pagamento no MP: tem de ser a conta recebedora da intencao (titularidade).
    mp_collector_id        BIGINT NULL,
    metodo                 TEXT NOT NULL CHECK (metodo IN ('pix', 'cartao')),
    payment_method_id      TEXT NULL,
    payment_type_id        TEXT NULL,
    status                 TEXT NOT NULL
        CHECK (status IN ('pendente', 'em_processo', 'autorizado', 'aprovado', 'recusado', 'cancelado', 'expirado',
                          'estornado_parcial', 'estornado', 'em_mediacao', 'contestado')),
    mp_status              TEXT NOT NULL,
    mp_status_detail       TEXT NULL,
    valor                  NUMERIC(12, 2) NOT NULL CHECK (valor > 0),
    valor_estornado        NUMERIC(12, 2) NOT NULL DEFAULT 0 CHECK (valor_estornado >= 0 AND valor_estornado <= valor),
    moeda                  TEXT NOT NULL DEFAULT 'BRL',
    pix_copia_cola         TEXT NULL,
    pix_qr_code_base64     TEXT NULL,
    pix_ticket_url         TEXT NULL,
    cartao_bandeira        TEXT NULL,
    cartao_final           TEXT NULL CHECK (cartao_final IS NULL OR cartao_final ~ '^[0-9]{4}$'),
    parcelas               INTEGER NULL,
    live_mode              BOOLEAN NOT NULL DEFAULT FALSE,
    expira_em              TIMESTAMPTZ NULL,
    aprovado_em            TIMESTAMPTZ NULL,
    -- date_last_updated do MP: evento mais velho que este nunca sobrescreve o estado.
    mp_atualizado_em       TIMESTAMPTZ NULL,
    versao                 INTEGER NOT NULL DEFAULT 1,
    conciliado_em          TIMESTAMPTZ NULL,
    created_at             TIMESTAMPTZ NOT NULL DEFAULT NOW(),
    updated_at             TIMESTAMPTZ NOT NULL DEFAULT NOW(),
    CONSTRAINT ux_pagamento_mp UNIQUE (ambiente, mp_payment_id)
);
CREATE INDEX IF NOT EXISTS ix_pagamento_intent ON pagamento (id_intent);
CREATE INDEX IF NOT EXISTS ix_pagamento_aberto ON pagamento (conciliado_em NULLS FIRST)
    WHERE status IN ('pendente', 'em_processo', 'autorizado', 'em_mediacao');

-- 8) Cobrancas do chat (atendente pede Pix no WhatsApp; usa o mesmo servico Pix do cardapio) ----------------------------------
CREATE TABLE IF NOT EXISTS cobranca_chat (
    id                     UUID PRIMARY KEY,
    id_estabelecimento     UUID NOT NULL REFERENCES estabelecimentos (id) ON DELETE CASCADE,
    conversa_id            UUID NOT NULL REFERENCES conversas (id) ON DELETE CASCADE,
    id_intent              UUID NULL REFERENCES pagamento_intent (id) ON DELETE SET NULL,
    autor_usuario_id       INTEGER NOT NULL,
    descricao              TEXT NOT NULL CHECK (length(trim(descricao)) BETWEEN 1 AND 140),
    valor                  NUMERIC(12, 2) NOT NULL CHECK (valor > 0),
    tipo                   TEXT NOT NULL CHECK (tipo IN ('avulsa', 'vinculada')),
    pedido_id              INTEGER NULL,
    pre_pedido_id          UUID NULL REFERENCES cardapio_pedido_publico (id) ON DELETE SET NULL,
    status                 TEXT NOT NULL DEFAULT 'criando'
        CHECK (status IN ('criando', 'aguardando', 'enviado', 'pago', 'expirado', 'cancelado',
                          'reembolso_solicitado', 'reembolsado', 'recusado')),
    versao                 INTEGER NOT NULL DEFAULT 1,
    validade_minutos       INTEGER NOT NULL DEFAULT 30 CHECK (validade_minutos BETWEEN 5 AND 1440),
    expira_em              TIMESTAMPTZ NULL,
    enviado_em             TIMESTAMPTZ NULL,
    id_mensagem            UUID NULL,
    pago_em                TIMESTAMPTZ NULL,
    cancelado_em           TIMESTAMPTZ NULL,
    cancelado_por          INTEGER NULL,
    motivo                 TEXT NULL,
    -- Reemitir cria outra cobranca apontando para a anterior (que fica cancelada/expirada).
    reemitida_de           UUID NULL REFERENCES cobranca_chat (id) ON DELETE SET NULL,
    idempotency_key        TEXT NOT NULL,
    created_at             TIMESTAMPTZ NOT NULL DEFAULT NOW(),
    updated_at             TIMESTAMPTZ NOT NULL DEFAULT NOW(),
    CONSTRAINT ux_cobranca_chat_idempotency UNIQUE (id_estabelecimento, idempotency_key),
    CONSTRAINT ck_cobranca_chat_vinculo CHECK (
        (tipo = 'avulsa' AND pedido_id IS NULL AND pre_pedido_id IS NULL)
        OR (tipo = 'vinculada' AND (pedido_id IS NOT NULL OR pre_pedido_id IS NOT NULL)))
);
CREATE INDEX IF NOT EXISTS ix_cobranca_chat_conversa ON cobranca_chat (conversa_id, created_at DESC);
CREATE INDEX IF NOT EXISTS ix_cobranca_chat_intent ON cobranca_chat (id_intent) WHERE id_intent IS NOT NULL;
-- Uma cobranca aberta por conversa e pedido vinculado: evita dois Pix do mesmo pedido no chat.
CREATE UNIQUE INDEX IF NOT EXISTS ux_cobranca_chat_aberta_pedido ON cobranca_chat (conversa_id, pedido_id)
    WHERE pedido_id IS NOT NULL AND status IN ('criando', 'aguardando', 'enviado');

CREATE TABLE IF NOT EXISTS cobranca_chat_evento (
    id                     BIGSERIAL PRIMARY KEY,
    id_cobranca            UUID NOT NULL REFERENCES cobranca_chat (id) ON DELETE CASCADE,
    status_anterior        TEXT NULL,
    status_novo            TEXT NOT NULL,
    ator_tipo              TEXT NOT NULL CHECK (ator_tipo IN ('usuario', 'sistema', 'webhook', 'conciliador')),
    usuario_id             INTEGER NULL,
    detalhe                JSONB NULL,
    created_at             TIMESTAMPTZ NOT NULL DEFAULT NOW()
);
CREATE INDEX IF NOT EXISTS ix_cobranca_chat_evento ON cobranca_chat_evento (id_cobranca, id);

-- 9) Estornos (parciais; a soma nunca passa do valor pago) ------------------------------------------------------------------
CREATE TABLE IF NOT EXISTS pagamento_estorno (
    id                     UUID PRIMARY KEY,
    id_pagamento           UUID NOT NULL REFERENCES pagamento (id) ON DELETE RESTRICT,
    id_estabelecimento     UUID NOT NULL REFERENCES estabelecimentos (id) ON DELETE RESTRICT,
    valor                  NUMERIC(12, 2) NOT NULL CHECK (valor > 0),
    status                 TEXT NOT NULL DEFAULT 'solicitado'
        CHECK (status IN ('solicitado', 'processando', 'concluido', 'recusado', 'falhou')),
    mp_refund_id           BIGINT NULL,
    motivo                 TEXT NULL,
    -- 'usuario' (painel/chat) ou 'mercadopago' (estorno feito fora da Zippy, visto no webhook/conciliador).
    origem                 TEXT NOT NULL DEFAULT 'usuario' CHECK (origem IN ('usuario', 'mercadopago')),
    solicitado_por         INTEGER NULL,
    idempotency_key        TEXT NOT NULL,
    erro_mensagem          TEXT NULL,
    created_at             TIMESTAMPTZ NOT NULL DEFAULT NOW(),
    concluido_em           TIMESTAMPTZ NULL,
    CONSTRAINT ux_pagamento_estorno_idempotency UNIQUE (idempotency_key)
);
CREATE UNIQUE INDEX IF NOT EXISTS ux_pagamento_estorno_mp ON pagamento_estorno (id_pagamento, mp_refund_id) WHERE mp_refund_id IS NOT NULL;
CREATE INDEX IF NOT EXISTS ix_pagamento_estorno_pagamento ON pagamento_estorno (id_pagamento);

-- Trava a soma: estornos vivos (nao recusados/falhos) <= valor do pagamento. Bloqueia a linha do pagamento para serializar
-- dois pedidos de estorno simultaneos.
CREATE OR REPLACE FUNCTION fn_pagamento_estorno_limite() RETURNS trigger AS $$
DECLARE
    total_pago NUMERIC(12, 2);
    ja_estornado NUMERIC(12, 2);
BEGIN
    IF NEW.status IN ('recusado', 'falhou') THEN
        RETURN NEW;
    END IF;
    SELECT valor INTO total_pago FROM pagamento WHERE id = NEW.id_pagamento FOR UPDATE;
    SELECT COALESCE(SUM(valor), 0) INTO ja_estornado
      FROM pagamento_estorno
     WHERE id_pagamento = NEW.id_pagamento AND id <> NEW.id AND status NOT IN ('recusado', 'falhou');
    IF ja_estornado + NEW.valor > total_pago THEN
        RAISE EXCEPTION 'estorno acima do valor pago (pago %, ja estornado %, pedido %)', total_pago, ja_estornado, NEW.valor
            USING ERRCODE = 'check_violation', CONSTRAINT = 'ck_pagamento_estorno_limite';
    END IF;
    RETURN NEW;
END;
$$ LANGUAGE plpgsql;

DROP TRIGGER IF EXISTS tg_pagamento_estorno_limite ON pagamento_estorno;
CREATE TRIGGER tg_pagamento_estorno_limite BEFORE INSERT OR UPDATE OF valor, status ON pagamento_estorno
    FOR EACH ROW EXECUTE FUNCTION fn_pagamento_estorno_limite();

-- 10) Chargebacks, mediacoes e reclamacoes ---------------------------------------------------------------------------------
CREATE TABLE IF NOT EXISTS pagamento_disputa (
    id                     UUID PRIMARY KEY,
    id_pagamento           UUID NOT NULL REFERENCES pagamento (id) ON DELETE RESTRICT,
    id_estabelecimento     UUID NOT NULL REFERENCES estabelecimentos (id) ON DELETE RESTRICT,
    tipo                   TEXT NOT NULL CHECK (tipo IN ('chargeback', 'mediacao', 'reclamacao')),
    -- id do chargeback/claim no MP; para mediacao vista so pelo status do pagamento, 'pagamento:<mp_payment_id>'.
    mp_referencia          TEXT NOT NULL,
    status                 TEXT NOT NULL DEFAULT 'aberta'
        CHECK (status IN ('aberta', 'em_analise', 'ganha', 'perdida', 'cancelada')),
    valor                  NUMERIC(12, 2) NULL,
    prazo_resposta_em      TIMESTAMPTZ NULL,
    detalhe                JSONB NULL,
    created_at             TIMESTAMPTZ NOT NULL DEFAULT NOW(),
    updated_at             TIMESTAMPTZ NOT NULL DEFAULT NOW(),
    CONSTRAINT ux_pagamento_disputa_mp UNIQUE (tipo, mp_referencia)
);
CREATE INDEX IF NOT EXISTS ix_pagamento_disputa_loja ON pagamento_disputa (id_estabelecimento, created_at DESC);

-- 11) Eventos de webhook (persistidos antes do 2xx; reprocessaveis) ---------------------------------------------------------
CREATE TABLE IF NOT EXISTS mp_webhook_evento (
    id                     UUID PRIMARY KEY,
    ambiente               TEXT NOT NULL CHECK (ambiente IN ('sandbox', 'producao')),
    -- payment, merchant_order, chargebacks, claim, subscription_preapproval, subscription_authorized_payment, mp-connect...
    topico                 TEXT NOT NULL,
    acao                   TEXT NULL,
    data_id                TEXT NOT NULL,
    mp_notification_id     TEXT NULL,
    mp_request_id          TEXT NULL,
    mp_user_id             BIGINT NULL,
    live_mode              BOOLEAN NULL,
    assinatura_ts          BIGINT NULL,
    -- topico|data_id|acao|notification_id: o mesmo aviso reenviado pelo MP cai aqui e nao repete o efeito.
    chave_dedup            TEXT NOT NULL,
    payload                JSONB NOT NULL,
    status                 TEXT NOT NULL DEFAULT 'recebido'
        CHECK (status IN ('recebido', 'processando', 'processado', 'ignorado', 'rejeitado', 'falhou')),
    tentativas             INTEGER NOT NULL DEFAULT 0,
    proxima_tentativa_em   TIMESTAMPTZ NULL,
    ultimo_erro            TEXT NULL,
    recebido_em            TIMESTAMPTZ NOT NULL DEFAULT NOW(),
    processado_em          TIMESTAMPTZ NULL,
    CONSTRAINT ux_mp_webhook_evento_dedup UNIQUE (ambiente, chave_dedup)
);
CREATE INDEX IF NOT EXISTS ix_mp_webhook_evento_reprocessar ON mp_webhook_evento (proxima_tentativa_em)
    WHERE status IN ('recebido', 'falhou');
CREATE INDEX IF NOT EXISTS ix_mp_webhook_evento_data ON mp_webhook_evento (topico, data_id);

-- 12) Auditoria financeira (somente insercao) ---------------------------------------------------------------------------------
CREATE TABLE IF NOT EXISTS pagamento_auditoria (
    id                     BIGSERIAL PRIMARY KEY,
    id_estabelecimento     UUID NULL,
    entidade               TEXT NOT NULL,
    entidade_id            TEXT NOT NULL,
    acao                   TEXT NOT NULL,
    ator_tipo              TEXT NOT NULL CHECK (ator_tipo IN ('usuario', 'sistema', 'webhook', 'conciliador')),
    ator_id                INTEGER NULL,
    -- Nunca tokens nem dados de cartao: so status, valores e ids.
    dados                  JSONB NULL,
    created_at             TIMESTAMPTZ NOT NULL DEFAULT NOW()
);
CREATE INDEX IF NOT EXISTS ix_pagamento_auditoria_entidade ON pagamento_auditoria (entidade, entidade_id, id);
CREATE INDEX IF NOT EXISTS ix_pagamento_auditoria_loja ON pagamento_auditoria (id_estabelecimento, created_at DESC);

CREATE OR REPLACE FUNCTION fn_pagamento_auditoria_imutavel() RETURNS trigger AS $$
BEGIN
    RAISE EXCEPTION 'pagamento_auditoria e somente insercao' USING ERRCODE = 'insufficient_privilege';
END;
$$ LANGUAGE plpgsql;

DROP TRIGGER IF EXISTS tg_pagamento_auditoria_imutavel ON pagamento_auditoria;
CREATE TRIGGER tg_pagamento_auditoria_imutavel BEFORE UPDATE OR DELETE ON pagamento_auditoria
    FOR EACH ROW EXECUTE FUNCTION fn_pagamento_auditoria_imutavel();

-- 13) Outbox dos eventos em tempo real (gravada na mesma transacao da mudanca; publicada depois do commit) ---------------------
CREATE TABLE IF NOT EXISTS pagamento_outbox (
    id                     BIGSERIAL PRIMARY KEY,
    event_id               UUID NOT NULL,
    id_estabelecimento     UUID NOT NULL,
    evento                 TEXT NOT NULL CHECK (evento IN ('pagamentoAtualizado', 'cobrancaAtualizada')),
    grupo                  TEXT NOT NULL,
    payload                JSONB NOT NULL,
    tentativas             INTEGER NOT NULL DEFAULT 0,
    ultimo_erro            TEXT NULL,
    created_at             TIMESTAMPTZ NOT NULL DEFAULT NOW(),
    publicado_em           TIMESTAMPTZ NULL,
    CONSTRAINT ux_pagamento_outbox_event UNIQUE (event_id)
);
CREATE INDEX IF NOT EXISTS ix_pagamento_outbox_pendente ON pagamento_outbox (id) WHERE publicado_em IS NULL;

INSERT INTO delivery_tracking_schema_versions (version)
VALUES ('20261031_01_mercadopago_nucleo')
ON CONFLICT (version) DO NOTHING;

COMMIT;
