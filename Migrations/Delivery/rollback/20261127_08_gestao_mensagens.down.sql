-- Desfaz 20261127_08_gestao_mensagens.sql. Apaga o historico de mensagens da Gestao.
BEGIN;
DROP TABLE IF EXISTS gestao_contato_optout;
DROP TABLE IF EXISTS gestao_mensagem;
DROP TABLE IF EXISTS gestao_canal_remetente;
DROP TABLE IF EXISTS gestao_canal;
COMMIT;
