BEGIN;
ALTER TABLE cardapio_produto ADD COLUMN IF NOT EXISTS atencao_motoboy boolean NOT NULL DEFAULT FALSE;
ALTER TABLE cardapio_grupo_adicional ADD COLUMN IF NOT EXISTS atencao_motoboy boolean NOT NULL DEFAULT FALSE;
COMMIT;
