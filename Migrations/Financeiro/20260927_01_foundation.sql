-- Apply explicitly when enabling the store finance module. No historic sales are imported.
BEGIN;
CREATE TABLE IF NOT EXISTS financeiro_contas (
    id uuid PRIMARY KEY,
    estabelecimento_id uuid NOT NULL REFERENCES estabelecimentos(id),
    nome varchar(120) NOT NULL CHECK (length(trim(nome)) > 0),
    ativo boolean NOT NULL DEFAULT true,
    UNIQUE (estabelecimento_id, id)
);
CREATE TABLE IF NOT EXISTS financeiro_categorias (
    id uuid PRIMARY KEY,
    estabelecimento_id uuid NOT NULL REFERENCES estabelecimentos(id),
    nome varchar(120) NOT NULL CHECK (length(trim(nome)) > 0),
    tipo varchar(7) NOT NULL CHECK (tipo IN ('entrada', 'saida')),
    ativo boolean NOT NULL DEFAULT true,
    UNIQUE (estabelecimento_id, id, tipo)
);
CREATE TABLE IF NOT EXISTS financeiro_lancamentos (
    id uuid PRIMARY KEY,
    estabelecimento_id uuid NOT NULL REFERENCES estabelecimentos(id),
    conta_id uuid NOT NULL,
    categoria_id uuid NOT NULL,
    tipo varchar(7) NOT NULL CHECK (tipo IN ('entrada', 'saida')),
    descricao varchar(500) NOT NULL CHECK (length(trim(descricao)) > 0),
    valor numeric(15,2) NOT NULL CHECK (valor > 0),
    moeda char(3) NOT NULL DEFAULT 'BRL' CHECK (moeda = 'BRL'),
    competencia date NOT NULL,
    vencimento date NOT NULL,
    liquidado_em timestamptz,
    status varchar(10) NOT NULL DEFAULT 'pendente' CHECK (status IN ('pendente', 'liquidado', 'cancelado')),
    origem varchar(30) NOT NULL DEFAULT 'manual',
    referencia_externa varchar(200),
    criado_por integer NOT NULL REFERENCES usuario(id),
    criado_em timestamptz NOT NULL DEFAULT now(),
    atualizado_em timestamptz NOT NULL DEFAULT now(),
    FOREIGN KEY (estabelecimento_id, conta_id) REFERENCES financeiro_contas(estabelecimento_id, id),
    FOREIGN KEY (estabelecimento_id, categoria_id, tipo) REFERENCES financeiro_categorias(estabelecimento_id, id, tipo),
    CHECK ((status = 'liquidado') = (liquidado_em IS NOT NULL)),
    UNIQUE (estabelecimento_id, origem, referencia_externa)
);
CREATE INDEX IF NOT EXISTS ix_financeiro_loja_competencia ON financeiro_lancamentos(estabelecimento_id, competencia, status);
COMMIT;
