-- Gestao 6C1-B: repasse do iFood conferido a mao (docs/zippy-gestao/CONTRATOS.md §9.10).
-- O dinheiro do iFood so entra no banco quando o gerente confirma o repasse: o livro quita a receita a receber
-- (Liquidar), sem criar outra receita. Esta tabela guarda quem confirmou, o comprovante e o valor que caiu; quando o valor
-- nao bate com o a receber, a linha fica 'divergente' para conciliar e nada e quitado.
-- Aditiva e idempotente. Nada se apaga e nada se reescreve.
BEGIN;

CREATE TABLE IF NOT EXISTS gestao_repasses_ifood (
    id uuid PRIMARY KEY,
    estabelecimento_id uuid NOT NULL REFERENCES estabelecimentos(id),
    chave_idempotencia varchar(160) NOT NULL CHECK (length(trim(chave_idempotencia)) > 0),
    hash_conteudo char(64) NOT NULL,
    pedido_id varchar(120) NOT NULL,
    venda_id uuid NOT NULL,
    lancamento_id uuid NOT NULL,
    conta_destino_id uuid NOT NULL,
    data_pagamento date NOT NULL,
    valor_a_receber numeric(15,2) NOT NULL,
    valor_recebido numeric(15,2) NOT NULL CHECK (valor_recebido > 0),
    situacao varchar(12) NOT NULL CHECK (situacao IN ('liquidado', 'divergente')),
    pagamento_id uuid,
    referencia varchar(120),
    responsavel_id integer NOT NULL REFERENCES usuario(id),
    registrado_em timestamptz NOT NULL DEFAULT now(),
    UNIQUE (estabelecimento_id, chave_idempotencia),
    FOREIGN KEY (estabelecimento_id, venda_id) REFERENCES gestao_acontecimentos (estabelecimento_id, id),
    FOREIGN KEY (estabelecimento_id, lancamento_id) REFERENCES gestao_lancamentos (estabelecimento_id, id),
    FOREIGN KEY (estabelecimento_id, conta_destino_id) REFERENCES gestao_contas (estabelecimento_id, id),
    FOREIGN KEY (estabelecimento_id, pagamento_id) REFERENCES gestao_acontecimentos (estabelecimento_id, id),
    CHECK ((situacao = 'liquidado') = (pagamento_id IS NOT NULL)),
    CHECK (situacao <> 'liquidado' OR valor_recebido = valor_a_receber)
);

-- Um recebivel se quita uma vez so.
CREATE UNIQUE INDEX IF NOT EXISTS ux_gestao_repasses_ifood_liquidado
    ON gestao_repasses_ifood (estabelecimento_id, lancamento_id) WHERE situacao = 'liquidado';
CREATE INDEX IF NOT EXISTS ix_gestao_repasses_ifood_pedido ON gestao_repasses_ifood (estabelecimento_id, pedido_id);

DROP TRIGGER IF EXISTS gestao_repasses_ifood_imutavel ON gestao_repasses_ifood;
CREATE TRIGGER gestao_repasses_ifood_imutavel BEFORE UPDATE OR DELETE ON gestao_repasses_ifood
    FOR EACH ROW EXECUTE FUNCTION gestao_bloqueia_alteracao();
DROP TRIGGER IF EXISTS gestao_repasses_ifood_sem_truncate ON gestao_repasses_ifood;
CREATE TRIGGER gestao_repasses_ifood_sem_truncate BEFORE TRUNCATE ON gestao_repasses_ifood
    FOR EACH STATEMENT EXECUTE FUNCTION gestao_bloqueia_alteracao();

COMMIT;
