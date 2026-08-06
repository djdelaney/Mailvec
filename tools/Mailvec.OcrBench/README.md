# ocrbench — OCR engine bake-off

A development tool for comparing OCR engines on **real documents from the
archive**. Not part of the shipped product: nothing in `src/` references it,
`ops/install.sh` never publishes it, and it wires into no service.

Built to answer one question — *should the embedder's OCR pass keep using the
local `qwen2.5vl:7b`, or move to a hosted `mistral-ocr` deployment?* — but the
engine list is a switch statement, so it generalises.

Read [`docs/contributing/attachment-ocr.md`](../../docs/contributing/attachment-ocr.md)
first; it's the design record for the pass being benchmarked.

## Safety

- **Read-only.** Opens the database with `Mode=ReadOnly` and reads `.eml` files
  through `MaildirAttachmentReader` (the same containment-guarded path the
  embedder uses). It writes only inside the working directory you name.
- **This machine's archive is a frozen eval corpus.** See
  [`local-dev-dataset.md`](../../docs/contributing/local-dev-dataset.md). The
  harness cannot disturb it, but don't run the services here to "check" anything.
- **A remote engine sends mail content off the machine.** Every page you
  benchmark against a hosted endpoint is a page of the user's mail — often a
  scanned bank statement or tax form, since that is exactly the population the
  OCR pass serves. The local pipeline's privacy property (nothing leaves the
  LAN, see [`docs/security.md`](../../docs/security.md)) does not survive a
  hosted engine. That is a decision to make deliberately, not a benchmark detail.
- **The API key comes from `MISTRAL_OCR_KEY` only**, never a flag — a flag lands
  in shell history and in `ps`.

## The three commands

```sh
ocrbench sample --work DIR --set truth|scans [--n 40] [--max-pages 3] [--seed 1]
ocrbench run    --work DIR --engine ollama|mistral [--mode page|document] [--label NAME]
ocrbench score  --work DIR [--corpus-pages N] [--cost-per-1k 1.0]
```

`sample` materialises everything to disk — the PDFs, the rendered page JPEGs,
and the reference text — so every engine is scored against **byte-identical
input**. That's the point: without it, a change in render settings between two
runs is indistinguishable from a quality difference between two engines. The
sample is deterministic in `--seed`.

`run` is deliberately sequential. Concurrency would destroy the latency numbers
(the local model serialises on one GPU regardless) and would turn a rate-limited
endpoint into a burst of 429s.

`score` is separate from `run` so an expensive run is scored, re-scored, and
compared without ever being repeated.

## The two sample sets

### `--set truth` — where the reference comes from

Draws PDFs the indexer extracted **natively** (`extraction_status='done'`), i.e.
ones carrying a real embedded text layer, and uses PdfPig's
`ContentOrderTextExtractor` output as the reference — the same extractor the
indexer uses, so the reference is exactly what Mailvec would have stored.

This gives objective ground truth over hundreds of real documents from the
user's own corpus, with zero labelling effort. There are ~1955 such PDFs.

**The caveat that must travel with every number it produces:** born-digital
pages are typographically cleaner than genuine scans. This measures *ceiling*
accuracy, not robustness to skew, noise, bleed-through, or a phone photo of a
document. It is a necessary comparison, not a sufficient one.

Pages whose reference is under 200 characters are excluded — an engine scored
against a near-empty reference produces a meaningless CER, since any output at
all is infinite relative error.

### `--set scans` — the population that actually matters

Draws genuine image-only PDFs (`extraction_status='ocr'`). There is no text
layer, so **there is no reference and nothing is scored as correct**. The report
gives pairwise engine agreement, output volume, latency, and — most usefully — a
ranked list of the pages where the engines disagree most. That list is the
read-by-hand shortlist; 15 pages of eyeballing on the biggest disagreements
tells you more than 500 pages of agreement does.

Complete the picture with ~15–20 hand-checked pages from this set. There is no
way around that: this is the population the OCR pass serves, and nothing in the
corpus knows what it says.

## Metrics, and why three of them

| metric | reads | fails at |
|---|---|---|
| CER / WER | classic OCR accuracy | punishes reordering brutally — and a markdown table legitimately reorders a PDF's content stream, as does any multi-column page |
| token F1 (+ recall, precision) | bag-of-words overlap, order-blind | blind to structure; a scrambled page scores well |
| length ratio | truncation and padding | says nothing about correctness |
| coverage | how much of the sample the engine actually answered | says nothing about quality — but a low value invalidates every other column |

**Coverage is not a footnote.** Pages lost to transport failures (rate limits,
timeouts) are excluded from the quality columns, because charging a deployment's
quota to a model's accuracy is simply a wrong measurement. Pages the engine
answered with *nothing* are different — that's a real transcription failure and
stays scored, under `empty`. The report shouts when coverage drops below 90%,
because a run that answered a third of the sample must never be read as a clean
comparison.

