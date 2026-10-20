-- Numero central de testes: um unico canal_whatsapp marcado como central. Todas as lojas criadas com "numero de teste"
-- usam este numero e este ID da Meta; quem RECEBE as mensagens e a loja a que o canal esta vinculado no momento
-- (a Gestao troca o vinculo com o mesmo "mover numero" que ja existe). Aditivo e idempotente.

ALTER TABLE canal_whatsapp ADD COLUMN IF NOT EXISTS central BOOLEAN NOT NULL DEFAULT FALSE;

-- No maximo um canal central.
CREATE UNIQUE INDEX IF NOT EXISTS ux_canal_whatsapp_central ON canal_whatsapp (central) WHERE central;
