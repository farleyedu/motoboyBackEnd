-- Gestao, 6C2-C: listas do Caixa por estabelecimento (motivos de sangria, suprimento, pagamento e devolucao, e justificativas de
-- fechamento). So rotulos: o movimento e o fechamento guardam o TEXTO escolhido, entao renomear ou desativar uma opcao nunca
-- reescreve o que ja foi gravado. Nada se apaga (so ativo = false). Formas de pagamento (din, pix, deb, cre, ifood) NAO entram aqui.
-- Os padroes sao criados por loja na primeira consulta (padrao_chave preenchida). Requer 20261110_01_gestao_caixa.sql.
-- Aditiva e idempotente.
BEGIN;

CREATE TABLE IF NOT EXISTS gestao_caixa_listas (
    id uuid PRIMARY KEY,
    estabelecimento_id uuid NOT NULL REFERENCES estabelecimentos(id),
    grupo varchar(12) NOT NULL CHECK (grupo IN ('sang', 'sup', 'pag', 'ret', 'fechamento')),
    nome varchar(80) NOT NULL CHECK (length(trim(nome)) >= 2),
    padrao_chave varchar(40),
    chave varchar(100),
    ativo boolean NOT NULL DEFAULT true,
    ordem integer NOT NULL DEFAULT 0,
    criado_por integer REFERENCES usuario(id),
    criado_em timestamptz NOT NULL DEFAULT now(),
    atualizado_em timestamptz NOT NULL DEFAULT now()
);
-- Um nome por grupo e por loja (ativo ou nao): incluir um nome que ja existe reativa a opcao.
CREATE UNIQUE INDEX IF NOT EXISTS ux_caixa_listas_nome ON gestao_caixa_listas (estabelecimento_id, grupo, lower(trim(nome)));
CREATE UNIQUE INDEX IF NOT EXISTS ux_caixa_listas_padrao ON gestao_caixa_listas (estabelecimento_id, grupo, padrao_chave) WHERE padrao_chave IS NOT NULL;
CREATE UNIQUE INDEX IF NOT EXISTS ux_caixa_listas_chave ON gestao_caixa_listas (estabelecimento_id, chave) WHERE chave IS NOT NULL;
CREATE INDEX IF NOT EXISTS ix_caixa_listas_loja ON gestao_caixa_listas (estabelecimento_id, grupo, ordem);

COMMIT;