Read them together. Observed on the first smoke run: qwen scored CER 0.345
against token F1 0.859 on the same six pages — the gap was entirely a
multi-column newsletter where qwen read the columns in a different order than
the PDF content stream. Precision 0.978 with recall 0.815 says it drops content
rather than inventing it. Any one metric alone would have told a wrong story.

Both sides are normalised before scoring — markdown stripped, NFKC folded,
punctuation and case dropped, whitespace collapsed — so the comparison is
transcription, not formatting. Without that, the winner would be whichever
engine happened to punctuate like PdfPig.

## Engines

**`--engine ollama`** runs the production `OllamaVisionClient` unmodified,
reading the same `Ollama:*` config the embedder reads. That means it competes
carrying its production mitigations: the `VisionMaxTokens` / `num_predict` cap
(2048, ~8k chars/page), `CollapseRepeatedLines`, and the document prompt. This
is intentional — it measures what runs today. If it loses, the follow-up
question is whether relaxing those closes the gap, and answering it means
another run with `Ollama__VisionMaxTokens=0` in the environment, not quietly
changing the engine.

**`--engine mistral`** has two modes, because they are genuinely different
products:

- `--mode page` sends one rendered JPEG per call (`type: "image_url"`).
  Apples-to-apples with the local engine: identical pixels in.
- `--mode document` sends the whole PDF (`type: "document_url"`) and takes its
  per-page markdown. This is what the model is built for, and its own
  rasterisation replaces PDFium's. Better numbers are expected here — and they
  are **not attributable to the model alone**, which is why both modes run.

Both use a base64 data URI, so no document is uploaded to blob storage or
exposed at a fetchable URL.

Endpoint plumbing is all flags, so the same binary hits Azure AI Foundry or
`api.mistral.ai`:

```sh
export MISTRAL_OCR_KEY=…
export MISTRAL_OCR_ENDPOINT=https://<resource>.services.ai.azure.com
ocrbench run --work ~/ocr-bench/truth --engine mistral --mode document \
    --model <deployment-name> --route providers/mistral/azure/ocr
```

### Azure AI Foundry specifics (observed 2026-08-06, verify rather than trust)

- **The route is `providers/mistral/azure/ocr`.** Not `v1/ocr` (that's
  `api.mistral.ai`), not `models/ocr?api-version=…`, not an
  `openai/deployments/…` path — all three 404. Probe with a deliberately
  malformed body if you need to rediscover it: a live route answers 422
  (`"body.document" field required`), a wrong one answers 404. That probe sends
  no mail content, which is why it's the right first step.
- **`--model` is the *deployment* name**, not `mistral-ocr-latest`.
- Either auth header works; `bearer` is the default.
- **`Content-Length` is mandatory.** The gateway rejects chunked bodies with
  `no_content_length_header` before the model sees them, so the client buffers
  the payload rather than streaming it. This is why `PostAsJsonAsync` cannot be
  used here.
- **Rate limits are the thing that will silently ruin a run.** A throttled call
  is rejected in milliseconds, so an unpaced benchmark reports a *fantastic*
  mean latency that is mostly the service saying no. Measured on a first
  attempt: page mode "completed" 71 pages at 0.9 s/page — with 60 of them 429s.
  Hence `--min-interval-ms` (default 1500) and `--max-retries` (default 5,
  honouring `Retry-After`), and hence the scorer's `coverage` column. Document
  mode is far more resilient simply because it makes one call per document
  instead of one per page.

## What this harness does NOT measure

**Retrieval quality.** The thing Mailvec actually cares about is whether a user
finds the scanned document, not the OCR text's edit distance. That is a
different experiment: re-embed each engine's text into a parallel database per
[`embedding-experiments.md`](../../docs/contributing/embedding-experiments.md)
and run `mailvec eval` against the frozen labelled query set. The corpus being
frozen makes that a clean A/B. Do it before any swap — a transcription win that
doesn't move retrieval isn't a reason to change anything.

## Full run

```sh
W=~/ocr-bench
ocrbench sample --work $W/truth --set truth --n 40 --max-pages 3
ocrbench sample --work $W/scans --set scans --n 25 --max-pages 3

ocrbench run --work $W/truth --engine ollama
ocrbench run --work $W/truth --engine mistral --mode page
ocrbench run --work $W/truth --engine mistral --mode document
ocrbench score --work $W/truth --corpus-pages 3000

ocrbench run --work $W/scans --engine ollama
ocrbench run --work $W/scans --engine mistral --mode document
ocrbench score --work $W/scans
```

Budget ~18 s/page for the local engine (measured on this Mac): a 120-page truth
set is about 35 minutes.
