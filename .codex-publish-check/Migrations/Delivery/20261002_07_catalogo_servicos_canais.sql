BEGIN;

-- ---------------------------------------------------------------------------
-- Atendimento por WhatsApp modular (plano: zippy-admin/docs/plano-atendimento-whatsapp.md, etapa 1).
--
--   servico_catalogo          o que o cliente final pode fazer (delivery, cardapio_web, agendamento)
--   tipo_servico              quais servicos cada tipo de estabelecimento permite e quais vem ligados por padrao
--   estabelecimento_servico   servicos ligados em cada loja (so a Gestao liga e desliga)
--   canal_whatsapp            numeros de WhatsApp; cada numero pertence a UMA loja; uma loja pode ter varios
--   canal_servico             quais servicos cada numero atende
--   canal_whatsapp_auditoria  quem criou, alterou ou moveu um numero
--
-- Substitui waba_phone (uma linha por loja, sem unicidade, com o mesmo valor em varios campos). waba_phone NAO e
-- alterada nem apagada aqui; o codigo novo passa a ler canal_whatsapp e a tabela antiga sai na etapa de limpeza.
-- Tudo aditivo e idempotente. Dados que nao podem virar canal sem perda vao para canal_whatsapp_migracao_pendencia.
-- ---------------------------------------------------------------------------

-- 1) Catalogo de servicos --------------------------------------------------------------------------------------
CREATE TABLE IF NOT EXISTS servico_catalogo (
    codigo TEXT PRIMARY KEY CHECK (codigo ~ '^[a-z][a-z0-9_]*$'),
    nome TEXT NOT NULL,
    descricao TEXT NULL,
    -- Valores de modulo_enum (MAIUSCULAS) que o servico exige; ligar o servico liga estes modulos junto.
    modulos_exigidos TEXT[] NOT NULL DEFAULT '{}',
    ordem INTEGER NOT NULL DEFAULT 0,
    ativo BOOLEAN NOT NULL DEFAULT TRUE,
    created_at_utc TIMESTAMPTZ NOT NULL DEFAULT NOW(),
    updated_at_utc TIMESTAMPTZ NOT NULL DEFAULT NOW()
);

INSERT INTO servico_catalogo (codigo, nome, descricao, modulos_exigidos, ordem) VALUES
    ('delivery', 'Delivery', 'Pedidos para entrega ou retirada, com acompanhamento.', ARRAY['DELIVERY', 'PEDIDOS'], 10),
    ('cardapio_web', 'Cardapio web', 'Cardapio publico com pedido pelo link.', ARRAY['CARDAPIO', 'CARDAPIOWEB', 'PEDIDOS'], 20),
    ('agendamento', 'Agendamento', 'Reservas e agendamentos de horarios.', ARRAY['AGENDAMENTOS'], 30)
ON CONFLICT (codigo) DO NOTHING;

CREATE TABLE IF NOT EXISTS tipo_servico (
    id_tipo_estabelecimento UUID NOT NULL REFERENCES tipo_estabelecimento (id) ON DELETE CASCADE,
    servico_codigo TEXT NOT NULL REFERENCES servico_catalogo (codigo) ON DELETE CASCADE,
    padrao BOOLEAN NOT NULL DEFAULT FALSE,
    PRIMARY KEY (id_tipo_estabelecimento, servico_codigo)
);

-- Restaurante: delivery e cardapio web vem ligados; agendamento e permitido, mas nasce desligado.
INSERT INTO tipo_servico (id_tipo_estabelecimento, servico_codigo, padrao)
SELECT te.id, s.codigo, s.padrao
  FROM tipo_estabelecimento te
 CROSS JOIN (VALUES ('delivery', TRUE), ('cardapio_web', TRUE), ('agendamento', FALSE)) AS s(codigo, padrao)
 WHERE lower(te.slug) = 'restaurante'
ON CONFLICT DO NOTHING;

CREATE TABLE IF NOT EXISTS estabelecimento_servico (
    id_estabelecimento UUID NOT NULL REFERENCES estabelecimentos (id) ON DELETE CASCADE,
    servico_codigo TEXT NOT NULL REFERENCES servico_catalogo (codigo),
    ativo BOOLEAN NOT NULL DEFAULT TRUE,
    config JSONB NOT NULL DEFAULT '{}'::jsonb,
    alterado_por_usuario_id INTEGER NULL,
    created_at_utc TIMESTAMPTZ NOT NULL DEFAULT NOW(),
    updated_at_utc TIMESTAMPTZ NOT NULL DEFAULT NOW(),
    PRIMARY KEY (id_estabelecimento, servico_codigo)
);

CREATE INDEX IF NOT EXISTS ix_estabelecimento_servico_codigo ON estabelecimento_servico (servico_codigo) WHERE ativo;

