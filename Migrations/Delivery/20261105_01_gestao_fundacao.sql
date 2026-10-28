-- Gestao (Financeiro, Caixa, Compras, Estoque, Equipe, Despesas): fundacao (docs/zippy-gestao/CONTRATOS.md).
-- Um livro unico de acontecimentos. Cada acontecimento gera, quando couber, lancamentos financeiros e/ou movimentos
-- de estoque que apontam para ele. Nada se apaga e nada se reescreve: os gatilhos abaixo recusam UPDATE, DELETE e
-- TRUNCATE. Correcao = novo acontecimento (estorno ou ajuste) ligado ao original. Mes fechado so aceita correcao.
-- Aditiva e idempotente: nao toca nas tabelas financeiro_* antigas (20260927, nunca usadas pelo codigo).
BEGIN;

-- Contas/carteiras da loja: banco, caixa, recebiveis (iFood, maquininha) e cartao da empresa (com fatura).
-- Configuracao, nao movimento: nome e ativo podem mudar; conta nunca e apagada.
CREATE TABLE IF NOT EXISTS gestao_contas (
    id uuid PRIMARY KEY,
    estabelecimento_id uuid NOT NULL REFERENCES estabelecimentos(id),
    tipo varchar(20) NOT NULL CHECK (tipo IN ('banco', 'caixa', 'recebivel', 'cartao_empresa')),
    subtipo varchar(20) CHECK (subtipo IN ('ifood', 'maquininha', 'outro')),
    nome varchar(80) NOT NULL CHECK (length(trim(nome)) > 0),
    dia_fechamento smallint CHECK (dia_fechamento BETWEEN 1 AND 31),
    dia_vencimento smallint CHECK (dia_vencimento BETWEEN 1 AND 31),
    ativo boolean NOT NULL DEFAULT true,
    chave_idempotencia varchar(160),
    criado_por integer REFERENCES usuario(id),
    criado_em timestamptz NOT NULL DEFAULT now(),
    UNIQUE (estabelecimento_id, id),
    CHECK ((tipo = 'recebivel') = (subtipo IS NOT NULL)),
    CHECK ((tipo = 'cartao_empresa') = (dia_fechamento IS NOT NULL AND dia_vencimento IS NOT NULL))
);
CREATE UNIQUE INDEX IF NOT EXISTS ux_gestao_contas_idempotencia
    ON gestao_contas (estabelecimento_id, chave_idempotencia) WHERE chave_idempotencia IS NOT NULL;

CREATE TABLE IF NOT EXISTS gestao_acontecimentos (
    id uuid PRIMARY KEY,
    estabelecimento_id uuid NOT NULL REFERENCES estabelecimentos(id),
    tipo varchar(40) NOT NULL CHECK (tipo ~ '^[a-z][a-z_]*$'),
    modulo varchar(20) NOT NULL CHECK (modulo IN
        ('financeiro', 'caixa', 'compras', 'estoque', 'equipe', 'despesas', 'pedidos', 'curadoria', 'sistema')),
    data_fato timestamptz NOT NULL,
    registrado_em timestamptz NOT NULL DEFAULT now(),
    mes_referencia date NOT NULL CHECK (extract(day FROM mes_referencia) = 1),
    responsavel_id integer REFERENCES usuario(id),
    origem_tipo varchar(40),
    origem_id varchar(120),
    documento_chave varchar(200),
    descricao varchar(300) NOT NULL CHECK (length(trim(descricao)) > 0),
    dados jsonb NOT NULL DEFAULT '{}'::jsonb,
    corrige_id uuid,
    correcao varchar(10) CHECK (correcao IN ('estorno', 'ajuste')),
    motivo varchar(500),
    valor_anterior numeric(15,2),
    valor_novo numeric(15,2),
    chave_idempotencia varchar(160) NOT NULL CHECK (length(trim(chave_idempotencia)) > 0),
    hash_conteudo char(64) NOT NULL,
    UNIQUE (estabelecimento_id, id),
    UNIQUE (estabelecimento_id, chave_idempotencia),
    FOREIGN KEY (estabelecimento_id, corrige_id) REFERENCES gestao_acontecimentos (estabelecimento_id, id),
    CHECK ((origem_tipo IS NULL) = (origem_id IS NULL)),
    CHECK ((corrige_id IS NULL) = (correcao IS NULL)),
    CHECK (corrige_id IS NULL OR corrige_id <> id),
    -- Toda correcao tem responsavel humano e motivo.
    CHECK (correcao IS NULL OR (responsavel_id IS NOT NULL AND length(trim(coalesce(motivo, ''))) >= 3))
);
-- Nunca contabilizar duas vezes: a mesma origem nao gera dois acontecimentos do mesmo tipo... (pagamento fica de fora:
-- depois de estornado pode ser refeito; a trava dele e "lancamento ja liquidado", conferida no servico sob lock)
CREATE UNIQUE INDEX IF NOT EXISTS ux_gestao_acontecimentos_origem
    ON gestao_acontecimentos (estabelecimento_id, tipo, origem_tipo, origem_id)
    WHERE origem_id IS NOT NULL AND corrige_id IS NULL AND tipo <> 'pagamento';
