-- O backfill so preenche pedido.id_estabelecimento quando estava NULL antes.
-- Nao ha marcador de "preenchido por esta migration" nas linhas, entao reverter
-- as escreveria de volta para NULL de forma indiscriminada -- isso derrubaria
-- pedidos que hoje ja dependem desse valor (mapa, fila). Por isso este script
-- so lista os candidatos, nao apaga nada. Revise manualmente antes de agir.

SELECT p.id, p.id_estabelecimento, p.motoboy_responsavel
  FROM pedido p
 WHERE p.id_estabelecimento IS NOT NULL
   AND EXISTS (
       SELECT 1
         FROM motoboy m
         JOIN motoboy_estabelecimento me
           ON me.motoboy_id = m.canonical_motoboy_id AND me.ativo = TRUE
        WHERE m.id = p.motoboy_responsavel
          AND me.estabelecimento_id = p.id_estabelecimento
   );
