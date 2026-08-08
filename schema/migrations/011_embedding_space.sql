-- v11: embedding-space identity (phase 1 of docs/proposals/embedding-providers.md).
--
-- Stamps metadata.embedding_space_id for existing databases from the
-- database's OWN stored model + dimensions — not from config, which may
-- disagree with what actually produced the stored vectors. The shape
-- 'ollama:<model>:<dimensions>' must stay in lockstep with
-- EmbeddingSpace.LegacySpaceId.
--
-- metadata.embedding_config_hash is deliberately NOT stamped here: it covers
-- config-side text transforms (Ollama:QueryInstructionPrefix) that SQL cannot
-- see. SchemaMigrator.StampConfigHashIfMissing computes it in code after
-- migration, and only when the binary's config agrees with the stored
-- model/dimensions — a hash asserted from mismatched config would be a false
-- provenance claim.
--
-- Vectors are untouched: this migration records identity, it does not change it.

INSERT INTO metadata(key, value)
SELECT 'embedding_space_id',
       'ollama:'
       || (SELECT value FROM metadata WHERE key = 'embedding_model')
       || ':'
       || (SELECT value FROM metadata WHERE key = 'embedding_dimensions')
WHERE NOT EXISTS (SELECT 1 FROM metadata WHERE key = 'embedding_space_id')
  AND EXISTS (SELECT 1 FROM metadata WHERE key = 'embedding_model')
  AND EXISTS (SELECT 1 FROM metadata WHERE key = 'embedding_dimensions');
