-- Migration: Add unique constraint for message_id to ensure idempotency
-- Date: 2025-01-15
-- Purpose: Prevent duplicate message processing in WhatsApp webhook

-- First, check if we need to add message_id column to conversas table
-- (assuming it might not exist yet based on the repository code)
DO $$
BEGIN
    IF NOT EXISTS (SELECT 1 FROM information_schema.columns 
                   WHERE table_name = 'conversas' AND column_name = 'message_id_whatsapp') THEN
        ALTER TABLE conversas ADD COLUMN message_id_whatsapp TEXT;
    END IF;
END $$;

-- Create unique index for WhatsApp message ID to prevent duplicates
CREATE UNIQUE INDEX IF NOT EXISTS idx_conversas_message_id_whatsapp_unique 
    ON conversas(message_id_whatsapp) 
    WHERE message_id_whatsapp IS NOT NULL;

-- Also add index for performance on lookups
CREATE INDEX IF NOT EXISTS idx_conversas_message_id_whatsapp 
    ON conversas(message_id_whatsapp) 
    WHERE message_id_whatsapp IS NOT NULL;

-- Add comment for documentation
COMMENT ON COLUMN conversas.message_id_whatsapp IS 'WhatsApp message ID for idempotency control';

COMMIT;