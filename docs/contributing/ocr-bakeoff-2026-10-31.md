# OCR bake-off: local models on a Mac Studio — observed 2026-10-31

**A dated measurement, not a standing claim.** Local OCR models move faster than
the hosted ones; re-measure with
[`tools/Mailvec.OcrBench`](../../tools/Mailvec.OcrBench/README.md) rather than
trusting these numbers later. This is the follow-up to
[`ocr-bakeoff-2026-08-06.md`](ocr-bakeoff-2026-08-06.md), on the same sample, to
answer one question: **does newer local hardware or a newer local model close the
gap to `mistral-ocr`?**

**Short answer: no model closes it, but the hardware made the incumbent viable.**
`qwen2.5vl:7b` scored the same as in August and ran about twice as fast. None of
the newer models tried beat it on accuracy. **No decision was made on the strength
of this**; the August doc's *What this does not answer* still applies unchanged.

## Setup

| | |
|---|---|
| Corpus | the frozen local dev archive (see [`local-dev-dataset.md`](local-dev-dataset.md)) |
| Host | Mac Studio, M5 Max, 40-core GPU, 128 GB, Ollama 0.40.11, reached over Tailscale |
| Client | the production `OllamaVisionClient`, unmodified (`num_predict=2048`, `CollapseRepeatedLines`, document prompt), via `ocrbench run --engine ollama` |
| Truth set | 40 documents / 71 pages, `extraction_status='done'` PDFs, reference = PdfPig `ContentOrderTextExtractor` |
| Scans set | 25 documents / 42 pages, `extraction_status='ocr'` (image-only, no reference) |
| Sample seed | 7: the same documents as August, so the rows below compare directly |
| mistral-ocr rows | copied from August, **not re-run** |

Models tried: `qwen2.5vl:7b` (the incumbent), `glm-ocr` (0.9B, a dedicated OCR
model), and `qwen3-vl:30b` (a general vision model, MoE). `glm-ocr` ran twice:
once through the production client with Mailvec's prompt, and once with its own
`Text Recognition:` task prompt (see *Reproducing*).

## Truth set — accuracy against the PDF text layer

| engine | coverage | CER ↓ | WER ↓ | token F1 ↑ | recall | precision | len ratio | empty | s/page |
|---|---|---|---|---|---|---|---|---|---|
| mistral-ocr, document (Aug) | 100% | 0.157 | 0.180 | 0.970 | 0.975 | 0.974 | 1.00 | — | 1.5 |
| mistral-ocr, page (Aug) | 100% | 0.151 | 0.172 | 0.971 | 0.976 | 0.974 | 1.01 | — | 2.4 |
| qwen2.5vl:7b, MacBook (Aug) | 100% | 0.169 | 0.195 | 0.959 | 0.957 | 0.971 | 0.99 | — | 12.3 |
| **qwen2.5vl:7b, Studio** | 100% | 0.168 | 0.192 | **0.960** | 0.959 | 0.969 | 0.99 | 0 | **6.6** |
| glm-ocr, Mailvec prompt | 100% | 0.172 | 0.198 | 0.943 | 0.920 | 0.977 | 0.94 | 0 | 3.5 |
| glm-ocr, native prompt | 100% | 0.174 | 0.199 | 0.945 | 0.928 | 0.972 | 0.96 | 0 | 3.1 |
| qwen3-vl:30b | 97% | 0.386 | 0.404 | 0.722 | 0.704 | 0.811 | 0.73 | 11 | 13.1 |

## Scans set — the population the OCR pass actually serves

No reference exists, so nothing here is scored as correct.

| engine | pages | chars/page | empty | errors | s/page |
|---|---|---|---|---|---|
| mistral-ocr, document (Aug) | 42 | 1960 | — | — | 5.1 |
| mistral-ocr, page (Aug) | 42 | 1979 | — | — | 2.8 |
| qwen2.5vl:7b, MacBook (Aug) | 42 | 1652 | — | — | 14.5 |
| **qwen2.5vl:7b, Studio** | 42 | 1664 | 0 | 0 | **8.7** |
| glm-ocr, Mailvec prompt | 42 | 1393 | 1 | 1 | 3.6 |
| glm-ocr, native prompt | 42 | 1571 | 0 | 0 | 3.3 |
| qwen3-vl:30b | 42 | 472 | 24 | 0 | 14.3 |

Pairwise agreement (token F1): qwen2.5vl vs glm-ocr native **0.846**, qwen2.5vl
vs glm-ocr Mailvec prompt 0.807, the two glm-ocr prompts 0.907. Everything
against qwen3-vl sits at 0.35–0.38, which is its empty pages, not disagreement.
Cross-run agreement with mistral was not computed: its August outputs were not
kept beside this run.

## Findings

### 1. The hardware changed the speed, not the accuracy

