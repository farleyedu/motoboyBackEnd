-- Seed pontual do estabelecimento de teste ZippyGo Restaurante: zonas de entrega, entrega gratis e
-- regras de operacao coerentes. So preenche o que ainda nao foi definido (idempotente; nunca
-- sobrescreve o que ja foi editado a mao). Aplicada automaticamente no boot.
BEGIN;

UPDATE estabelecimentos
   SET entrega_gratis_acima_de = COALESCE(entrega_gratis_acima_de, 120.00),
       data_atualizacao = NOW()
 WHERE id = '97c5d396-a42c-47de-8f21-a38c3f79d118'
   AND entrega_gratis_acima_de IS NULL;

INSERT INTO delivery_zona (id, estabelecimento_id, nome, raio_ate_km, taxa, cor, ordem)
SELECT gen_random_uuid(), '97c5d396-a42c-47de-8f21-a38c3f79d118', nome, raio, taxa, cor, ordem
  FROM (VALUES
    ('Zona 1 - Centro e arredores', 5.0, 6.90, '#16A34A', 1),
    ('Zona 2', 8.0, 8.90, '#F59E0B', 2),
    ('Zona 3', 12.0, 11.90, '#DC2626', 3)
  ) AS seed(nome, raio, taxa, cor, ordem)
 WHERE NOT EXISTS (SELECT 1 FROM delivery_zona WHERE estabelecimento_id = '97c5d396-a42c-47de-8f21-a38c3f79d118');

-- So atualiza: a linha de delivery_settings deste estabelecimento ja existe (a tela de
-- configuracoes de delivery ja foi usada nesta sessao de teste). Se nao existir ainda, a proxima
-- vez que a tela salvar cria a linha e este seed deixa de ter efeito (nada a fazer).
UPDATE delivery_settings
   SET retirada_balcao_ativa = TRUE,
       retirada_tempo_preparo_min = COALESCE(retirada_tempo_preparo_min, 15),
       autoatribuir_motoboy = TRUE
 WHERE id_estabelecimento = '97c5d396-a42c-47de-8f21-a38c3f79d118';

INSERT INTO estabelecimento_horario_especial (estabelecimento_id, data, fechado, motivo)
VALUES
    ('97c5d396-a42c-47de-8f21-a38c3f79d118', DATE '2027-01-01', TRUE, 'Ano novo'),
    ('97c5d396-a42c-47de-8f21-a38c3f79d118', DATE '2026-12-25', TRUE, 'Natal')
ON CONFLICT (estabelecimento_id, data) DO NOTHING;

COMMIT;
