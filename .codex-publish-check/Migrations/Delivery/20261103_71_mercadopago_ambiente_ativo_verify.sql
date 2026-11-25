-- Conferencia da 20261103_70 (somente leitura). Cada linha deve sair com ok = true.
SELECT 'mp_ambiente_ativo' AS item, to_regclass('mp_ambiente_ativo') IS NOT NULL AS ok;