-- Lojas que ja tinham o modulo viram lojas com o servico.
INSERT INTO estabelecimento_servico (id_estabelecimento, servico_codigo)
SELECT e.id, s.codigo
  FROM estabelecimentos e
 CROSS JOIN servico_catalogo s
 WHERE EXISTS (
        SELECT 1
          FROM unnest(e.modulos_ativos::text[]) AS m(nome)
         WHERE upper(m.nome) = CASE s.codigo
                                   WHEN 'delivery' THEN 'DELIVERY'
                                   WHEN 'cardapio_web' THEN 'CARDAPIOWEB'
                                   WHEN 'agendamento' THEN 'AGENDAMENTOS'
                               END)
ON CONFLICT DO NOTHING;

-- 2) Canais de WhatsApp ----------------------------------------------------------------------------------------
CREATE TABLE IF NOT EXISTS canal_whatsapp (
    id UUID PRIMARY KEY,
    id_estabelecimento UUID NOT NULL REFERENCES estabelecimentos (id) ON DELETE CASCADE,
    -- ID do numero na Meta (WhatsApp Business Manager). Nao e o telefone: tem uns 15 digitos.
    phone_number_id TEXT NOT NULL CHECK (phone_number_id ~ '^[0-9]+$'),
    -- Telefone que o cliente enxerga, normalizado uma vez: +DDI DDD numero.
    numero_e164 TEXT NOT NULL CHECK (numero_e164 ~ '^\+[0-9]{10,15}$'),
    nome TEXT NULL,
    -- Token da Meta deste numero, cifrado pela aplicacao (nunca em texto puro).
    token_cifrado TEXT NULL,
    status TEXT NOT NULL DEFAULT 'configurando' CHECK (status IN ('configurando', 'ativo', 'erro', 'inativo')),
    modo_atendimento TEXT NOT NULL DEFAULT 'hibrido' CHECK (modo_atendimento IN ('bot', 'humano', 'hibrido')),
    config JSONB NOT NULL DEFAULT '{}'::jsonb,
    verificado_em TIMESTAMPTZ NULL,
    ultimo_recebimento_em TIMESTAMPTZ NULL,
    ultimo_envio_ok_em TIMESTAMPTZ NULL,
    ultimo_erro TEXT NULL,
    ultimo_erro_em TIMESTAMPTZ NULL,
    created_at_utc TIMESTAMPTZ NOT NULL DEFAULT NOW(),
    updated_at_utc TIMESTAMPTZ NOT NULL DEFAULT NOW()
);

-- Um numero pertence a uma loja so: estes dois indices sao a regra que acaba com a disputa de dados.
CREATE UNIQUE INDEX IF NOT EXISTS ux_canal_whatsapp_phone_number_id ON canal_whatsapp (phone_number_id);
CREATE UNIQUE INDEX IF NOT EXISTS ux_canal_whatsapp_numero ON canal_whatsapp (numero_e164);
CREATE INDEX IF NOT EXISTS ix_canal_whatsapp_estabelecimento ON canal_whatsapp (id_estabelecimento);

CREATE TABLE IF NOT EXISTS canal_servico (
    id_canal UUID NOT NULL REFERENCES canal_whatsapp (id) ON DELETE CASCADE,
    servico_codigo TEXT NOT NULL REFERENCES servico_catalogo (codigo),
    PRIMARY KEY (id_canal, servico_codigo)
);

CREATE TABLE IF NOT EXISTS canal_whatsapp_auditoria (
    id BIGSERIAL PRIMARY KEY,
    id_canal UUID NULL,
    acao TEXT NOT NULL,
    id_estabelecimento_origem UUID NULL,
    id_estabelecimento_destino UUID NULL,
    usuario_id INTEGER NULL,
    detalhe JSONB NULL,
    created_at_utc TIMESTAMPTZ NOT NULL DEFAULT NOW()
);

CREATE INDEX IF NOT EXISTS ix_canal_whatsapp_auditoria_canal ON canal_whatsapp_auditoria (id_canal, created_at_utc DESC);

CREATE TABLE IF NOT EXISTS canal_whatsapp_migracao_pendencia (
    id BIGSERIAL PRIMARY KEY,
    id_estabelecimento UUID NULL,
    phone_number_id TEXT NULL,
    display_phone_number TEXT NULL,
    motivo TEXT NOT NULL,
    created_at_utc TIMESTAMPTZ NOT NULL DEFAULT NOW()
);

