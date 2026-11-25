-- Objecoes, recomendacoes e coaching do CRM (docs/crm-inteligente, tarefa 16).
--   * crm_objecao: varias objecoes por oportunidade, cada uma com EVIDENCIA obrigatoria, origem (manual | audio), certeza
--     (declarada | hipotese), estado, impacto e abordagem. Hipotese nunca e tratada como fato (nao pode ser impeditiva).
--     Objecao vinda do audio so entra a partir de entrada CONFIRMADA pela pessoa; (entrada_audio_id, evidencia) e unico.
--   * crm_objecao_auditoria: toda criacao/correcao manual ou importacao, com antes/depois e motivo.
--   * crm_recomendacao: sugestao com evidencias auditaveis (jsonb), hipotese, acao e ciclo
--     sugerida -> aceita|rejeitada -> executada -> reacao/resultado. Resultado e associacao observada, nunca causa.
--   * crm_recomendacao_evento: trilha de cada passo do ciclo.
-- Nenhuma coluna guarda probabilidade, desconto ou valor financeiro. Aditiva e idempotente (roda a cada boot).
BEGIN;

CREATE TABLE IF NOT EXISTS crm_objecao (
    id                      UUID PRIMARY KEY,
    oportunidade_id         UUID NOT NULL REFERENCES crm_oportunidade(id) ON DELETE CASCADE,
    tipo                    TEXT NOT NULL
                            CHECK (tipo IN ('preco', 'concorrente', 'funcionalidade', 'integracao', 'prazo', 'decisor', 'confianca', 'timing', 'suporte', 'outro')),
    descricao               TEXT NOT NULL CHECK (length(btrim(descricao)) > 0),
    evidencia               TEXT NOT NULL CHECK (length(btrim(evidencia)) > 0),
    origem                  TEXT NOT NULL CHECK (origem IN ('manual', 'audio')),
    entrada_audio_id        UUID NULL REFERENCES crm_assistente_entrada(id),
    certeza                 TEXT NOT NULL CHECK (certeza IN ('declarada', 'hipotese')),
    estado                  TEXT NOT NULL DEFAULT 'levantada'
                            CHECK (estado IN ('levantada', 'investigando', 'respondida', 'resolvida', 'impeditiva')),
    impacto                 TEXT NOT NULL DEFAULT 'desconhecido' CHECK (impacto IN ('desconhecido', 'baixo', 'medio', 'alto')),
    abordagem               TEXT NULL,
    modulo_citado           TEXT NULL,
    resposta_prometida_ate  TIMESTAMPTZ NULL,
    ocorrida_em             TIMESTAMPTZ NOT NULL,
    criado_por              INTEGER NOT NULL REFERENCES usuario(id),
    criado_em               TIMESTAMPTZ NOT NULL DEFAULT NOW(),
    atualizado_em           TIMESTAMPTZ NOT NULL DEFAULT NOW(),
    versao                  INTEGER NOT NULL DEFAULT 1,
    CONSTRAINT ck_crm_objecao_hipotese_nao_impeditiva CHECK (NOT (certeza = 'hipotese' AND estado = 'impeditiva')),
    CONSTRAINT ck_crm_objecao_audio_tem_entrada CHECK (origem <> 'audio' OR entrada_audio_id IS NOT NULL)
);

CREATE INDEX IF NOT EXISTS ix_crm_objecao_oportunidade ON crm_objecao (oportunidade_id, ocorrida_em DESC);
CREATE INDEX IF NOT EXISTS ix_crm_objecao_tipo_estado ON crm_objecao (tipo, estado);
CREATE UNIQUE INDEX IF NOT EXISTS ux_crm_objecao_audio ON crm_objecao (entrada_audio_id, md5(evidencia)) WHERE entrada_audio_id IS NOT NULL;

CREATE TABLE IF NOT EXISTS crm_objecao_auditoria (
    id              BIGSERIAL PRIMARY KEY,
    objecao_id      UUID NOT NULL REFERENCES crm_objecao(id) ON DELETE CASCADE,
    oportunidade_id UUID NOT NULL,
    evento          TEXT NOT NULL CHECK (evento IN ('criada', 'corrigida', 'importada_audio')),
    ator_usuario_id INTEGER NOT NULL REFERENCES usuario(id),
    antes           JSONB NULL,
    depois          JSONB NULL,
    motivo          TEXT NULL,
    em              TIMESTAMPTZ NOT NULL DEFAULT NOW()
);
CREATE INDEX IF NOT EXISTS ix_crm_objecao_auditoria_objecao ON crm_objecao_auditoria (objecao_id, em);

CREATE TABLE IF NOT EXISTS crm_recomendacao (
    id                       UUID PRIMARY KEY,
    oportunidade_id          UUID NOT NULL REFERENCES crm_oportunidade(id) ON DELETE CASCADE,
    responsavel_usuario_id   INTEGER NULL REFERENCES usuario(id),
    regra                    TEXT NOT NULL CHECK (regra IN ('barreira_real', 'diagnostico', 'compromisso_vencido', 'abordagem_comparavel')),
    prioridade               INTEGER NOT NULL CHECK (prioridade BETWEEN 1 AND 4),
    impressao_digital        TEXT NOT NULL,
    sugestao                 TEXT NOT NULL,
    hipotese                 TEXT NOT NULL,
    acao                     TEXT NOT NULL,
    evidencias               JSONB NOT NULL,
    redacao_ia               TEXT NULL,
    estado                   TEXT NOT NULL DEFAULT 'sugerida' CHECK (estado IN ('sugerida', 'aceita', 'rejeitada', 'executada')),
    motivo_rejeicao          TEXT NULL,
    reacao                   TEXT NULL CHECK (reacao IS NULL OR reacao IN ('positiva', 'neutra', 'negativa', 'sem_resposta')),
    resultado                TEXT NULL CHECK (resultado IS NULL OR resultado IN ('avancou', 'manteve', 'recuou', 'perdida', 'indefinido')),
    observacao               TEXT NULL,
    criado_em                TIMESTAMPTZ NOT NULL DEFAULT NOW(),
    atualizado_em            TIMESTAMPTZ NOT NULL DEFAULT NOW(),
    versao                   INTEGER NOT NULL DEFAULT 1,
    CONSTRAINT ck_crm_recomendacao_resultado_pos_execucao CHECK ((reacao IS NULL AND resultado IS NULL) OR estado = 'executada')
);

CREATE INDEX IF NOT EXISTS ix_crm_recomendacao_oportunidade ON crm_recomendacao (oportunidade_id, prioridade, criado_em DESC);
-- Mesma situacao nao gera duas sugestoes abertas: chamar GET varias vezes e idempotente.
CREATE UNIQUE INDEX IF NOT EXISTS ux_crm_recomendacao_aberta ON crm_recomendacao (oportunidade_id, impressao_digital)
    WHERE estado IN ('sugerida', 'aceita');

CREATE TABLE IF NOT EXISTS crm_recomendacao_evento (
    id               BIGSERIAL PRIMARY KEY,
    recomendacao_id  UUID NOT NULL REFERENCES crm_recomendacao(id) ON DELETE CASCADE,
    evento           TEXT NOT NULL CHECK (evento IN ('gerada', 'aceita', 'rejeitada', 'executada', 'reacao', 'resultado')),
    ator_usuario_id  INTEGER NULL REFERENCES usuario(id),
    detalhe          JSONB NULL,
    em               TIMESTAMPTZ NOT NULL DEFAULT NOW()
);
CREATE INDEX IF NOT EXISTS ix_crm_recomendacao_evento_rec ON crm_recomendacao_evento (recomendacao_id, em);

COMMIT;
