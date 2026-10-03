-- LIMPEZA MANUAL (NAO e aplicada no boot: esta pasta nao e lida pelo executor de migrations).
--
-- Remove as tabelas dos atendimentos verticais (garagem, nautica, oficina, servicos) e das regras de IA antigas, cujo
-- codigo foi apagado do backend. Sem clientes em producao nesses verticais (decisao do dono, 02/10/2026).
-- Rodar so depois de conferir, no banco de producao, que as tabelas estao mesmo sem uso:
--   SELECT relname, n_live_tup FROM pg_stat_user_tables
--    WHERE relname IN ('cliente_garagem','cliente_nautica','cliente_oficina','cliente_servicos','ia_regras','ia_respostas',
--                      'garagem_veiculo','garagem_config','oficina_agendamentos');
--
-- NAO mexe em: waba_phone (ainda alimenta o adaptador de numeros), reservas (usada pelo CRM), estabelecimentos.whatsapp_e164.
-- Reversao: so por backup. Para desfazer o que ja foi aplicado, restaure o dump antes de rodar.

BEGIN;

DROP TABLE IF EXISTS cliente_garagem_simulacao_arquivo;
DROP TABLE IF EXISTS cliente_garagem_simulacao;
DROP TABLE IF EXISTS garagem_veiculo_foto;
DROP TABLE IF EXISTS garagem_veiculo;
DROP TABLE IF EXISTS garagem_config;
DROP TABLE IF EXISTS cliente_garagem;
DROP TABLE IF EXISTS cliente_nautica;
DROP TABLE IF EXISTS cliente_oficina;
DROP TABLE IF EXISTS cliente_servicos;
DROP TABLE IF EXISTS oficina_agendamentos;
DROP TABLE IF EXISTS ia_respostas;
DROP TABLE IF EXISTS ia_regras;

COMMIT;
