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
- [x] **Deep-history retrieval.** Covered by **q038** (2013 Toyota Financial billing
  statements, no sender filter — a pure corpus-dilution test) and **q040** (2017
  landscaping thread). q038 scores 1.0 across all modes; decade-old transactional mail
  retrieves cleanly.
- [x] **Attachment-driven matches.** Covered by **q034** (PDF, "uniform construction
  code" only in attachment), **q035** (DOCX, "bus stop"), **q036** (two SimpleLab PDF
  reports). Each verified term lives in `attachment_text` only, never the body.
- [x] **Negative / zero-result queries.** Covered by **q041** ("timeshare …", DB-verified
  absent in 2026). Required harness support: `expectEmpty` queries are scored as
  *specificity* (returned-nothing = 1.0), reported separately, and excluded from the
  NDCG/MRR/Recall aggregate (undefined over empty gold). Finding: **every mode hallucinates**
  — keyword/hybrid return 10 false hits (OR-ish term matching pulls "vacation/offer/resort"
  mail), semantic 8 (specificity 0.20). A query for something you don't have surfaces
  confident-looking junk; there's no relevance floor.
- [x] **Conversational / human mail.** Covered by **q039** (2019 bachelor-party thread) and
  **q040** (2017 landscaping complaint). Both expose the predicted semantic-leg weakness on
  human prose: thin idiomatic replies ("Magic/barcade", World Cup) miss (q039 recall 0.60),
  while the distinctive "sewer vent cap" carries keyword recall to 1.0 in q040 (semantic
  0.67). This is the gap's thesis, now measurable.

## Failing queries in the current set (real misses, separate from coverage)

- [ ] **q011 "pool setup scheduled" — recall 0.500 (was 0.571).** Genuine miss. Gold grew
  7→8 in the 2026-06-02 audit (added the `tgbeggs` "Re: Crane on pool curve" reply, which
  also ranks outside top-10). Human thread replies in the pool-setup discussion rank below
  the automated/announcement mail. Worst real failure — investigate first.
- [ ] **q023 "fortress floor quote" — recall@10 0.588, but NOT a ranking failure.** Gold
  grew 10→17 in the audit (added the human quote negotiation: `johnh@` vendor replies + the
  homeowner's replies, which hybrid hadn't surfaced). With gold=17 > k=10, recall@10 is
  *structurally capped* at 10/17=0.588 — at least 7 are always outside the top-10 window
  regardless of ranking. The honest signals: **NDCG@10 = 1.000** (top-10 all relevant) and
  **recall@20 = 0.941** (16/17 land in ranks 1–16; only the 05-14 homeowner reply falls past
  20). Decision: **keep the full gold=17 and read NDCG@10** as the quality signal for this
  query (recall@10's 0.588 is a known ceiling, not a regression). General note: **any query
  whose gold exceeds the eval's `--top-k` can never reach recall@k = 1.0** — recall@k examines
  only the top k. NDCG@k and MRR are unaffected.
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
- [x] Add 2–3 deep-history queries (pre-2020 `dateFrom`). → q038 (2013), q040 (2017).
- [x] Add graded relevance so NDCG can reward correct ordering. → q037 (Cometeer
  lifecycle). q037 currently scores NDCG 0.859: hybrid ranks the thin "Delivered!" ping
  above the info-rich "Order Confirmed" — a real ordering weakness the grade scheme now
  exposes.
- [ ] Investigate q011 (recall 0.571) — worst real failure.

> Set grew 27 → 35 (q030–q037), then → 39 (q038–q041). Full hybrid baseline at 39q
> (38 scored + 1 negative): NDCG 0.931 / MRR 0.969 / Recall 0.961 — stable vs the 27-set
> (0.937 / 0.975 / 0.966); the conversational adds (q039 0.60, q040 hybrid recall) pull the
> mean down slightly on purpose, since they expose the semantic-leg weakness the set was
> blind to. Negative query q041: specificity keyword 0.000 / semantic 0.200 / hybrid 0.000.
> Baseline snapshot: `baselines/2026-06-02-39q.json`.
>
> Remaining coverage gap: none of the original "zero/thin" gaps are open. What's left is
> the **failing-query investigations** below (q011, q006, q005, q008, q004) — real ranking
> misses, not coverage holes.

> Before landing any ranking-affecting change, capture an eval baseline first
> (`mailvec eval --json baselines/<date>.json`) — see `baselines/README.md`.
