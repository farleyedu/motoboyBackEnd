-- Catálogo editável de pacotes. Executar após 20261020_03_modulos_novos.sql.
CREATE TABLE IF NOT EXISTS pacotes_comerciais (
    chave TEXT PRIMARY KEY CHECK (chave IN ('partida', 'rota', 'turbo', 'frota')),
    nome TEXT NOT NULL,
    modulos_ativos modulo_enum[] NOT NULL,
    pacotes_motoboy_base INTEGER NOT NULL CHECK (pacotes_motoboy_base >= 1),
    atualizado_em TIMESTAMPTZ NOT NULL DEFAULT NOW(),
    atualizado_por INTEGER NULL
);

INSERT INTO pacotes_comerciais (chave, nome, modulos_ativos, pacotes_motoboy_base) VALUES
('partida', 'Partida', ARRAY['GERAL','CARDAPIO','CARDAPIOWEB','PEDIDOS','CLIENTES','IMPRESSAO']::modulo_enum[], 1),
('rota', 'Rota', ARRAY['GERAL','CARDAPIO','CARDAPIOWEB','PEDIDOS','CLIENTES','IMPRESSAO','DELIVERY']::modulo_enum[], 1),
('turbo', 'Turbo', ARRAY['GERAL','CARDAPIO','CARDAPIOWEB','PEDIDOS','CLIENTES','IMPRESSAO','DELIVERY','IFOOD','WHATSAPP','REMUNERACAO']::modulo_enum[], 1),
('frota', 'Frota', ARRAY['GERAL','CARDAPIO','CARDAPIOWEB','PEDIDOS','CLIENTES','IMPRESSAO','DELIVERY','IFOOD','WHATSAPP','REMUNERACAO','INTELIGENCIA','FINANCEIRO']::modulo_enum[], 2)
ON CONFLICT (chave) DO NOTHING;

INSERT INTO delivery_tracking_schema_versions (version)
VALUES ('20261021_01_pacotes_comerciais')
ON CONFLICT (version) DO NOTHING;