-- ...o mesmo documento (cupom, NFC-e, boleto) nao entra duas vezes...
CREATE UNIQUE INDEX IF NOT EXISTS ux_gestao_acontecimentos_documento
    ON gestao_acontecimentos (estabelecimento_id, documento_chave)
    WHERE documento_chave IS NOT NULL AND corrige_id IS NULL;
-- ...e um acontecimento so pode ser estornado uma vez.
CREATE UNIQUE INDEX IF NOT EXISTS ux_gestao_acontecimentos_estorno
    ON gestao_acontecimentos (estabelecimento_id, corrige_id) WHERE correcao = 'estorno';
CREATE INDEX IF NOT EXISTS ix_gestao_acontecimentos_fato
    ON gestao_acontecimentos (estabelecimento_id, data_fato DESC, registrado_em DESC);
CREATE INDEX IF NOT EXISTS ix_gestao_acontecimentos_mes
    ON gestao_acontecimentos (estabelecimento_id, mes_referencia);
CREATE INDEX IF NOT EXISTS ix_gestao_acontecimentos_origem_busca
    ON gestao_acontecimentos (estabelecimento_id, origem_tipo, origem_id);

-- Lancamento financeiro. valor com sinal: positivo no sentido da natureza; negativo so em correcao.
-- status e o do momento do registro; pagamento/recebimento posterior vira gestao_liquidacoes (ver a view).
CREATE TABLE IF NOT EXISTS gestao_lancamentos (
    id uuid PRIMARY KEY,
    estabelecimento_id uuid NOT NULL,
    acontecimento_id uuid NOT NULL,
    linha smallint NOT NULL CHECK (linha > 0),
    conta_id uuid,
    natureza varchar(15) NOT NULL CHECK (natureza IN ('receita', 'despesa', 'transferencia', 'aporte', 'retirada')),
    sentido varchar(7) NOT NULL CHECK (sentido IN ('entrada', 'saida')),
    valor numeric(15,2) NOT NULL CHECK (valor <> 0),
    status varchar(10) NOT NULL CHECK (status IN ('previsto', 'a_pagar', 'pago', 'a_receber', 'recebido')),
    mes_referencia date NOT NULL CHECK (extract(day FROM mes_referencia) = 1),
    data_vencimento date,
    data_pagamento date,
    parcela smallint NOT NULL DEFAULT 1,
    parcelas smallint NOT NULL DEFAULT 1,
    fatura_mes date CHECK (fatura_mes IS NULL OR extract(day FROM fatura_mes) = 1),
    categoria varchar(60),
    finalidade varchar(60),
    descricao varchar(300),
    corrige_lancamento_id uuid,
    UNIQUE (estabelecimento_id, id),
    UNIQUE (estabelecimento_id, acontecimento_id, linha),
    FOREIGN KEY (estabelecimento_id, acontecimento_id) REFERENCES gestao_acontecimentos (estabelecimento_id, id),
    FOREIGN KEY (estabelecimento_id, conta_id) REFERENCES gestao_contas (estabelecimento_id, id),
    FOREIGN KEY (estabelecimento_id, corrige_lancamento_id) REFERENCES gestao_lancamentos (estabelecimento_id, id),
    CHECK (parcelas BETWEEN 1 AND 48 AND parcela BETWEEN 1 AND parcelas),
    CHECK ((sentido = 'entrada' AND status IN ('previsto', 'a_receber', 'recebido'))
        OR (sentido = 'saida' AND status IN ('previsto', 'a_pagar', 'pago'))),
    CHECK ((status IN ('pago', 'recebido')) = (data_pagamento IS NOT NULL)),
    CHECK (natureza <> 'receita' OR sentido = 'entrada'),
    CHECK (natureza <> 'despesa' OR sentido = 'saida'),
    CHECK (natureza <> 'aporte' OR sentido = 'entrada'),
    CHECK (natureza <> 'retirada' OR sentido = 'saida'),
    CHECK (natureza <> 'transferencia' OR (conta_id IS NOT NULL AND status IN ('pago', 'recebido'))),
    CHECK (valor > 0 OR corrige_lancamento_id IS NOT NULL)
);
CREATE INDEX IF NOT EXISTS ix_gestao_lancamentos_mes
    ON gestao_lancamentos (estabelecimento_id, mes_referencia, natureza);
