-- Verificacao dos enderecos do cliente. Somente leitura; nao e aplicada automaticamente.

SELECT count(*) AS enderecos, count(*) FILTER (WHERE principal) AS principais, count(DISTINCT id_cliente) AS clientes
  FROM cliente_enderecos
 WHERE ativo;

-- Deve voltar vazio: cliente com mais de um endereco principal ativo.
SELECT id_cliente, count(*) FROM cliente_enderecos WHERE principal AND ativo GROUP BY id_cliente HAVING count(*) > 1;

SELECT version, applied_at_utc FROM delivery_tracking_schema_versions WHERE version = '20261005_01_cliente_enderecos';
