# Mistral External OCR Integration Review

Date: 2026-08-06  
Scope: final integrated state of the external OCR work introduced in `3b16bf3` and its follow-up commits through `2523b76`.

## Summary

The integration has several strong safeguards: the API key is scoped to the embedder container, deployment secrets are excluded from git and Docker build context, documents are submitted inline rather than through fetchable URLs, API error bodies are kept out of exception messages, OCR output is bounded, and the off-network PII and retention implications are documented clearly.

However, three failure-classification and response-validation issues can permanently remove valid attachments from the OCR queue during provider or configuration failures. These should be fixed before relying on Mistral for unattended archive backfills. Recovery is possible with `mailvec reocr --include-failed`, but only after the loss is detected.

| Priority | Finding | Impact |
|---|---|---|
| P1 | Ambiguous HTTP 400/422 responses are classified as document-fatal | Provider/configuration errors can permanently retire good attachments across the corpus |
| P1 | A successful response with no `pages` is accepted as “no text” | API drift or an invalid gateway response can silently terminally mark attachments without OCR text |
| P1 | Exhausted HTTP 503 backpressure becomes a transient document strike | Temporary provider overload can eventually retire good attachments |
| P2 | Hosted OCR accepts cleartext HTTP endpoints | API keys and sensitive rendered mail pages can be transmitted without TLS |
| P2 | Successful no-text image OCR is not recorded as health success | A functioning image OCR pass can appear unproven or indeterminate indefinitely |

## Findings

### P1 — Ambiguous 400/422 responses permanently retire documents

Location: `src/Mailvec.Core/Mistral/MistralOcrClient.cs:220`

The availability probe deliberately submits an incomplete request and treats any HTTP 422 response as proof that the endpoint, credentials, and model configuration are available. Actual OCR requests then classify every HTTP 400 or 422 response as `DocumentFatal`.

This is unsafe because 400/422 can also describe request-envelope, model, deployment, or provider schema problems. In particular, the probe does not prove the configured model is valid if validation stops at the deliberately missing `document` field. `AttachmentOcrService` immediately marks a `DocumentFatal` attachment as `failed`, and nothing selects failed rows again automatically. Although the code comment says a misclassification is bounded to one attachment, the same systematic configuration failure is applied independently to every attachment in subsequent batches.

Recommended change:

- Treat ambiguous 400/422 responses as `AuthOrConfig` or `Transient`, aborting the batch without retiring documents.
- Reserve `DocumentFatal` for provider error codes or response details known to refer to the submitted image itself.
- If model validation requires a real OCR request, use an explicit startup/doctor probe with a synthetic image and acknowledge that it may be billable, rather than using corpus documents as the validation mechanism.
- Add integration tests showing that systematic 400/422 responses never change attachment status to `failed`.

### P1 — Invalid successful responses become terminal no-text results

Location: `src/Mailvec.Core/Mistral/MistralOcrClient.cs:170`

After a 2xx response, a missing `pages` property is converted to an empty list. The caller joins that list into an empty transcription and treats it as legitimate blank OCR.

For scanned PDFs, `SaveOcrText` commits an empty terminal `ocr` status. For images, the attachment is terminally marked `no_text`. In either case it leaves the candidate queue and is not retried. A gateway returning an unexpected 2xx body, a provider response-schema change, or a partially valid response can therefore silently drain the OCR queue without recovering text or recording a failure.

Recommended change:

- Require `pages` to be present and contain the expected page entry for an image request.
- Treat missing/empty pages, duplicate or invalid indexes, and other schema violations as classified protocol failures.
- Continue accepting an empty `markdown` value inside a valid page entry as the legitimate blank-page case.
- Add tests for `{}`, `{ "pages": null }`, `{ "pages": [] }`, and structurally invalid page entries.

### P1 — Exhausted 503 backpressure becomes document strikes

Location: `src/Mailvec.Core/Mistral/MistralOcrClient.cs:213`

The client retries HTTP 429 and all 5xx responses. When retries are exhausted, only 429 maps to `Backpressure`; HTTP 503 and every other 5xx fall through to `Transient`, even when the response carries `Retry-After`.

