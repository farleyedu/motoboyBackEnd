-- Verificação da 20261210_01_assinatura_regras: só leitura. Cada linha deve ter ok = true.
SELECT 'assinatura.ativada_em' AS item, EXISTS (SELECT 1 FROM information_schema.columns WHERE table_name = 'assinatura' AND column_name = 'ativada_em') AS ok
UNION ALL SELECT 'colunas novas da cobrança', (SELECT COUNT(DISTINCT column_name) FROM information_schema.columns
    WHERE table_name = 'comercial_cobranca' AND column_name IN ('expira_em', 'divida_valor', 'divida_desde', 'agrupada_em', 'excedente_em',
        'excedente_devolvido', 'excedente_devolvido_em', 'excedente_devolvido_por')) = 8
UNION ALL SELECT 'cortesia e dias no extra', (SELECT COUNT(DISTINCT column_name) FROM information_schema.columns
    WHERE table_name = 'compra_extra' AND column_name IN ('cortesia', 'dias')) = 2
UNION ALL SELECT 'extra aceita cortesia', EXISTS (SELECT 1 FROM pg_constraint WHERE conname = 'ck_compra_extra_valores')
UNION ALL SELECT 'nenhuma assinatura suspensa', NOT EXISTS (SELECT 1 FROM assinatura WHERE financeiro = 'suspensa')
UNION ALL SELECT 'nenhum aviso ameaça suspender', NOT EXISTS (SELECT 1 FROM comercial_aviso WHERE mensagem ILIKE '%suspens%')
UNION ALL SELECT 'versão registrada', EXISTS (SELECT 1 FROM delivery_tracking_schema_versions WHERE version = '20261210_01_assinatura_regras');
