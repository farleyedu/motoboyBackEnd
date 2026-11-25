-- Desfaz 20261127_06_canais_teste.sql. Reverta ANTES 20261127_07 se quiser voltar ao estado anterior por completo (veja o README).
-- Preserva id, token, numero, Phone Number ID, destino (id_estabelecimento) e a coluna central: so saem as marcacoes de teste.
-- Recusa se existir numero de teste que nao e o principal, finalidade diferente de 'geral' ou evento de Gestao ainda pendente: nesses
-- casos a marcacao e o unico jeito de impedir que o numero vire canal de cliente ou que a mensagem seja entregue na loja errada.
BEGIN;

DO $$
DECLARE
    n BIGINT;
BEGIN
    IF EXISTS (SELECT 1 FROM information_schema.columns WHERE table_name = 'canal_whatsapp' AND column_name = 'teste') THEN
        EXECUTE 'SELECT COUNT(*) FROM canal_whatsapp WHERE teste AND NOT central' INTO n;
        IF n > 0 THEN
            RAISE EXCEPTION 'Rollback recusado: % numero(s) de teste que nao sao o principal. Desative ou remova esses canais antes.', n;
        END IF;
        EXECUTE 'SELECT COUNT(*) FROM canal_whatsapp WHERE finalidade_teste <> ''geral''' INTO n;
        IF n > 0 THEN
            RAISE EXCEPTION 'Rollback recusado: % canal(is) com finalidade de teste diferente de geral (ex.: gestao).', n;
        END IF;
    END IF;
    IF EXISTS (SELECT 1 FROM information_schema.columns WHERE table_name = 'wa_evento' AND column_name = 'rota') THEN
        EXECUTE 'SELECT COUNT(*) FROM wa_evento WHERE rota = ''gestao'' AND estado IN (''pendente'', ''processando'', ''bloqueado'')' INTO n;
        IF n > 0 THEN
            RAISE EXCEPTION 'Rollback recusado: % evento(s) de Gestao ainda pendentes ou bloqueados. Processe ou trate antes.', n;
        END IF;
    END IF;
END $$;

DROP INDEX IF EXISTS ix_canal_whatsapp_teste;
ALTER TABLE IF EXISTS canal_whatsapp DROP CONSTRAINT IF EXISTS ck_canal_whatsapp_teste_origem;
ALTER TABLE IF EXISTS canal_whatsapp DROP CONSTRAINT IF EXISTS ck_canal_whatsapp_central_teste;
ALTER TABLE IF EXISTS canal_whatsapp DROP CONSTRAINT IF EXISTS ck_canal_whatsapp_finalidade_teste;
ALTER TABLE IF EXISTS canal_whatsapp DROP COLUMN IF EXISTS finalidade_teste, DROP COLUMN IF EXISTS teste;

COMMIT;
