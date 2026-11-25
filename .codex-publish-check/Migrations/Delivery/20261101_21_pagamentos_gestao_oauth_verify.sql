-- Confere 20261101_20_pagamentos_gestao_oauth (somente leitura; nao e aplicado no boot).
DO $$
BEGIN
    IF to_regclass('mp_ambiente_estado') IS NULL OR to_regclass('mp_verificacao') IS NULL OR to_regclass('pagamento_politica') IS NULL THEN
        RAISE EXCEPTION 'Faltam tabelas da Gestao de Pagamentos';
    END IF;
    IF (SELECT count(*) FROM mp_ambiente_estado) <> 2 THEN
        RAISE EXCEPTION 'mp_ambiente_estado deve ter sandbox e producao';
    END IF;
    IF NOT EXISTS (SELECT 1 FROM pagamento_politica WHERE id = 1) THEN
        RAISE EXCEPTION 'Politica padrao ausente';
    END IF;
    IF EXISTS (SELECT 1 FROM mp_ambiente_estado WHERE lojas_podem_conectar AND NOT habilitado) THEN
        RAISE EXCEPTION 'Lojas liberadas em ambiente desabilitado';
    END IF;
END $$;
