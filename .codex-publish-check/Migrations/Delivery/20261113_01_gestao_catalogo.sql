-- Gestao, Integracao 6B: catalogo da loja (insumos importados/criados, finalidade por loja), kits do catalogo global,
-- "nao controlar" por nome de cupom e "sugerir ao Zippy" (vira candidato na curadoria).
-- Regra 6: o global e consulta/vinculo; o produto vira da loja ao ser importado ou ter vinculo confirmado numa compra.
-- Regra 7: categoria e do global; finalidade e da loja (gestao_catalogo_loja.finalidades).
-- Regra 9: gestao_catalogo_global.ilustracao e o arquivo da ilustracao Zippy; foto de vitrine nunca entra aqui.
-- Regra 2: nada se apaga. Mudanca de finalidade e de "nao controlar" fica em gestao_catalogo_eventos (antes/depois, quem, quando).
-- insumo_id sem FK, como em gestao_vinculos (a migracao de insumos nao roda em todo ambiente; o servico confere a loja).
-- Requer 20261105_01_gestao_fundacao.sql e 20261107_01_gestao_vinculos.sql. Aditiva e idempotente.
BEGIN;

ALTER TABLE gestao_catalogo_global ADD COLUMN IF NOT EXISTS ilustracao varchar(120);
DO $$
BEGIN
    IF NOT EXISTS (SELECT 1 FROM pg_constraint WHERE conname = 'ck_gestao_catalogo_ilustracao_arquivo') THEN
        -- Nome do arquivo no Painel, sem extensao (/zippy-gestao/ilustracoes/{nome}.webp), nunca URL: foto de cliente nao cabe aqui.
        ALTER TABLE gestao_catalogo_global ADD CONSTRAINT ck_gestao_catalogo_ilustracao_arquivo
            CHECK (ilustracao IS NULL OR ilustracao ~ '^[a-z0-9][a-z0-9_-]*$');
    END IF;
END $$;

CREATE TABLE IF NOT EXISTS gestao_catalogo_kits (
    id uuid PRIMARY KEY,
    nome varchar(80) NOT NULL CHECK (length(trim(nome)) > 0),
    descricao varchar(300),
    ativo boolean NOT NULL DEFAULT true,
    criado_por integer REFERENCES usuario(id),
    criado_em timestamptz NOT NULL DEFAULT now()
);

CREATE TABLE IF NOT EXISTS gestao_catalogo_kit_itens (
    kit_id uuid NOT NULL REFERENCES gestao_catalogo_kits(id),
    global_id uuid NOT NULL REFERENCES gestao_catalogo_global(id),
    ordem integer NOT NULL DEFAULT 0,
    PRIMARY KEY (kit_id, global_id)
);

CREATE TABLE IF NOT EXISTS gestao_catalogo_loja (
    estabelecimento_id uuid NOT NULL REFERENCES estabelecimentos(id),
    insumo_id uuid NOT NULL,
    origem varchar(10) NOT NULL CHECK (origem IN ('global', 'loja', 'novo')),
    global_id uuid REFERENCES gestao_catalogo_global(id),
    finalidades text[] NOT NULL DEFAULT '{}',
    criado_por integer NOT NULL REFERENCES usuario(id),
    criado_em timestamptz NOT NULL DEFAULT now(),
    atualizado_em timestamptz NOT NULL DEFAULT now(),
    PRIMARY KEY (estabelecimento_id, insumo_id),
    CHECK (origem <> 'global' OR global_id IS NOT NULL)
);
-- O mesmo item global nao entra duas vezes na loja (importar de novo = DUPLICADO).
CREATE UNIQUE INDEX IF NOT EXISTS ux_gestao_catalogo_loja_global
    ON gestao_catalogo_loja (estabelecimento_id, global_id) WHERE global_id IS NOT NULL;

DROP TRIGGER IF EXISTS gestao_catalogo_loja_sem_delete ON gestao_catalogo_loja;
CREATE TRIGGER gestao_catalogo_loja_sem_delete BEFORE DELETE ON gestao_catalogo_loja
    FOR EACH ROW EXECUTE FUNCTION gestao_bloqueia_exclusao();

