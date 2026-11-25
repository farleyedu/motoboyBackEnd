BEGIN;

-- ---------------------------------------------------------------------------
-- O pedido manual (atendente/simulador) grava nome, endereco, telefone e observacoes
-- digitados livremente pelo usuario. As colunas legadas de "pedido" sao varchar(n)
-- estreitos de um schema antigo, e um endereco real (rua + numero + bairro, ex.:
-- "Avenida Getulio Vargas, 1126 - Osvaldo Rezende") ou um bairro mais longo passava
-- do limite: Postgres recusava com 22001 (string_data_right_truncation) sem apontar
-- tabela/coluna na excecao, e a API so conseguia devolver "o banco recusou um dos
-- valores enviados". TEXT nao tem limite de tamanho e nao reescreve a tabela (troca
-- so o metadado da coluna, como ja foi feito para motoboy.avatar em 20260930_01).
-- Idempotente (rodar de novo com a coluna ja TEXT nao da erro).
-- ---------------------------------------------------------------------------

ALTER TABLE pedido
    ALTER COLUMN nome_cliente TYPE TEXT,
    ALTER COLUMN endereco_entrega TYPE TEXT,
    ALTER COLUMN telefone_cliente TYPE TEXT,
    ALTER COLUMN entrega_rua TYPE TEXT,
    ALTER COLUMN entrega_bairro TYPE TEXT,
    ALTER COLUMN entrega_cidade TYPE TEXT,
    ALTER COLUMN region TYPE TEXT,
    ALTER COLUMN observacoes TYPE TEXT,
    ALTER COLUMN tipo_pagamento TYPE TEXT;

INSERT INTO delivery_tracking_schema_versions (version)
VALUES ('20260927_05_pedido_colunas_texto')
ON CONFLICT (version) DO NOTHING;

COMMIT;
