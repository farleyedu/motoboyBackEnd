-- Gestao, Logica E2: vinculos de compras (linha do cupom -> insumo da loja), catalogo global e historico de preco.
-- Regra 6: o vinculo da loja vale na hora para ela; para todas as lojas, so depois da curadoria Zippy (catalogo 'validado').
-- Curadoria nunca bloqueia a loja. Nada se apaga: vinculo trocado fica 'desfeito' (quem, quando, motivo).
-- A chave do vinculo inclui a unidade do cupom: o fator e da embalagem (PCT de 50 x UN).
-- insumo_id nao tem FK: a migracao de insumos (Inteligencia) nao roda em todo ambiente; o servico confere a loja.
-- Requer 20261105_01_gestao_fundacao.sql (gestao_bloqueia_alteracao). Aditiva e idempotente.
BEGIN;

CREATE TABLE IF NOT EXISTS gestao_catalogo_global (
    id uuid PRIMARY KEY,
    nome varchar(120) NOT NULL CHECK (length(trim(nome)) > 0),
    nome_chave varchar(160) NOT NULL,
    unidade varchar(2) NOT NULL CHECK (unidade IN ('g', 'ml', 'un')),
    categoria varchar(60),
    ean varchar(14),
    status varchar(10) NOT NULL DEFAULT 'candidato' CHECK (status IN ('candidato', 'validado', 'rejeitado', 'mesclado')),
    mesclado_em uuid REFERENCES gestao_catalogo_global(id),
    criado_por_loja uuid REFERENCES estabelecimentos(id),
    criado_por integer NOT NULL REFERENCES usuario(id),
    criado_em timestamptz NOT NULL DEFAULT now(),
    curado_por integer REFERENCES usuario(id),
    curado_em timestamptz,
    motivo_curadoria varchar(300),
    CHECK (status <> 'mesclado' OR mesclado_em IS NOT NULL),
    CHECK (status = 'candidato' OR (curado_por IS NOT NULL AND curado_em IS NOT NULL))
);
CREATE UNIQUE INDEX IF NOT EXISTS ux_gestao_catalogo_validado_nome ON gestao_catalogo_global (nome_chave, unidade) WHERE status = 'validado';
CREATE UNIQUE INDEX IF NOT EXISTS ux_gestao_catalogo_validado_ean ON gestao_catalogo_global (ean) WHERE status = 'validado' AND ean IS NOT NULL;
CREATE INDEX IF NOT EXISTS ix_gestao_catalogo_candidatos ON gestao_catalogo_global (criado_em) WHERE status = 'candidato';

CREATE TABLE IF NOT EXISTS gestao_vinculos (
    id uuid PRIMARY KEY,
    estabelecimento_id uuid NOT NULL REFERENCES estabelecimentos(id),
    -- '' = vale para qualquer fornecedor da loja.
    fornecedor_cnpj varchar(14) NOT NULL DEFAULT '',
    nome_chave varchar(160) NOT NULL CHECK (length(nome_chave) > 0),
    descricao varchar(200) NOT NULL,
    unidade_cupom varchar(10) NOT NULL,
    ean varchar(14),
    insumo_id uuid NOT NULL,
    global_id uuid REFERENCES gestao_catalogo_global(id),
    unidade_base varchar(2) NOT NULL CHECK (unidade_base IN ('g', 'ml', 'un')),
    fator numeric(18,6) NOT NULL CHECK (fator > 0),
    estado varchar(10) NOT NULL DEFAULT 'confirmado' CHECK (estado IN ('confirmado', 'desfeito')),
    documento_id uuid REFERENCES gestao_documentos(id),
    confirmado_por integer NOT NULL REFERENCES usuario(id),
    confirmado_em timestamptz NOT NULL DEFAULT now(),
    desfeito_por integer REFERENCES usuario(id),
    desfeito_em timestamptz,
    motivo_desfeito varchar(300),
    CHECK (estado = 'confirmado' OR (desfeito_por IS NOT NULL AND desfeito_em IS NOT NULL AND motivo_desfeito IS NOT NULL))
);
CREATE UNIQUE INDEX IF NOT EXISTS ux_gestao_vinculos_ativo
    ON gestao_vinculos (estabelecimento_id, fornecedor_cnpj, nome_chave, unidade_cupom) WHERE estado = 'confirmado';
