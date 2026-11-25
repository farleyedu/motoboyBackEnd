-- ---------------------------------------------------------------------------
-- Modulo Cardapio: cria o schema completo que Repository/CardapioRepository.cs ja espera e usa em
-- producao (CRUD via Controllers/CardapioController.cs, CardapioContractController.cs,
-- CardapioFichaController.cs, PublicCardapioController.cs, PublicCardapioSnapshotController.cs) mas
-- que nunca foi criado por nenhuma migration rastreada -- ficou faltando desde antes deste sistema
-- de migrations existir. Isso bloqueava, entre outras coisas, 20260926_01_produto_atendimento.sql
-- (FK pra cardapio_produto) e o seed de demonstracao 20260927_90_seed_uberlandia.sql.
--
-- PedidoCoreService/PedidoPricing usam cardapio_produto como fonte de verdade do preco ao criar um
-- pedido com itens, mas pedido_item.produto_id NAO tem FK pra cardapio_produto -- e' copia (snapshot)
-- de nome/preco no momento do pedido, de proposito, pra pedidos antigos nao mudarem se o cardapio
-- mudar depois. Por isso esta migration nao mexe em pedido/pedido_item.
--
-- Ordem das tabelas: categoria -> produto (FK categoria) -> grupo_adicional -> grupo_adicional_item
-- (FK grupo) -> produto_grupo (FK produto e grupo) -> web_config -> pedido_publico.
-- Aditiva e idempotente (IF NOT EXISTS em tudo).
-- ---------------------------------------------------------------------------
BEGIN;

CREATE TABLE IF NOT EXISTS cardapio_categoria (
    id                 UUID PRIMARY KEY,
    id_estabelecimento UUID NOT NULL REFERENCES estabelecimentos (id) ON DELETE CASCADE,
    nome               TEXT NOT NULL CHECK (length(trim(nome)) > 0),
    slug               TEXT NOT NULL CHECK (length(trim(slug)) > 0),
    emoji              TEXT NULL,
    descricao          TEXT NULL,
    imagem_url         TEXT NULL,
    ordem              INTEGER NOT NULL DEFAULT 0,
    ativo              BOOLEAN NOT NULL DEFAULT TRUE,
    created_at         TIMESTAMPTZ NOT NULL DEFAULT NOW(),
    updated_at         TIMESTAMPTZ NOT NULL DEFAULT NOW(),
    deleted_at         TIMESTAMPTZ NULL
);
CREATE INDEX IF NOT EXISTS ix_cardapio_categoria_estab ON cardapio_categoria (id_estabelecimento, deleted_at);
-- Slug unico por loja, mas so entre linhas ativas: GerarSlugUnicoAsync (CardapioRepository.cs) so
-- considera linhas com deleted_at IS NULL ao gerar um slug novo, entao a constraint tem que ser
-- parcial pra permitir reaproveitar o slug de algo ja excluido.
CREATE UNIQUE INDEX IF NOT EXISTS ux_cardapio_categoria_estab_slug
    ON cardapio_categoria (id_estabelecimento, slug) WHERE deleted_at IS NULL;

