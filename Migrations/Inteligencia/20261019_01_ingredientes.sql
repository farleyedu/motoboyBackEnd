-- Gestao e Inteligencia, etapa I1: ingredientes (insumos) e historico de custo.
-- Aditiva: nao altera nem apaga nada existente. Aplicar ao ativar o modulo Inteligencia em uma loja.
-- Custo desconhecido = nenhuma linha em insumo_custo (nunca zero). Custo zero so com gratuito = true e motivo.
BEGIN;

CREATE TABLE IF NOT EXISTS insumo (
    id uuid PRIMARY KEY,
    estabelecimento_id uuid NOT NULL REFERENCES estabelecimentos(id),
    nome varchar(120) NOT NULL CHECK (length(trim(nome)) > 0),
    unidade varchar(2) NOT NULL CHECK (unidade IN ('g', 'ml', 'un')),
    categoria varchar(60),
    observacao varchar(500),
    foto_url text,
    foto_credito text,
    ativo boolean NOT NULL DEFAULT true,
    versao integer NOT NULL DEFAULT 1 CHECK (versao > 0),
    chave_idempotencia varchar(80),
    criado_por integer NOT NULL REFERENCES usuario(id),
    criado_em timestamptz NOT NULL DEFAULT now(),
    atualizado_em timestamptz NOT NULL DEFAULT now(),
    UNIQUE (estabelecimento_id, id)
);
CREATE INDEX IF NOT EXISTS ix_insumo_loja_nome ON insumo (estabelecimento_id, lower(nome));
CREATE INDEX IF NOT EXISTS ix_insumo_loja_ativo ON insumo (estabelecimento_id, ativo);
CREATE UNIQUE INDEX IF NOT EXISTS ux_insumo_idempotencia
    ON insumo (estabelecimento_id, chave_idempotencia) WHERE chave_idempotencia IS NOT NULL;

CREATE TABLE IF NOT EXISTS insumo_custo (
    id uuid PRIMARY KEY,
    estabelecimento_id uuid NOT NULL,
    insumo_id uuid NOT NULL,
    quantidade_compra numeric(18,4) NOT NULL CHECK (quantidade_compra > 0),
    unidade_compra varchar(2) NOT NULL CHECK (unidade_compra IN ('g', 'kg', 'ml', 'l', 'un')),
    valor_compra numeric(15,4) NOT NULL CHECK (valor_compra >= 0),
    custo_base numeric(18,8) NOT NULL CHECK (custo_base >= 0),
    gratuito boolean NOT NULL DEFAULT false,
    origem varchar(20) NOT NULL DEFAULT 'informado' CHECK (origem IN ('informado', 'compra')),
    documento_id uuid,
    motivo varchar(300),
    vigencia_inicio timestamptz NOT NULL DEFAULT now(),
    criado_por integer NOT NULL REFERENCES usuario(id),
    criado_em timestamptz NOT NULL DEFAULT now(),
    chave_idempotencia varchar(80),
    FOREIGN KEY (estabelecimento_id, insumo_id) REFERENCES insumo (estabelecimento_id, id),
    CHECK (custo_base > 0 OR (gratuito AND length(trim(coalesce(motivo, ''))) > 0))
);
CREATE INDEX IF NOT EXISTS ix_insumo_custo_vigencia ON insumo_custo (estabelecimento_id, insumo_id, vigencia_inicio DESC, criado_em DESC);
CREATE UNIQUE INDEX IF NOT EXISTS ux_insumo_custo_idempotencia
    ON insumo_custo (estabelecimento_id, chave_idempotencia) WHERE chave_idempotencia IS NOT NULL;

COMMIT;
