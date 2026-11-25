-- Verificacao do catalogo de servicos e dos canais de WhatsApp. Somente leitura; nao e aplicada automaticamente.

SELECT codigo, nome, modulos_exigidos, ativo FROM servico_catalogo ORDER BY ordem;

SELECT te.slug, ts.servico_codigo, ts.padrao
  FROM tipo_servico ts JOIN tipo_estabelecimento te ON te.id = ts.id_tipo_estabelecimento
 ORDER BY te.slug, ts.servico_codigo;

SELECT servico_codigo, COUNT(*) AS lojas FROM estabelecimento_servico WHERE ativo GROUP BY servico_codigo ORDER BY servico_codigo;

-- Canais criados e em que estado. 'erro' = ID da Meta que parece telefone (corrigir em Gestao).
SELECT e.nome_fantasia, c.numero_e164, c.phone_number_id, c.status, c.modo_atendimento, c.ultimo_erro
  FROM canal_whatsapp c JOIN estabelecimentos e ON e.id = c.id_estabelecimento
 ORDER BY e.nome_fantasia, c.numero_e164;

-- Linhas de waba_phone que NAO viraram canal (revisar e cadastrar na tela de Gestao).
SELECT motivo, COUNT(*) AS total FROM canal_whatsapp_migracao_pendencia GROUP BY motivo ORDER BY motivo;

SELECT version, applied_at_utc FROM delivery_tracking_schema_versions
 WHERE version = '20261002_07_catalogo_servicos_canais';