`qwen2.5vl:7b` on the Studio scored 0.960 against 0.959 on the MacBook in
August. That is the same model and client, so the agreement is expected. The
mean latency halved on the truth set (12.3 → 6.6 s/page) and fell by 40% on scans
(14.5 → 8.7). The scans p95 is 22 s, so dense pages are still slow.

Projected to 3000 pages, that is about 5.5–7 h, down from 10–12 h. That is a
backfill you can run overnight, and irrelevant for steady-state mail, which
arrives a few scans at a time. The gap to mistral is still about 0.01 token F1
and 3–4× in latency.

### 2. glm-ocr is faster and misses more

It is about 2× faster than qwen2.5vl and has the highest precision of any local
run (0.977): what it emits is right. But its recall is 0.92–0.93 against 0.96,
and its length ratio is 0.94–0.96, so it drops text. For search, a missed term
costs more than a slow page.

Its own task prompt helped a little: recall rose from 0.920 to 0.928, and the
token-repeat abort on one scan page went away. It did not change the conclusion.

### 3. Textless pages are still unsolved locally, in a new way for each model

`a4701` (`photos.pdf`, photographs, no text) is the same page that produced
August's hallucination. Correct output is nothing, which mistral got right.

| engine | output on a4701 |
|---|---|
| qwen2.5vl:7b | `` ```markdown system``` `` (August: an invented `Front View / Side View / Top View`) |
| glm-ocr, Mailvec prompt | **the prompt itself**, echoed back (`You are an OCR engine. Transcribe ALL text…`) |
| glm-ocr, native prompt | a description of the photographs (`The image shows three different views of a storage unit…`) |
| qwen3-vl:30b | empty: correct, though see finding 4 for why that is weak evidence |

Every non-empty answer above would be written back as `status='ocr'` and indexed,
and the prompt echo would make the document a hit for the words "OCR engine".
Mailvec's `NO_TEXT_FOUND` sentinel is something none of these models produced
here. This matters most for the **image** pass, which mostly sees photographs.
Any local engine switch needs a "this output is not a transcription" filter of
the kind `MistralOcrClient.StripPlaceholders` is for mistral.

### 4. qwen3-vl:30b is a reasoning model, and the client does not turn it off

It returned nothing on 35 of 113 pages. The cause is not the model's reading:
`qwen3-vl` thinks before it answers, the hidden reasoning counts against
`num_predict=2048`, and on a dense page the budget runs out before any response
text. `OllamaVisionClient` sends no `think` parameter, so it gets Ollama's
default for the model, which is thinking on.

A hand test on one failed page with `think:false` returned a correct
transcription in about 16 s. So the model is not ruled out on quality, but using
it needs a client change, and at 13–16 s/page it is slower than the incumbent.
The two pages it lost outright (coverage 97%) were a token-repeat abort and the
120 s `VisionRequestTimeoutSeconds`.

**Any future reasoning-capable vision model hits this.** It shows up as a high
`empty` count and a low length ratio, not as an error.

### 5. Shared host

The Studio is also production's embedding host. Running OCR there puts it on the
same Ollama as `mxbai-embed-large`. The embedding model stayed loaded throughout
this run (128 GB has room for both). But the embedder's OCR pass runs **before**
its embed pass on the one worker, so a large OCR backlog delays new mail being
embedded. `Embedder:OcrMaxSecondsPerPass` bounds each pass, not the backlog.

## Not tried

Considered but not run: PaddleOCR-VL, LightOnOCR, Chandra, DeepSeek-OCR,
olmOCR 2, Qwen3.5 and Gemma 4. They were skipped because, as of this run,
they were either not packaged for Ollama or are not dedicated OCR models
expected to beat the incumbent. Any of them that reaches Ollama can be dropped
into the same harness run with `Ollama__VisionModel`.

## Reproducing

```sh
export Archive__DatabasePath="$HOME/Library/Application Support/Mailvec/archive.sqlite"
export Ingest__MaildirRoot="$HOME/Mail/Fastmail"
ocrbench sample --work <work>/truth --set truth --n 40 --max-pages 3 --seed 7
ocrbench sample --work <work>/scans --set scans --n 25 --max-pages 3 --seed 7
# per model, per set:
Ollama__BaseUrl=http://<host>:11434 Ollama__VisionModel=<model> \
  ocrbench run --work <work>/truth --engine ollama --label <label>
ocrbench score --work <work>/truth
```

`ocrbench` is `dotnet run --project tools/Mailvec.OcrBench --`. The harness opens
the archive read-only, so it is safe on the frozen corpus.

**The glm-ocr native-prompt row was not produced by the harness.** The harness
uses the production client, whose prompt is fixed, so that row came from a
throwaway script that called `/api/generate` with `prompt: "Text Recognition:"`,
`num_predict: 2048` and `temperature: 0` on the harness's page JPEGs, and then
wrote a results file in the harness schema for `score`. Reproducing it means
writing the same script, or giving the harness a prompt override.

The sample directories hold copies of real mail attachments. Delete them after
scoring; nothing from them belongs in the repo.
