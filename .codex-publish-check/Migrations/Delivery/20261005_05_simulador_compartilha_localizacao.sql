BEGIN;

-- ---------------------------------------------------------------------------
-- O aviso "motoboy chegando" so sai se o motoboy autorizou compartilhar a localizacao
-- (compartilhar_localizacao_cliente, padrao FALSE para motoboy de verdade). Motoboy simulado nao tem
-- esse dilema de privacidade -- o simulador existe pra validar o fluxo de ponta a ponta -- entao passa
-- a nascer com a autorizacao ligada (CreateSimulatorMotoboyAsync) e os simuladores ja criados antes
-- dessa mudanca sao corrigidos aqui.
-- ---------------------------------------------------------------------------

UPDATE motoboy
   SET compartilhar_localizacao_cliente = TRUE
 WHERE is_simulated = TRUE
   AND compartilhar_localizacao_cliente = FALSE;

INSERT INTO delivery_tracking_schema_versions (version)
VALUES ('20261005_05_simulador_compartilha_localizacao')
ON CONFLICT (version) DO NOTHING;

COMMIT;
