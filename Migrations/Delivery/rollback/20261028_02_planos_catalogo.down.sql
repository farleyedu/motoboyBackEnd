BEGIN;
DROP TABLE IF EXISTS produtos_avulsos;
-- Os planos criados pela Gestao precisam sair antes de voltar a trava das quatro chaves.
UPDATE estabelecimentos SET plano_comercial = 'personalizado' WHERE plano_comercial NOT IN ('partida', 'rota', 'turbo', 'frota', 'personalizado') AND plano_comercial IS NOT NULL;
DELETE FROM pacotes_comerciais WHERE chave NOT IN ('partida', 'rota', 'turbo', 'frota');
ALTER TABLE pacotes_comerciais DROP CONSTRAINT IF EXISTS ck_pacotes_comerciais_chave;
ALTER TABLE pacotes_comerciais ADD CONSTRAINT pacotes_comerciais_chave_check CHECK (chave IN ('partida', 'rota', 'turbo', 'frota'));
ALTER TABLE pacotes_comerciais DROP COLUMN IF EXISTS preco, DROP COLUMN IF EXISTS preco_anual, DROP COLUMN IF EXISTS cor,
    DROP COLUMN IF EXISTS icone, DROP COLUMN IF EXISTS frase, DROP COLUMN IF EXISTS arquivado, DROP COLUMN IF EXISTS ordem;
COMMIT;