CREATE INDEX IF NOT EXISTS ix_gestao_lancamentos_vencimento
    ON gestao_lancamentos (estabelecimento_id, data_vencimento) WHERE data_vencimento IS NOT NULL;
CREATE INDEX IF NOT EXISTS ix_gestao_lancamentos_pagamento
    ON gestao_lancamentos (estabelecimento_id, data_pagamento) WHERE data_pagamento IS NOT NULL;
CREATE INDEX IF NOT EXISTS ix_gestao_lancamentos_corrige
    ON gestao_lancamentos (estabelecimento_id, corrige_lancamento_id) WHERE corrige_lancamento_id IS NOT NULL;

-- Pagamento/recebimento de um lancamento que nasceu a pagar/a receber. Estorno = linha negativa ligada a original.
CREATE TABLE IF NOT EXISTS gestao_liquidacoes (
    id uuid PRIMARY KEY,
    estabelecimento_id uuid NOT NULL,
    acontecimento_id uuid NOT NULL,
    lancamento_id uuid NOT NULL,
    conta_id uuid NOT NULL,
    valor numeric(15,2) NOT NULL CHECK (valor <> 0),
    data_pagamento date NOT NULL,
    corrige_liquidacao_id uuid,
    UNIQUE (estabelecimento_id, id),
    UNIQUE (estabelecimento_id, acontecimento_id, lancamento_id),
    FOREIGN KEY (estabelecimento_id, acontecimento_id) REFERENCES gestao_acontecimentos (estabelecimento_id, id),
    FOREIGN KEY (estabelecimento_id, lancamento_id) REFERENCES gestao_lancamentos (estabelecimento_id, id),
    FOREIGN KEY (estabelecimento_id, conta_id) REFERENCES gestao_contas (estabelecimento_id, id),
    -- Mesmo sinal do lancamento quitado; o estorno da liquidacao tem o sinal oposto e aponta a original.
    FOREIGN KEY (estabelecimento_id, corrige_liquidacao_id) REFERENCES gestao_liquidacoes (estabelecimento_id, id)
);
CREATE INDEX IF NOT EXISTS ix_gestao_liquidacoes_lancamento ON gestao_liquidacoes (estabelecimento_id, lancamento_id);
CREATE INDEX IF NOT EXISTS ix_gestao_liquidacoes_data ON gestao_liquidacoes (estabelecimento_id, data_pagamento);

