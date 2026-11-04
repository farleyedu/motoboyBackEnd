-- Jornada comercial (zippy-admin/docs/crm-inteligente/05-jornada-comercial-decisoes.md).
--   * acompanhamento_proposta_versao: envio registrado (canal, destinatario, quem apresentou, evidencia), resposta, revisao e R$ 0 consciente.
--     "edicao" e a versao de concorrencia de cada linha; "chave_envio"/"chave_resposta" seguram o duplo clique.
--   * acompanhamento_proximo_passo: ciclo de execucao (feito, sem_sucesso, reagendado, cancelado), com resultado, realizado_em e o passo de origem.
--   * acompanhamento_marcacao: tipo precisa_ajuda, categoria e acao esperada.
-- Dados antigos: Proposta so com envio registrado. Com proposta_json vira versao enviada (nao_informado, crm_legado); sem nada volta para Conversa,
-- com linha no historico (autor sistema). Marcacao "atencao" ativa vira "precisa_ajuda" (era o que a tela mostrava).
-- Aditiva e idempotente: roda a cada boot; a devolucao dos dados so na primeira vez (a coluna "edicao" ainda nao existia).
BEGIN;

CREATE TEMP TABLE crm_jornada_17_execucao ON COMMIT DROP AS
SELECT NOT EXISTS (
    SELECT 1 FROM pg_attribute
     WHERE attrelid = 'acompanhamento_proposta_versao'::regclass AND attname = 'edicao' AND NOT attisdropped) AS primeira;

-- ---- propostas

ALTER TABLE acompanhamento_proposta_versao
    ADD COLUMN IF NOT EXISTS canal                 TEXT NULL,
    ADD COLUMN IF NOT EXISTS destinatario          TEXT NULL,
    ADD COLUMN IF NOT EXISTS apresentada_por       INTEGER NULL,
    ADD COLUMN IF NOT EXISTS evidencia_mensagem_id UUID NULL,
    ADD COLUMN IF NOT EXISTS origem                TEXT NULL,
    ADD COLUMN IF NOT EXISTS revisa_versao         INTEGER NULL,
    ADD COLUMN IF NOT EXISTS resposta              TEXT NULL,
    ADD COLUMN IF NOT EXISTS resposta_nota         TEXT NULL,
    ADD COLUMN IF NOT EXISTS resposta_em           TIMESTAMPTZ NULL,
    ADD COLUMN IF NOT EXISTS valor_zero_consciente BOOLEAN NOT NULL DEFAULT FALSE,
    ADD COLUMN IF NOT EXISTS atualizada_em         TIMESTAMPTZ NOT NULL DEFAULT NOW(),
    ADD COLUMN IF NOT EXISTS edicao                BIGINT NOT NULL DEFAULT 1,
    ADD COLUMN IF NOT EXISTS chave_envio           VARCHAR(120) NULL,
    ADD COLUMN IF NOT EXISTS chave_resposta        VARCHAR(120) NULL;

ALTER TABLE acompanhamento_proposta_versao DROP CONSTRAINT IF EXISTS acompanhamento_proposta_versao_status_check;

DO $$
BEGIN
    IF NOT EXISTS (SELECT 1 FROM pg_constraint WHERE conname = 'ck_acompanhamento_proposta_status') THEN
        ALTER TABLE acompanhamento_proposta_versao ADD CONSTRAINT ck_acompanhamento_proposta_status
            CHECK (status IN ('rascunho', 'enviada', 'em_negociacao', 'aceita', 'recusada', 'substituida', 'guardada'));
    END IF;
    IF NOT EXISTS (SELECT 1 FROM pg_constraint WHERE conname = 'ck_acompanhamento_proposta_canal') THEN
        ALTER TABLE acompanhamento_proposta_versao ADD CONSTRAINT ck_acompanhamento_proposta_canal
            CHECK (canal IS NULL OR canal IN ('whatsapp', 'email', 'presencial', 'ligacao', 'reuniao', 'nao_informado'));
    END IF;
    IF NOT EXISTS (SELECT 1 FROM pg_constraint WHERE conname = 'ck_acompanhamento_proposta_resposta') THEN
        ALTER TABLE acompanhamento_proposta_versao ADD CONSTRAINT ck_acompanhamento_proposta_resposta
            CHECK (resposta IS NULL OR resposta IN ('interessado', 'pediu_ajuste', 'recusou', 'sem_resposta'));
    END IF;
