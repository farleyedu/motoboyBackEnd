-- REGISTRO MANUAL (NAO e aplicado no boot: esta pasta nao e lida pelo executor de migrations).
--
-- Registra em ifood_produto_vinculo as chaves (items[].id) dos pedidos iFood que ja existiam antes do vinculo, lendo os snapshots
-- oficiais, e tenta ligar cada chave nova ao produto da loja pela regra do nome (exatamente UM produto ativo com o mesmo nome,
-- ignorando caixa e espacos; acento diferencia). Zero ou mais de um deixa a linha 'pendente'.
--
-- Pre-requisito: 20261024_01_ifood_produto_vinculo.sql aplicada.
-- Idempotente: pode rodar de novo a qualquer momento.
--   * chave que ja existe NAO e tocada (nem contagem, nem estado) - ON CONFLICT DO NOTHING;
--   * 'desfeito' por pessoa e 'confirmado' nunca mudam; so 'pendente' com nome unico e ligado.
-- Nao altera pedido, snapshot, valores nem cardapio. Reversao: DELETE das linhas com origem 'nome' ou DROP via rollback/.down.sql.
BEGIN;

WITH itens AS (
    SELECT s.estabelecimento_id, s.merchant_id, s.external_order_id,
           btrim(i->>'id') AS chave_id,
           nullif(btrim(i->>'name'), '') AS nome,
           nullif(btrim(i->>'externalCode'), '') AS external_code,
           CASE WHEN i->>'imageUrl' ~* '^https?://' AND length(i->>'imageUrl') <= 2048 THEN btrim(i->>'imageUrl') END AS imagem_url,
           l.created_at_utc AS visto_em
    FROM ifood_order_snapshots s
    JOIN ifood_order_links l USING (merchant_id, external_order_id, estabelecimento_id)
    CROSS JOIN LATERAL jsonb_array_elements(CASE WHEN jsonb_typeof(s.payload->'items') = 'array' THEN s.payload->'items' ELSE '[]'::jsonb END) AS i
    WHERE jsonb_typeof(i) = 'object'
      AND nullif(btrim(i->>'id'), '') IS NOT NULL
      AND nullif(btrim(i->>'name'), '') IS NOT NULL
), agregados AS (
    SELECT estabelecimento_id, merchant_id, chave_id,
           (array_agg(nome ORDER BY visto_em DESC))[1] AS nome,
           (array_agg(external_code ORDER BY visto_em DESC) FILTER (WHERE external_code IS NOT NULL))[1] AS external_code,
           (array_agg(imagem_url ORDER BY visto_em DESC) FILTER (WHERE imagem_url IS NOT NULL))[1] AS imagem_url,
           count(DISTINCT external_order_id)::int AS pedidos,
           min(visto_em) AS primeira, max(visto_em) AS ultima
    FROM itens
    GROUP BY estabelecimento_id, merchant_id, chave_id
)
INSERT INTO ifood_produto_vinculo (estabelecimento_id, merchant_id, tipo, chave_id, external_code, nome_ifood, imagem_url,
                                   pedidos_count, visto_primeira_em, visto_ultima_em)
SELECT estabelecimento_id, merchant_id, 'item', chave_id, external_code, nome, imagem_url, pedidos, primeira, ultima
FROM agregados
ON CONFLICT (estabelecimento_id, merchant_id, tipo, chave_id) DO NOTHING;

-- Regra do nome, so para quem ainda esta pendente (desfeito por pessoa nunca e religado automaticamente).
UPDATE ifood_produto_vinculo v
   SET estado = 'confirmado', origem = 'nome', produto_id = m.produto_id, confirmado_em = NOW()
  FROM (
        SELECT v2.id AS vinculo_id, (array_agg(p.id))[1] AS produto_id
          FROM ifood_produto_vinculo v2
          JOIN cardapio_produto p
            ON p.id_estabelecimento = v2.estabelecimento_id AND p.deleted_at IS NULL
           AND lower(btrim(regexp_replace(p.nome, '\s+', ' ', 'g'))) = lower(btrim(regexp_replace(v2.nome_ifood, '\s+', ' ', 'g')))
         WHERE v2.tipo = 'item' AND v2.estado = 'pendente'
         GROUP BY v2.id
        HAVING count(*) = 1
       ) m
 WHERE v.id = m.vinculo_id AND v.estado = 'pendente';

-- Resumo por loja (conferir antes de confirmar a transacao).
SELECT estabelecimento_id, estado, count(*) AS chaves, sum(pedidos_count) AS pedidos
  FROM ifood_produto_vinculo
 GROUP BY estabelecimento_id, estado
 ORDER BY estabelecimento_id, estado;

COMMIT;
