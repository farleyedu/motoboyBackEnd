-- Planos v2 (decisao do dono em 21/10): iFood e chatbot no Partida, mapa sem motoboy no Rota, app do motoboy no Turbo,
-- Frota com Inteligencia e 5 motoboys por loja. Motoboys deixam de ser "pacotes de 3": a loja tem uma base (do plano) e
-- compra pacotes de 3 ou 7 motoboys que valem 30 dias (mensal) ou 365 dias (anual). Rodar depois de 20261022_01. Idempotente.
BEGIN;

-- Lojas que ja tem Delivery continuam com o mapa (ninguem perde tela).
UPDATE estabelecimentos
   SET modulos_ativos = array_append(modulos_ativos, 'MAPA'::modulo_enum)
 WHERE 'DELIVERY'::modulo_enum = ANY (modulos_ativos)
   AND NOT ('MAPA'::modulo_enum = ANY (modulos_ativos));

-- Base de motoboys da loja. Lojas atuais: 3 por pacote antigo (ninguem perde motoboy).
ALTER TABLE estabelecimentos ADD COLUMN IF NOT EXISTS motoboys_base INTEGER NULL;
UPDATE estabelecimentos SET motoboys_base = 3 * GREATEST(1, COALESCE(pacotes_motoboy, 1)) WHERE motoboys_base IS NULL;
ALTER TABLE estabelecimentos ALTER COLUMN motoboys_base SET DEFAULT 3;
ALTER TABLE estabelecimentos ALTER COLUMN motoboys_base SET NOT NULL;
DO $$
BEGIN
    IF NOT EXISTS (SELECT 1 FROM pg_constraint WHERE conname = 'ck_estabelecimentos_motoboys_base') THEN
        ALTER TABLE estabelecimentos ADD CONSTRAINT ck_estabelecimentos_motoboys_base CHECK (motoboys_base >= 0);
    END IF;
END $$;

-- Pacotes de motoboy comprados: somam a base so enquanto vigentes. Nada roda sozinho; vencido, deixa de contar.
CREATE TABLE IF NOT EXISTS motoboy_pacotes (
    id BIGSERIAL PRIMARY KEY,
    estabelecimento_id UUID NOT NULL REFERENCES estabelecimentos(id) ON DELETE CASCADE,
    quantidade INTEGER NOT NULL CHECK (quantidade IN (3, 7)),
    periodicidade TEXT NOT NULL CHECK (periodicidade IN ('mensal', 'anual')),
    inicio_utc TIMESTAMPTZ NOT NULL,
    fim_utc TIMESTAMPTZ NOT NULL,
    lancado_por INTEGER NULL,
    lancado_em TIMESTAMPTZ NOT NULL DEFAULT NOW(),
    cancelado_em TIMESTAMPTZ NULL,
    cancelado_por INTEGER NULL,
    CHECK (fim_utc > inicio_utc)
);
CREATE INDEX IF NOT EXISTS ix_motoboy_pacotes_loja_vigencia
    ON motoboy_pacotes (estabelecimento_id, fim_utc) WHERE cancelado_em IS NULL;

-- Entrega manual (sem app do motoboy): a loja marca saiu e entregue. Colunas com fuso para o mapa recortar pela janela
-- (horario_saida/horario_entrega sao texto em algumas instalacoes e continuam preenchidas por compatibilidade).
ALTER TABLE pedido ADD COLUMN IF NOT EXISTS entregador_nome TEXT NULL;
ALTER TABLE pedido ADD COLUMN IF NOT EXISTS entrega_manual_saiu_utc TIMESTAMPTZ NULL;
ALTER TABLE pedido ADD COLUMN IF NOT EXISTS entrega_manual_entregue_utc TIMESTAMPTZ NULL;

-- Catalogo: base de motoboys por plano e composicao nova.
ALTER TABLE pacotes_comerciais ADD COLUMN IF NOT EXISTS motoboys_base INTEGER NOT NULL DEFAULT 0;
UPDATE pacotes_comerciais SET nome = 'Partida', motoboys_base = 0,
       modulos_ativos = ARRAY['GERAL','CARDAPIO','CARDAPIOWEB','PEDIDOS','CLIENTES','IMPRESSAO','IFOOD','WHATSAPP']::modulo_enum[]
 WHERE chave = 'partida';
UPDATE pacotes_comerciais SET nome = 'Rota', motoboys_base = 0,
       modulos_ativos = ARRAY['GERAL','CARDAPIO','CARDAPIOWEB','PEDIDOS','CLIENTES','IMPRESSAO','IFOOD','WHATSAPP','MAPA']::modulo_enum[]
 WHERE chave = 'rota';
UPDATE pacotes_comerciais SET nome = 'Turbo', motoboys_base = 3,
       modulos_ativos = ARRAY['GERAL','CARDAPIO','CARDAPIOWEB','PEDIDOS','CLIENTES','IMPRESSAO','IFOOD','WHATSAPP','MAPA','DELIVERY','REMUNERACAO']::modulo_enum[]
 WHERE chave = 'turbo';
UPDATE pacotes_comerciais SET nome = 'Frota', motoboys_base = 5,
       modulos_ativos = ARRAY['GERAL','CARDAPIO','CARDAPIOWEB','PEDIDOS','CLIENTES','IMPRESSAO','IFOOD','WHATSAPP','MAPA','DELIVERY','REMUNERACAO','INTELIGENCIA']::modulo_enum[]
 WHERE chave = 'frota';

-- Lojas ja ligadas a um plano ganham o que a composicao nova traz, sem perder nada que tinham (decisao do dono:
-- lojas existentes mantem tudo). A base de motoboys so sobe.
UPDATE estabelecimentos e
   SET modulos_ativos = ARRAY(SELECT DISTINCT unnest(e.modulos_ativos || p.modulos_ativos)),
       motoboys_base = GREATEST(e.motoboys_base, p.motoboys_base)
  FROM pacotes_comerciais p
 WHERE e.plano_comercial = p.chave;

INSERT INTO delivery_tracking_schema_versions (version)
VALUES ('20261022_02_planos_v2')
ON CONFLICT (version) DO NOTHING;

COMMIT;
