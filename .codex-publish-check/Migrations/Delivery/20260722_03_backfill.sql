BEGIN;

-- Garante perfil para todo usuario com papel ativo de motoboy. Nao associa por nome.
INSERT INTO motoboy (nome, status, id_usuario, id_estabelecimento, is_simulated)
SELECT COALESCE(NULLIF(TRIM(u.nome), ''), u.email, 'Motoboy'),
       2,
       u.id,
       (ARRAY_AGG(ue.id_estabelecimento ORDER BY ue.created_at, ue.id_estabelecimento))[1],
       FALSE
  FROM usuario u
  JOIN usuario_estabelecimentos ue ON ue.id_usuario = u.id
 WHERE u.deleted_at IS NULL
   AND LOWER(COALESCE(ue.tipo_acesso, '')) = 'motoboy'
   AND COALESCE(ue.ativo, TRUE) = TRUE
   AND LOWER(COALESCE(ue.status, 'ativo')) = 'ativo'
   AND NOT EXISTS (SELECT 1 FROM motoboy m WHERE m.id_usuario = u.id)
 GROUP BY u.id, u.nome, u.email;

-- O menor id do mesmo usuario vira canônico; linhas restantes permanecem como aliases.
WITH canonical AS (
    SELECT id_usuario, MIN(id) AS canonical_id
      FROM motoboy
     WHERE id_usuario IS NOT NULL
     GROUP BY id_usuario
)
UPDATE motoboy m
   SET canonical_motoboy_id = c.canonical_id
  FROM canonical c
 WHERE m.id_usuario = c.id_usuario;

UPDATE motoboy
   SET canonical_motoboy_id = id
 WHERE canonical_motoboy_id IS NULL;

-- Vínculos legados válidos.
INSERT INTO motoboy_estabelecimento (
    motoboy_id, estabelecimento_id, ativo, simulator_enabled, created_at_utc, updated_at_utc)
SELECT DISTINCT m.canonical_motoboy_id,
       m.id_estabelecimento,
       TRUE,
       FALSE,
       NOW(),
       NOW()
  FROM motoboy m
  JOIN estabelecimentos e ON e.id = m.id_estabelecimento
 WHERE m.id_estabelecimento IS NOT NULL
ON CONFLICT (motoboy_id, estabelecimento_id) DO NOTHING;

-- Vínculos oriundos da autorização são a fonte para motoboys reais.
INSERT INTO motoboy_estabelecimento (
    motoboy_id, estabelecimento_id, ativo, simulator_enabled, created_at_utc, updated_at_utc)
SELECT DISTINCT m.canonical_motoboy_id,
       ue.id_estabelecimento,
       TRUE,
       FALSE,
       COALESCE(ue.created_at, NOW()),
       NOW()
  FROM usuario_estabelecimentos ue
  JOIN motoboy m ON m.id_usuario = ue.id_usuario
 WHERE LOWER(COALESCE(ue.tipo_acesso, '')) = 'motoboy'
   AND COALESCE(ue.ativo, TRUE) = TRUE
   AND LOWER(COALESCE(ue.status, 'ativo')) = 'ativo'
ON CONFLICT (motoboy_id, estabelecimento_id) DO NOTHING;

-- Normaliza sessoes legadas sem invalidar a mais recente ainda dentro do TTL.
UPDATE motoboy_active_sessions s
   SET motoboy_id = m.canonical_motoboy_id
  FROM motoboy m
 WHERE s.motoboy_id = m.id
   AND s.motoboy_id <> m.canonical_motoboy_id;

UPDATE motoboy_active_sessions
   SET session_epoch = COALESCE(session_epoch, nextval('motoboy_session_epoch_seq')),
       origin = CASE
           WHEN LOWER(COALESCE(origin, device_type, 'mobile')) = 'simulator' THEN 'simulator'
           ELSE 'mobile'
       END,
       started_at_utc = COALESCE(started_at_utc, created_at, NOW()),
       last_heartbeat_at_utc = COALESCE(last_heartbeat_at_utc, last_seen_at, created_at, NOW()),
       expires_at_utc = COALESCE(expires_at_utc, COALESCE(last_seen_at, created_at, NOW()) + INTERVAL '90 seconds'),
       ended_at_utc = COALESCE(ended_at_utc, revoked_at),
       end_reason = COALESCE(end_reason, revoke_reason);

UPDATE motoboy_active_sessions
   SET ended_at_utc = NOW(),
       end_reason = COALESCE(end_reason, 'migration_expired'),
       revoked_at = COALESCE(revoked_at, NOW()),
       revoke_reason = COALESCE(revoke_reason, 'migration_expired')
 WHERE ended_at_utc IS NULL
   AND revoked_at IS NULL
   AND expires_at_utc <= NOW();

-- Sessao sem vínculo ativo não é operacionalmente válida.
UPDATE motoboy_active_sessions s
   SET ended_at_utc = NOW(),
       end_reason = 'migration_no_active_link',
       revoked_at = COALESCE(revoked_at, NOW()),
       revoke_reason = 'migration_no_active_link'
 WHERE s.ended_at_utc IS NULL
   AND s.revoked_at IS NULL
   AND NOT EXISTS (
       SELECT 1
         FROM motoboy_estabelecimento me
        WHERE me.motoboy_id = s.motoboy_id
          AND me.estabelecimento_id = s.id_estabelecimento
          AND me.ativo = TRUE
   );

-- Se aliases mantiveram mais de uma sessão global, preserva somente a mais recente.
WITH ranked AS (
    SELECT session_id,
           ROW_NUMBER() OVER (
               PARTITION BY motoboy_id
               ORDER BY last_heartbeat_at_utc DESC, started_at_utc DESC, session_id
           ) AS rn
      FROM motoboy_active_sessions
     WHERE ended_at_utc IS NULL
       AND revoked_at IS NULL
)
UPDATE motoboy_active_sessions s
   SET ended_at_utc = NOW(),
       end_reason = 'migration_duplicate',
       revoked_at = NOW(),
       revoke_reason = 'migration_duplicate'
  FROM ranked r
 WHERE s.session_id = r.session_id
   AND r.rn > 1;

INSERT INTO delivery_tracking_schema_versions (version)
VALUES ('20260722_03_backfill')
ON CONFLICT (version) DO NOTHING;

COMMIT;
