# Future ideas

Considered, then deferred. Captured here so the reasoning isn't lost if someone re-opens the question later.

## Cross-vendor cloud clients

The Cloudflare Tunnel and Access Managed OAuth already give Claude's cloud a
public MCP endpoint ([current wiring](remote-access-cloudflare.md)). A second
cloud connector could use it, but Mailvec has no per-client or per-tool scope:
it would receive the same access to the whole mailbox. Defer until there is a
reason to add a non-Claude cloud client, then design that scope before adding
its credential. The [security model](security.md#whats-out-of-scope)
covers the existing boundary.

## Multi-user / federated identity

Still out of scope — the archive is single-account and nothing scopes results per-caller, so a second identity on the Access policy would get the owner's entire mailbox rather than a view of their own. That's a data-model problem, not an auth-config one. See [security.md → What's out of scope](security.md#whats-out-of-scope).

## Adversarial testing of the prompt-injection framing

**The one deferred item that makes a shipped control weaker than it looks**, so
it's written down rather than left implied.

Mailvec classifies everything it returns as untrusted sender-controlled data —
in `ServerInstructions`, in every mail-bearing tool description
(`ToolText.UntrustedContent`), and via `ReadOnly`/`OpenWorld` tool annotations.
`McpSurfaceTests` pins all of it at the wire. **But those tests assert the text
*reaches the client*, not that a model *acts on it*.** A crafted message that
talked an agent into chaining Mailvec output into another connector would pass
every test in this repo today. The framing is a mitigation of unmeasured
strength; the test count says nothing about its efficacy.

What closing it would actually take — closer in shape to the `baselines/` eval
harness than to a unit test:

- **Hostile fixtures across every channel the framing claims to cover**: plain
  text body, hidden HTML (the `font-size:0` / preheader tricks `HtmlToText`
  already strips for other reasons), an attachment filename, extracted PDF text,
  and OCR'd text rendered *into* a page image. The last is the interesting one —
  it's the only channel where the payload never exists as text anywhere in the
  pipeline, so nothing upstream could filter it even in principle.
- **A real model with a second, observable, harmless tool attached** — the
  measurement is whether the model reaches for that tool, not whether it says
  something alarming. Without a second tool there's no exfiltration path to
  observe and the test proves nothing.
- **A pass criterion that survives model updates.** Injection success is
  probabilistic, so a single run is noise; this needs a rate over N trials and a
  threshold, which is why it belongs with the eval harness rather than in
  `dotnet test`. It will also need re-running on model changes, like a ranking
  baseline.

Deliberately **not** the answer: regex/heuristic detection of "injection-looking"
text. It fails open on everything it doesn't match while reading like a control
that works, and it would make this item look closed. See
[security.md → Hostile mail content](security.md#hostile-mail-content-indirect-prompt-injection).

Un-defer when either a mutating tool lands (raising what a successful injection
gets you from "read your own mail" to "act on your behalf"), or Mailvec is
routinely used alongside connectors that can send or post.

## A GUI, if one is ever wanted again

A SwiftUI menu-bar tray app and the plain-REST `/tray/*` surface it polled were
**removed** once the container became the only deployment in use and the author
stopped using the tray. Recoverable from git history if that reverses.

Two things to know before recovering it, because neither is visible in the diff:

- **The Swift app recovers cleanly; the C# glue does not.** The app talks JSON
  over HTTP and is self-contained. The ~3.4k lines of `Core/Tray` + `Mcp/Tray`
  glue were wired into Core and Mcp APIs that will have moved, so expect to
  rewrite it rather than cherry-pick.
- **A remote GUI needs identity work that dominates the effort.** `/tray/*` had
  no per-request auth and relied entirely on being loopback-only. Exposing it
  needs origin authentication (`Mcp:Access` could cover it with an audience
  policy), a non-interactive credential the app can hold — which makes it a
  *second identity* and invalidates the single-identity acceptances in
  security.md — and CSRF protection on the mutating POSTs, which previously
  relied on being unreachable rather than on any token.

## Packaged distribution (installer + notarized artifacts)

Today the **only** way to get any part of Mailvec is to build from source: clone
the repo, install the prereqs via Homebrew (including the .NET 10 SDK), then
`ops/install-all.sh`. That's fine for the author and for contributors; it's a
real adoption wall for anyone else. A distribution story would have two
artifacts, both buildable from the existing scripts:

1. **Services + CLI.** The four .NET binaries are already `dotnet publish`-ed
   by `ops/install.sh`; a release artifact would be that published output
   (self-contained, like the MCPB, to drop the .NET SDK prerequisite) plus
   the installer running against it instead of the working tree. Signing +
   notarization applies here too — launchd runs local unsigned binaries fine,
   but downloaded ones carry quarantine. A Homebrew tap/cask is the
   alternative packaging, with its own update story.
2. **Prebuilt `.mcpb` per release.** `ops/build-mcpb.sh` output attached to
   the GitHub Release — it's already self-contained; it just isn't published
   anywhere. (It's the read-side only: without the installed services there
   is nothing to search — the `setupHint` guard covers that failure mode.)

CI can build both on a `v*` tag now that unified versioning + tagging
exist. What stays user-owned regardless of packaging: mbsync config, the IMAP
app-password in the Keychain, and Ollama model pulls — the installer
prompts/checks for these but deliberately doesn't own them.

Deferred until there are actual second users to distribute to.

## Internationalization (CJK search + localized reply trimming)

Parked until there's a real user with substantial non-English mail. Two
separate problems, one trigger:

1. **CJK is dead in the keyword leg.** `messages_fts` uses `porter unicode61`,
   which segments on whitespace/punctuation — Chinese/Japanese text indexes as
   one giant token, so BM25 matches nothing inside it and hybrid quietly
   degrades to vector-only (losing exact-match strength: names, order numbers,
   domains). The standard fix is FTS5's built-in **trigram** tokenizer (works
   for any language, no segmenter), but it changes BM25 behavior for English
   too — shifting ranking on *every* keyword query. That makes it a design
   session, not a patch: full `rebuild-fts`, complete eval re-baseline, and
   possibly a dual-index design (porter for Latin, trigram shadow index) to
   avoid regressing the tuned English experience. Measurement gap: the eval
   query set is English — scoring a CJK improvement needs CJK mail and CJK
   labeled queries first.
2. **ReplyTrimmer only speaks English** ("On … wrote:",
   "-----Original Message-----"). Localized markers ("Am … schrieb:",
   "Le … a écrit :", 差出人:) sail past it, so non-English reply threads
   re-embed the full quoted history in every message — inflating the vector
   space and BM25 term counts with exactly the duplication the trimmer exists
   to prevent. Mechanically easy (Gmail/Outlook localizations are well
   documented; add patterns + per-language fixtures) and the cheap first move
   when the trigger arrives; still needs re-processing affected messages and
   a re-baseline.

## Polling below one minute

The Docker mbsync loop already waits 60 seconds **after** each sync finishes,
so runs cannot overlap. Its heartbeat has a separate timer; tying heartbeat
to sync completion would falsely report a busy sidecar as dead during a long
backlog pull. The macOS launchd job remains at 600 seconds: its independent
`StartInterval` caused `.mbsyncstate` lock failures at 300 seconds in a dated
observation, so the Docker result does not transfer.

A faster INBOX-only poll alongside a slower full-account sync remains an option
if provider throttling or SQLite contention is measured. It would change
freshness for labels, moves, deletions, and flags, and would need a single
serialized runner. Re-measure on the target deployment before implementing it.

## Still open (small)

Carried forward from the original design doc — none are committed work, all gated on a problem actually being observed:

- **Thread reconstruction.** Today's `In-Reply-To` / `References` heuristic is acceptable; revisit if mismatches with Fastmail's JMAP threading become a usability issue.
- **JMAP-specific metadata.** IMAP flags are available via mbsync, but JMAP-only fields (masked email, server-side labels) would require a separate JMAP path. Not currently planned.
- **WAL checkpointing strategy.** No periodic auto-checkpoint beyond SQLite's default (every 1000 frames). Measure long-running `-wal` growth before adding one; `mailvec checkpoint` handles one-off cleanup.
- **Duplicate-worker exclusion.** Heartbeat detection reports two indexers sharing a database, but does not serialize their scans. If that becomes an operational risk, add an interprocess lock around `ScanAll`; do not turn a fresh heartbeat from a normal restart into a startup refusal. See [CLAUDE.md](../CLAUDE.md#schema--data-invariants).

## Out of scope entirely

Sending mail, modifying server-side state (marking read, moving, deleting), multi-account support, calendar/contacts/files (even though Fastmail offers these via CalDAV/CardDAV/WebDAV — this project is mail-only), a web UI, and real-time push notifications (mbsync is timer-driven, not IDLE/JMAP push).