-- Movimento de estoque, sempre em unidade base (g, ml, un) e sempre confirmado por uma pessoa.
CREATE TABLE IF NOT EXISTS gestao_movimentos_estoque (
    id uuid PRIMARY KEY,
    estabelecimento_id uuid NOT NULL,
    acontecimento_id uuid NOT NULL,
    linha smallint NOT NULL CHECK (linha > 0),
    item_id uuid NOT NULL,
    item_nome varchar(120) NOT NULL CHECK (length(trim(item_nome)) > 0),
    quantidade numeric(18,4) NOT NULL CHECK (quantidade <> 0),
    unidade varchar(2) NOT NULL CHECK (unidade IN ('g', 'ml', 'un')),
    tipo varchar(12) NOT NULL CHECK (tipo IN ('entrada', 'saida', 'perda', 'divergencia', 'ajuste', 'estorno')),
    motivo varchar(300),
    mes_referencia date NOT NULL CHECK (extract(day FROM mes_referencia) = 1),
    confirmado_por integer NOT NULL REFERENCES usuario(id),
    confirmado_em timestamptz NOT NULL,
    corrige_movimento_id uuid,
    UNIQUE (estabelecimento_id, id),
    UNIQUE (estabelecimento_id, acontecimento_id, linha),
    FOREIGN KEY (estabelecimento_id, acontecimento_id) REFERENCES gestao_acontecimentos (estabelecimento_id, id),
    FOREIGN KEY (estabelecimento_id, corrige_movimento_id) REFERENCES gestao_movimentos_estoque (estabelecimento_id, id),
    CHECK (tipo <> 'entrada' OR quantidade > 0),
    CHECK (tipo NOT IN ('saida', 'perda') OR quantidade < 0),
    CHECK (tipo NOT IN ('perda', 'divergencia', 'ajuste', 'estorno') OR length(trim(coalesce(motivo, ''))) > 0),
    CHECK ((tipo = 'estorno') = (corrige_movimento_id IS NOT NULL) OR tipo = 'ajuste')
);
CREATE INDEX IF NOT EXISTS ix_gestao_movimentos_item ON gestao_movimentos_estoque (estabelecimento_id, item_id, confirmado_em);
CREATE INDEX IF NOT EXISTS ix_gestao_movimentos_mes ON gestao_movimentos_estoque (estabelecimento_id, mes_referencia);

-- Fechamento de mes: definitivo (nada se apaga). Depois dele, o mes so recebe correcao (estorno/ajuste).
CREATE TABLE IF NOT EXISTS gestao_meses_fechados (
    estabelecimento_id uuid NOT NULL REFERENCES estabelecimentos(id),
    mes date NOT NULL CHECK (extract(day FROM mes) = 1),
    acontecimento_id uuid NOT NULL,
    fechado_por integer NOT NULL REFERENCES usuario(id),
    fechado_em timestamptz NOT NULL DEFAULT now(),
    PRIMARY KEY (estabelecimento_id, mes),
    FOREIGN KEY (estabelecimento_id, acontecimento_id) REFERENCES gestao_acontecimentos (estabelecimento_id, id)
);

-- Situacao atual de cada lancamento (status de registro + liquidacoes ativas).
CREATE OR REPLACE VIEW gestao_lancamentos_situacao AS
SELECT l.id, l.estabelecimento_id, l.acontecimento_id, l.linha, l.conta_id, l.natureza, l.sentido, l.valor,
       l.status AS status_registro, l.mes_referencia, l.data_vencimento, l.parcela, l.parcelas, l.fatura_mes,
       l.categoria, l.finalidade, l.descricao, l.corrige_lancamento_id,
       coalesce(q.total, 0)::numeric(15,2) AS liquidado,
       CASE WHEN l.status IN ('pago', 'recebido') THEN l.status
            WHEN coalesce(q.total, 0) <> 0 THEN CASE l.sentido WHEN 'entrada' THEN 'recebido' ELSE 'pago' END
            ELSE l.status END AS status,
       CASE WHEN l.status IN ('pago', 'recebido') THEN l.data_pagamento
            WHEN coalesce(q.total, 0) <> 0 THEN q.ultima_data END AS data_pagamento,
       CASE WHEN l.status IN ('pago', 'recebido') THEN l.conta_id
            WHEN coalesce(q.total, 0) <> 0 THEN q.ultima_conta ELSE l.conta_id END AS conta_pagamento_id
  FROM gestao_lancamentos l
  LEFT JOIN LATERAL (
      SELECT sum(x.valor) AS total,
             (array_agg(x.data_pagamento ORDER BY x.data_pagamento DESC) FILTER (WHERE x.corrige_liquidacao_id IS NULL))[1] AS ultima_data,
             (array_agg(x.conta_id ORDER BY x.data_pagamento DESC) FILTER (WHERE x.corrige_liquidacao_id IS NULL))[1] AS ultima_conta
        FROM gestao_liquidacoes x
       WHERE x.estabelecimento_id = l.estabelecimento_id AND x.lancamento_id = l.id
  ) q ON true;

