# The synthetic dev corpus

`tools/Mailvec.DevCorpus` writes an invented mailbox you can run Mailvec over by
hand: a Maildir, an empty `state/` directory for the database, an `env.sh` that
points every Mailvec binary at them, a labelled eval query set, and a manifest.
It exists for places with no real mail — a
[Claude cloud session](cloud-development.md) — and for scratch runs on the dev
Mac that must not touch the [frozen corpus](local-dev-dataset.md).

It is **not** for ranking work. The frozen real corpus decides quality questions
(`baselines/` measure it and nothing else). Synthetic mail answers "does the
pipeline do what it claims with this shape of message", which the unit tests
check one shape at a time and this checks all together.

## Using it

```sh
dotnet run --project tools/Mailvec.DevCorpus -- /tmp/mvdev     # new or empty dir
. /tmp/mvdev/env.sh
dotnet run --project src/Mailvec.Indexer     # Ctrl-C once "MaildirScanner: seen=…" logs
dotnet run --project src/Mailvec.Cli -- status
dotnet run --project src/Mailvec.Cli -- search cedar
dotnet run --project src/Mailvec.Mcp         # MCP over HTTP at http://127.0.0.1:3333 (the root, no /mcp)
```

`env.sh` sets `Archive__DatabasePath`, `Ingest__MaildirRoot`, an empty
`Fastmail__AccountId` (no webmail links to messages that exist nowhere), and
`MAILVEC_DEV_EVAL_QUERIES`. Environment variables outrank every appsettings
file, including the shared one on the dev Mac, so a shell that sourced it runs
against the dev corpus. **Check before running a writer:** `mailvec status`
prints the `Database:` and `Maildir:` it resolved, right under the version
line.

To reach the MCP tools from a Claude Code session (cloud or local) while the
server runs, register it for that session:
`claude mcp add --transport http mailvec-dev http://127.0.0.1:3333` (see
[docs/clients/claude-code.md](../clients/claude-code.md)). Keep the production
Mailvec connector off in the same session, so the two are never confused.

Options: `--filler N` (background messages, default 250), `--hazards` and
`--embedding fireworks` (both below). Output is deterministic: the same options
produce the same bytes.

Layout:

| Path | What |
|---|---|
| `Mail/` | The Maildir, mbsync-shaped: `INBOX`, `Sent`, `Drafts`, `Trash`, `Archive`, `Archive/2025`, `Lists/rust-users`, `Receipts`, `Newsletters`, each with `cur/new/tmp` |
| `state/` | Where `archive.sqlite` is created on first run |
| `eval/queries.json` | `mailvec eval --queries "$MAILVEC_DEV_EVAL_QUERIES"`. A plumbing check; needs embeddings for the semantic and hybrid legs |
| `manifest.json` | Every scenario: what it is for, its files, and the outcome the pipeline must produce |
| `env.sh` | The exports above |
| `outside/` | `--hazards` only: a file beside the Maildir that a symlink inside it points to |

## Where it refuses to write

The target must be new or empty, and must not sit anywhere a real pipeline
would ingest it. It refuses a directory inside a Maildir folder or a Maildir
root; anything under a directory holding `.mbsyncstate`,
`.mailvec-mbsync-heartbeat`, `.mailvec-mbsync-sync`, `archive.sqlite` or
`.frozen-corpus`; and anything inside the Maildir root, database directory or
config directory named by the Mailvec shared config
(`~/Library/Application Support/Mailvec/appsettings.Local.json`). It resolves
symlinks in the path first. An unreadable shared config is a refusal, not a
pass: it is the one file that knows where the real mail is.

An indexer run over a Maildir that gained a few hundred invented messages is
exactly the silent drift the frozen-corpus guard exists to prevent. Like that
guard, this is enforcement, not advice. The tests pin every refusal.

## What is in it

`manifest.json` is the complete list. In outline:

- **Structure:** multiple folders including a nested one; mbsync filenames with
  and without the `:2,` info suffix (`new/` vs `cur/`); drafts and trash flags;
  a three-message thread (References / In-Reply-To, quoting, a `-- `
  signature); a lone message.
- **Identity:** a message without a Message-ID; an IDN (punycode) Message-ID;
  one message in two folders as identical bytes; one Message-ID with divergent
  copies (a list footer on one).
- **Dates:** a pair whose ISO strings sort opposite to their instants (mixed
  offsets); a message with no Date.
- **Bodies:** plain UTF-8; multipart/alternative; HTML-only marketing mail with a
  hidden preheader, a `font-size:0` wrapper around real text, a tracking pixel,
  unsubscribe links and a `<footer>`; ISO-8859-1 quoted-printable; undeclared
  Windows-1252 8-bit; base64 UTF-8 with CJK and emoji; RFC 2047 headers; a
  long multi-chunk body.
