# OCR bake-off: `qwen2.5vl:7b` vs `mistral-ocr` — observed 2026-08-06

**A dated measurement, not a standing claim.** Both models move; re-measure with
[`tools/Mailvec.OcrBench`](../../tools/Mailvec.OcrBench/README.md) rather than
trusting these numbers a year from now. The harness is the durable artefact —
this file is one run through it.

**No decision was made on the strength of this.** The deciding experiment
(retrieval eval) had not been run when this was written. See *What this does not
answer*.

## Setup

| | |
|---|---|
| Corpus | the frozen local dev archive (see [`local-dev-dataset.md`](local-dev-dataset.md)) |
| Incumbent | `qwen2.5vl:7b` via the production `OllamaVisionClient`, unmodified (`num_predict=2048`, `CollapseRepeatedLines`, document prompt), on this Mac's GPU |
| Challenger | `mistral-ocr-4-0` on Azure AI Foundry, `providers/mistral/azure/ocr`, eastus |
| Truth set | 40 documents / 71 pages, `extraction_status='done'` PDFs, reference = PdfPig `ContentOrderTextExtractor` |
| Scans set | 25 documents / 42 pages, `extraction_status='ocr'` (image-only, no reference) |
| Sample seed | 7 |

## Truth set — accuracy against the PDF text layer

| engine | coverage | CER ↓ | WER ↓ | token F1 ↑ | recall | precision | len ratio | s/page |
|---|---|---|---|---|---|---|---|---|
| qwen2.5vl:7b | 100% | 0.169 | 0.195 | 0.959 | 0.957 | 0.971 | 0.99 | 12.3 |
| mistral-ocr (document) | 100% | 0.157 | 0.180 | 0.970 | 0.975 | 0.974 | 1.00 | 1.5 |
| mistral-ocr (page) | 100% | 0.151 | 0.172 | 0.971 | 0.976 | 0.974 | 1.01 | 2.4 |

Born-digital pages — a ceiling on accuracy, not a measure of robustness to real
scan degradation.

**The quality gap is small: ~0.012 token F1.** The latency gap is not: 5–8×.

## Scans set — the population the OCR pass actually serves

No reference exists, so nothing here is scored as correct.

| engine | pages | chars/page | s/page |
|---|---|---|---|
| qwen2.5vl:7b | 42 | 1652 | 14.5 |
| mistral-ocr (document) | 42 | 1960 | 5.1 |
| mistral-ocr (page) | 42 | 1979 | 2.8 |

Pairwise agreement (token F1): qwen vs mistral-document **0.813**, qwen vs
mistral-page **0.818**, mistral-document vs mistral-page **0.917**.

## Three findings worth more than the aggregate numbers

### 1. mistral's advantage is the model, not its PDF handling

The two mistral modes score the same (F1 0.971 vs 0.970) and agree with each
other at 0.917. Document mode sends the whole PDF and uses Mistral's own
rasterisation; page mode consumes the *identical JPEGs* `PdfRenderer` feeds
qwen. They tie.

**So `PdfRenderer`'s output is not what holds qwen back**, and a swap would not
require restructuring the per-page pipeline — `IVisionClient` stays the seam and
a `MistralOcrVisionClient` drops into it. The shape mismatch that looked like the
main architectural obstacle is empirically a non-issue.

### 2. qwen hallucinated on a textless page; mistral did not

`a4701` (`Front Views.pdf`, a document of photographs) — agreement 0.000:

- qwen: `1. Front View / 2. Side View / 3. Top View` — **invented**
- both mistral modes: image placeholders only, i.e. correctly no text

In production that qwen output is written back as `status='ocr'` and indexed, so
a search surfaces text the document does not contain, with nothing to flag it as
suspect. This is the hallucination risk the design proposal accepted as "fine for
fuzzy search" — now with a concrete instance, on a page whose correct answer was
silence. It bears on the **image**-OCR pass most: that pass has 2172 image
attachments at `status='ocr'` and 1373 at `no_text`, and textless images are
exactly its diet.

### 3. mistral repetition-loops too

`a248` (`1212 Mayapple Lane - EXISTING.pdf`, an architectural drawing), document
mode: 3562 chars, largely `ARCHITECTURAL ALLIANCE SHEET 1 • … SHEET 2 • …`
repeating. Page mode produced 280 chars on the same page.

Note this defeats `CollapseRepeatedLines`, which squashes repeated *lines* — here
the repetition is *within* one pipe-table line. Anyone porting the incumbent's
mitigations to a new engine should not assume they transfer.

## Cost and throughput, projected to 3000 pages

| engine | projected wall clock | API cost |
|---|---|---|
| qwen2.5vl:7b | ~10–12 h | — (local) |
| mistral-ocr (document) | ~1.3 h | ~$3 |
| mistral-ocr (page) | ~2 h | ~$3 |

Cost is not a decision input at this corpus size. Throughput might be.

## Methodology traps this run walked into

Both are now handled by the harness; both would have produced a confident wrong
answer.

1. **Rate limits masquerading as speed.** The first unpaced page-mode run
   reported a headline **0.9 s/page** — with 60 of 71 pages 429'd. A rejected
   call returns in milliseconds, so throttling flatters the mean. Fixed with
   pacing + `Retry-After` retry, a `coverage` column, and exclusion of failed
   calls from both the quality and latency figures.
2. **CER is not the metric here.** An early 6-page run showed qwen at CER 0.345
   against token F1 0.859; the gap was entirely a multi-column newsletter read in
   a different order than the PDF content stream. Nothing was wrong. Read CER,
   token F1 and length ratio together, or not at all.

## What this does not answer

**Whether retrieval improves.** Mailvec cares whether the user *finds* the
document, not the edit distance of its transcription. That needs each engine's
text embedded into a parallel DB ([`embedding-experiments.md`](embedding-experiments.md))
and `mailvec eval` run against the frozen labelled query set. A 0.012 F1 edge is
not on its own a reason to change the pipeline.

**Whether the privacy trade is acceptable.** The local model was chosen because
scanned documents hold SSNs and financials and the OCR pass feeds them
**unattended** — no tool call, no user in the loop ([`../security.md`](../security.md)).
A hosted engine gives that up for every scanned page in the archive. That is a
judgement, and nothing in this file makes it.