CREATE TABLE IF NOT EXISTS gestao_catalogo_eventos (
    id uuid PRIMARY KEY,
    estabelecimento_id uuid NOT NULL REFERENCES estabelecimentos(id),
    insumo_id uuid,
    vinculo_id uuid REFERENCES gestao_vinculos(id),
    nome_chave varchar(160),
    tipo varchar(20) NOT NULL CHECK (tipo IN ('importado', 'criado', 'finalidade', 'nao_controlar', 'controlar', 'sugerido')),
    detalhe varchar(300),
    antes varchar(300),
    depois varchar(300),
    por integer NOT NULL REFERENCES usuario(id),
    em timestamptz NOT NULL DEFAULT now(),
    CHECK (insumo_id IS NOT NULL OR vinculo_id IS NOT NULL OR nome_chave IS NOT NULL)
);
CREATE INDEX IF NOT EXISTS ix_gestao_catalogo_eventos_insumo ON gestao_catalogo_eventos (estabelecimento_id, insumo_id, em DESC);
CREATE INDEX IF NOT EXISTS ix_gestao_catalogo_eventos_nome ON gestao_catalogo_eventos (estabelecimento_id, nome_chave, em DESC);

DROP TRIGGER IF EXISTS gestao_catalogo_eventos_imutavel ON gestao_catalogo_eventos;
CREATE TRIGGER gestao_catalogo_eventos_imutavel BEFORE UPDATE OR DELETE ON gestao_catalogo_eventos
    FOR EACH ROW EXECUTE FUNCTION gestao_bloqueia_alteracao();

-- "Nao controlar": a loja decidiu nao levar este nome de cupom ao estoque. A compra continua no financeiro.
CREATE TABLE IF NOT EXISTS gestao_catalogo_nao_controlar (
    id uuid PRIMARY KEY,
    estabelecimento_id uuid NOT NULL REFERENCES estabelecimentos(id),
    fornecedor_cnpj varchar(14) NOT NULL DEFAULT '',
    nome_chave varchar(160) NOT NULL CHECK (length(nome_chave) > 0),
    descricao varchar(200) NOT NULL,
    estado varchar(10) NOT NULL DEFAULT 'ativo' CHECK (estado IN ('ativo', 'desfeito')),
    marcado_por integer NOT NULL REFERENCES usuario(id),
    marcado_em timestamptz NOT NULL DEFAULT now(),
    desfeito_por integer REFERENCES usuario(id),
    desfeito_em timestamptz,
    CHECK (estado = 'ativo' OR (desfeito_por IS NOT NULL AND desfeito_em IS NOT NULL))
);
CREATE UNIQUE INDEX IF NOT EXISTS ux_gestao_catalogo_nao_controlar_ativo
    ON gestao_catalogo_nao_controlar (estabelecimento_id, fornecedor_cnpj, nome_chave) WHERE estado = 'ativo';

DROP TRIGGER IF EXISTS gestao_catalogo_nao_controlar_sem_delete ON gestao_catalogo_nao_controlar;
CREATE TRIGGER gestao_catalogo_nao_controlar_sem_delete BEFORE DELETE ON gestao_catalogo_nao_controlar
    FOR EACH ROW EXECUTE FUNCTION gestao_bloqueia_exclusao();

-- "Sugerir ao Zippy": o vinculo da loja aponta para um candidato do catalogo global (a curadoria le os candidatos).
-- Na loja nada muda (regra 6: curadoria nunca bloqueia a operacao local).
CREATE TABLE IF NOT EXISTS gestao_catalogo_sugestoes (
    id uuid PRIMARY KEY,
    estabelecimento_id uuid NOT NULL REFERENCES estabelecimentos(id),
    vinculo_id uuid NOT NULL REFERENCES gestao_vinculos(id),
    global_id uuid NOT NULL REFERENCES gestao_catalogo_global(id),
    sugerido_por integer NOT NULL REFERENCES usuario(id),
    sugerido_em timestamptz NOT NULL DEFAULT now(),
    UNIQUE (vinculo_id)
);
CREATE INDEX IF NOT EXISTS ix_gestao_catalogo_sugestoes_global ON gestao_catalogo_sugestoes (global_id);

DROP TRIGGER IF EXISTS gestao_catalogo_sugestoes_imutavel ON gestao_catalogo_sugestoes;
CREATE TRIGGER gestao_catalogo_sugestoes_imutavel BEFORE UPDATE OR DELETE ON gestao_catalogo_sugestoes
    FOR EACH ROW EXECUTE FUNCTION gestao_bloqueia_alteracao();

COMMIT;
