BEGIN;

-- Mesas, P07 (CONTRATO-API §1.4): sessao da mesa (quem abriu separado de quem atende), juncao de mesas, pessoas na mesa,
-- comandas (coletiva e individual), chamados, solicitacoes ao gerente, cobertura entre garcons e anotacoes.
-- Requer 20261203_03_mesas_nucleo.sql (mesa_evento, mesa_comando), 20261205_05_mesas_planta.sql (mesa, mesa_estado)
-- e a Gestao (gestao_pessoas, gestao_bloqueia_exclusao). Aditiva e idempotente. Nada e apagado.

CREATE TABLE IF NOT EXISTS mesa_sessao (
    id                 UUID PRIMARY KEY,
    estabelecimento_id UUID NOT NULL REFERENCES estabelecimentos (id),
    -- Mesa principal (na juncao, a que ficou com a sessao).
    mesa_id            UUID NOT NULL REFERENCES mesa (id),
    estado             TEXT NOT NULL DEFAULT 'aberta',
    aberta_em          TIMESTAMPTZ NOT NULL DEFAULT NOW(),
    -- Quem abriu (pessoa logada) e quem atende (garcom) ficam separados: host abre, Rafa atende.
    aberta_por         TEXT NOT NULL CHECK (length(aberta_por) <= 80),
    aberta_por_id      TEXT NULL,
    garcom_id          UUID NULL REFERENCES gestao_pessoas (id),
    cobertura_de       UUID NULL,
    tipo_comanda       TEXT NOT NULL DEFAULT 'mesa',
    ocupantes_anonimos SMALLINT NOT NULL DEFAULT 0,
    conta_pedida_em    TIMESTAMPTZ NULL,
    -- P08 atualiza a cada pedido lancado; o alerta "sem pedido" conta a partir daqui (ou da abertura).
    ultimo_pedido_em   TIMESTAMPTZ NULL,
    encerrada_em       TIMESTAMPTZ NULL,
    encerrada_por      TEXT NULL CHECK (encerrada_por IS NULL OR length(encerrada_por) <= 80),
    reaberta_vezes     SMALLINT NOT NULL DEFAULT 0,
    -- Sessao que foi juntada em outra (juntarMesas): fica encerrada e aponta para a que continuou.
    unida_a            UUID NULL,
    reserva_id         UUID NULL,
    rodizio            JSONB NULL,
    rodizio_inicio     TIMESTAMPTZ NULL,
    rolhas             SMALLINT NOT NULL DEFAULT 0,
    pedido_id          UUID NULL,
    -- Token do QR da mesa: vale enquanto a sessao estiver aberta (P18 le por aqui).
    qr_token           TEXT NOT NULL CHECK (length(qr_token) >= 22),
    criado_em          TIMESTAMPTZ NOT NULL DEFAULT NOW(),
    UNIQUE (estabelecimento_id, id)
);
ALTER TABLE mesa_sessao DROP CONSTRAINT IF EXISTS ck_mesa_sessao_estado;
ALTER TABLE mesa_sessao ADD CONSTRAINT ck_mesa_sessao_estado CHECK (estado IN ('aberta', 'encerrada'));
ALTER TABLE mesa_sessao DROP CONSTRAINT IF EXISTS ck_mesa_sessao_tipo_comanda;
ALTER TABLE mesa_sessao ADD CONSTRAINT ck_mesa_sessao_tipo_comanda CHECK (tipo_comanda IN ('mesa', 'pessoa'));
ALTER TABLE mesa_sessao DROP CONSTRAINT IF EXISTS ck_mesa_sessao_numeros;
ALTER TABLE mesa_sessao ADD CONSTRAINT ck_mesa_sessao_numeros CHECK (ocupantes_anonimos BETWEEN 0 AND 200 AND reaberta_vezes >= 0 AND rolhas >= 0);
ALTER TABLE mesa_sessao DROP CONSTRAINT IF EXISTS ck_mesa_sessao_encerrada;
ALTER TABLE mesa_sessao ADD CONSTRAINT ck_mesa_sessao_encerrada CHECK ((estado = 'encerrada') = (encerrada_em IS NOT NULL));
CREATE UNIQUE INDEX IF NOT EXISTS ux_mesa_sessao_aberta ON mesa_sessao (mesa_id) WHERE estado = 'aberta';
CREATE UNIQUE INDEX IF NOT EXISTS ux_mesa_sessao_qr ON mesa_sessao (qr_token);
CREATE INDEX IF NOT EXISTS ix_mesa_sessao_loja ON mesa_sessao (estabelecimento_id, estado);
CREATE INDEX IF NOT EXISTS ix_mesa_sessao_mesa ON mesa_sessao (estabelecimento_id, mesa_id, aberta_em DESC);
CREATE INDEX IF NOT EXISTS ix_mesa_sessao_garcom ON mesa_sessao (estabelecimento_id, garcom_id) WHERE estado = 'aberta';

