BEGIN;

-- ---------------------------------------------------------------------------
-- Pedidos capturados do iFood passam a ter origem 'ifood' e a referencia externa igual
-- ao id do iFood (base da idempotencia por origem_ref). Os demais ficam 'atendente'
-- (padrao da coluna). Nao apaga nem reescreve nenhum outro dado; rodar de novo nao muda nada.
-- ---------------------------------------------------------------------------

UPDATE pedido
   SET origem = 'ifood',
       origem_ref = id_ifood
 WHERE id_ifood IS NOT NULL
   AND origem = 'atendente'
   AND origem_ref IS NULL;

INSERT INTO delivery_tracking_schema_versions (version)
VALUES ('20260925_03_backfill')
ON CONFLICT (version) DO NOTHING;

COMMIT;