Transient OCR failures accrue per-document strikes whenever another page succeeds in the cycle or the follow-up health probe succeeds. After five counted cycles, the attachment is permanently marked `failed`. Temporary service overload can therefore be converted into a permanent document verdict—the exact failure mode the `Backpressure` taxonomy was introduced to prevent.

Recommended change:

- Classify HTTP 503 with `Retry-After` as `Backpressure` after retries are exhausted; consider treating all 503 responses as backpressure.
- Honor both delta and absolute-date forms of `Retry-After`.
- Bound provider-supplied retry delays so a malformed header cannot stall the single embedder worker indefinitely.
- Add an end-to-end OCR service test proving repeated 503 responses never accrue retirement strikes.

### P2 — Hosted OCR accepts cleartext HTTP endpoints

Location: `src/Mailvec.Core/Options/VisionOptions.cs:129`

`MistralVisionOptions.Validate` requires only non-empty endpoint, model, and API key values. `VisionRegistration` accepts the resulting URI without checking its scheme. An `http://` endpoint therefore sends both the API key and base64-encoded rendered mail pages without transport encryption.

This is especially significant here because OCR candidates can include bank statements, tax documents, medical correspondence, identity documents, and other PII-heavy attachments, and submission is unattended.

Recommended change:

- Parse and validate the endpoint as an absolute URI.
- Require HTTPS for the hosted provider.
- If local development must support HTTP, make the exception explicit and restricted to loopback rather than silently permitting arbitrary cleartext hosts.
- Add validation tests for relative, non-HTTP, HTTP, and HTTPS endpoints.

### P2 — Successful no-text image OCR is invisible to health reporting

Location: `src/Mailvec.Embedder/Services/AttachmentOcrService.cs:871`

A successful provider call returning no usable image text increments the in-cycle vision-success and page counters, then commits `no_text` and exits before `RecordOcrSuccess`. Consequently, an images-only deployment—or a backlog dominated by legitimate photos—can process documents successfully while `mailvec status` continues to report that no successful OCR is on record and the stalled state remains unknown.

The existing success marker is documented as “a document gained text,” so simply calling it for a no-text decision would change its meaning.

Recommended change:

- Record a separate “last successful OCR decision/provider response” timestamp for committed text and committed no-text outcomes.
- Keep “last text recovered” separate if that product metric remains useful.
- Base operational stalled detection on successful terminal OCR decisions, not only on documents that happened to contain enough text.
- Add an images-only test where several textless images drain successfully and health reports a recent successful OCR decision.

## Security and PII observations

The following controls are implemented well:

- `Vision__Mistral__ApiKey` is supplied only to the embedder in `compose.yml`; the internet-facing MCP container does not receive it.
- `.env`, `secrets/`, and local settings files are excluded from git and Docker build context.
- Rendered pages are sent as inline data URIs, so the integration does not create externally fetchable document URLs or blob-storage artifacts.
- API response bodies are not included in `VisionException.Message`, reducing the chance that an error echo containing document text is written to the current logs.
- Mistral output is capped before indexing to contain repetition loops.
- Documentation explicitly calls out unattended off-network submission, provider-controlled retention, and the changed threat model.

Additional hardening considerations:

- The first 400 characters of an API error body are retained in `Exception.Data`. The current Serilog format does not intentionally reference this field, but future exception destructuring or telemetry exporters could serialize it. Treat it as potentially containing PII and redact or omit it unless a controlled diagnostic path needs it.
- Consider disabling automatic cross-origin redirects, especially when `AuthHeader` is a custom header such as `api-key`; custom authentication headers may not receive the same redirect stripping behavior as the standard `Authorization` header.
- The cumulative page counter excludes some real provider calls, notably the billable blank-image health probe used after zero-success cycles. It should not be described as a complete spending gauge unless every submitted/billable call is counted.

## Validation performed

- Reviewed the final integrated changes from `3b16bf3` through `2523b76`, including provider registration, configuration, Mistral HTTP behavior, OCR write-back and retirement, re-OCR, health/status reporting, Docker secret scoping, documentation, and tests.
- `git diff --check c42c95f..HEAD` passed.
- Full solution test suite passed: 1,224 tests, 0 failures.
- Worktree was clean after review.

No implementation changes were made as part of this review.