-- Mesas da sessao (a principal e as juntadas). ate = NULL enquanto a mesa estiver na sessao.
CREATE TABLE IF NOT EXISTS mesa_sessao_mesa (
    id                 UUID PRIMARY KEY,
    estabelecimento_id UUID NOT NULL REFERENCES estabelecimentos (id),
    sessao_id          UUID NOT NULL REFERENCES mesa_sessao (id),
    mesa_id            UUID NOT NULL REFERENCES mesa (id),
    desde              TIMESTAMPTZ NOT NULL DEFAULT NOW(),
    ate                TIMESTAMPTZ NULL,
    criado_em          TIMESTAMPTZ NOT NULL DEFAULT NOW()
);
CREATE UNIQUE INDEX IF NOT EXISTS ux_mesa_sessao_mesa_ativa ON mesa_sessao_mesa (mesa_id) WHERE ate IS NULL;
CREATE INDEX IF NOT EXISTS ix_mesa_sessao_mesa_sessao ON mesa_sessao_mesa (sessao_id);

CREATE TABLE IF NOT EXISTS mesa_pessoa (
    id                 UUID PRIMARY KEY,
    estabelecimento_id UUID NOT NULL REFERENCES estabelecimentos (id),
    sessao_id          UUID NOT NULL REFERENCES mesa_sessao (id),
    nome               TEXT NULL CHECK (nome IS NULL OR length(nome) <= 80),
    celular            TEXT NULL CHECK (celular IS NULL OR celular ~ '^\+[1-9][0-9]{7,14}$'),
    cadeira_id         TEXT NULL CHECK (cadeira_id IS NULL OR length(cadeira_id) <= 120),
    origem             TEXT NOT NULL DEFAULT 'garcom',
    entrou_em          TIMESTAMPTZ NOT NULL DEFAULT NOW(),
    saiu_em            TIMESTAMPTZ NULL,
    cor                TEXT NOT NULL CHECK (length(cor) <= 20),
    alergia            TEXT NULL CHECK (alergia IS NULL OR length(alergia) <= 200),
    consentimento_em   TIMESTAMPTZ NULL,
    titular            BOOLEAN NOT NULL DEFAULT FALSE,
    criado_em          TIMESTAMPTZ NOT NULL DEFAULT NOW()
);
ALTER TABLE mesa_pessoa DROP CONSTRAINT IF EXISTS ck_mesa_pessoa_origem;
ALTER TABLE mesa_pessoa ADD CONSTRAINT ck_mesa_pessoa_origem CHECK (origem IN ('garcom', 'qr', 'reserva'));
CREATE UNIQUE INDEX IF NOT EXISTS ux_mesa_pessoa_cadeira ON mesa_pessoa (sessao_id, cadeira_id)
    WHERE saiu_em IS NULL AND cadeira_id IS NOT NULL;
CREATE INDEX IF NOT EXISTS ix_mesa_pessoa_sessao ON mesa_pessoa (sessao_id);

CREATE TABLE IF NOT EXISTS mesa_comanda (
    id                 UUID PRIMARY KEY,
    estabelecimento_id UUID NOT NULL REFERENCES estabelecimentos (id),
    sessao_id          UUID NOT NULL REFERENCES mesa_sessao (id),
    tipo               TEXT NOT NULL,
    pessoa_id          UUID NULL REFERENCES mesa_pessoa (id),
    nome               TEXT NOT NULL CHECK (length(nome) <= 80),
    ordem              SMALLINT NOT NULL DEFAULT 1,
    aberta             BOOLEAN NOT NULL DEFAULT TRUE,
    fechada_em         TIMESTAMPTZ NULL,
    fechada_por        TEXT NULL CHECK (fechada_por IS NULL OR length(fechada_por) <= 80),
    -- P14: subtotal, desconto, taxa, total, compartilhados. Comanda com fechamento = paga (reabrir a mesa nao a reabre).
    fechamento         JSONB NULL,
    cpf_cnpj           TEXT NULL CHECK (cpf_cnpj IS NULL OR cpf_cnpj ~ '^[0-9]{11}([0-9]{3})?$'),
    avaliada           BOOLEAN NOT NULL DEFAULT FALSE,
    criado_em          TIMESTAMPTZ NOT NULL DEFAULT NOW()
);
ALTER TABLE mesa_comanda DROP CONSTRAINT IF EXISTS ck_mesa_comanda_tipo;
ALTER TABLE mesa_comanda ADD CONSTRAINT ck_mesa_comanda_tipo CHECK (tipo IN ('coletiva', 'individual'));
ALTER TABLE mesa_comanda DROP CONSTRAINT IF EXISTS ck_mesa_comanda_pessoa;
ALTER TABLE mesa_comanda ADD CONSTRAINT ck_mesa_comanda_pessoa CHECK ((tipo = 'individual') = (pessoa_id IS NOT NULL));
ALTER TABLE mesa_comanda DROP CONSTRAINT IF EXISTS ck_mesa_comanda_fechada;
ALTER TABLE mesa_comanda ADD CONSTRAINT ck_mesa_comanda_fechada CHECK (aberta = (fechada_em IS NULL));
CREATE UNIQUE INDEX IF NOT EXISTS ux_mesa_comanda_coletiva ON mesa_comanda (sessao_id) WHERE tipo = 'coletiva';
CREATE UNIQUE INDEX IF NOT EXISTS ux_mesa_comanda_pessoa_aberta ON mesa_comanda (pessoa_id) WHERE aberta AND pessoa_id IS NOT NULL;
CREATE INDEX IF NOT EXISTS ix_mesa_comanda_sessao ON mesa_comanda (sessao_id);