CREATE TABLE IF NOT EXISTS cardapio_produto (
    id                 UUID PRIMARY KEY,
    id_estabelecimento UUID NOT NULL REFERENCES estabelecimentos (id) ON DELETE CASCADE,
    categoria_id       UUID NOT NULL REFERENCES cardapio_categoria (id),
    nome               TEXT NOT NULL CHECK (length(trim(nome)) > 0),
    slug               TEXT NOT NULL CHECK (length(trim(slug)) > 0),
    emoji              TEXT NULL,
    descricao          TEXT NULL,
    descricao_curta    TEXT NULL,
    preco_base         NUMERIC(10,2) NOT NULL CHECK (preco_base >= 0),
    preco_de           NUMERIC(10,2) NULL CHECK (preco_de IS NULL OR preco_de >= 0),
    badge_desconto     TEXT NULL,
    is_club            BOOLEAN NOT NULL DEFAULT FALSE,
    imagem_url         TEXT NULL,
    eco_friendly       BOOLEAN NOT NULL DEFAULT FALSE,
    extras_titulo      TEXT NULL,
    extras_subtitulo   TEXT NULL,
    ordem              INTEGER NOT NULL DEFAULT 0,
    ativo              BOOLEAN NOT NULL DEFAULT TRUE,
    destaque           BOOLEAN NOT NULL DEFAULT FALSE,
    disponivel         BOOLEAN NOT NULL DEFAULT TRUE,
    publico_web        BOOLEAN NOT NULL DEFAULT TRUE,
    created_at         TIMESTAMPTZ NOT NULL DEFAULT NOW(),
    updated_at         TIMESTAMPTZ NOT NULL DEFAULT NOW(),
    deleted_at         TIMESTAMPTZ NULL
);
CREATE INDEX IF NOT EXISTS ix_cardapio_produto_estab ON cardapio_produto (id_estabelecimento, deleted_at);
CREATE INDEX IF NOT EXISTS ix_cardapio_produto_categoria ON cardapio_produto (categoria_id);
CREATE UNIQUE INDEX IF NOT EXISTS ux_cardapio_produto_estab_slug
    ON cardapio_produto (id_estabelecimento, slug) WHERE deleted_at IS NULL;

CREATE TABLE IF NOT EXISTS cardapio_grupo_adicional (
    id                 UUID PRIMARY KEY,
    id_estabelecimento UUID NOT NULL REFERENCES estabelecimentos (id) ON DELETE CASCADE,
    nome               TEXT NOT NULL CHECK (length(trim(nome)) > 0),
    tipo               TEXT NOT NULL DEFAULT 'adicional_global',
    descricao          TEXT NULL,
    min_selecionados   INTEGER NOT NULL DEFAULT 0 CHECK (min_selecionados >= 0),
    max_selecionados   INTEGER NOT NULL DEFAULT 1 CHECK (max_selecionados >= min_selecionados),
    ordem              INTEGER NOT NULL DEFAULT 0,
    ativo              BOOLEAN NOT NULL DEFAULT TRUE,
    created_at         TIMESTAMPTZ NOT NULL DEFAULT NOW(),
    updated_at         TIMESTAMPTZ NOT NULL DEFAULT NOW(),
    deleted_at         TIMESTAMPTZ NULL
);
CREATE INDEX IF NOT EXISTS ix_cardapio_grupo_adicional_estab ON cardapio_grupo_adicional (id_estabelecimento, deleted_at);

-- Sem deleted_at de proposito: SincronizarItensDeGrupoAsync (CardapioRepository.cs) deleta de verdade
-- (hard delete) os itens que saem do grupo a cada edicao, nao faz soft delete.
CREATE TABLE IF NOT EXISTS cardapio_grupo_adicional_item (
    id         UUID PRIMARY KEY,
    id_grupo   UUID NOT NULL REFERENCES cardapio_grupo_adicional (id) ON DELETE CASCADE,
    nome       TEXT NOT NULL CHECK (length(trim(nome)) > 0),
    descricao  TEXT NULL,
    preco      NUMERIC(10,2) NOT NULL DEFAULT 0 CHECK (preco >= 0),
    ordem      INTEGER NOT NULL DEFAULT 0,
    ativo      BOOLEAN NOT NULL DEFAULT TRUE,
    created_at TIMESTAMPTZ NOT NULL DEFAULT NOW(),
    updated_at TIMESTAMPTZ NOT NULL DEFAULT NOW()
);
CREATE INDEX IF NOT EXISTS ix_cardapio_grupo_adicional_item_grupo ON cardapio_grupo_adicional_item (id_grupo);

