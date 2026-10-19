BEGIN;
CREATE TABLE IF NOT EXISTS impressao_config (
 estabelecimento_id uuid PRIMARY KEY REFERENCES estabelecimentos(id), automatica boolean NOT NULL DEFAULT false,
 revisao bigint NOT NULL DEFAULT 1, criada_em timestamptz NOT NULL DEFAULT now());
CREATE TABLE IF NOT EXISTS impressao_estacao (
 id uuid PRIMARY KEY, estabelecimento_id uuid NOT NULL REFERENCES estabelecimentos(id), nome text NOT NULL,
 tipo text NOT NULL CHECK(tipo IN ('navegador','agente')), ativa boolean NOT NULL DEFAULT true,
 token_hash text, computador text, ip_local text, versao_agente text, ultimo_sinal_em timestamptz,
 criada_em timestamptz NOT NULL DEFAULT now(), criada_por int NOT NULL, revogada_em timestamptz,
 UNIQUE(estabelecimento_id,id));
CREATE UNIQUE INDEX IF NOT EXISTS impressao_token ON impressao_estacao(token_hash) WHERE token_hash IS NOT NULL;
CREATE TABLE IF NOT EXISTS impressao_estacao_sessao (
 estabelecimento_id uuid NOT NULL, estacao_id uuid PRIMARY KEY, sessao_id uuid NOT NULL,
 usuario_id int NOT NULL, navegador text NOT NULL, ultima_atividade timestamptz NOT NULL,
 FOREIGN KEY(estabelecimento_id,estacao_id) REFERENCES impressao_estacao(estabelecimento_id,id));
CREATE TABLE IF NOT EXISTS impressao_impressora (
 id uuid PRIMARY KEY, estabelecimento_id uuid NOT NULL, estacao_id uuid NOT NULL, dados jsonb NOT NULL,
 FOREIGN KEY(estabelecimento_id,estacao_id) REFERENCES impressao_estacao(estabelecimento_id,id), UNIQUE(estabelecimento_id,id));
CREATE TABLE IF NOT EXISTS impressao_modelo (
 id uuid PRIMARY KEY, estabelecimento_id uuid NOT NULL REFERENCES estabelecimentos(id), dados jsonb NOT NULL,
 UNIQUE(estabelecimento_id,id));
CREATE TABLE IF NOT EXISTS impressao_regra (
 id uuid PRIMARY KEY, estabelecimento_id uuid NOT NULL REFERENCES estabelecimentos(id), dados jsonb NOT NULL,
 UNIQUE(estabelecimento_id,id));
CREATE TABLE IF NOT EXISTS impressao_job (
 id uuid PRIMARY KEY, estabelecimento_id uuid NOT NULL REFERENCES estabelecimentos(id), pedido_id int,
 regra_id uuid, impressora_id uuid, estacao_id uuid, via int NOT NULL DEFAULT 1,
 evento text NOT NULL, chave_unica text NOT NULL UNIQUE, documento jsonb,
 estado text NOT NULL CHECK(estado IN ('pendente','entregue','enviado','impresso','falhou','expirado','cancelado','ignorado')),
 motivo text, origem_disparo text NOT NULL, origem text NOT NULL, usuario_id int, sessao_id uuid,
 criado_em timestamptz NOT NULL DEFAULT now(), entregue_em timestamptz, impresso_em timestamptz,
 expira_em timestamptz NOT NULL, descartado boolean NOT NULL DEFAULT false,
 FOREIGN KEY(estabelecimento_id,estacao_id) REFERENCES impressao_estacao(estabelecimento_id,id),
 FOREIGN KEY(estabelecimento_id,impressora_id) REFERENCES impressao_impressora(estabelecimento_id,id));
