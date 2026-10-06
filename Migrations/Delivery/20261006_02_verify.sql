-- Verificacao do cadastro estruturado do motoboy (Fase A). Somente leitura; nao e aplicada automaticamente.

SELECT count(*) AS motoboys,
       count(*) FILTER (WHERE cpf IS NOT NULL) AS com_cpf,
       count(*) FILTER (WHERE status_cadastro = 'ativo') AS ativos,
       count(*) FILTER (WHERE status_cadastro = 'bloqueado') AS bloqueados
  FROM motoboy;

-- Deve voltar vazio: CPF duplicado entre motoboys.
SELECT cpf, count(*) FROM motoboy WHERE cpf IS NOT NULL GROUP BY cpf HAVING count(*) > 1;

SELECT version, applied_at_utc FROM delivery_tracking_schema_versions WHERE version = '20261006_01_motoboy_perfil';