CREATE TABLE IF NOT EXISTS mesa_chamado (
    id                 UUID PRIMARY KEY,
    estabelecimento_id UUID NOT NULL REFERENCES estabelecimentos (id),
    sessao_id          UUID NOT NULL REFERENCES mesa_sessao (id),
    pessoa_id          UUID NULL REFERENCES mesa_pessoa (id),
    tipo               TEXT NOT NULL DEFAULT 'garcom',
    motivo             TEXT NULL CHECK (motivo IS NULL OR length(motivo) <= 140),
    estado             TEXT NOT NULL DEFAULT 'aberto',
    garcom_id          UUID NULL REFERENCES gestao_pessoas (id),
    a_caminho_em       TIMESTAMPTZ NULL,
    a_caminho_por      TEXT NULL,
    atendido_em        TIMESTAMPTZ NULL,
    atendido_por       TEXT NULL,
    item_id            UUID NULL,
    criado_por         TEXT NULL,
    criado_em          TIMESTAMPTZ NOT NULL DEFAULT NOW()
);
ALTER TABLE mesa_chamado DROP CONSTRAINT IF EXISTS ck_mesa_chamado_tipo;
ALTER TABLE mesa_chamado ADD CONSTRAINT ck_mesa_chamado_tipo CHECK (tipo IN ('garcom', 'conta', 'contestacao', 'outro'));
ALTER TABLE mesa_chamado DROP CONSTRAINT IF EXISTS ck_mesa_chamado_estado;
ALTER TABLE mesa_chamado ADD CONSTRAINT ck_mesa_chamado_estado CHECK (estado IN ('aberto', 'a_caminho', 'atendido', 'cancelado'));
CREATE INDEX IF NOT EXISTS ix_mesa_chamado_loja ON mesa_chamado (estabelecimento_id, estado);
CREATE INDEX IF NOT EXISTS ix_mesa_chamado_sessao ON mesa_chamado (sessao_id) WHERE estado IN ('aberto', 'a_caminho');

CREATE TABLE IF NOT EXISTS mesa_solicitacao (
    id                 UUID PRIMARY KEY,
    estabelecimento_id UUID NOT NULL REFERENCES estabelecimentos (id),
    tipo               TEXT NOT NULL CHECK (length(tipo) BETWEEN 1 AND 80),
    mesa_id            UUID NULL REFERENCES mesa (id),
    sessao_id          UUID NULL REFERENCES mesa_sessao (id),
    item_id            UUID NULL,
    detalhe            TEXT NULL CHECK (detalhe IS NULL OR length(detalhe) <= 280),
    acao               JSONB NULL,
    pedido_por         TEXT NOT NULL,
    pedido_por_nome    TEXT NULL,
    estado             TEXT NOT NULL DEFAULT 'pendente',
    respondida_por     TEXT NULL,
    respondida_em      TIMESTAMPTZ NULL,
    motivo             TEXT NULL CHECK (motivo IS NULL OR length(motivo) <= 280),
    resultado          JSONB NULL,
    comando_chave      UUID NULL,
    criado_em          TIMESTAMPTZ NOT NULL DEFAULT NOW()
);
ALTER TABLE mesa_solicitacao DROP CONSTRAINT IF EXISTS ck_mesa_solicitacao_estado;
ALTER TABLE mesa_solicitacao ADD CONSTRAINT ck_mesa_solicitacao_estado CHECK (estado IN ('pendente', 'aprovada', 'recusada', 'cancelada'));
CREATE INDEX IF NOT EXISTS ix_mesa_solicitacao_loja ON mesa_solicitacao (estabelecimento_id, estado);

