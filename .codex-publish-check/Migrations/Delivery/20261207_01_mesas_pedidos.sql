BEGIN;

-- Mesas, P08 (CONTRATO-API §1.5): pedidos e itens da mesa, divisao do item entre comandas, historico imutavel do item
-- (base dos tempos e do "Voltar o ultimo"), recados para a cozinha, perdas e avisos ao gestor (cortesia, perda).
-- Requer 20261206_01_mesas_sessao.sql (mesa_sessao, mesa_comanda), 20261205_05_mesas_planta.sql (mesa),
-- 20261001_01_cardapio_schema.sql (cardapio_produto) e a Gestao (gestao_pessoas, gestao_bloqueia_*). Aditiva e idempotente.

CREATE TABLE IF NOT EXISTS mesa_pedido (
    id                 UUID PRIMARY KEY,
    estabelecimento_id UUID NOT NULL REFERENCES estabelecimentos (id),
    sessao_id          UUID NOT NULL REFERENCES mesa_sessao (id),
    mesa_id            UUID NOT NULL REFERENCES mesa (id),
    origem             TEXT NOT NULL,
    ator_id            TEXT NOT NULL,
    ator_nome          TEXT NULL CHECK (ator_nome IS NULL OR length(ator_nome) <= 80),
    -- Cliente que pediu pelo celular (P18) ou pessoa escolhida pelo garcom.
    pessoa_id          UUID NULL,
    estado             TEXT NOT NULL DEFAULT 'rascunho',
    segurar_principal  BOOLEAN NOT NULL DEFAULT FALSE,
    solto_em           TIMESTAMPTZ NULL,
    solto_por          TEXT NULL,
    visto_em           TIMESTAMPTZ NULL,
    visto_por          TEXT NULL,
    escalado_para      UUID NULL,
    escalado_em        TIMESTAMPTZ NULL,
    rodada_de          UUID NULL,
    criado_em          TIMESTAMPTZ NOT NULL DEFAULT NOW(),
    atualizado_em      TIMESTAMPTZ NOT NULL DEFAULT NOW()
);
ALTER TABLE mesa_pedido DROP CONSTRAINT IF EXISTS ck_mesa_pedido_origem;
ALTER TABLE mesa_pedido ADD CONSTRAINT ck_mesa_pedido_origem CHECK (origem IN ('garcom', 'cliente', 'gestao', 'balcao'));
ALTER TABLE mesa_pedido DROP CONSTRAINT IF EXISTS ck_mesa_pedido_estado;
ALTER TABLE mesa_pedido ADD CONSTRAINT ck_mesa_pedido_estado
    CHECK (estado IN ('rascunho', 'aguardando_aprovacao', 'enviado', 'recusado', 'cancelado'));
CREATE INDEX IF NOT EXISTS ix_mesa_pedido_loja ON mesa_pedido (estabelecimento_id, estado);
CREATE INDEX IF NOT EXISTS ix_mesa_pedido_sessao ON mesa_pedido (sessao_id);

