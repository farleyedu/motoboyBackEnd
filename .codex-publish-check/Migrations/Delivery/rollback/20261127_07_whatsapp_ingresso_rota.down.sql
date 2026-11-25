-- Desfaz 20261127_07_whatsapp_ingresso_rota.sql DE FORMA PARCIAL e nao destrutiva.
-- Recusa se houver mensagem pendente, em processamento ou bloqueada: o destino capturado no ingresso e a unica garantia de que ela
-- nao sera entregue na loja errada, e o worker anterior recalcularia o destino pelo phone_number_id.
-- Remove so os indices de diagnostico. As colunas de rota/destino/remetente/midia e as constraints NAO sao apagadas: sao nulaveis, o
-- worker antigo as ignora, e guardam a prova de para onde cada mensagem ja processada foi roteada. O CHECK de estado (que aceita
-- 'bloqueado') tambem fica: e um superconjunto do anterior. Remocao fisica das colunas: so manual, apos exportar (rollback/README.md).
BEGIN;

DO $$
DECLARE
    n BIGINT;
BEGIN
    IF EXISTS (SELECT 1 FROM information_schema.columns WHERE table_name = 'wa_evento' AND column_name = 'rota') THEN
        EXECUTE 'SELECT COUNT(*) FROM wa_evento WHERE tipo = ''mensagem'' AND estado IN (''pendente'', ''processando'', ''bloqueado'')' INTO n;
        IF n > 0 THEN
            RAISE EXCEPTION 'Rollback recusado: % mensagem(ns) pendente(s), em processamento ou bloqueada(s). Esvazie a fila antes.', n;
        END IF;
    END IF;
END $$;

DROP INDEX IF EXISTS ix_wa_evento_destino;
DROP INDEX IF EXISTS ix_wa_evento_bloqueado;

COMMIT;
