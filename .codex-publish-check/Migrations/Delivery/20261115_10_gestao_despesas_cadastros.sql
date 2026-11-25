-- Gestao, L1 (auditoria-edicao itens 1, 7 e 19): historico das contas fixas, equipamentos no servidor e finalidades com id.
-- Nada se apaga. O livro (gestao_lancamentos.finalidade, texto) nao e reescrito: cada nome que a finalidade ja teve fica em
-- gestao_finalidades_nomes apontando para o id, e a tela mostra o nome atual. gestao_finalidades (antiga, imutavel) continua.
-- Requer 20261105_01, 20261107_01 (gestao_bloqueia_exclusao) e 20261111_01. Aditiva e idempotente.
BEGIN;

-- Cada mudanca na conta fixa (criada, editada, arquivada, reativada) com antes, depois e o mes em que passa a valer.
CREATE TABLE IF NOT EXISTS gestao_contas_fixas_historico (
    id uuid PRIMARY KEY,
    estabelecimento_id uuid NOT NULL REFERENCES estabelecimentos(id),
    conta_fixa_id uuid NOT NULL REFERENCES gestao_contas_fixas(id),
    versao integer NOT NULL,
    acao varchar(20) NOT NULL CHECK (acao IN ('criada', 'editada', 'arquivada', 'reativada')),
    antes jsonb,
    depois jsonb NOT NULL,
    vale_a_partir date NOT NULL,
    motivo varchar(300),
    por integer NOT NULL REFERENCES usuario(id),
    em timestamptz NOT NULL DEFAULT now()
);
CREATE INDEX IF NOT EXISTS ix_gestao_contas_fixas_historico ON gestao_contas_fixas_historico (estabelecimento_id, conta_fixa_id, em);
-- Reativada: so lembra do mes da reativacao em diante (os meses em que ficou arquivada nao viram pendencia).
ALTER TABLE gestao_contas_fixas ADD COLUMN IF NOT EXISTS ativa_desde date;

-- Equipamentos: o id e texto ("equip-<uuid>") porque as manutencoes ja lancadas guardam esse id em dados.equipamentoId.
CREATE TABLE IF NOT EXISTS gestao_equipamentos (
    estabelecimento_id uuid NOT NULL REFERENCES estabelecimentos(id),
    id varchar(80) NOT NULL CHECK (length(id) >= 3),
    nome varchar(120) NOT NULL CHECK (length(trim(nome)) >= 2),
    modelo varchar(120) NOT NULL DEFAULT '',
    icone varchar(20) NOT NULL,
    cor varchar(20) NOT NULL,
    estado varchar(20) NOT NULL CHECK (estado IN ('funcionando', 'revisao', 'parado')),
    comprado_em varchar(7),
    valor_do_bem numeric(15,2) CHECK (valor_do_bem IS NULL OR valor_do_bem >= 0),
    garantia_ate varchar(10),
    local varchar(120),
    arquivado boolean NOT NULL DEFAULT false,
    -- 'navegador' = veio do cadastro antigo guardado no navegador.
    origem varchar(20) NOT NULL DEFAULT 'painel',
    versao integer NOT NULL DEFAULT 1,
    criado_por integer NOT NULL REFERENCES usuario(id),
    criado_em timestamptz NOT NULL DEFAULT now(),
    atualizado_por integer NOT NULL REFERENCES usuario(id),
    atualizado_em timestamptz NOT NULL DEFAULT now(),
    PRIMARY KEY (estabelecimento_id, id)
);

