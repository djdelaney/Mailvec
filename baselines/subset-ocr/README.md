# baselines/subset-ocr

Eval numbers in this folder are measured against the **~/MailvecSubsetOCR test
corpus**, not the frozen full corpus that the JSON files in the parent
`baselines/` directory were measured against. **The two families are not
comparable** — compare subset numbers only with other subset numbers.

Corpus identity (observed 2026-08-07, at the first baseline):

- 662 live messages (697 Maildir files) curated from the Vault source —
  provenance and per-file curation flags in `~/MailvecSubsetOCR/MANIFEST.json`.
- Chosen for OCR coverage: 72 attachments carry vision-recovered text
  (`extraction_status='ocr'`; engine `ollama:qwen2.5vl:7b`), 178 natively
  extracted (`done`), 21 `no_text`, 71 `unsupported`, 4 `encrypted`.
- 7,564 chunks, `mxbai-embed-large` @ 1024d, 100% embed coverage,
  WAL-checkpointed.
- Query set: the 70-query `queries.json` (q001–q072 numbering, includes one
  negative query) installed 2026-08-07 into the default eval location.

Known caveat: the negative query scores 0.000 specificity on this corpus in
all modes (it was authored against the full mailbox; the subset appears to
contain lookalike matches). Judge specificity deltas accordingly.

Binary provenance: `2026-08-07.json` was measured with a **working-tree build
of main** (post-`5baaf6d`, with the ranking-neutral v11 space-identity change
— neutrality verified by an A/B against `5baaf6d` on an identical DB copy:
per-query results bit-identical). An earlier capture of this file used the
stale July-2 published CLI, which predates main's KNN-escalation change and
scored lower across the board (keyword NDCG 0.877 vs 0.906); it was replaced
the same day and must not be compared against. Baselines here are only valid
against the binary lineage that produced them — record the commit when
capturing a new one.

Neutrality gates run against this baseline (per-query results bit-identical,
timing stripped): phase 2a (`ff21281`), phase 2b (`5a45148`), and the
post-review hardening (read-side guard + v2 config hash), each measured with
the working tree at that commit against this corpus.
