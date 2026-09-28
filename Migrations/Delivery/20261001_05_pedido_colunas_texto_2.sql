BEGIN;

-- ---------------------------------------------------------------------------
-- Continuacao de 20260927_05_pedido_colunas_texto: aquela migration alargou
-- nome_cliente, endereco_entrega, telefone_cliente, entrega_rua, entrega_bairro,
-- entrega_cidade, region, observacoes e tipo_pagamento, mas deixou de fora outras
-- colunas legadas igualmente estreitas da tabela "pedido" (schema antigo,
-- pre-datando esta pasta de migrations). Um pedido novo (atendente/simulador)
-- voltou a bater em 22001 (value too long / string_data_right_truncation) sem
-- apontar tabela/coluna na excecao -- mesma classe de bug, coluna diferente:
-- desta vez em entrega_numero, entrega_estado, entrega_cep, status_pagamento
-- (o INSERT grava o literal 'Pendente', 8 caracteres) e/ou codigo_entrega
-- (a regra de negocio em ManualOrderRules aceita ate 20 caracteres, mais do
-- que a coluna legada permite). TEXT nao tem limite de tamanho e nao reescreve
-- a tabela (troca so o metadado da coluna). Idempotente.
-- ---------------------------------------------------------------------------

ALTER TABLE pedido
    ALTER COLUMN entrega_numero TYPE TEXT,
    ALTER COLUMN entrega_estado TYPE TEXT,
    ALTER COLUMN entrega_cep TYPE TEXT,
    ALTER COLUMN status_pagamento TYPE TEXT,
    ALTER COLUMN codigo_entrega TYPE TEXT;

INSERT INTO delivery_tracking_schema_versions (version)
VALUES ('20261001_05_pedido_colunas_texto_2')
ON CONFLICT (version) DO NOTHING;

COMMIT;