CREATE TABLE IF NOT EXISTS mesa_cobertura (
    id                 UUID PRIMARY KEY,
    estabelecimento_id UUID NOT NULL REFERENCES estabelecimentos (id),
    mesa_ids           UUID[] NOT NULL,
    de_garcom_id       UUID NOT NULL REFERENCES gestao_pessoas (id),
    para_garcom_id     UUID NOT NULL REFERENCES gestao_pessoas (id),
    estado             TEXT NOT NULL DEFAULT 'pendente',
    minutos            SMALLINT NULL CHECK (minutos IS NULL OR minutos BETWEEN 1 AND 600),
    aceita_em          TIMESTAMPTZ NULL,
    devolvida_em       TIMESTAMPTZ NULL,
    criado_em          TIMESTAMPTZ NOT NULL DEFAULT NOW()
);
ALTER TABLE mesa_cobertura DROP CONSTRAINT IF EXISTS ck_mesa_cobertura_estado;
ALTER TABLE mesa_cobertura ADD CONSTRAINT ck_mesa_cobertura_estado CHECK (estado IN ('pendente', 'ativa', 'devolvida', 'recusada'));
CREATE INDEX IF NOT EXISTS ix_mesa_cobertura_loja ON mesa_cobertura (estabelecimento_id, estado);

CREATE TABLE IF NOT EXISTS mesa_anotacao (
    id                 UUID PRIMARY KEY,
    estabelecimento_id UUID NOT NULL REFERENCES estabelecimentos (id),
    sessao_id          UUID NOT NULL REFERENCES mesa_sessao (id),
    tipo               TEXT NOT NULL,
    por                TEXT NOT NULL CHECK (length(por) <= 80),
    texto              TEXT NULL CHECK (texto IS NULL OR length(texto) <= 200),
    criado_em          TIMESTAMPTZ NOT NULL DEFAULT NOW()
);
ALTER TABLE mesa_anotacao DROP CONSTRAINT IF EXISTS ck_mesa_anotacao_tipo;
ALTER TABLE mesa_anotacao ADD CONSTRAINT ck_mesa_anotacao_tipo CHECK (tipo IN ('demora', 'comida', 'atendimento', 'elogio'));
CREATE INDEX IF NOT EXISTS ix_mesa_anotacao_sessao ON mesa_anotacao (sessao_id);

-- Nada disso some: sessao, pessoa e comanda sao referencia de pedido, pagamento e auditoria.
DROP TRIGGER IF EXISTS mesa_sessao_sem_delete ON mesa_sessao;
CREATE TRIGGER mesa_sessao_sem_delete BEFORE DELETE ON mesa_sessao FOR EACH ROW EXECUTE FUNCTION gestao_bloqueia_exclusao();
DROP TRIGGER IF EXISTS mesa_sessao_mesa_sem_delete ON mesa_sessao_mesa;
CREATE TRIGGER mesa_sessao_mesa_sem_delete BEFORE DELETE ON mesa_sessao_mesa FOR EACH ROW EXECUTE FUNCTION gestao_bloqueia_exclusao();
DROP TRIGGER IF EXISTS mesa_pessoa_sem_delete ON mesa_pessoa;
CREATE TRIGGER mesa_pessoa_sem_delete BEFORE DELETE ON mesa_pessoa FOR EACH ROW EXECUTE FUNCTION gestao_bloqueia_exclusao();
DROP TRIGGER IF EXISTS mesa_comanda_sem_delete ON mesa_comanda;
CREATE TRIGGER mesa_comanda_sem_delete BEFORE DELETE ON mesa_comanda FOR EACH ROW EXECUTE FUNCTION gestao_bloqueia_exclusao();
DROP TRIGGER IF EXISTS mesa_chamado_sem_delete ON mesa_chamado;
CREATE TRIGGER mesa_chamado_sem_delete BEFORE DELETE ON mesa_chamado FOR EACH ROW EXECUTE FUNCTION gestao_bloqueia_exclusao();
DROP TRIGGER IF EXISTS mesa_solicitacao_sem_delete ON mesa_solicitacao;
CREATE TRIGGER mesa_solicitacao_sem_delete BEFORE DELETE ON mesa_solicitacao FOR EACH ROW EXECUTE FUNCTION gestao_bloqueia_exclusao();
DROP TRIGGER IF EXISTS mesa_cobertura_sem_delete ON mesa_cobertura;
CREATE TRIGGER mesa_cobertura_sem_delete BEFORE DELETE ON mesa_cobertura FOR EACH ROW EXECUTE FUNCTION gestao_bloqueia_exclusao();
DROP TRIGGER IF EXISTS mesa_anotacao_sem_delete ON mesa_anotacao;
CREATE TRIGGER mesa_anotacao_sem_delete BEFORE DELETE ON mesa_anotacao FOR EACH ROW EXECUTE FUNCTION gestao_bloqueia_exclusao();

INSERT INTO delivery_tracking_schema_versions (version)
VALUES ('20261206_01_mesas_sessao')
ON CONFLICT (version) DO NOTHING;

COMMIT;