CREATE TABLE IF NOT EXISTS mesa_item (
    id                 UUID PRIMARY KEY,
    estabelecimento_id UUID NOT NULL REFERENCES estabelecimentos (id),
    pedido_id          UUID NOT NULL REFERENCES mesa_pedido (id),
    sessao_id          UUID NOT NULL REFERENCES mesa_sessao (id),
    mesa_id            UUID NOT NULL REFERENCES mesa (id),
    produto_id         UUID NOT NULL,
    nome               TEXT NOT NULL CHECK (length(nome) BETWEEN 1 AND 120),
    foto_url           TEXT NULL,
    -- entrada | principal | outro (segurar o principal ate a entrada sair).
    tipo_prato         TEXT NOT NULL DEFAULT 'outro',
    qtd                NUMERIC(8,2) NOT NULL CHECK (qtd > 0),
    preco              NUMERIC(12,2) NOT NULL CHECK (preco >= 0),
    preco_cheio        NUMERIC(12,2) NULL CHECK (preco_cheio IS NULL OR preco_cheio >= 0),
    estacao_id         TEXT NOT NULL CHECK (length(estacao_id) BETWEEN 1 AND 40),
    etapas             JSONB NOT NULL DEFAULT '[]'::jsonb,
    etapa_atual        SMALLINT NOT NULL DEFAULT 0,
    estado             TEXT NOT NULL DEFAULT 'rascunho',
    origem             TEXT NOT NULL,
    obs                TEXT NULL CHECK (obs IS NULL OR length(obs) <= 200),
    alergia            TEXT NULL CHECK (alergia IS NULL OR length(alergia) <= 200),
    regra_aprovacao    TEXT NULL,
    quem_leva          TEXT NULL,
    quer_buscar        BOOLEAN NOT NULL DEFAULT FALSE,
    leva_garcom_id     UUID NULL REFERENCES gestao_pessoas (id),
    avisado_em         TIMESTAMPTZ NULL,
    pegou_em           TIMESTAMPTZ NULL,
    pegou_por          TEXT NULL,
    pegou_por_nome     TEXT NULL,
    preparo_por        UUID NULL REFERENCES gestao_pessoas (id),
    retirada_estacao   TEXT NULL,
    retirada_desde     TIMESTAMPTZ NULL,
    retirado_em        TIMESTAMPTZ NULL,
    retirado_por       TEXT NULL,
    incluso            BOOLEAN NOT NULL DEFAULT FALSE,
    extra              BOOLEAN NOT NULL DEFAULT FALSE,
    cortesia           JSONB NULL,
    refazer            SMALLINT NOT NULL DEFAULT 0,
    entrega_presumida  BOOLEAN NOT NULL DEFAULT FALSE,
    entrega_direta     BOOLEAN NOT NULL DEFAULT FALSE,
    aprovado_por       TEXT NULL,
    motivo             TEXT NULL CHECK (motivo IS NULL OR length(motivo) <= 280),
    movido_de          UUID NULL,
    -- Alerta de atraso ja dado (o worker avisa uma vez por passo).
    atraso_avisado_em  TIMESTAMPTZ NULL,
    criado_em          TIMESTAMPTZ NOT NULL DEFAULT NOW(),
    t_fila             TIMESTAMPTZ NULL,
    t_preparo          TIMESTAMPTZ NULL,
    t_pronto           TIMESTAMPTZ NULL,
    t_entregue         TIMESTAMPTZ NULL
);
ALTER TABLE mesa_item DROP CONSTRAINT IF EXISTS ck_mesa_item_estado;
ALTER TABLE mesa_item ADD CONSTRAINT ck_mesa_item_estado CHECK (estado IN
    ('rascunho', 'aguardando_aprovacao', 'em_fila', 'em_preparo', 'pronto', 'aguardando_retirada', 'entregue', 'cancelado'));
ALTER TABLE mesa_item DROP CONSTRAINT IF EXISTS ck_mesa_item_quem_leva;
ALTER TABLE mesa_item ADD CONSTRAINT ck_mesa_item_quem_leva CHECK (quem_leva IS NULL OR quem_leva IN ('garcom', 'cliente'));
ALTER TABLE mesa_item DROP CONSTRAINT IF EXISTS ck_mesa_item_tipo_prato;
ALTER TABLE mesa_item ADD CONSTRAINT ck_mesa_item_tipo_prato CHECK (tipo_prato IN ('entrada', 'principal', 'outro'));
CREATE INDEX IF NOT EXISTS ix_mesa_item_estacao ON mesa_item (estabelecimento_id, estacao_id, estado);
CREATE INDEX IF NOT EXISTS ix_mesa_item_sessao ON mesa_item (sessao_id);
CREATE INDEX IF NOT EXISTS ix_mesa_item_pedido ON mesa_item (pedido_id);

