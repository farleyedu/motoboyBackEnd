-- Gestao, Caixa: fechamento com aprovacao do gerente. O operador conta as cegas; se a diferenca passar da tolerancia da loja, o
-- fechamento fica ENVIADO (envio_*) esperando o gerente, sem nada no livro, e o caixa fica travado (sem movimento, sem venda na
-- gaveta e sem abrir o dia seguinte). O gerente aprova (o fechamento vai para o livro como sempre), reconta e aprova, ou devolve
-- para o operador recontar (devolvido_*). A tolerancia e o "operador ve o saldo" ficam em gestao_caixa_config, por loja.
-- Requer 20261110_01_gestao_caixa.sql. Aditiva e idempotente.
BEGIN;

CREATE TABLE IF NOT EXISTS gestao_caixa_config (
    estabelecimento_id uuid PRIMARY KEY REFERENCES estabelecimentos(id),
    tolerancia_fechamento numeric(15,2) NOT NULL DEFAULT 0 CHECK (tolerancia_fechamento >= 0),
    operador_ve_saldo boolean NOT NULL DEFAULT true,
    atualizado_por integer REFERENCES usuario(id),
    atualizado_em timestamptz NOT NULL DEFAULT now()
);

ALTER TABLE gestao_caixa_dias
    ADD COLUMN IF NOT EXISTS envio_em timestamptz,
    ADD COLUMN IF NOT EXISTS envio_por integer REFERENCES usuario(id),
    ADD COLUMN IF NOT EXISTS envio jsonb,
    ADD COLUMN IF NOT EXISTS aprovado_por integer REFERENCES usuario(id),
    ADD COLUMN IF NOT EXISTS devolvido_em timestamptz,
    ADD COLUMN IF NOT EXISTS devolvido_por integer REFERENCES usuario(id),
    ADD COLUMN IF NOT EXISTS devolucao_motivo varchar(300);

CREATE INDEX IF NOT EXISTS ix_gestao_caixa_dias_aguardando ON gestao_caixa_dias (estabelecimento_id, dia)
    WHERE envio_em IS NOT NULL AND fechado_em IS NULL;

COMMIT;