-- 3) waba_phone -> canal_whatsapp ------------------------------------------------------------------------------
DO $migracao$
BEGIN
    IF to_regclass('public.waba_phone') IS NULL THEN
        RETURN;
    END IF;

    IF NOT EXISTS (SELECT 1 FROM information_schema.columns WHERE table_name = 'waba_phone' AND column_name = 'display_phone_number')
       OR NOT EXISTS (SELECT 1 FROM information_schema.columns WHERE table_name = 'waba_phone' AND column_name = 'phone_number_id') THEN
        RETURN;
    END IF;

    -- Linhas que nao tem como virar canal: ficam registradas para revisao em vez de sumir.
    INSERT INTO canal_whatsapp_migracao_pendencia (id_estabelecimento, phone_number_id, display_phone_number, motivo)
    SELECT w.id_estabelecimento, w.phone_number_id, w.display_phone_number,
           CASE WHEN COALESCE(btrim(w.display_phone_number), '') = '' THEN 'sem_numero_de_exibicao'
                WHEN COALESCE(btrim(w.phone_number_id), '') = '' THEN 'sem_phone_number_id'
                ELSE 'numero_de_exibicao_invalido' END
      FROM waba_phone w
     WHERE COALESCE(w.ativo, TRUE)
       AND (COALESCE(btrim(w.display_phone_number), '') = ''
            OR COALESCE(btrim(w.phone_number_id), '') = ''
            OR length(regexp_replace(w.display_phone_number, '\D', '', 'g')) NOT BETWEEN 10 AND 15
            OR btrim(w.phone_number_id) !~ '^[0-9]+$');

    CREATE TEMP TABLE _waba_candidatos ON COMMIT DROP AS
    SELECT w.id_estabelecimento,
           btrim(w.phone_number_id) AS phone_number_id,
           CASE WHEN length(d.digitos) IN (10, 11) THEN '+55' || d.digitos ELSE '+' || d.digitos END AS numero_e164,
           w.display_phone_number,
           COALESCE(w.data_atualizacao, NOW()) AS atualizado
      FROM waba_phone w
     CROSS JOIN LATERAL (SELECT regexp_replace(w.display_phone_number, '\D', '', 'g') AS digitos) d
     WHERE COALESCE(w.ativo, TRUE)
       AND btrim(w.phone_number_id) ~ '^[0-9]+$'
       AND length(d.digitos) BETWEEN 10 AND 15
       AND EXISTS (SELECT 1 FROM estabelecimentos e WHERE e.id = w.id_estabelecimento);

    -- Quando o mesmo ID ou o mesmo telefone aparece em mais de uma linha, vence a mais recente.
    CREATE TEMP TABLE _waba_escolhidos ON COMMIT DROP AS
    SELECT x.id_estabelecimento, x.phone_number_id, x.numero_e164, x.display_phone_number
      FROM (
            SELECT c.*, row_number() OVER (PARTITION BY c.numero_e164 ORDER BY c.atualizado DESC) AS rn_numero
              FROM (
                    SELECT c.*, row_number() OVER (PARTITION BY c.phone_number_id ORDER BY c.atualizado DESC) AS rn_id
                      FROM _waba_candidatos c
                   ) c
             WHERE c.rn_id = 1
           ) x
     WHERE x.rn_numero = 1;

    INSERT INTO canal_whatsapp_migracao_pendencia (id_estabelecimento, phone_number_id, display_phone_number, motivo)
    SELECT c.id_estabelecimento, c.phone_number_id, c.display_phone_number, 'numero_duplicado_em_outra_loja'
      FROM _waba_candidatos c
     WHERE NOT EXISTS (
            SELECT 1 FROM _waba_escolhidos e
             WHERE e.id_estabelecimento = c.id_estabelecimento AND e.phone_number_id = c.phone_number_id);

    -- phone_number_id curto e um telefone digitado no lugar do ID da Meta: o canal nasce em erro, nao ativo.
    INSERT INTO canal_whatsapp (id, id_estabelecimento, phone_number_id, numero_e164, status, ultimo_erro)
    SELECT gen_random_uuid(), e.id_estabelecimento, e.phone_number_id, e.numero_e164,
           CASE WHEN length(e.phone_number_id) <= 13 THEN 'erro' ELSE 'ativo' END,
           CASE WHEN length(e.phone_number_id) <= 13
                THEN 'O ID da Meta parece um telefone: informe o Phone Number ID do WhatsApp Business Manager.' END
      FROM _waba_escolhidos e
    ON CONFLICT DO NOTHING;
END
$migracao$;

-- O modo de atendimento da loja ('ia' ou 'humano') passa para os numeros dela: 'ia' vira 'bot'.
UPDATE canal_whatsapp c
   SET modo_atendimento = CASE a.modo WHEN 'ia' THEN 'bot' ELSE 'humano' END
  FROM estabelecimento_atendimento_config a
 WHERE a.estabelecimento_id = c.id_estabelecimento;

-- Antes havia um numero por loja que servia para tudo: cada canal herda os servicos ativos da loja.
INSERT INTO canal_servico (id_canal, servico_codigo)
SELECT c.id, es.servico_codigo
  FROM canal_whatsapp c
  JOIN estabelecimento_servico es ON es.id_estabelecimento = c.id_estabelecimento AND es.ativo
ON CONFLICT DO NOTHING;

INSERT INTO delivery_tracking_schema_versions (version)
VALUES ('20261002_07_catalogo_servicos_canais')
ON CONFLICT (version) DO NOTHING;

COMMIT;
