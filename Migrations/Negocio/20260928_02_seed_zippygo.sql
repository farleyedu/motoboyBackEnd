-- Seed pontual: preenche com dados coerentes SO os campos que ainda estao vazios do
-- estabelecimento de teste "ZippyGo Restaurante" (id fixo, o mesmo usado nas sessoes de teste
-- desta fundacao). So preenche o que esta NULL/vazio -- nunca sobrescreve o que ja foi editado a
-- mao. Idempotente (rodar de novo nao muda nada depois da primeira vez). Nao e aplicada
-- automaticamente.
BEGIN;

UPDATE estabelecimentos SET
    cnpj_loja = COALESCE(NULLIF(cnpj_loja, ''), '12345678000190'),
    telefone = COALESCE(NULLIF(telefone, ''), '5531912345678'),
    site_url = COALESCE(site_url, 'https://zippygo.com.br'),
    favicon_url = COALESCE(favicon_url, url_logo),
    cor_primaria = COALESCE(cor_primaria, '#16A34A'),
    cor_secundaria = COALESCE(cor_secundaria, '#F97316'),
    tipografia = COALESCE(tipografia, 'Inter'),
    instagram_url = COALESCE(instagram_url, 'https://instagram.com/zippygo'),
    data_atualizacao = NOW()
WHERE id = '97c5d396-a42c-47de-8f21-a38c3f79d118'
  AND (cnpj_loja IS NULL OR NULLIF(cnpj_loja, '') IS NULL
       OR telefone IS NULL OR NULLIF(telefone, '') IS NULL
       OR site_url IS NULL OR cor_primaria IS NULL OR cor_secundaria IS NULL
       OR tipografia IS NULL OR instagram_url IS NULL);

INSERT INTO estabelecimento_horario (estabelecimento_id, dia_semana, fechado, abre_as, fecha_as)
SELECT '97c5d396-a42c-47de-8f21-a38c3f79d118', dia, fechado, abre, fecha
  FROM (VALUES
    (0, FALSE, TIME '11:00', TIME '23:00'), -- segunda
    (1, FALSE, TIME '11:00', TIME '23:00'), -- terca
    (2, FALSE, TIME '11:00', TIME '23:00'), -- quarta
    (3, FALSE, TIME '11:00', TIME '23:00'), -- quinta
    (4, FALSE, TIME '11:00', TIME '23:30'), -- sexta
    (5, FALSE, TIME '11:00', TIME '23:30'), -- sabado
    (6, TRUE,  NULL,         NULL)          -- domingo (fechado)
  ) AS seed(dia, fechado, abre, fecha)
ON CONFLICT (estabelecimento_id, dia_semana) DO NOTHING;

COMMIT;
