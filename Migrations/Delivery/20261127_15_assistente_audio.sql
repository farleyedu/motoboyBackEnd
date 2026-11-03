-- Assistente de visitas por audio (docs/crm-inteligente/02-backend-whatsapp-ia.md, Parte B, cenarios K-M).
--   * crm_assistente_consentimento: sem consentimento ativo do vendedor nenhum audio e baixado nem enviado a fornecedor externo.
--   * crm_assistente_entrada: um audio recebido (mensagem Meta unica = idempotencia). Guarda a transcricao original e a proposta
--     (fatos declarados, inferencias, alteracoes propostas) separadas do negocio: nada aqui altera crm_oportunidade. O arquivo de
--     audio NUNCA e gravado, so o hash (dedup) e o tamanho. Fila assincrona: estado + proxima_tentativa_em + trava (lease).
--   * crm_assistente_uso: razao de custo por etapa (a linha 'reserva' segura o pior caso enquanto a entrada processa); o teto mensal soma esta tabela. Valores vem da configuracao, nunca de padrao.
--   * crm_assistente_auditoria: confirmacao, edicao, rejeicao e reprocessamento, com o antes/depois das alteracoes.
-- Retencao: o worker apaga transcricao e proposta de entradas encerradas apos CrmAssistente:Audio:RetencaoDias (o historico do
-- CRM guarda so o texto aprovado). Aditiva e idempotente (roda a cada boot).
BEGIN;

CREATE TABLE IF NOT EXISTS crm_assistente_consentimento (
    usuario_id   INTEGER PRIMARY KEY REFERENCES usuario(id),
    versao_texto TEXT NOT NULL,
    consentido_em TIMESTAMPTZ NOT NULL DEFAULT NOW(),
    revogado_em  TIMESTAMPTZ NULL
);

CREATE TABLE IF NOT EXISTS crm_assistente_entrada (
    id                   UUID PRIMARY KEY,
    wa_mensagem_id       TEXT NOT NULL,
    usuario_id           INTEGER NOT NULL REFERENCES usuario(id),
    canal_id             UUID NOT NULL,
    loja_gestao_id       UUID NOT NULL,
    phone_number_id      TEXT NOT NULL,
    numero_canal_e164    TEXT NOT NULL,
    telefone_e164        TEXT NOT NULL,
    midia_id             TEXT NULL,
    midia_mime           TEXT NULL,
    midia_hash           TEXT NULL,
    midia_bytes          INTEGER NULL CHECK (midia_bytes IS NULL OR midia_bytes >= 0),
    duracao_segundos     NUMERIC(10,2) NULL,
    estado               TEXT NOT NULL DEFAULT 'recebido'
                         CHECK (estado IN ('recebido', 'processando', 'aguardando_revisao', 'aplicando', 'confirmada', 'rejeitada',
                                           'falha', 'duplicada', 'bloqueada', 'expirada')),
    etapa                TEXT NULL,
    erro_codigo          TEXT NULL,
    erro_detalhe         TEXT NULL,
    tentativas           INTEGER NOT NULL DEFAULT 0 CHECK (tentativas >= 0),
    proxima_tentativa_em TIMESTAMPTZ NULL,
    travado_ate          TIMESTAMPTZ NULL,
    transcricao          TEXT NULL,
    proposta             JSONB NULL,
    oportunidade_id      UUID NULL,
    duplicada_de         UUID NULL REFERENCES crm_assistente_entrada(id),
    custo_estimado       NUMERIC(14,6) NOT NULL DEFAULT 0 CHECK (custo_estimado >= 0),
    custo_real           NUMERIC(14,6) NOT NULL DEFAULT 0 CHECK (custo_real >= 0),
    chave_confirmacao    TEXT NULL,
    resultado            JSONB NULL,
    retorno_enviado_em   TIMESTAMPTZ NULL,
    recebida_em          TIMESTAMPTZ NOT NULL,
    criado_em            TIMESTAMPTZ NOT NULL DEFAULT NOW(),
    atualizado_em        TIMESTAMPTZ NOT NULL DEFAULT NOW(),
    encerrada_em         TIMESTAMPTZ NULL,
    versao               INTEGER NOT NULL DEFAULT 1
);

-- Mesma mensagem da Meta nunca gera duas entradas, mesmo com webhook reentregue ou reprocessado.
CREATE UNIQUE INDEX IF NOT EXISTS ux_crm_assistente_entrada_wa ON crm_assistente_entrada (wa_mensagem_id);
CREATE INDEX IF NOT EXISTS ix_crm_assistente_entrada_usuario ON crm_assistente_entrada (usuario_id, recebida_em DESC);
CREATE INDEX IF NOT EXISTS ix_crm_assistente_entrada_hash ON crm_assistente_entrada (usuario_id, midia_hash) WHERE midia_hash IS NOT NULL;
CREATE INDEX IF NOT EXISTS ix_crm_assistente_entrada_fila ON crm_assistente_entrada (proxima_tentativa_em)
    WHERE estado IN ('recebido', 'processando');
CREATE INDEX IF NOT EXISTS ix_crm_assistente_entrada_retencao ON crm_assistente_entrada (encerrada_em)
    WHERE transcricao IS NOT NULL OR proposta IS NOT NULL;

CREATE TABLE IF NOT EXISTS crm_assistente_uso (
    id          BIGSERIAL PRIMARY KEY,
    entrada_id  UUID NOT NULL REFERENCES crm_assistente_entrada(id),
    competencia DATE NOT NULL,
    etapa       TEXT NOT NULL CHECK (etapa IN ('reserva', 'transcricao', 'interpretacao')),
    fornecedor  TEXT NOT NULL,
    modelo      TEXT NOT NULL,
    unidades    NUMERIC(14,3) NOT NULL DEFAULT 0,
    custo       NUMERIC(14,6) NOT NULL CHECK (custo >= 0),
    criado_em   TIMESTAMPTZ NOT NULL DEFAULT NOW()
);

CREATE INDEX IF NOT EXISTS ix_crm_assistente_uso_competencia ON crm_assistente_uso (competencia);
CREATE INDEX IF NOT EXISTS ix_crm_assistente_uso_entrada ON crm_assistente_uso (entrada_id);

CREATE TABLE IF NOT EXISTS crm_assistente_auditoria (
    id             BIGSERIAL PRIMARY KEY,
    entrada_id     UUID NOT NULL REFERENCES crm_assistente_entrada(id),
    evento         TEXT NOT NULL,
    ator_usuario_id INTEGER NULL,
    detalhe        JSONB NULL,
    criado_em      TIMESTAMPTZ NOT NULL DEFAULT NOW()
);

CREATE INDEX IF NOT EXISTS ix_crm_assistente_auditoria_entrada ON crm_assistente_auditoria (entrada_id, criado_em);

COMMIT;