CREATE TABLE IF NOT EXISTS mesa_item_parte (
    id                 UUID PRIMARY KEY,
    estabelecimento_id UUID NOT NULL REFERENCES estabelecimentos (id),
    item_id            UUID NOT NULL REFERENCES mesa_item (id),
    comanda_id         UUID NOT NULL REFERENCES mesa_comanda (id),
    percentual         NUMERIC(5,2) NOT NULL CHECK (percentual > 0 AND percentual <= 100),
    valor              NUMERIC(12,2) NOT NULL CHECK (valor >= 0),
    UNIQUE (item_id, comanda_id)
);
CREATE INDEX IF NOT EXISTS ix_mesa_item_parte_comanda ON mesa_item_parte (comanda_id);

-- Historico do item: cada passo de estado (de -> para). Imutavel; "Voltar o ultimo" grava um passo novo com desfaz_id.
CREATE TABLE IF NOT EXISTS mesa_item_evento (
    id                 UUID PRIMARY KEY,
    estabelecimento_id UUID NOT NULL REFERENCES estabelecimentos (id),
    item_id            UUID NOT NULL REFERENCES mesa_item (id),
    comando            TEXT NOT NULL,
    de                 TEXT NULL,
    para               TEXT NOT NULL,
    etapa_de           SMALLINT NULL,
    etapa_para         SMALLINT NULL,
    por                TEXT NOT NULL,
    por_nome           TEXT NULL,
    quando             TIMESTAMPTZ NOT NULL DEFAULT NOW(),
    presumido          BOOLEAN NOT NULL DEFAULT FALSE,
    desfaz_id          UUID NULL
);
CREATE INDEX IF NOT EXISTS ix_mesa_item_evento_item ON mesa_item_evento (item_id, quando);

CREATE TABLE IF NOT EXISTS mesa_recado (
    id                 UUID PRIMARY KEY,
    estabelecimento_id UUID NOT NULL REFERENCES estabelecimentos (id),
    pedido_id          UUID NOT NULL REFERENCES mesa_pedido (id),
    tipo               TEXT NOT NULL,
    texto              TEXT NULL CHECK (texto IS NULL OR length(texto) <= 140),
    para               TEXT NULL,
    por                TEXT NOT NULL,
    por_nome           TEXT NULL,
    quando             TIMESTAMPTZ NOT NULL DEFAULT NOW()
);
ALTER TABLE mesa_recado DROP CONSTRAINT IF EXISTS ck_mesa_recado_tipo;
ALTER TABLE mesa_recado ADD CONSTRAINT ck_mesa_recado_tipo CHECK (tipo IN ('recado', 'cobranca', 'atraso', 'aviso_pronto'));
CREATE INDEX IF NOT EXISTS ix_mesa_recado_pedido ON mesa_recado (pedido_id, quando);

CREATE TABLE IF NOT EXISTS mesa_perda (
    id                 UUID PRIMARY KEY,
    estabelecimento_id UUID NOT NULL REFERENCES estabelecimentos (id),
    produto_id         UUID NOT NULL,
    nome               TEXT NOT NULL,
    item_id            UUID NULL REFERENCES mesa_item (id),
    sessao_id          UUID NULL REFERENCES mesa_sessao (id),
    qtd                NUMERIC(8,2) NOT NULL CHECK (qtd > 0),
    motivo             TEXT NOT NULL CHECK (length(motivo) BETWEEN 1 AND 280),
    -- Custo de producao: 0 ate a ficha tecnica do Mesas existir (P14/P15 leem daqui).
    custo              NUMERIC(12,2) NOT NULL DEFAULT 0 CHECK (custo >= 0),
    por                TEXT NOT NULL,
    por_nome           TEXT NULL,
    criado_em          TIMESTAMPTZ NOT NULL DEFAULT NOW()
);
CREATE INDEX IF NOT EXISTS ix_mesa_perda_loja ON mesa_perda (estabelecimento_id, criado_em);

