BEGIN;

-- Mesas, P05 (CONTRATO-API §1.3): planta do salao (documento com versoes), cadastro de mesas derivado ao publicar,
-- andares e areas, e o estado vivo de cada mesa. A faixa 20261205_01..04 ficou com o Assistente; P05 usa _05.._08.
-- Requer 20261203_03_mesas_nucleo.sql (mesa_evento) e as migracoes da Gestao (gestao_bloqueia_exclusao). Aditiva e idempotente.
-- Nada e apagado: mesa que sai da planta fica ativa = false; versao publicada antiga vira 'arquivada'.

CREATE TABLE IF NOT EXISTS mesa_planta (
    id                 UUID PRIMARY KEY,
    estabelecimento_id UUID NOT NULL REFERENCES estabelecimentos (id),
    -- Numero da versao da planta na loja (1, 2, 3...). O rascunho ja nasce com o proximo numero.
    versao             INTEGER NOT NULL,
    -- Contador do rascunho para a versao otimista (sobe a cada salvar).
    revisao            INTEGER NOT NULL DEFAULT 1,
    estado             TEXT NOT NULL,
    -- Formato do prototipo (ZM.dados): { pisos:[{id,nome,nivel,largura,altura,paredeEsp,contorno,setores,elementos}], mesas:[...] }, em cm.
    documento          JSONB NOT NULL,
    salvo_por          INTEGER NULL,
    atualizado_em      TIMESTAMPTZ NOT NULL DEFAULT NOW(),
    publicado_em       TIMESTAMPTZ NULL,
    publicado_por      INTEGER NULL,
    criado_em          TIMESTAMPTZ NOT NULL DEFAULT NOW()
);
ALTER TABLE mesa_planta DROP CONSTRAINT IF EXISTS ck_mesa_planta_estado;
-- 'descartada' (acrescimo do P05 ao contrato): rascunho jogado fora fica guardado, como tudo no Mesas.
ALTER TABLE mesa_planta ADD CONSTRAINT ck_mesa_planta_estado CHECK (estado IN ('rascunho', 'publicada', 'arquivada', 'descartada'));
ALTER TABLE mesa_planta DROP CONSTRAINT IF EXISTS ck_mesa_planta_versao;
ALTER TABLE mesa_planta ADD CONSTRAINT ck_mesa_planta_versao CHECK (versao > 0 AND revisao > 0);
ALTER TABLE mesa_planta DROP CONSTRAINT IF EXISTS ck_mesa_planta_publicada;
ALTER TABLE mesa_planta ADD CONSTRAINT ck_mesa_planta_publicada CHECK (estado IN ('rascunho', 'descartada') OR publicado_em IS NOT NULL);
CREATE UNIQUE INDEX IF NOT EXISTS ux_mesa_planta_versao ON mesa_planta (estabelecimento_id, versao);
CREATE UNIQUE INDEX IF NOT EXISTS ux_mesa_planta_rascunho ON mesa_planta (estabelecimento_id) WHERE estado = 'rascunho';
CREATE UNIQUE INDEX IF NOT EXISTS ux_mesa_planta_publicada ON mesa_planta (estabelecimento_id) WHERE estado = 'publicada';

-- Andares (pisos do documento). chave_planta = id do piso no documento, estavel entre versoes.
CREATE TABLE IF NOT EXISTS mesa_andar (
    id                 UUID PRIMARY KEY,
    estabelecimento_id UUID NOT NULL REFERENCES estabelecimentos (id),
    chave_planta       TEXT NOT NULL CHECK (length(chave_planta) BETWEEN 1 AND 80),
    nome               TEXT NOT NULL CHECK (length(nome) <= 80),
    nivel              SMALLINT NOT NULL DEFAULT 0,
    ativa              BOOLEAN NOT NULL DEFAULT TRUE,
    planta_versao      INTEGER NOT NULL,
    atualizado_em      TIMESTAMPTZ NOT NULL DEFAULT NOW(),
    criado_em          TIMESTAMPTZ NOT NULL DEFAULT NOW()
);
CREATE UNIQUE INDEX IF NOT EXISTS ux_mesa_andar_chave ON mesa_andar (estabelecimento_id, chave_planta);

-- Areas (setores do documento), sempre dentro de um andar.
CREATE TABLE IF NOT EXISTS mesa_area (
    id                 UUID PRIMARY KEY,
    estabelecimento_id UUID NOT NULL REFERENCES estabelecimentos (id),
    chave_planta       TEXT NOT NULL CHECK (length(chave_planta) BETWEEN 1 AND 80),
    piso_id            TEXT NOT NULL,
    nome               TEXT NOT NULL CHECK (length(nome) <= 80),
    cor                TEXT NULL CHECK (cor IS NULL OR length(cor) <= 20),
    ativa              BOOLEAN NOT NULL DEFAULT TRUE,
    planta_versao      INTEGER NOT NULL,
    atualizado_em      TIMESTAMPTZ NOT NULL DEFAULT NOW(),
    criado_em          TIMESTAMPTZ NOT NULL DEFAULT NOW()
);
CREATE UNIQUE INDEX IF NOT EXISTS ux_mesa_area_chave ON mesa_area (estabelecimento_id, chave_planta);

