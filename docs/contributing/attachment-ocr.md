# Design proposal — OCR for scanned (image-only) PDFs

**Status:** in progress — steps 1–5 implemented. Scanned PDFs are now OCR'd by
the embedder and fully searchable (semantic + keyword). Steps 6–7 remaining
(doctor/install vision-model checks, docs + backfill drain). See the phased plan
at the bottom.

## Goal

Make the ~309 `extraction_status='no_text'` PDFs (scanned / image-only documents)
**searchable** by their content. Today they're invisible to keyword and
semantic search — only viewable on demand via `get_attachment_page_image`.

**Scope (phase 1):** scanned PDFs only. Standalone images (~445) are a phase 2
on the same machinery; not in this proposal.

## Spike result (why this is worth doing)

Render page → local Ollama `qwen2.5vl:7b` produced production-grade text on 8
real samples (criminal record check, mortgage statement, pay stub, dental card,
fax, architectural drawing): names, dollar amounts, account/loan numbers, dates,
addresses all captured, tables preserved as markdown. ~22 s/page, fully offline
(important — these hold SSNs and financials). A couple of cosmetic OCR slips,
fine for fuzzy search.

## Placement: extend the embedder

The OCR step runs in `Mailvec.Embedder` as a pass *before* the existing embed
pass. The embedder already polls Ollama and owns the "turn attachment text into
searchable content" job, so this keeps one Ollama-bound worker.

```
Embedder loop, each cycle:
  1. OCR pass   — find a batch of no_text PDF attachments, render + OCR each,
                  write extracted_text + status='ocr', then clear the parent
                  message's embedded_at + chunks (re-queues it for embedding).
  2. Embed pass — existing logic. Now picks up the re-queued messages and
                  embeds the new OCR text exactly like native-extracted text.
```

**Why this shape:** it unifies new mail and backfill — the OCR pass simply finds
*all* `no_text` PDFs, so the first run drains the 309-doc backlog and steady
state handles new scans. No separate backfill command needed. It's also
self-healing: if a re-index ever resets an attachment to `no_text`, the next OCR
pass re-OCRs it.

## Data flow / schema

- OCR text is written to the existing **`attachments.extracted_text`**. The
  embedder's `LoadAttachmentTexts` keys off `extracted_text` presence (not
  status), so the **vector leg works with no change**.
- **New status value `ocr`** on `AttachmentTextExtractor` (`StatusOcr = "ocr"`),
  distinct from `done` (native) — gives provenance ("text via OCR", surfaced in
  `get_email`) and lets us re-run OCR later with a better model by selecting
  `status='ocr'`. Two spots filter on `'done'` and must become `IN ('done','ocr')`:
  1. `MessageRepository.BuildAttachmentText` (the FTS `attachment_text` column —
     required for **keyword** search to see OCR text).
  2. `GetEmailTool` `AttachmentInfo.IndexedForSearch` (cosmetic flag).
  - *Alternative considered:* reuse `status='done'` → zero downstream changes,
    but loses the native-vs-OCR distinction. Recommend `ocr` for transparency.
- No new tables/columns. `extracted_at` records when OCR ran.

## New components

1. **Shared renderer project.** `PdfRenderer` currently lives in `Mailvec.Mcp`
   (internal). The embedder can't reference Mcp, and putting PDFtoImage in
   `Core` would spread the native PDFium dep to Indexer/Cli (violating the
   "native dep only where needed" invariant). **Factor `PdfRenderer` + the
   `PDFtoImage` reference into a small `Mailvec.Pdf` project**, referenced by
   `Mailvec.Mcp` and `Mailvec.Embedder` only. Core/Indexer/Cli stay native-free.
2. **Vision client.** A new `IVisionClient.OcrAsync(byte[] image, ct)` (Ollama
   `/api/generate` with `images`, the OCR prompt, `temperature:0`). Separate
   from `IEmbeddingClient` — OCR isn't embedding. `OllamaVisionClient` impl.
3. **Maildir read in the embedder.** OCR needs the PDF *bytes*, which aren't in
   the DB — so the embedder must read the `.eml` from the Maildir. **This breaks
   the current "embedder is the only executable that never reads the filesystem"
   invariant** (CLAUDE.md) — call it out and update that doc. Reuse a
   decode-only helper (refactor `AttachmentExtractor` to expose attachment bytes
   without writing to the download dir, or a small `MaildirAttachmentReader`).
