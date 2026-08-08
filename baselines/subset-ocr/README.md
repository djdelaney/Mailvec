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