-- Juncao produto <-> grupo de adicionais. Sem coluna id propria: toda operacao e por (produto,grupo).
CREATE TABLE IF NOT EXISTS cardapio_produto_grupo (
    id_produto UUID NOT NULL REFERENCES cardapio_produto (id) ON DELETE CASCADE,
    id_grupo   UUID NOT NULL REFERENCES cardapio_grupo_adicional (id) ON DELETE CASCADE,
    ordem      INTEGER NULL,
    created_at TIMESTAMPTZ NOT NULL DEFAULT NOW(),
    PRIMARY KEY (id_produto, id_grupo)
);
CREATE INDEX IF NOT EXISTS ix_cardapio_produto_grupo_grupo ON cardapio_produto_grupo (id_grupo);

-- Configuracao do cardapio publico (web) por loja: uma linha por estabelecimento.
CREATE TABLE IF NOT EXISTS cardapio_web_config (
    id_estabelecimento  UUID PRIMARY KEY REFERENCES estabelecimentos (id) ON DELETE CASCADE,
    nome_publico        TEXT NULL,
    emoji               TEXT NULL,
    logo_url            TEXT NULL,
    banner_url          TEXT NULL,
    rating              NUMERIC(3,2) NULL,
    review_count        INTEGER NULL,
    delivery_time_label TEXT NULL,
    delivery_fee_value  NUMERIC(10,2) NOT NULL DEFAULT 0,
    delivery_fee_label  TEXT NULL,
    service_fee_value   NUMERIC(10,2) NOT NULL DEFAULT 0,
    aceita_entrega      BOOLEAN NOT NULL DEFAULT TRUE,
    aceita_retirada     BOOLEAN NOT NULL DEFAULT TRUE,
    publicado           BOOLEAN NOT NULL DEFAULT FALSE,
    created_at          TIMESTAMPTZ NOT NULL DEFAULT NOW(),
    updated_at          TIMESTAMPTZ NOT NULL DEFAULT NOW()
);

-- Pedido feito pelo cliente final no cardapio web publico (sem login). Independente da tabela
-- "pedido" do fluxo interno -- e' o rascunho do cliente antes de virar pedido de verdade.
CREATE TABLE IF NOT EXISTS cardapio_pedido_publico (
    id                     UUID PRIMARY KEY,
    id_estabelecimento     UUID NOT NULL REFERENCES estabelecimentos (id) ON DELETE CASCADE,
    codigo                 TEXT NOT NULL,
    status                 TEXT NOT NULL DEFAULT 'pendente',
    tipo_entrega           TEXT NOT NULL DEFAULT 'retirada',
    nome_cliente           TEXT NOT NULL CHECK (length(trim(nome_cliente)) > 0),
    telefone_cliente       TEXT NOT NULL CHECK (length(trim(telefone_cliente)) > 0),
    email_cliente          TEXT NULL,
    forma_pagamento        TEXT NULL,
    observacoes            TEXT NULL,
    subtotal_produtos      NUMERIC(10,2) NOT NULL DEFAULT 0,
    subtotal_adicionais    NUMERIC(10,2) NOT NULL DEFAULT 0,
    taxa_entrega           NUMERIC(10,2) NOT NULL DEFAULT 0,
    total                  NUMERIC(10,2) NOT NULL DEFAULT 0,
    itens_json             JSONB NOT NULL DEFAULT '[]'::jsonb,
    endereco_entrega_json  JSONB NULL,
    status_pagamento       TEXT NOT NULL DEFAULT 'pendente',
    created_at             TIMESTAMPTZ NOT NULL DEFAULT NOW(),
    updated_at             TIMESTAMPTZ NOT NULL DEFAULT NOW()
);
CREATE INDEX IF NOT EXISTS ix_cardapio_pedido_publico_estab ON cardapio_pedido_publico (id_estabelecimento, created_at);
CREATE UNIQUE INDEX IF NOT EXISTS ux_cardapio_pedido_publico_codigo ON cardapio_pedido_publico (codigo);

INSERT INTO delivery_tracking_schema_versions (version)
VALUES ('20261001_01_cardapio_schema')
ON CONFLICT (version) DO NOTHING;

COMMIT;
