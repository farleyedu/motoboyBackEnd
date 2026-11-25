-- Desfaz 20261127_17_crm_jornada_comercial.sql. Remove colunas e restricoes novas e traz status, estados e tipos de volta para os valores antigos.
-- Nao recoloca em Proposta quem voltou para Conversa, nao apaga as versoes vindas do /crm e nao mexe em acompanhamento_historico: o historico fica.
-- Perde envio, canal, resposta, resultado das atividades, categoria e acao esperada. Com dados assim, exige confirmacao explicita (exporte antes).
BEGIN;

-- Trava: recusa se ja houver registro que so existe com a jornada nova, a menos que a sessao confirme (SET zippy.rollback_confirmado = 'sim').
DO $$
BEGIN
    IF COALESCE(current_setting('zippy.rollback_confirmado', true), '') = 'sim' THEN RETURN; END IF;
    IF EXISTS (SELECT 1 FROM acompanhamento_proposta_versao
                WHERE status IN ('em_negociacao', 'recusada', 'substituida') OR (canal IS NOT NULL AND canal <> 'nao_informado') OR resposta IS NOT NULL)
       OR EXISTS (SELECT 1 FROM acompanhamento_proximo_passo WHERE estado IN ('feito', 'sem_sucesso', 'reagendado', 'cancelado') OR resultado IS NOT NULL)
       OR EXISTS (SELECT 1 FROM acompanhamento_marcacao WHERE categoria IS NOT NULL OR acao_esperada IS NOT NULL) THEN
        RAISE EXCEPTION 'Rollback recusado: ha envios, respostas ou atividades concluidas (historico comercial). Exporte e rode com SET zippy.rollback_confirmado = ''sim''.';
    END IF;
END $$;

DROP INDEX IF EXISTS ix_acompanhamento_proximo_passo_origem;
DROP INDEX IF EXISTS ix_acompanhamento_proximo_passo_realizado;
DROP INDEX IF EXISTS ux_acompanhamento_proposta_chave_envio;
DROP INDEX IF EXISTS ux_acompanhamento_proposta_aberta;
DROP INDEX IF EXISTS ux_acompanhamento_proposta_rascunho;

ALTER TABLE acompanhamento_marcacao DROP CONSTRAINT IF EXISTS ck_acompanhamento_marcacao_tipo;
UPDATE acompanhamento_marcacao SET tipo = 'atencao' WHERE tipo = 'precisa_ajuda';
ALTER TABLE acompanhamento_marcacao ADD CONSTRAINT acompanhamento_marcacao_tipo_check CHECK (tipo IN ('atencao', 'risco_de_saida', 'nao_incomodar'));
ALTER TABLE acompanhamento_marcacao DROP COLUMN IF EXISTS acao_esperada, DROP COLUMN IF EXISTS categoria;

ALTER TABLE acompanhamento_proximo_passo DROP CONSTRAINT IF EXISTS fk_acompanhamento_proximo_passo_origem;
ALTER TABLE acompanhamento_proximo_passo DROP CONSTRAINT IF EXISTS ck_acompanhamento_proximo_passo_resultado;
ALTER TABLE acompanhamento_proximo_passo DROP CONSTRAINT IF EXISTS ck_acompanhamento_proximo_passo_estado;
UPDATE acompanhamento_proximo_passo SET estado = CASE WHEN estado IN ('feito', 'sem_sucesso') THEN 'feito' ELSE 'substituido' END
 WHERE estado IN ('feito', 'sem_sucesso', 'reagendado', 'cancelado');
ALTER TABLE acompanhamento_proximo_passo ADD CONSTRAINT acompanhamento_proximo_passo_estado_check CHECK (estado IN ('aberto', 'substituido', 'feito'));
ALTER TABLE acompanhamento_proximo_passo
    DROP COLUMN IF EXISTS encerrado_motivo, DROP COLUMN IF EXISTS origem_passo_id, DROP COLUMN IF EXISTS realizado_em,
    DROP COLUMN IF EXISTS resultado_nota, DROP COLUMN IF EXISTS resultado;

ALTER TABLE acompanhamento_proposta_versao DROP CONSTRAINT IF EXISTS ck_acompanhamento_proposta_resposta;
ALTER TABLE acompanhamento_proposta_versao DROP CONSTRAINT IF EXISTS ck_acompanhamento_proposta_canal;
ALTER TABLE acompanhamento_proposta_versao DROP CONSTRAINT IF EXISTS ck_acompanhamento_proposta_status;
UPDATE acompanhamento_proposta_versao SET status = CASE WHEN status = 'em_negociacao' THEN 'enviada' ELSE 'guardada' END
 WHERE status IN ('em_negociacao', 'recusada', 'substituida');
ALTER TABLE acompanhamento_proposta_versao ADD CONSTRAINT acompanhamento_proposta_versao_status_check CHECK (status IN ('rascunho', 'enviada', 'aceita', 'guardada'));
ALTER TABLE acompanhamento_proposta_versao
    DROP COLUMN IF EXISTS chave_resposta, DROP COLUMN IF EXISTS chave_envio, DROP COLUMN IF EXISTS edicao, DROP COLUMN IF EXISTS atualizada_em,
    DROP COLUMN IF EXISTS valor_zero_consciente, DROP COLUMN IF EXISTS resposta_em, DROP COLUMN IF EXISTS resposta_nota, DROP COLUMN IF EXISTS resposta,
    DROP COLUMN IF EXISTS revisa_versao, DROP COLUMN IF EXISTS origem, DROP COLUMN IF EXISTS evidencia_mensagem_id, DROP COLUMN IF EXISTS apresentada_por,
    DROP COLUMN IF EXISTS destinatario, DROP COLUMN IF EXISTS canal;

COMMIT;
