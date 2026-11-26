-- Limpeza dos dados antigos: cobranças de TROCA DE PLANO e de EXTRA que ficaram em aberto antes das regras de 26/11/2026.
-- Elas não são dívida e, sem data de expiração, o servidor nunca as cancela sozinho.
--
-- NÃO é migração. Rodar à mão, depois da 20261210_01, em duas etapas:
--   1) Rodar só o PASSO 1 e mostrar a lista ao dono.
--   2) Com o "pode" dele, rodar o PASSO 2 inteiro (uma transação).
-- Nada é apagado: cobranças e compras ficam como canceladas.

-- ---------- PASSO 1: lista (só leitura) ----------
SELECT c.id AS cobranca, e.nome_fantasia AS loja, c.tipo, c.descricao, c.valor, c.status, c.created_at AS criada_em,
       c.valor_excedente, ce.status AS situacao_da_compra,
       EXISTS (SELECT 1 FROM pagamento_intent i WHERE i.origem_ref_id = c.id AND i.status IN ('criado', 'aguardando_pagamento', 'processando')) AS tem_pagamento_em_andamento
  FROM comercial_cobranca c
  JOIN estabelecimentos e ON e.id = c.id_estabelecimento
  LEFT JOIN compra_extra ce ON ce.id = c.id_compra
 WHERE c.tipo IN ('upgrade', 'compra_extra') AND c.status IN ('aberta', 'processando') AND c.expira_em IS NULL
 ORDER BY e.nome_fantasia, c.created_at;

-- ---------- PASSO 2: cancelar (só depois do "pode" do dono) ----------
-- Cobranças com pagamento em andamento ficam de fora; rode de novo mais tarde se aparecerem no PASSO 1.
/*
BEGIN;

CREATE TEMP TABLE limpeza_alvo ON COMMIT DROP AS
SELECT c.id, c.id_estabelecimento, c.id_compra FROM comercial_cobranca c
 WHERE c.tipo IN ('upgrade', 'compra_extra') AND c.status IN ('aberta', 'processando') AND c.expira_em IS NULL
   AND NOT EXISTS (SELECT 1 FROM pagamento_intent i WHERE i.origem_ref_id = c.id AND i.status IN ('criado', 'aguardando_pagamento', 'processando'))
   FOR UPDATE;

UPDATE comercial_cobranca c
   SET status = 'cancelada', cancelada_em = NOW(), cancelamento_motivo = 'limpeza_20261210', versao = versao + 1, updated_at = NOW()
  FROM limpeza_alvo a WHERE a.id = c.id;

UPDATE compra_extra ce
   SET status = 'cancelada', cancelada_em = NOW(), versao = versao + 1, updated_at = NOW()
  FROM limpeza_alvo a WHERE a.id_compra = ce.id AND ce.status = 'aguardando_pagamento';

INSERT INTO pagamento_auditoria (id_estabelecimento, entidade, entidade_id, acao, ator_tipo, ator_id, dados)
SELECT id_estabelecimento, 'comercial_cobranca', id::text, 'cancelada', 'sistema', NULL, '{"motivo":"limpeza_20261210"}'::jsonb FROM limpeza_alvo;

SELECT COUNT(*) AS canceladas FROM limpeza_alvo;

COMMIT;
*/