CREATE INDEX IF NOT EXISTS impressao_fila ON impressao_job(estabelecimento_id,estacao_id,criado_em) WHERE estado='pendente';
CREATE INDEX IF NOT EXISTS impressao_pedido ON impressao_job(estabelecimento_id,pedido_id,criado_em DESC);
CREATE TABLE IF NOT EXISTS impressao_pareamento (
 codigo_hash text PRIMARY KEY, estabelecimento_id uuid NOT NULL, estacao_id uuid NOT NULL,
 criado_por int NOT NULL, expira_em timestamptz NOT NULL, usado_em timestamptz,
 FOREIGN KEY(estabelecimento_id,estacao_id) REFERENCES impressao_estacao(estabelecimento_id,id));
CREATE TABLE IF NOT EXISTS impressao_rascunho (
 estabelecimento_id uuid NOT NULL REFERENCES estabelecimentos(id), usuario_id int NOT NULL,
 passo text NOT NULL, conteudo jsonb NOT NULL, atualizado_em timestamptz NOT NULL DEFAULT now(),
 PRIMARY KEY(estabelecimento_id,usuario_id));
-- Durable acceptance inbox: written with the business transaction, dispatched only after commit.
CREATE TABLE IF NOT EXISTS impressao_aceite (
 estabelecimento_id uuid NOT NULL, pedido_id int NOT NULL, criado_em timestamptz NOT NULL DEFAULT now(),
 processado_em timestamptz, tentativas int NOT NULL DEFAULT 0, proxima_em timestamptz NOT NULL DEFAULT now(),
 PRIMARY KEY(estabelecimento_id,pedido_id));
CREATE OR REPLACE FUNCTION impressao_capturar_aceite() RETURNS trigger LANGUAGE plpgsql AS $$
BEGIN
 IF NEW.origem <> 'ifood' AND NEW.origem <> 'simulador' AND NEW.status_pedido::text = '1'
    AND (TG_OP='INSERT' OR OLD.status_pedido::text = '6') THEN
   INSERT INTO impressao_aceite(estabelecimento_id,pedido_id)
   SELECT NEW.id_estabelecimento,NEW.id WHERE EXISTS(SELECT 1 FROM impressao_config WHERE estabelecimento_id=NEW.id_estabelecimento)
   ON CONFLICT DO NOTHING;
 END IF;
 RETURN NEW;
END $$;
DROP TRIGGER IF EXISTS impressao_aceite_pedido ON pedido;
CREATE TRIGGER impressao_aceite_pedido AFTER INSERT OR UPDATE OF status_pedido ON pedido FOR EACH ROW EXECUTE FUNCTION impressao_capturar_aceite();
CREATE OR REPLACE FUNCTION impressao_capturar_ifood() RETURNS trigger LANGUAGE plpgsql AS $$
BEGIN
 IF NEW.remote_status='CONFIRMED' AND (TG_OP='INSERT' OR OLD.remote_status IS DISTINCT FROM NEW.remote_status) THEN
  INSERT INTO impressao_aceite(estabelecimento_id,pedido_id)
  SELECT NEW.estabelecimento_id,l.pedido_id FROM ifood_order_links l
  WHERE l.estabelecimento_id=NEW.estabelecimento_id AND l.external_order_id=NEW.external_order_id AND l.merchant_id=NEW.merchant_id
  AND EXISTS(SELECT 1 FROM impressao_config WHERE estabelecimento_id=NEW.estabelecimento_id) ON CONFLICT DO NOTHING;
 END IF;
 RETURN NEW;
END $$;
DROP TRIGGER IF EXISTS impressao_aceite_ifood ON ifood_order_snapshots;
CREATE TRIGGER impressao_aceite_ifood AFTER INSERT OR UPDATE OF remote_status ON ifood_order_snapshots FOR EACH ROW EXECUTE FUNCTION impressao_capturar_ifood();
INSERT INTO delivery_tracking_schema_versions(version) VALUES ('20261019_01_impressao') ON CONFLICT DO NOTHING;
COMMIT;