-- Cadastro da mesa. O id e da mesa para sempre: renumerar muda so o numero (o historico fica no mesa_evento).
CREATE TABLE IF NOT EXISTS mesa (
    id                 UUID PRIMARY KEY,
    estabelecimento_id UUID NOT NULL REFERENCES estabelecimentos (id),
    numero             TEXT NOT NULL CHECK (length(numero) BETWEEN 1 AND 20),
    piso_id            TEXT NOT NULL,
    setor_id           TEXT NULL,
    capacidade         SMALLINT NOT NULL,
    forma              TEXT NOT NULL,
    reservavel         BOOLEAN NOT NULL DEFAULT TRUE,
    ativa              BOOLEAN NOT NULL DEFAULT TRUE,
    planta_versao      INTEGER NOT NULL,
    chave_planta       TEXT NOT NULL CHECK (length(chave_planta) BETWEEN 1 AND 80),
    atualizado_em      TIMESTAMPTZ NOT NULL DEFAULT NOW(),
    criado_em          TIMESTAMPTZ NOT NULL DEFAULT NOW(),
    UNIQUE (estabelecimento_id, id)
);
ALTER TABLE mesa DROP CONSTRAINT IF EXISTS ck_mesa_capacidade;
ALTER TABLE mesa ADD CONSTRAINT ck_mesa_capacidade CHECK (capacidade > 0);
ALTER TABLE mesa DROP CONSTRAINT IF EXISTS ck_mesa_forma;
ALTER TABLE mesa ADD CONSTRAINT ck_mesa_forma CHECK (forma IN ('redonda', 'quadrada', 'retangular', 'oval'));
CREATE UNIQUE INDEX IF NOT EXISTS ux_mesa_chave_planta ON mesa (estabelecimento_id, chave_planta);
CREATE UNIQUE INDEX IF NOT EXISTS ux_mesa_numero_ativa ON mesa (estabelecimento_id, numero) WHERE ativa;
CREATE INDEX IF NOT EXISTS ix_mesa_piso ON mesa (estabelecimento_id, piso_id) WHERE ativa;

-- Estado vivo da mesa (P07 abre e fecha a sessao; P05 cria a linha ao publicar e bloqueia/desbloqueia).
CREATE TABLE IF NOT EXISTS mesa_estado (
    mesa_id            UUID PRIMARY KEY REFERENCES mesa (id),
    estabelecimento_id UUID NOT NULL REFERENCES estabelecimentos (id),
    status             TEXT NOT NULL DEFAULT 'livre',
    versao             INTEGER NOT NULL DEFAULT 1,
    sessao_id          UUID NULL,
    motivo_bloqueio    TEXT NULL CHECK (motivo_bloqueio IS NULL OR length(motivo_bloqueio) <= 280),
    atualizado_em      TIMESTAMPTZ NOT NULL DEFAULT NOW(),
    criado_em          TIMESTAMPTZ NOT NULL DEFAULT NOW()
);
ALTER TABLE mesa_estado DROP CONSTRAINT IF EXISTS ck_mesa_estado_status;
ALTER TABLE mesa_estado ADD CONSTRAINT ck_mesa_estado_status CHECK (status IN
    ('livre', 'reservada', 'ocupada', 'em_atendimento', 'aguardando_pagamento', 'aguardando_limpeza', 'indisponivel'));
ALTER TABLE mesa_estado DROP CONSTRAINT IF EXISTS ck_mesa_estado_sessao;
ALTER TABLE mesa_estado ADD CONSTRAINT ck_mesa_estado_sessao
    CHECK ((sessao_id IS NOT NULL) = (status IN ('ocupada', 'em_atendimento', 'aguardando_pagamento')));
CREATE INDEX IF NOT EXISTS ix_mesa_estado_loja ON mesa_estado (estabelecimento_id, status);

-- Planta, mesa, andar e area nunca somem (a mesa e referencia de sessao, pedido e pagamento).
DROP TRIGGER IF EXISTS mesa_planta_sem_delete ON mesa_planta;
CREATE TRIGGER mesa_planta_sem_delete BEFORE DELETE ON mesa_planta FOR EACH ROW EXECUTE FUNCTION gestao_bloqueia_exclusao();
DROP TRIGGER IF EXISTS mesa_sem_delete ON mesa;
CREATE TRIGGER mesa_sem_delete BEFORE DELETE ON mesa FOR EACH ROW EXECUTE FUNCTION gestao_bloqueia_exclusao();
DROP TRIGGER IF EXISTS mesa_andar_sem_delete ON mesa_andar;
CREATE TRIGGER mesa_andar_sem_delete BEFORE DELETE ON mesa_andar FOR EACH ROW EXECUTE FUNCTION gestao_bloqueia_exclusao();
DROP TRIGGER IF EXISTS mesa_area_sem_delete ON mesa_area;
CREATE TRIGGER mesa_area_sem_delete BEFORE DELETE ON mesa_area FOR EACH ROW EXECUTE FUNCTION gestao_bloqueia_exclusao();

INSERT INTO delivery_tracking_schema_versions (version)
VALUES ('20261205_05_mesas_planta')
ON CONFLICT (version) DO NOTHING;

COMMIT;
