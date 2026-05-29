# Eval coverage TODO

Gaps and next steps from the 2026-05-29 eval run over the 27-query set
(`~/Library/Application Support/Mailvec/eval/queries.json`).

Baseline results (top-10): keyword NDCG 0.911 / MRR 0.917 / Recall 0.925 ·
semantic 0.799 / 0.856 / 0.847 · **hybrid 0.937 / 0.975 / 0.966**.

## Coverage gaps — features with zero or thin eval coverage

- [ ] **`folder` filter — 0/27.** Relevant docs span 8 folders (INBOX, Shopping,
  Utilities, Travel, Kids, Sent, Finance, Local) but no query filters by one. The
  `folder` leg of `SearchFilterSql.Append` is untested across all three search legs.
- [ ] **`fromExact` filter — 0/27.** Never tested. `fromContains` only appears in 3
  (q017/q018/q025). `fromExact` is the recommended path for "all mail from <addr>".
- [ ] **Graded relevance — 0/27.** Every query uses binary relevance, so NDCG can't
  distinguish "perfect" from "good-enough" ordering. The grade 1/2/3 mechanism is
  unexercised — not testing whether the best hit outranks a near-duplicate.
- [ ] **Query-less browse — 0/27.** `MessageRepository.BrowseByFilters` (date-sorted,
  no-query path behind "show me recent X") has no eval coverage at all.
- [ ] **Deep-history retrieval — thin.** 21/27 queries have `dateFrom` in 2026; only 3
  in 2023, 3 in 2025. Archive spans 2005–2026 (74k msgs). Nothing tests decade-old mail
  where vector drift and corpus dilution are worst.
- [ ] **Attachment-driven matches — incidental, not deliberate.** 25 of 136 relevant
  docs carry extracted-text attachments (133 attachment-source chunks), but no query is
  built so the term lives *only* in the attachment and not the body. No test proves a
  PDF/DOCX-only match surfaces.
- [ ] **Negative / zero-result queries — 0/27.** No query that should return nothing, so
  false-positive behavior (precision under an empty gold set) is untested.
- [ ] **Conversational / human mail — thin.** Set skews heavily transactional (receipts,
  order confirmations, bills). Semantic leg's relative weakness (0.799) reflects this.
  Real back-and-forth human threads, where semantic recall earns its keep, are
  underrepresented.

## Failing queries in the current set (real misses, separate from coverage)

- [ ] **q011 "pool setup scheduled" — recall 0.571.** 3 of 7 relevant docs missed
  entirely. Ranks `[1,2,0,0,6,4,0]`. Worst real failure — investigate first.
- [ ] **q006 "ATA tournament" — MRR 0.333.** Top relevant doc only at rank 4. Ranks
  `[4,3,7,8,9]`. Ranking problem, not recall.
- [ ] **q005 "domain registration renewal" — recall 0.75.** 1 missed. Ranks `[1,2,3,0]`.
- [ ] **q008 "Venmo payment sent" — recall 0.75.** 2 missed. Ranks `[…,0,0]`.
- [ ] **q004 "HOA dues assessments" — NDCG 0.825.** Relevant docs scattered to rank 10.
  Ranks `[1,4,7,6,8,10]`.

## Suggested new queries (highest leverage first)

- [ ] Add a `folder`-filtered query (e.g. scope a Utilities-folder bill).
- [ ] Add a `fromExact` query ("all mail from a specific sender address").
- [ ] Add an attachment-only query — term in an indexed PDF/DOCX but not the email body,
  so a pass proves the attachment pipeline.
- [ ] Add 2–3 deep-history queries (pre-2020 `dateFrom`).
- [ ] Add graded relevance to a few existing multi-hit queries (q002-style) so NDCG can
  reward correct ordering.
- [ ] Investigate q011 (recall 0.571) — worst real failure.

> Before landing any ranking-affecting change, capture an eval baseline first
> (`mailvec eval --json baselines/<date>.json`) — see `baselines/README.md`.
