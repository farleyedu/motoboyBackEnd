-- Migration: Create waba_phone table for WhatsApp Business Account phone number mapping
-- Date: 2025-01-15
-- Purpose: Map WhatsApp phone_number_id to estabelecimento for webhook processing

CREATE TABLE IF NOT EXISTS waba_phone (
    id UUID PRIMARY KEY DEFAULT gen_random_uuid(),
    phone_number_id TEXT NOT NULL UNIQUE,
    id_estabelecimento UUID NOT NULL REFERENCES estabelecimentos(id) ON DELETE CASCADE,
    ativo BOOLEAN NOT NULL DEFAULT true,
    descricao TEXT,
    data_criacao TIMESTAMP WITH TIME ZONE NOT NULL DEFAULT now(),
    data_atualizacao TIMESTAMP WITH TIME ZONE NOT NULL DEFAULT now()
);

-- Create indexes for performance
CREATE INDEX IF NOT EXISTS idx_waba_phone_estabelecimento ON waba_phone(id_estabelecimento);
CREATE INDEX IF NOT EXISTS idx_waba_phone_ativo ON waba_phone(ativo) WHERE ativo = true;

-- Add trigger for auto-updating data_atualizacao
CREATE OR REPLACE FUNCTION update_waba_phone_timestamp()
RETURNS TRIGGER AS $$
BEGIN
    NEW.data_atualizacao = now();
    RETURN NEW;
END;
$$ LANGUAGE plpgsql;

CREATE TRIGGER trigger_update_waba_phone_timestamp
    BEFORE UPDATE ON waba_phone
    FOR EACH ROW
    EXECUTE FUNCTION update_waba_phone_timestamp();

-- Insert example data (replace with your actual phone_number_id and estabelecimento)
-- Uncomment and modify the line below with your real data:
-- INSERT INTO waba_phone (phone_number_id, id_estabelecimento, descricao) 
-- VALUES ('YOUR_PHONE_NUMBER_ID_HERE', (SELECT id FROM estabelecimentos LIMIT 1), 'WhatsApp Principal');

COMMIT;