4. **Multi-page.** Render + OCR each page up to a cap, concatenate (the spike did
   page 1 only). Several samples were 3–4 pages.

## Config (new)

- `Ollama:VisionModel` (default `qwen2.5vl:7b`).
- `Embedder:OcrEnabled` (default **true**).
- `Embedder:OcrMaxPagesPerPdf` (cap cost on huge scans, e.g. 20).
- `Ollama:VisionKeepAlive` (default Ollama's own ~5 min — **not** pinned; see below).
- Reuses the renderer's 150-DPI / 1536px cap.

## Model lifetime: load on demand, do NOT pin

The 6 GB vision model is **not** kept resident. We pass Ollama's default
`keep_alive` (~5 min), so the model loads when the OCR pass runs, stays loaded
through a burst (e.g. a backfill or a multi-page doc), and Ollama unloads it once
idle. Rationale: scans arrive ~1–2×/week, so pinning 6 GB the rest of the time
isn't worth it — we accept the per-burst load cost (a few seconds) and some
reload churn when OCR and embed passes alternate. **The two models therefore
never need to coexist in RAM**, which removes the "verify both fit" constraint
entirely. `Ollama:VisionKeepAlive` exposes this if it ever needs tuning; never
set it to `-1` (pin-forever).

## Risks & notes

- **Throughput.** ~22 s/page → backfill of 309 PDFs (~500–600 pages) ≈ 3–4 h
  one-time, trivial ongoing. It's a background drain; surface OCR backlog in
  `mailvec status` / `/health` like the embedding backlog.
- **Default-on means auto-backfill.** With `OcrEnabled=true`, the first embedder
  run after this ships starts OCR'ing all 309 existing `no_text` PDFs (the ~3–4 h
  drain above). Expected, but note it in the changelog so an upgrade's burst of
  Ollama activity isn't a surprise.
- **Graceful degradation if the model is missing.** Since OCR is on by default,
  a fresh box without `qwen2.5vl:7b` pulled must **log a warning and skip OCR**,
  not crash the embedder (leave the PDFs `no_text` for a later pass). `mailvec
  doctor` should check the vision model is present when OCR is enabled, and
  `ops/install-all.sh` should offer to `ollama pull` it.
- **Hallucination.** VLMs can invent text; acceptable for fuzzy search, and the
  `ocr` status flags lower trust than native extraction.
- **Re-baseline search eval** (`mailvec eval`) after — adding ~309 docs' worth of
  content shifts ranking.
- **Ollama floor** gains a vision-model requirement; document in `ops/UPGRADING.md`.

## Phased plan

1. ✅ `Mailvec.Pdf` shared project (moved `PdfRenderer`); Mcp + Embedder reference it.
2. ✅ `IVisionClient` / `OllamaVisionClient` + `Ollama:VisionModel`/`VisionKeepAlive`/`VisionRequestTimeoutSeconds` config.
3. ✅ `MaildirAttachmentReader` (decode-only Maildir bytes); `AttachmentExtractor` delegates to it.
4. ✅ Embedder OCR pass (`AttachmentOcrService`): render → OCR → write `status='ocr'` →
   re-queue parent. Default-on, graceful skip when the model isn't pulled, poison-PDF→`failed`,
   transient→retry. **Multi-page included here** (was listed under step 5). After this, OCR'd
   scans are *semantic*-searchable via the re-embed.
5. ✅ Keyword/FTS: `SaveOcrText` rebuilds `messages.attachment_text` (the FTS trigger then syncs
   `messages_fts`), so OCR'd text is keyword-searchable too; `get_email` `IndexedForSearch` now
   `is "done" or "ocr"`. `BuildAttachmentText` needed no change — it already keys off
   `extracted_text` presence, not status. OCR backlog shown in `mailvec status` (a `/health` field
   can fold into step 6's doctor work).
6. ⬜ `mailvec doctor` vision-model check; `ops/install-all.sh` offers to `ollama pull qwen2.5vl:7b`.
7. ⬜ Let the backfill drain, re-baseline `mailvec eval`, update CLAUDE.md
   (embedder-reads-filesystem, status enum) + UPGRADING.md (vision-model floor).