- **Attachments:** text PDF (`done`); image-only scanned PDF (`no_text`, an OCR
  candidate); password-protected PDF (`encrypted`); DOCX, XLSX, PPTX; `.ics`;
  a vCard labelled `application/octet-stream`; a Windows-1252 `.txt` with no
  charset; a PNG sent as `application/octet-stream`; an inline `cid:` image
  after a regular attachment (the `part_index` ordering); TIFF; HEIC; an
  RFC 2231 non-ASCII filename.
- **Prompt injection:** a message addressed to "any AI assistant", with an
  instruction-shaped attachment filename, for seeing the MCP trust-model text
  work against it.
- **Filler:** seeded receipts, newsletters, family mail, project updates and
  travel bookings across 2024–2026, so search has distractors and pagination
  has pages.

The scanned PDF, PNG and TIFF carry legible bitmap text, so on a machine with a
vision model the OCR pass has something real to transcribe.

## `--hazards`

These are the cases Mailvec refuses or degrades on by design. They're off by
default so a plain corpus indexes clean. Each outcome is pinned by
`HazardTests`:

| Case | Outcome |
|---|---|
| `h01` DOCX zip bomb | attachment `failed`, fast; the message is indexed |
| `h02` 26 MB attachment | `oversize`, never decoded |
| `h03` symlink from the Maildir to `outside/` | skipped with a warning; not indexed. The scanner follows no symlink below the Maildir root |
| `h04` a folder literally named `tmp` | skipped with a warning; not indexed |

Adversarial PDFs that crash or hang PDFium live in `tools/Mailvec.ParserBench`,
not here: they are measurement fixtures, not something to index casually.

## Embeddings

Indexing needs nothing external. Embedding needs Ollama or a hosted profile,
and OCR needs a vision model. Without an embedding backend, keyword search and
every read tool work, and `hybrid` and `semantic` search answer "retry with
mode=keyword".

**`--embedding fireworks`** adds a hosted profile to `env.sh`: Fireworks
`qwen3-embedding-8b` at 1024 dimensions (the request shape the weekly cloud
smoke test uses, at the width production's mxbai has), with its own
space id (`fireworks:qwen3-embedding-8b:1024:devcorpus`), OCR switched off,
and `Proxy=environment`: a cloud session's egress only works through
`HTTPS_PROXY`, which is also where the key is attached. Hosted clients ignore
the proxy unless a profile opts in, and a profile holding its own key can't.
It is how a Claude cloud session exercises the hosted embedding path
(`OpenAiCompatibleTransport`, normalisation, sentinel fingerprints, the
space guards) that production, running Ollama, never touches.

**It carries no key, by design.** The profile uses `Auth:Scheme=none`, so
Mailvec sends no `Authorization` header. The key is attached outside the
session, by the cloud environment's egress proxy, from an API credential
scoped to `api.fireworks.ai` (setup in
[cloud-development.md](cloud-development.md#setting-up-the-environment)).
Nothing in the session holds it: not the process, the environment, a file, or
the transcript. Anywhere without that credential, every call is a 401.

```sh
dotnet run --project tools/Mailvec.DevCorpus -- /tmp/mvdev --embedding fireworks
. /tmp/mvdev/env.sh
dotnet run --project src/Mailvec.Indexer      # Ctrl-C once the scan logs
dotnet run --project src/Mailvec.Embedder     # Ctrl-C once status shows full coverage
dotnet run --project src/Mailvec.Cli -- status
dotnet run --project src/Mailvec.Cli -- search --hybrid "greenhouse sensors"
dotnet run --project src/Mailvec.Cli -- eval --queries "$MAILVEC_DEV_EVAL_QUERIES"
```

Sending mail text to a hosted provider is acceptable here **only** because
the corpus is invented. The profile belongs to dev corpora and nothing else.

The profile name is `fireworks_dev`, not `fireworks-dev`: it is spelled
inside every exported variable name, and a shell refuses `-` there without
stopping the script that sourced it. `HostedEmbeddingTests` sources the real
`env.sh` in bash and fails on any error output for that reason.

## Changing it

Add a scenario in `tools/Mailvec.DevCorpus/Catalog.cs` with its `Expect`
(fields in `Model.cs`; MIME and attachment helpers in `Mime.cs` and
`Documents.cs`; eval queries and hazards in `Extras.cs`), then run
`dotnet test tests/Mailvec.DevCorpus.Tests`. It indexes the corpus with the
real scanner and parser and checks every expectation, so a scenario that stops
meaning what its description says fails CI. Rules the tests hold you to:

- A scenario must resolve to exactly one live message: by its Message-ID, or
  by its subject when it has none (so that subject must be unique).
- An eval target must be eligible for a vector: a body of at least
  `Embedder:MinBodyCharsForVector` (100) characters, or attachment text.
- A new **hazard** is not checked automatically; add its assertion to
  `HazardTests` (or say there why it is deliberately unpinned).

Filenames come from one counter shared with the filler, so a new scenario
renames every later file. Output stays deterministic run to run, but counts
recorded elsewhere (the "284 messages" in cloud-development.md's dated
observations) describe the corpus as it was then. Update "What is in it"
above by hand. Keep everything invented and on reserved example
domains. **This repository is public: never seed the generator from real mail.**
