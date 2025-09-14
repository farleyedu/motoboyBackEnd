-- ================= ZIPPYGO AUTOMATION MIGRATION =================
-- Script: 001_create_waba_phone_table.sql
-- Descrição: Cria tabela waba_phone para mapeamento phone_number_id → estabelecimento
-- Data: 2024-01-15
-- ================================================================

-- Criar tabela waba_phone
CREATE TABLE IF NOT EXISTS waba_phone (
    id UUID PRIMARY KEY DEFAULT gen_random_uuid(),
    phone_number_id VARCHAR(50) NOT NULL UNIQUE,
    id_estabelecimento UUID NOT NULL,
    ativo BOOLEAN NOT NULL DEFAULT true,
    descricao VARCHAR(255),
    data_criacao TIMESTAMP WITH TIME ZONE NOT NULL DEFAULT NOW(),
    data_atualizacao TIMESTAMP WITH TIME ZONE NOT NULL DEFAULT NOW()
);

-- Criar índices para performance
CREATE INDEX IF NOT EXISTS idx_waba_phone_phone_number_id ON waba_phone(phone_number_id);
CREATE INDEX IF NOT EXISTS idx_waba_phone_estabelecimento ON waba_phone(id_estabelecimento);
CREATE INDEX IF NOT EXISTS idx_waba_phone_ativo ON waba_phone(ativo);

-- Comentários na tabela
COMMENT ON TABLE waba_phone IS 'Mapeamento entre phone_number_id do WhatsApp Business API e estabelecimentos';
COMMENT ON COLUMN waba_phone.phone_number_id IS 'ID do número de telefone fornecido pelo Meta WhatsApp Business API';
COMMENT ON COLUMN waba_phone.id_estabelecimento IS 'ID do estabelecimento associado ao número';
COMMENT ON COLUMN waba_phone.ativo IS 'Indica se o mapeamento está ativo';
COMMENT ON COLUMN waba_phone.descricao IS 'Descrição opcional do número/estabelecimento';

-- Inserir dados de exemplo (ajustar conforme necessário)
-- INSERT INTO waba_phone (phone_number_id, id_estabelecimento, descricao) 
-- VALUES 
--     ('123456789012345', '11111111-1111-1111-1111-111111111111', 'Estabelecimento Principal'),
--     ('987654321098765', '22222222-2222-2222-2222-222222222222', 'Filial Centro');

-- Verificar se a tabela foi criada corretamente
SELECT 
    table_name, 
    column_name, 
    data_type, 
    is_nullable,
    column_default
FROM information_schema.columns 
WHERE table_name = 'waba_phone' 
ORDER BY ordinal_position;

PRINT 'Tabela waba_phone criada com sucesso!';