END $$;

-- ---- atividades

ALTER TABLE acompanhamento_proximo_passo
    ADD COLUMN IF NOT EXISTS resultado        TEXT NULL,
    ADD COLUMN IF NOT EXISTS resultado_nota   TEXT NULL,
    ADD COLUMN IF NOT EXISTS realizado_em     TIMESTAMPTZ NULL,
    ADD COLUMN IF NOT EXISTS origem_passo_id  UUID NULL,
    ADD COLUMN IF NOT EXISTS encerrado_motivo TEXT NULL;

ALTER TABLE acompanhamento_proximo_passo DROP CONSTRAINT IF EXISTS acompanhamento_proximo_passo_estado_check;

DO $$
BEGIN
    IF NOT EXISTS (SELECT 1 FROM pg_constraint WHERE conname = 'ck_acompanhamento_proximo_passo_estado') THEN
        ALTER TABLE acompanhamento_proximo_passo ADD CONSTRAINT ck_acompanhamento_proximo_passo_estado
            CHECK (estado IN ('aberto', 'feito', 'sem_sucesso', 'reagendado', 'cancelado', 'substituido'));
    END IF;
    IF NOT EXISTS (SELECT 1 FROM pg_constraint WHERE conname = 'ck_acompanhamento_proximo_passo_resultado') THEN
        ALTER TABLE acompanhamento_proximo_passo ADD CONSTRAINT ck_acompanhamento_proximo_passo_resultado
            CHECK (resultado IS NULL OR resultado IN ('contato_realizado', 'sem_resposta', 'interessado', 'negociacao_pendente', 'problema_encontrado', 'aguardando_terceiro'));
    END IF;
    IF NOT EXISTS (SELECT 1 FROM pg_constraint WHERE conname = 'fk_acompanhamento_proximo_passo_origem') THEN
        ALTER TABLE acompanhamento_proximo_passo ADD CONSTRAINT fk_acompanhamento_proximo_passo_origem
            FOREIGN KEY (origem_passo_id) REFERENCES acompanhamento_proximo_passo (id) ON DELETE SET NULL;
    END IF;
END $$;

-- ---- sinalizacao

ALTER TABLE acompanhamento_marcacao
    ADD COLUMN IF NOT EXISTS categoria      TEXT NULL,
    ADD COLUMN IF NOT EXISTS acao_esperada  TEXT NULL;

ALTER TABLE acompanhamento_marcacao DROP CONSTRAINT IF EXISTS acompanhamento_marcacao_tipo_check;

DO $$
BEGIN
    IF NOT EXISTS (SELECT 1 FROM pg_constraint WHERE conname = 'ck_acompanhamento_marcacao_tipo') THEN
        ALTER TABLE acompanhamento_marcacao ADD CONSTRAINT ck_acompanhamento_marcacao_tipo
            CHECK (tipo IN ('atencao', 'precisa_ajuda', 'risco_de_saida', 'nao_incomodar'));
    END IF;
END $$;

-- ---- dados antigos (uma vez so)

DO $$
DECLARE
    alvo RECORD;
    numero INTEGER;
    saas NUMERIC;
    marketing NUMERIC;
    implantacao NUMERIC;
    mensal NUMERIC;
