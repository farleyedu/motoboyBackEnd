-- Desfaz 20261121_01_gestao_caixa_acertos.sql. O dinheiro dos acertos fica no livro (liquidacoes e quebra/sobra); some so o lote.
BEGIN;
DROP TABLE IF EXISTS gestao_caixa_acerto_itens;
DROP TABLE IF EXISTS gestao_caixa_acertos;
-- A conta "Dinheiro com motoboys" pode ter lançamentos: o subtipo só volta ao conjunto antigo se nenhuma conta o usar.
DO $$ BEGIN
  IF NOT EXISTS (SELECT 1 FROM gestao_contas WHERE subtipo = 'motoboys') THEN
    ALTER TABLE gestao_contas DROP CONSTRAINT IF EXISTS gestao_contas_subtipo_check;
    ALTER TABLE gestao_contas ADD CONSTRAINT gestao_contas_subtipo_check CHECK (subtipo IN ('ifood', 'maquininha', 'outro'));
  END IF;
END $$;
COMMIT;
