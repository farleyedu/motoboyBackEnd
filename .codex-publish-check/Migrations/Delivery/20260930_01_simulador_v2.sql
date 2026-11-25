BEGIN;

-- ---------------------------------------------------------------------------
-- Simulador v2: eventos e sessoes persistidos, cadastro de cliente estendido e etapas de preparo do pedido.
-- Tudo aditivo e idempotente; nao muda nenhum comportamento existente.
-- ---------------------------------------------------------------------------

-- 1) Etapas de preparo do pedido (Confirmado / Em preparo). Ficam FORA do status do pedido de proposito: o status
--    continua sendo pendente/atribuido/em_rota/concluido/cancelado, que e o que fila, rota e motoboy entendem.
ALTER TABLE pedido
    ADD COLUMN IF NOT EXISTS confirmado_em_utc TIMESTAMPTZ NULL,
    ADD COLUMN IF NOT EXISTS preparo_em_utc TIMESTAMPTZ NULL,
    -- Canal por onde o pedido chegou (whatsapp, app, balcao): diferente da origem (quem o criou no sistema).
    ADD COLUMN IF NOT EXISTS canal TEXT NULL;

-- 2) Cadastro de cliente estendido (tabela CLIENTES do WhatsApp, ver 20260929_02).
ALTER TABLE clientes
    ADD COLUMN IF NOT EXISTS avatar TEXT NULL,
    ADD COLUMN IF NOT EXISTS cpf TEXT NULL,
    ADD COLUMN IF NOT EXISTS data_nascimento DATE NULL,
    ADD COLUMN IF NOT EXISTS referencia TEXT NULL,
    ADD COLUMN IF NOT EXISTS canal_preferido TEXT NULL,
    ADD COLUMN IF NOT EXISTS origem TEXT NULL,
    ADD COLUMN IF NOT EXISTS tags TEXT[] NOT NULL DEFAULT '{}',
    ADD COLUMN IF NOT EXISTS consentimento_whatsapp BOOLEAN NOT NULL DEFAULT TRUE;

-- 2b) Foto do motoboy de teste guarda uma imagem pequena (data URL): a coluna precisa ser TEXT. varchar -> text nao reescreve a tabela.
ALTER TABLE motoboy ALTER COLUMN avatar TYPE TEXT;

-- 3) Eventos do simulador (feed do hub, log de atividades, log de alteracoes e logs de cenario).
CREATE TABLE IF NOT EXISTS simulador_evento (
    id BIGSERIAL PRIMARY KEY,
    estabelecimento_id UUID NOT NULL REFERENCES estabelecimentos (id) ON DELETE CASCADE,
    entidade TEXT NOT NULL CHECK (entidade IN ('motoboy', 'cliente', 'pedido', 'conversa', 'sistema', 'cenario')),
    entidade_ref TEXT NULL,
    tipo TEXT NOT NULL,
    titulo TEXT NOT NULL,
    detalhe TEXT NULL,
    status TEXT NOT NULL DEFAULT 'sucesso' CHECK (status IN ('sucesso', 'atencao', 'erro')),
    cenario_id TEXT NULL,
    dados JSONB NULL,
    usuario_id INTEGER NULL,
    usuario_nome TEXT NULL,
    criado_em_utc TIMESTAMPTZ NOT NULL DEFAULT NOW()
);

CREATE INDEX IF NOT EXISTS ix_simulador_evento_estab_tempo
    ON simulador_evento (estabelecimento_id, criado_em_utc DESC);
CREATE INDEX IF NOT EXISTS ix_simulador_evento_ref
    ON simulador_evento (estabelecimento_id, entidade, entidade_ref, criado_em_utc DESC);

-- 4) Sessoes de simulacao (persistem entre abas e recargas; "Persistir sessao" liga/desliga o salvamento).
CREATE TABLE IF NOT EXISTS simulador_sessao (
    id UUID PRIMARY KEY,
    estabelecimento_id UUID NOT NULL REFERENCES estabelecimentos (id) ON DELETE CASCADE,
    usuario_id INTEGER NULL,
    tipo TEXT NOT NULL CHECK (tipo IN ('motoboy', 'cliente', 'pedido', 'conversa', 'cenario')),
    ref TEXT NULL,
    titulo TEXT NULL,
    estado JSONB NOT NULL DEFAULT '{}'::jsonb,
    ativa BOOLEAN NOT NULL DEFAULT TRUE,
    criada_em_utc TIMESTAMPTZ NOT NULL DEFAULT NOW(),
    ultima_atividade_utc TIMESTAMPTZ NOT NULL DEFAULT NOW()
);

CREATE INDEX IF NOT EXISTS ix_simulador_sessao_estab
    ON simulador_sessao (estabelecimento_id, tipo, ativa, ultima_atividade_utc DESC);

INSERT INTO delivery_tracking_schema_versions (version)
VALUES ('20260930_01_simulador_v2')
ON CONFLICT (version) DO NOTHING;

COMMIT;