BEGIN
    IF NOT (SELECT primeira FROM crm_jornada_17_execucao) THEN
        RETURN;
    END IF;

    -- Com proposta_json o envio ja foi declarado no /crm: vira versao enviada, sem inventar canal.
    IF to_regclass('crm_oportunidade') IS NOT NULL THEN
        FOR alvo IN
            SELECT o.id AS oportunidade_id, a.id AS acompanhamento_id, o.proposta_json, o.proposta_enviada_em
              FROM crm_oportunidade o
              LEFT JOIN acompanhamento a ON a.oportunidade_id = o.id
             WHERE (a.fase = 'proposta'
                    OR (a.id IS NULL AND o.deleted_at IS NULL AND o.status::text = 'ativo' AND o.coluna::text IN ('proposta', 'quase', 'aguardando')))
               AND o.proposta_json IS NOT NULL
               AND o.proposta_json::text NOT IN ('null', '{}')
               AND NOT EXISTS (SELECT 1 FROM acompanhamento_proposta_versao v
                                WHERE v.oportunidade_id = o.id AND v.status IN ('enviada', 'em_negociacao'))
        LOOP
            saas := CASE WHEN alvo.proposta_json ->> 'saaSMensalidade' ~ '^[0-9]+(\.[0-9]+)?$' THEN (alvo.proposta_json ->> 'saaSMensalidade')::numeric END;
            marketing := CASE WHEN alvo.proposta_json ->> 'marketingMensalidade' ~ '^[0-9]+(\.[0-9]+)?$' THEN (alvo.proposta_json ->> 'marketingMensalidade')::numeric END;
            implantacao := CASE WHEN alvo.proposta_json ->> 'implantacao' ~ '^[0-9]+(\.[0-9]+)?$' THEN (alvo.proposta_json ->> 'implantacao')::numeric END;
            mensal := NULLIF(COALESCE(saas, 0) + COALESCE(marketing, 0), 0);

            UPDATE acompanhamento_proposta_versao SET status = 'guardada', atualizada_em = NOW()
             WHERE oportunidade_id = alvo.oportunidade_id AND status = 'rascunho';

            SELECT COALESCE(MAX(versao), 0) + 1 INTO numero FROM acompanhamento_proposta_versao WHERE oportunidade_id = alvo.oportunidade_id;

            INSERT INTO acompanhamento_proposta_versao
                (oportunidade_id, versao, status, valor_mensal, valor_implantacao, dados, enviada_em, validade_ate, canal, origem)
            VALUES
                (alvo.oportunidade_id, numero, 'enviada', mensal, implantacao, alvo.proposta_json, alvo.proposta_enviada_em,
                 (alvo.proposta_enviada_em AT TIME ZONE 'America/Sao_Paulo')::date + 7, 'nao_informado', 'crm_legado');

            IF alvo.acompanhamento_id IS NOT NULL THEN
                INSERT INTO acompanhamento_relacionamento_evento (acompanhamento_id, tipo, detalhe, em)
                VALUES (alvo.acompanhamento_id, 'proposta_enviada',
                        jsonb_build_object('versao', numero, 'canal', 'nao_informado', 'origem', 'crm_legado', 'valorMensal', mensal, 'migracao', TRUE),
                        COALESCE(alvo.proposta_enviada_em, NOW()));
            END IF;
        END LOOP;

        -- Sem envio registrado a oportunidade deixa de estar em Proposta tambem no /crm.
        UPDATE crm_oportunidade o
           SET coluna = 'conversa', updated_at = NOW()
          FROM (SELECT x.id
                  FROM crm_oportunidade x
                  LEFT JOIN acompanhamento a ON a.oportunidade_id = x.id
                 WHERE x.status::text = 'ativo' AND x.coluna::text IN ('proposta', 'quase', 'aguardando')
                   AND (a.id IS NULL OR a.fase = 'proposta')
                   AND NOT EXISTS (SELECT 1 FROM acompanhamento_proposta_versao v
                                    WHERE v.oportunidade_id = x.id AND v.status IN ('enviada', 'em_negociacao'))) s
         WHERE o.id = s.id;
    END IF;

    -- Proposta sem envio volta para Conversa: o rascunho fica guardado e o historico explica.
    WITH volta AS (
        SELECT a.id, a.oportunidade_id
          FROM acompanhamento a
         WHERE a.fase = 'proposta'
           AND NOT EXISTS (SELECT 1 FROM acompanhamento_proposta_versao v
                            WHERE v.oportunidade_id = a.oportunidade_id AND v.status IN ('enviada', 'em_negociacao'))
    ), guardadas AS (
        UPDATE acompanhamento_proposta_versao v SET status = 'guardada', atualizada_em = NOW()
          FROM volta WHERE v.oportunidade_id = volta.oportunidade_id AND v.status = 'rascunho'
        RETURNING 1
    ), historico AS (
        INSERT INTO acompanhamento_historico (acompanhamento_id, tipo, de, para, autor_tipo, motivo)
        SELECT id, 'fase', 'proposta', 'conversa', 'sistema', 'Sem proposta enviada registrada: voltou para Conversa.' FROM volta
        RETURNING 1
    )
    UPDATE acompanhamento a
       SET fase = 'conversa', fase_desde = NOW(), versao = versao + 1, atualizado_em = NOW()
      FROM volta
     WHERE a.id = volta.id;

    -- Versoes que ficaram abertas depois de perder, testar ou fechar nao contam mais ao retomar.
    UPDATE acompanhamento_proposta_versao v SET status = 'guardada', atualizada_em = NOW()
      FROM acompanhamento a
     WHERE a.oportunidade_id = v.oportunidade_id AND a.fase <> 'proposta' AND v.status IN ('rascunho', 'enviada');

    -- Passo aberto de quem saiu do quadro nao volta vencido ao retomar.
    UPDATE acompanhamento_proximo_passo p
       SET estado = 'cancelado', encerrado_em = NOW(), encerrado_motivo = 'Encerrado com a saída do quadro.'
      FROM acompanhamento a
     WHERE a.id = p.acompanhamento_id AND p.estado = 'aberto' AND a.fase IN ('perdido', 'encerrado');

    -- Marcacao ativa: "atencao" era exibida como "precisa de ajuda". O historico removido fica como foi gravado.
    WITH convertidas AS (
        UPDATE acompanhamento_marcacao SET tipo = 'precisa_ajuda'
         WHERE tipo = 'atencao' AND removida_em IS NULL
        RETURNING acompanhamento_id
    )
    INSERT INTO acompanhamento_relacionamento_evento (acompanhamento_id, tipo, detalhe)
    SELECT acompanhamento_id, 'marcacao_convertida', jsonb_build_object('de', 'atencao', 'para', 'precisa_ajuda') FROM convertidas;
END $$;

-- ---- indices (depois da devolucao dos dados)

CREATE UNIQUE INDEX IF NOT EXISTS ux_acompanhamento_proposta_rascunho ON acompanhamento_proposta_versao (oportunidade_id) WHERE status = 'rascunho';
CREATE UNIQUE INDEX IF NOT EXISTS ux_acompanhamento_proposta_aberta ON acompanhamento_proposta_versao (oportunidade_id) WHERE status IN ('enviada', 'em_negociacao');
CREATE UNIQUE INDEX IF NOT EXISTS ux_acompanhamento_proposta_chave_envio ON acompanhamento_proposta_versao (oportunidade_id, chave_envio) WHERE chave_envio IS NOT NULL;
CREATE INDEX IF NOT EXISTS ix_acompanhamento_proximo_passo_realizado ON acompanhamento_proximo_passo (acompanhamento_id, realizado_em DESC) WHERE realizado_em IS NOT NULL;
CREATE INDEX IF NOT EXISTS ix_acompanhamento_proximo_passo_origem ON acompanhamento_proximo_passo (origem_passo_id) WHERE origem_passo_id IS NOT NULL;

COMMIT;
