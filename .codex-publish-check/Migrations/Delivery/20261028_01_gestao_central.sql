-- Gestao central (zippy-admin, /apps/gestao): o que a Zippy paga, o que a fila do dono adiou ou dispensou, notas por loja
-- e o registro do que a Gestao fez. A carteira e o dinheiro que entra sao lidos de tabelas que ja existem
-- (estabelecimentos, pedido, crm_contrato, crm_lancamento); aqui so nasce o que ainda nao tinha lugar.
-- Aditiva e idempotente. Rollback em rollback/20261028_01_gestao_central.down.sql.
BEGIN;

-- Contas a pagar da propria Zippy (servidor, WhatsApp/Meta, dominio, impostos...). Cada vencimento e uma linha;
-- ao pagar uma conta que se repete, a proxima nasce sozinha com o mesmo valor (estimado ate alguem lancar o real).
CREATE TABLE IF NOT EXISTS zippy_conta (
    id                BIGSERIAL PRIMARY KEY,
    descricao         TEXT NOT NULL,
    categoria         TEXT NOT NULL DEFAULT 'outros',
    valor             NUMERIC(12, 2) NOT NULL CHECK (valor >= 0),
    estimado          BOOLEAN NOT NULL DEFAULT FALSE,
    vencimento        DATE NOT NULL,
    recorrencia       TEXT NOT NULL DEFAULT 'unica' CHECK (recorrencia IN ('unica', 'mensal', 'anual')),
    responsavel_id    INTEGER NULL,
    status            TEXT NOT NULL DEFAULT 'a_pagar' CHECK (status IN ('a_pagar', 'paga', 'cancelada')),
    valor_pago        NUMERIC(12, 2) NULL,
    pago_em           TIMESTAMPTZ NULL,
    pago_por          INTEGER NULL,
    observacao        TEXT NULL,
    origem_id         BIGINT NULL REFERENCES zippy_conta (id) ON DELETE SET NULL,
    criado_por        INTEGER NULL,
    criado_em         TIMESTAMPTZ NOT NULL DEFAULT NOW()
);
CREATE INDEX IF NOT EXISTS ix_zippy_conta_vencimento ON zippy_conta (status, vencimento);

-- Itens da fila "O que precisa de mim": a chave identifica o caso (ex.: "teste_vence:<loja>"). Adiar esconde ate a data;
-- dispensar esconde de vez, com motivo. Casos novos com a mesma chave nao voltam ate o motivo mudar de chave.
CREATE TABLE IF NOT EXISTS gestao_pendencia_estado (
    chave             TEXT PRIMARY KEY,
    adiado_ate        TIMESTAMPTZ NULL,
    dispensado_motivo TEXT NULL,
    usuario_id        INTEGER NULL,
    atualizado_em     TIMESTAMPTZ NOT NULL DEFAULT NOW()
);

-- Notas internas da Gestao sobre uma loja ("prometi desconto ate dezembro"), com lembrete opcional.
CREATE TABLE IF NOT EXISTS gestao_nota (
    id                 BIGSERIAL PRIMARY KEY,
    estabelecimento_id UUID NOT NULL REFERENCES estabelecimentos (id) ON DELETE CASCADE,
    texto              TEXT NOT NULL,
    lembrar_em         DATE NULL,
    concluida_em       TIMESTAMPTZ NULL,
    autor_id           INTEGER NULL,
    criado_em          TIMESTAMPTZ NOT NULL DEFAULT NOW()
);
CREATE INDEX IF NOT EXISTS ix_gestao_nota_estab ON gestao_nota (estabelecimento_id, criado_em DESC);

-- O que a Gestao fez (quem, quando, em qual loja, antes e depois). Alimenta a linha do tempo da ficha.
CREATE TABLE IF NOT EXISTS gestao_acao_log (
    id                 BIGSERIAL PRIMARY KEY,
    usuario_id         INTEGER NULL,
    estabelecimento_id UUID NULL REFERENCES estabelecimentos (id) ON DELETE SET NULL,
    acao               TEXT NOT NULL,
    detalhe            TEXT NULL,
    criado_em          TIMESTAMPTZ NOT NULL DEFAULT NOW()
);
CREATE INDEX IF NOT EXISTS ix_gestao_acao_log_estab ON gestao_acao_log (estabelecimento_id, criado_em DESC);

COMMIT;