-- Finalidade com id: o nome muda, o id fica. Juntada = aponta para a que ficou (nunca em cadeia).
CREATE TABLE IF NOT EXISTS gestao_finalidades_cadastro (
    id uuid PRIMARY KEY,
    estabelecimento_id uuid NOT NULL REFERENCES estabelecimentos(id),
    nome varchar(60) NOT NULL CHECK (length(trim(nome)) >= 2),
    ativa boolean NOT NULL DEFAULT true,
    juntada_em uuid REFERENCES gestao_finalidades_cadastro(id),
    versao integer NOT NULL DEFAULT 1,
    criado_por integer NOT NULL REFERENCES usuario(id),
    criado_em timestamptz NOT NULL DEFAULT now(),
    atualizado_por integer NOT NULL REFERENCES usuario(id),
    atualizado_em timestamptz NOT NULL DEFAULT now(),
    CHECK (juntada_em IS NULL OR juntada_em <> id)
);
CREATE INDEX IF NOT EXISTS ix_gestao_finalidades_cadastro_loja ON gestao_finalidades_cadastro (estabelecimento_id, nome);

-- Todo nome que uma finalidade ja teve (chave sem acento/maiuscula). Uma chave, um id, para sempre.
CREATE TABLE IF NOT EXISTS gestao_finalidades_nomes (
    estabelecimento_id uuid NOT NULL REFERENCES estabelecimentos(id),
    chave varchar(60) NOT NULL,
    nome varchar(60) NOT NULL,
    finalidade_id uuid NOT NULL REFERENCES gestao_finalidades_cadastro(id),
    criado_em timestamptz NOT NULL DEFAULT now(),
    PRIMARY KEY (estabelecimento_id, chave)
);
CREATE INDEX IF NOT EXISTS ix_gestao_finalidades_nomes_id ON gestao_finalidades_nomes (estabelecimento_id, finalidade_id);

CREATE TABLE IF NOT EXISTS gestao_finalidades_historico (
    id uuid PRIMARY KEY,
    estabelecimento_id uuid NOT NULL REFERENCES estabelecimentos(id),
    finalidade_id uuid NOT NULL REFERENCES gestao_finalidades_cadastro(id),
    acao varchar(20) NOT NULL CHECK (acao IN ('criada', 'renomeada', 'desativada', 'reativada', 'juntada', 'recebeu')),
    antes jsonb,
    depois jsonb NOT NULL,
    motivo varchar(300),
    por integer NOT NULL REFERENCES usuario(id),
    em timestamptz NOT NULL DEFAULT now()
);
CREATE INDEX IF NOT EXISTS ix_gestao_finalidades_historico ON gestao_finalidades_historico (estabelecimento_id, finalidade_id, em);

DROP TRIGGER IF EXISTS gestao_contas_fixas_historico_imutavel ON gestao_contas_fixas_historico;
CREATE TRIGGER gestao_contas_fixas_historico_imutavel BEFORE UPDATE OR DELETE ON gestao_contas_fixas_historico
    FOR EACH ROW EXECUTE FUNCTION gestao_bloqueia_alteracao();
DROP TRIGGER IF EXISTS gestao_equipamentos_sem_delete ON gestao_equipamentos;
CREATE TRIGGER gestao_equipamentos_sem_delete BEFORE DELETE ON gestao_equipamentos FOR EACH ROW EXECUTE FUNCTION gestao_bloqueia_exclusao();
DROP TRIGGER IF EXISTS gestao_finalidades_cadastro_sem_delete ON gestao_finalidades_cadastro;
CREATE TRIGGER gestao_finalidades_cadastro_sem_delete BEFORE DELETE ON gestao_finalidades_cadastro
    FOR EACH ROW EXECUTE FUNCTION gestao_bloqueia_exclusao();
DROP TRIGGER IF EXISTS gestao_finalidades_nomes_imutavel ON gestao_finalidades_nomes;
CREATE TRIGGER gestao_finalidades_nomes_imutavel BEFORE UPDATE OR DELETE ON gestao_finalidades_nomes
    FOR EACH ROW EXECUTE FUNCTION gestao_bloqueia_alteracao();
DROP TRIGGER IF EXISTS gestao_finalidades_historico_imutavel ON gestao_finalidades_historico;
CREATE TRIGGER gestao_finalidades_historico_imutavel BEFORE UPDATE OR DELETE ON gestao_finalidades_historico
    FOR EACH ROW EXECUTE FUNCTION gestao_bloqueia_alteracao();

COMMIT;