CREATE TABLE IF NOT EXISTS mesa_aviso_gestor (
    id                 UUID PRIMARY KEY,
    estabelecimento_id UUID NOT NULL REFERENCES estabelecimentos (id),
    tipo               TEXT NOT NULL,
    mesa_id            UUID NULL REFERENCES mesa (id),
    item_id            UUID NULL REFERENCES mesa_item (id),
    valor              NUMERIC(12,2) NOT NULL DEFAULT 0 CHECK (valor >= 0),
    motivo             TEXT NULL CHECK (motivo IS NULL OR length(motivo) <= 280),
    por                TEXT NOT NULL,
    por_nome           TEXT NULL,
    texto              TEXT NULL CHECK (texto IS NULL OR length(texto) <= 280),
    lido               BOOLEAN NOT NULL DEFAULT FALSE,
    lido_por           TEXT NULL,
    lido_em            TIMESTAMPTZ NULL,
    criado_em          TIMESTAMPTZ NOT NULL DEFAULT NOW()
);
ALTER TABLE mesa_aviso_gestor DROP CONSTRAINT IF EXISTS ck_mesa_aviso_gestor_tipo;
ALTER TABLE mesa_aviso_gestor ADD CONSTRAINT ck_mesa_aviso_gestor_tipo CHECK (tipo IN ('cortesia', 'perda', 'avaliacao'));
CREATE INDEX IF NOT EXISTS ix_mesa_aviso_gestor_loja ON mesa_aviso_gestor (estabelecimento_id, lido, criado_em);

-- Historico nao muda nem some; pedido, item, perda e aviso nao somem (cancelado/lido em vez de apagar).
DROP TRIGGER IF EXISTS mesa_item_evento_imutavel ON mesa_item_evento;
CREATE TRIGGER mesa_item_evento_imutavel BEFORE UPDATE OR DELETE ON mesa_item_evento
    FOR EACH ROW EXECUTE FUNCTION gestao_bloqueia_alteracao();
DROP TRIGGER IF EXISTS mesa_recado_imutavel ON mesa_recado;
CREATE TRIGGER mesa_recado_imutavel BEFORE UPDATE OR DELETE ON mesa_recado
    FOR EACH ROW EXECUTE FUNCTION gestao_bloqueia_alteracao();
DROP TRIGGER IF EXISTS mesa_pedido_sem_delete ON mesa_pedido;
CREATE TRIGGER mesa_pedido_sem_delete BEFORE DELETE ON mesa_pedido FOR EACH ROW EXECUTE FUNCTION gestao_bloqueia_exclusao();
DROP TRIGGER IF EXISTS mesa_item_sem_delete ON mesa_item;
CREATE TRIGGER mesa_item_sem_delete BEFORE DELETE ON mesa_item FOR EACH ROW EXECUTE FUNCTION gestao_bloqueia_exclusao();
DROP TRIGGER IF EXISTS mesa_perda_sem_delete ON mesa_perda;
CREATE TRIGGER mesa_perda_sem_delete BEFORE DELETE ON mesa_perda FOR EACH ROW EXECUTE FUNCTION gestao_bloqueia_exclusao();
DROP TRIGGER IF EXISTS mesa_aviso_gestor_sem_delete ON mesa_aviso_gestor;
CREATE TRIGGER mesa_aviso_gestor_sem_delete BEFORE DELETE ON mesa_aviso_gestor FOR EACH ROW EXECUTE FUNCTION gestao_bloqueia_exclusao();
-- mesa_item_parte pode ser trocada (mover ou dividir o item): a historia fica em mesa_evento.

INSERT INTO delivery_tracking_schema_versions (version)
VALUES ('20261207_01_mesas_pedidos')
ON CONFLICT (version) DO NOTHING;

COMMIT;
