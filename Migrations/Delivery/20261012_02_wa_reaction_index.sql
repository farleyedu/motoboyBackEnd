-- O predicado acompanha a busca das reações; outros eventos não entram no índice.
CREATE INDEX IF NOT EXISTS ix_wa_evento_reaction_message_id
ON wa_evento ((payload->'message'->'reaction'->>'message_id'), recebido_em DESC)
WHERE tipo='mensagem' AND payload->'message'->>'type'='reaction';
