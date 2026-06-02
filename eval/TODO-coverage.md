# Eval coverage TODO

Gaps and next steps from the 2026-05-29 eval run over the 27-query set
(`~/Library/Application Support/Mailvec/eval/queries.json`).

Baseline results (top-10): keyword NDCG 0.911 / MRR 0.917 / Recall 0.925 ·
semantic 0.799 / 0.856 / 0.847 · **hybrid 0.937 / 0.975 / 0.966**.

## Coverage gaps — features with zero or thin eval coverage

- [x] **`folder` filter.** Covered by **q030** (Utilities energy/electric bills) and
  **q031** (Finance credit-card statements). Both lean on the folder leg keeping
  similar-but-wrong-folder mail in scope while ranking/relevance excludes it.
- [x] **`fromExact` filter.** Covered by **q032** (`no.reply.alerts@chase.com`, with the
  same-sender auto-loan statement as the discriminator).
- [x] **Graded relevance.** Covered by **q037** (Cometeer lifecycle, 3/2/1 grades).
- [x] **Query-less browse.** Covered by **q033** (`BrowseByFilters`, Monarch, date-desc).
  Required an engine change — `DbEvalRankingSource` now routes empty-query+filters to
  `BrowseByFilters`, and `EvalQuerySet.Validate` allows an empty query when a filter
  scopes it.
- [ ] **Deep-history retrieval — thin.** 21/27 queries have `dateFrom` in 2026; only 3
  in 2023, 3 in 2025. Archive spans 2005–2026 (74k msgs). Nothing tests decade-old mail
  where vector drift and corpus dilution are worst.
- [x] **Attachment-driven matches.** Covered by **q034** (PDF, "uniform construction
  code" only in attachment), **q035** (DOCX, "bus stop"), **q036** (two SimpleLab PDF
  reports). Each verified term lives in `attachment_text` only, never the body.
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

- [x] Add a `folder`-filtered query (e.g. scope a Utilities-folder bill). → q030, q031.
- [x] Add a `fromExact` query ("all mail from a specific sender address"). → q032.
- [x] Add an attachment-only query — term in an indexed PDF/DOCX but not the email body,
  so a pass proves the attachment pipeline. → q034 (PDF), q035 (DOCX), q036 (PDF ×2).
- [ ] Add 2–3 deep-history queries (pre-2020 `dateFrom`).
- [x] Add graded relevance so NDCG can reward correct ordering. → q037 (Cometeer
  lifecycle). q037 currently scores NDCG 0.859: hybrid ranks the thin "Delivered!" ping
  above the info-rich "Order Confirmed" — a real ordering weakness the grade scheme now
  exposes.
- [ ] Investigate q011 (recall 0.571) — worst real failure.

> Set grew 27 → 35 (q030–q037). Full hybrid baseline after merge: NDCG 0.936 / MRR 0.967
> / Recall 0.968 (vs 0.937 / 0.975 / 0.966 on the 27-set — stable, new queries are
> well-calibrated). Per-query recall@10: q030 1.0, q031 0.80 (Amex "Important Notice"
> statement phrasing ranks below 10 — genuine miss, kept tagged), q032 1.0, q033 1.0,
> q034–q036 1.0, q037 1.0 (NDCG 0.859 on grade ordering).

> Before landing any ranking-affecting change, capture an eval baseline first
> (`mailvec eval --json baselines/<date>.json`) — see `baselines/README.md`.
