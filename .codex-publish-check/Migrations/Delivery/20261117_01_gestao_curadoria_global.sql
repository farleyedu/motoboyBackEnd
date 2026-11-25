-- Gestao, L7 da auditoria de edicao: o super admin corrige o catalogo global (produto, kits, categorias) e os criterios
-- de promocao da curadoria. Regra 2: cada correcao vira decisao imutavel em gestao_curadoria_decisoes
-- (caso_tipo 'global', decisao 'corrigir', antes/depois, responsavel, motivo); criterios sao versoes so INSERT.
-- Regra 6: nada aqui toca o insumo de uma loja nem o vinculo dela.
-- Requer 20261105_01, 20261107_01, 20261113_01 e 20261114_01. Aditiva e idempotente.
BEGIN;

-- Correcao do global entra no mesmo livro de decisoes: global:produto:<id>, global:kit:<id>, global:categoria:<id>.
ALTER TABLE gestao_curadoria_decisoes DROP CONSTRAINT IF EXISTS gestao_curadoria_decisoes_caso_tipo_check;
ALTER TABLE gestao_curadoria_decisoes DROP CONSTRAINT IF EXISTS ck_gestao_curadoria_decisoes_caso_tipo;
ALTER TABLE gestao_curadoria_decisoes ADD CONSTRAINT ck_gestao_curadoria_decisoes_caso_tipo
    CHECK (caso_tipo IN ('depara', 'prod', 'contra', 'dup', 'cat', 'inc', 'rec', 'global'));
CREATE INDEX IF NOT EXISTS ix_gestao_curadoria_decisoes_global ON gestao_curadoria_decisoes (em DESC) WHERE caso_tipo = 'global';

-- Categorias globais (antes so texto livre em gestao_catalogo_global.categoria). Nunca se apagam: desativar tira da escolha.
-- O servico registra aqui as categorias que ja estao em uso (nome_chave sai do mesmo normalizador do cupom).
CREATE TABLE IF NOT EXISTS gestao_catalogo_categorias (
    id uuid PRIMARY KEY,
    nome varchar(60) NOT NULL CHECK (length(trim(nome)) > 0),
    nome_chave varchar(80) NOT NULL CHECK (length(nome_chave) > 0),
    ativa boolean NOT NULL DEFAULT true,
    criado_por integer REFERENCES usuario(id),
    criado_em timestamptz NOT NULL DEFAULT now()
);
CREATE UNIQUE INDEX IF NOT EXISTS ux_gestao_catalogo_categorias_chave ON gestao_catalogo_categorias (nome_chave);

DROP TRIGGER IF EXISTS gestao_catalogo_categorias_sem_delete ON gestao_catalogo_categorias;
CREATE TRIGGER gestao_catalogo_categorias_sem_delete BEFORE DELETE ON gestao_catalogo_categorias
    FOR EACH ROW EXECUTE FUNCTION gestao_bloqueia_exclusao();

-- Criterios de promocao (antes fixos: 5 lojas, 10 confirmacoes, 10%). Cada mudanca e uma versao nova; vale a ultima.
CREATE TABLE IF NOT EXISTS gestao_curadoria_criterios (
    id uuid PRIMARY KEY,
    lojas integer NOT NULL CHECK (lojas BETWEEN 1 AND 100),
    confirmacoes integer NOT NULL CHECK (confirmacoes BETWEEN 1 AND 1000),
    divergencia_max numeric(5,4) NOT NULL CHECK (divergencia_max >= 0 AND divergencia_max <= 0.5),
    responsavel_id integer NOT NULL REFERENCES usuario(id),
    responsavel_nome varchar(120) NOT NULL CHECK (length(trim(responsavel_nome)) > 0),
    motivo varchar(500) NOT NULL CHECK (length(trim(motivo)) > 0),
    em timestamptz NOT NULL
);
CREATE INDEX IF NOT EXISTS ix_gestao_curadoria_criterios_em ON gestao_curadoria_criterios (em DESC, id);

DROP TRIGGER IF EXISTS gestao_curadoria_criterios_imutavel ON gestao_curadoria_criterios;
CREATE TRIGGER gestao_curadoria_criterios_imutavel BEFORE UPDATE OR DELETE ON gestao_curadoria_criterios
    FOR EACH ROW EXECUTE FUNCTION gestao_bloqueia_alteracao();
DROP TRIGGER IF EXISTS gestao_curadoria_criterios_sem_truncate ON gestao_curadoria_criterios;
CREATE TRIGGER gestao_curadoria_criterios_sem_truncate BEFORE TRUNCATE ON gestao_curadoria_criterios
    FOR EACH STATEMENT EXECUTE FUNCTION gestao_bloqueia_alteracao();

COMMIT;