-- Comunidade: o mesmo texto de cupom confirmado em outras lojas.
CREATE INDEX IF NOT EXISTS ix_gestao_vinculos_nome ON gestao_vinculos (nome_chave) WHERE estado = 'confirmado';
CREATE INDEX IF NOT EXISTS ix_gestao_vinculos_ean ON gestao_vinculos (ean) WHERE estado = 'confirmado' AND ean IS NOT NULL;
CREATE INDEX IF NOT EXISTS ix_gestao_vinculos_global ON gestao_vinculos (global_id) WHERE global_id IS NOT NULL;
CREATE INDEX IF NOT EXISTS ix_gestao_vinculos_insumo ON gestao_vinculos (estabelecimento_id, insumo_id);

-- Vinculo nunca some (desfazer e um estado).
CREATE OR REPLACE FUNCTION gestao_bloqueia_exclusao() RETURNS trigger LANGUAGE plpgsql AS $$
BEGIN
    RAISE EXCEPTION 'Registro da gestao nao pode ser apagado (%).', TG_TABLE_NAME;
END $$;
DROP TRIGGER IF EXISTS gestao_vinculos_sem_delete ON gestao_vinculos;
CREATE TRIGGER gestao_vinculos_sem_delete BEFORE DELETE ON gestao_vinculos
    FOR EACH ROW EXECUTE FUNCTION gestao_bloqueia_exclusao();
DROP TRIGGER IF EXISTS gestao_catalogo_sem_delete ON gestao_catalogo_global;
CREATE TRIGGER gestao_catalogo_sem_delete BEFORE DELETE ON gestao_catalogo_global
    FOR EACH ROW EXECUTE FUNCTION gestao_bloqueia_exclusao();

-- Preco por unidade base observado em cada linha de documento: base da divergencia (mediana). Imutavel.
CREATE TABLE IF NOT EXISTS gestao_vinculo_precos (
    id uuid PRIMARY KEY,
    estabelecimento_id uuid NOT NULL REFERENCES estabelecimentos(id),
    vinculo_id uuid NOT NULL REFERENCES gestao_vinculos(id),
    insumo_id uuid NOT NULL,
    documento_id uuid REFERENCES gestao_documentos(id),
    linha integer,
    quantidade_base numeric(18,4) NOT NULL CHECK (quantidade_base > 0),
    valor_total numeric(15,2) NOT NULL CHECK (valor_total >= 0),
    preco_base numeric(18,6) NOT NULL CHECK (preco_base >= 0),
    observado_em timestamptz NOT NULL,
    criado_por integer NOT NULL REFERENCES usuario(id),
    criado_em timestamptz NOT NULL DEFAULT now()
);
CREATE UNIQUE INDEX IF NOT EXISTS ux_gestao_vinculo_precos_linha
    ON gestao_vinculo_precos (vinculo_id, documento_id, linha) WHERE documento_id IS NOT NULL;
CREATE INDEX IF NOT EXISTS ix_gestao_vinculo_precos_insumo ON gestao_vinculo_precos (estabelecimento_id, insumo_id, observado_em DESC);

DROP TRIGGER IF EXISTS gestao_vinculo_precos_imutavel ON gestao_vinculo_precos;
CREATE TRIGGER gestao_vinculo_precos_imutavel BEFORE UPDATE OR DELETE ON gestao_vinculo_precos
    FOR EACH ROW EXECUTE FUNCTION gestao_bloqueia_alteracao();

COMMIT;