-- Nada se apaga, nada se reescreve.
CREATE OR REPLACE FUNCTION gestao_bloqueia_alteracao() RETURNS trigger LANGUAGE plpgsql AS $$
BEGIN
    RAISE EXCEPTION 'gestao: % em % nao e permitido; registre um ajuste ou estorno', TG_OP, TG_TABLE_NAME
        USING ERRCODE = 'P0001';
END $$;

-- Mes fechado so aceita correcao. Defesa no banco alem da regra no servico.
CREATE OR REPLACE FUNCTION gestao_bloqueia_mes_fechado() RETURNS trigger LANGUAGE plpgsql AS $$
DECLARE
    v_mes date;
    v_correcao varchar(10);
BEGIN
    IF TG_TABLE_NAME = 'gestao_liquidacoes' THEN
        v_mes := date_trunc('month', NEW.data_pagamento)::date;
    ELSE
        v_mes := NEW.mes_referencia;
    END IF;
    IF EXISTS (SELECT 1 FROM gestao_meses_fechados f WHERE f.estabelecimento_id = NEW.estabelecimento_id AND f.mes = v_mes) THEN
        SELECT a.correcao INTO v_correcao FROM gestao_acontecimentos a
         WHERE a.estabelecimento_id = NEW.estabelecimento_id AND a.id = NEW.acontecimento_id;
        IF v_correcao IS NULL THEN
            RAISE EXCEPTION 'gestao: mes % fechado; so aceita ajuste ou estorno', to_char(v_mes, 'MM/YYYY')
                USING ERRCODE = 'P0002';
        END IF;
    END IF;
    RETURN NEW;
END $$;

DO $$
DECLARE t text;
BEGIN
    FOREACH t IN ARRAY ARRAY['gestao_acontecimentos', 'gestao_lancamentos', 'gestao_liquidacoes',
                             'gestao_movimentos_estoque', 'gestao_meses_fechados'] LOOP
        EXECUTE format('DROP TRIGGER IF EXISTS %I ON %I', t || '_imutavel', t);
        EXECUTE format('CREATE TRIGGER %I BEFORE UPDATE OR DELETE ON %I FOR EACH ROW EXECUTE FUNCTION gestao_bloqueia_alteracao()',
                       t || '_imutavel', t);
        EXECUTE format('DROP TRIGGER IF EXISTS %I ON %I', t || '_sem_truncate', t);
        EXECUTE format('CREATE TRIGGER %I BEFORE TRUNCATE ON %I FOR EACH STATEMENT EXECUTE FUNCTION gestao_bloqueia_alteracao()',
                       t || '_sem_truncate', t);
    END LOOP;
    FOREACH t IN ARRAY ARRAY['gestao_lancamentos', 'gestao_liquidacoes', 'gestao_movimentos_estoque'] LOOP
        EXECUTE format('DROP TRIGGER IF EXISTS %I ON %I', t || '_mes_fechado', t);
        EXECUTE format('CREATE TRIGGER %I BEFORE INSERT ON %I FOR EACH ROW EXECUTE FUNCTION gestao_bloqueia_mes_fechado()',
                       t || '_mes_fechado', t);
    END LOOP;
END $$;

COMMIT;
