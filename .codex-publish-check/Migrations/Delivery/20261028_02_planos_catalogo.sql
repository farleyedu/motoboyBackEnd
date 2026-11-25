-- Catalogo da Gestao (zippy-admin, /apps/gestao/catalogo): planos criados pela Gestao, com preco, cara e arquivamento,
-- e produtos avulsos vendidos em cima de qualquer plano. Os quatro planos de 20/10 ganham o preco de tabela aprovado.
-- Aditiva e idempotente. Rollback em rollback/20261028_02_planos_catalogo.down.sql.
BEGIN;

-- A chave deixa de ser uma das quatro fixas: qualquer chave curta em minusculas, menos 'personalizado'.
ALTER TABLE pacotes_comerciais DROP CONSTRAINT IF EXISTS pacotes_comerciais_chave_check;
DO $$
BEGIN
    IF NOT EXISTS (SELECT 1 FROM pg_constraint WHERE conname = 'ck_pacotes_comerciais_chave') THEN
        ALTER TABLE pacotes_comerciais ADD CONSTRAINT ck_pacotes_comerciais_chave
            CHECK (chave ~ '^[a-z0-9_]{2,30}$' AND chave <> 'personalizado');
    END IF;
END $$;

ALTER TABLE pacotes_comerciais ADD COLUMN IF NOT EXISTS preco NUMERIC(10, 2) NOT NULL DEFAULT 0;
ALTER TABLE pacotes_comerciais ADD COLUMN IF NOT EXISTS preco_anual NUMERIC(10, 2) NULL;
ALTER TABLE pacotes_comerciais ADD COLUMN IF NOT EXISTS cor TEXT NULL;
ALTER TABLE pacotes_comerciais ADD COLUMN IF NOT EXISTS icone TEXT NULL;
ALTER TABLE pacotes_comerciais ADD COLUMN IF NOT EXISTS frase TEXT NULL;
ALTER TABLE pacotes_comerciais ADD COLUMN IF NOT EXISTS arquivado BOOLEAN NOT NULL DEFAULT FALSE;
ALTER TABLE pacotes_comerciais ADD COLUMN IF NOT EXISTS ordem INTEGER NOT NULL DEFAULT 100;

UPDATE pacotes_comerciais SET preco = 149, preco_anual = 1490, cor = '#f97316', icone = 'utensils', frase = 'Para vender online sem complicação', ordem = 1 WHERE chave = 'partida' AND preco = 0;
UPDATE pacotes_comerciais SET preco = 249, preco_anual = 2490, cor = '#0ea5e9', icone = 'map', frase = 'Entregas próprias com rastreio', ordem = 2 WHERE chave = 'rota' AND preco = 0;
UPDATE pacotes_comerciais SET preco = 349, preco_anual = 3490, cor = '#10b981', icone = 'rocket', frase = 'Todos os canais num quadro só', ordem = 3 WHERE chave = 'turbo' AND preco = 0;
UPDATE pacotes_comerciais SET preco = 549, preco_anual = 5490, cor = '#7c3aed', icone = 'truck', frase = 'Operação grande, com lucro na mão', ordem = 4 WHERE chave = 'frota' AND preco = 0;

-- Avulsos: o que se vende a mais em cima de qualquer plano (ex.: +3 motoboys, Atendimento avulso).
CREATE TABLE IF NOT EXISTS produtos_avulsos (
    id             BIGSERIAL PRIMARY KEY,
    nome           TEXT NOT NULL,
    preco          NUMERIC(10, 2) NOT NULL CHECK (preco >= 0),
    modulos_ativos modulo_enum[] NOT NULL DEFAULT ARRAY[]::modulo_enum[],
    motoboys       INTEGER NOT NULL DEFAULT 0 CHECK (motoboys >= 0),
    cor            TEXT NULL,
    arquivado      BOOLEAN NOT NULL DEFAULT FALSE,
    criado_em      TIMESTAMPTZ NOT NULL DEFAULT NOW(),
    atualizado_em  TIMESTAMPTZ NOT NULL DEFAULT NOW()
);

INSERT INTO produtos_avulsos (nome, preco, motoboys, cor)
SELECT '+3 motoboys', 79, 3, '#16a34a' WHERE NOT EXISTS (SELECT 1 FROM produtos_avulsos);

COMMIT;
