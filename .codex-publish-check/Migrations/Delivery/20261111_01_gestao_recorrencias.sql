-- Gestao, Logica E6: contas fixas (recorrencias) e finalidades da loja (regra 7: categoria global x finalidade da loja).
-- O lancamento de cada mes vive no livro: acontecimento 'despesa_fixa' origem recorrencia:{id}:{AAAA-MM} (nunca duas vezes no mes).
-- Conta fixa nao se apaga: sai da lista (arquivada); o que foi lancado continua no historico.
-- Requer 20261105_01_gestao_fundacao.sql e 20261107_01_gestao_vinculos.sql (gestao_bloqueia_exclusao). Aditiva e idempotente.
BEGIN;

CREATE TABLE IF NOT EXISTS gestao_contas_fixas (
    id uuid PRIMARY KEY,
    estabelecimento_id uuid NOT NULL REFERENCES estabelecimentos(id),
    nome varchar(120) NOT NULL CHECK (length(trim(nome)) >= 2),
    finalidade varchar(60),
    valor_previsto numeric(15,2) NOT NULL CHECK (valor_previsto > 0),
    dia_vencimento smallint NOT NULL CHECK (dia_vencimento BETWEEN 1 AND 31),
    valor_varia boolean NOT NULL DEFAULT false,
    -- Conta de onde costuma sair (so sugestao para a tela; o lancamento nasce a pagar).
    conta_id uuid,
    arquivada boolean NOT NULL DEFAULT false,
    versao integer NOT NULL DEFAULT 1,
    criado_por integer NOT NULL REFERENCES usuario(id),
    criado_em timestamptz NOT NULL DEFAULT now(),
    atualizado_por integer NOT NULL REFERENCES usuario(id),
    atualizado_em timestamptz NOT NULL DEFAULT now()
);
CREATE INDEX IF NOT EXISTS ix_gestao_contas_fixas_loja ON gestao_contas_fixas (estabelecimento_id, arquivada, nome);

CREATE TABLE IF NOT EXISTS gestao_finalidades (
    estabelecimento_id uuid NOT NULL REFERENCES estabelecimentos(id),
    nome varchar(60) NOT NULL CHECK (length(trim(nome)) >= 2),
    chave varchar(60) NOT NULL,
    criado_por integer NOT NULL REFERENCES usuario(id),
    criado_em timestamptz NOT NULL DEFAULT now(),
    PRIMARY KEY (estabelecimento_id, chave)
);

DROP TRIGGER IF EXISTS gestao_contas_fixas_sem_delete ON gestao_contas_fixas;
CREATE TRIGGER gestao_contas_fixas_sem_delete BEFORE DELETE ON gestao_contas_fixas FOR EACH ROW EXECUTE FUNCTION gestao_bloqueia_exclusao();
DROP TRIGGER IF EXISTS gestao_finalidades_imutavel ON gestao_finalidades;
CREATE TRIGGER gestao_finalidades_imutavel BEFORE UPDATE OR DELETE ON gestao_finalidades
    FOR EACH ROW EXECUTE FUNCTION gestao_bloqueia_alteracao();

COMMIT;
