-- Delivery Parte 1 - diagnostico somente leitura.
-- Execute antes das migrations mutaveis e salve o resultado para auditoria.

SELECT id_usuario,
       COUNT(*) AS motoboy_rows,
       ARRAY_AGG(id ORDER BY id) AS motoboy_ids
  FROM motoboy
 WHERE id_usuario IS NOT NULL
 GROUP BY id_usuario
HAVING COUNT(*) > 1
 ORDER BY id_usuario;

SELECT m.id AS motoboy_id,
       m.id_usuario,
       m.id_estabelecimento
  FROM motoboy m
  LEFT JOIN estabelecimentos e ON e.id = m.id_estabelecimento
 WHERE m.id_estabelecimento IS NOT NULL
   AND e.id IS NULL
 ORDER BY m.id;

SELECT ue.id_usuario,
       ue.id_estabelecimento
  FROM usuario_estabelecimentos ue
  LEFT JOIN motoboy m ON m.id_usuario = ue.id_usuario
 WHERE LOWER(COALESCE(ue.tipo_acesso, '')) = 'motoboy'
   AND COALESCE(ue.ativo, TRUE) = TRUE
   AND LOWER(COALESCE(ue.status, 'ativo')) = 'ativo'
   AND m.id IS NULL
 ORDER BY ue.id_usuario, ue.id_estabelecimento;

SELECT s.motoboy_id,
       COUNT(*) AS open_sessions,
       ARRAY_AGG(s.session_id ORDER BY s.last_seen_at DESC, s.created_at DESC) AS session_ids
  FROM motoboy_active_sessions s
 WHERE s.revoked_at IS NULL
 GROUP BY s.motoboy_id
HAVING COUNT(*) > 1
 ORDER BY s.motoboy_id;

