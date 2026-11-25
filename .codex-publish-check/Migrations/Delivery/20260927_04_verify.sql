-- Verificacao da Fase 5 (atendimento). Somente leitura; nao e aplicada automaticamente.

-- 1) Tabelas (deve listar 3 linhas).
SELECT to_regclass('estabelecimento_atendimento_config') AS config,
       to_regclass('atendimento_respostas_rapidas')      AS respostas,
       to_regclass('delivery_motoboy_message')           AS mensagens_motoboy;

-- 2) Modo 'ia' antes de existir o modulo de IA (deveria voltar vazio na etapa 1).
SELECT estabelecimento_id FROM estabelecimento_atendimento_config WHERE modo = 'ia';

-- 3) Mensagem ao motoboy com pedido de outro estabelecimento (nunca deveria acontecer).
SELECT m.id FROM delivery_motoboy_message m
  JOIN pedido p ON p.id = m.pedido_id
 WHERE p.id_estabelecimento IS DISTINCT FROM m.estabelecimento_id;

-- 4) Pedido ligado a conversa que nao existe (a coluna nao tem FK; deveria voltar vazio).
SELECT p.id, p.conversa_id FROM pedido p
  LEFT JOIN conversas c ON c.id = p.conversa_id
 WHERE p.conversa_id IS NOT NULL AND c.id IS NULL;

SELECT version, applied_at_utc FROM delivery_tracking_schema_versions WHERE version = '20260927_03_atendimento';
