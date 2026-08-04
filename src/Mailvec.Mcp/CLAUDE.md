# CLAUDE.md — Mailvec.Mcp

Loaded when working under `src/Mailvec.Mcp/`. Same policy as the root file: only invariants whose violation is **silent**. The root `CLAUDE.md` still owns the version/release process and the eval-baseline rule.

## MCP API stability

Once Phase 5 starts (Gemini CLI / Codex CLI / ChatGPT desktop pointing at the same server), tool names, parameter names, and response field names become a **contract** — renames break every client at once. Treat this list as locked unless you're deliberately bumping the version:

- **Tool names**: `search_emails`, `get_email`, `get_thread`, `list_folders`, `view_attachment`, `get_attachment_text`, `get_attachment_page_image`. Set via `[McpServerTool(Name = "...")]` on each tool class — don't let the SDK infer from the C# method name. Registration goes through `ToolSurface.Resolve` (`Mcp:DisabledTools` removes named tools per deployment); a new tool class must be added to `ToolSurface.All` or a test fails, and an unknown name in the option fails startup rather than silently leaving a tool exposed. **`Mcp:DisabledTools` is empty in the live container** — the `Mcp__DisabledTools__*` lines in `compose.yml` are staged but commented, a deliberate call whose conditions are written out in `docs/security.md` ("Untrusted PDFs and images…"). Read that acceptance before assuming either state; earlier revisions of this file asserted the tools were dropped, which the compose file never did.
- **Tool annotations**: every tool carries `ReadOnly = true, OpenWorld = false`. Clients gate confirmation prompts on these, so a tool that gains a write path while keeping `ReadOnly = true` actively tells clients to stop asking. Pinned by `McpSurfaceTests.Every_tool_is_annotated_read_only_and_closed_world` — a failure there when adding a mutating tool is the alarm, not a stale assertion.
- **Hostile-content framing is part of the surface, not commentary.** `ServerInstructions` and every mail-bearing tool description classify returned mail as untrusted sender-controlled data (`ToolText.UntrustedContent`). It's free text, so only a test notices an edit that drops it — `McpSurfaceTests` asserts the distinctive terms at the wire. Rationale and what it is *not* worth: `docs/security.md` "Hostile mail content". **Those tests prove the text reaches the client, not that a model obeys it** — the efficacy is unmeasured, and closing that is tracked in `docs/future-ideas.md` "Adversarial testing of the prompt-injection framing". Don't read a green suite as evidence the framing works.
- **Parameter names** that travel back as references between tools: `partIndex` (returned by `get_email`, consumed by `view_attachment` / `get_attachment_text` / `get_attachment_page_image`); `id` and `messageId` everywhere; `mode` ∈ {`hybrid`, `keyword`, `semantic`}; `fromContains` / `fromExact` / `dateFrom` / `dateTo` / `folder` / `hasAttachments` / `attachmentType` filter set; `maxChars` / `offset` (get_attachment_text paging, sized from `get_email`'s per-attachment `extractedTextChars`).
- **Response field names** that clients narrate to users: `matchedAttachment.{partIndex,fileName}`, `archiveStats.{totalMessages,oldestDate,latestDate}`, `appliedFilters.*`, `get_thread`'s `{count,totalCount,truncated}` + per-entry `bodyTruncated` (**`count` means "entries in `messages`" and always equals its length** — the full thread size is `totalCount`; don't "fix" `count` to mean the thread size, that silently contradicts the array clients iterate), `webmailUrl`, `webmailLink` (the pre-escaped `[subject](url)` Markdown link, built by `WebmailLinkBuilder.MarkdownLink` and rendered verbatim by clients — the tools construct it server-side precisely so the untrusted subject can't be assembled into a spoofed link by the model; the tool descriptions tell clients to render it, not to build their own).
- **Server identity**: `serverInfo.name = "mailvec"` (lowercase, the protocol identifier — Phase 5 client configs key off it). Bump `serverInfo.version` whenever you ship a tool-surface change so a client log line of "I'm talking to mailvec 0.1.16" tells you which build you're seeing.

**The contract above is enforced at the wire, in [`McpSurfaceTests`](../../tests/Mailvec.Mcp.Tests/McpSurfaceTests.cs)** — the only tests that go through JSON-RPC rather than calling a tool class directly. That distinction is the whole point: the direct tool tests pass C#-named arguments, so an IDE rename refactors production and tests in lockstep, leaves the suite green, and breaks every client. Verified by mutation — renaming the `fromContains` parameter, or changing only a response field's wire name via `[JsonPropertyName]`, leaves every other test in the repo passing and fails only there. When you add a tool or a parameter, extend `LockedInputSchemas`; the `required` set is asserted exactly, because promoting a parameter to required breaks every call that omits it. Don't "fix" a failure there by editing the table to match the code — that's the alarm, not the bug.

Because this file only loads when you're working under `src/Mailvec.Mcp/`, `tests/Mailvec.Mcp.Tests/McpSurfaceTests.cs` is the backstop for anyone who edits the tests alone — a failure there is the contract breaking, not a stale table.

## Security controls read RESOLVED options, never the builder-time snapshot

`RunHttp` holds two `McpOptions`: the builder-time `mcpOpts` (a
`Configuration.Get<McpOptions>()` taken before `Build()`) and `resolvedMcpOpts`
(from `IOptions<McpOptions>` after it). **Every security decision must read the
resolved one.** The builder-time snapshot misses anything the options pipeline
applies later — which in tests is `WebApplicationFactory`'s config, and in
production is the shape of override an operator reaches for under pressure. A
control keyed off the snapshot silently reads its default and looks fine: this
is exactly how origin auth first shipped inert, with all 17 of its negative
tests passing because nothing was enforcing anything.

## Access signing keys come from `/cdn-cgi/access/certs`, and are fetched at boot

**Cloudflare Access publishes no OIDC discovery document at the team domain.**
`JwtBearerOptions.MetadataAddress` is therefore unusable, and v0.2.0 shipped
pointed at `/cdn-cgi/access/.well-known/openid-configuration`, which 404s on
every team domain. `AccessCertsRetriever` fetches the bare JWKS instead, wrapped
in a `ConfigurationManager` for its caching/refresh/backoff. **Don't "simplify"
this back to `MetadataAddress` or `Authority`** — both assume discovery.

The failure mode is why this is here rather than in a comment alone. A metadata
failure is caught by `JsonWebTokenHandler`, logged as IDX10261 to
`IdentityModelEventSource` — an `EventSource`, so **Serilog never sees it** — and
validation proceeds with zero keys. The origin logs "validation ENABLED", passes
its healthcheck (loopback is exempt), and 401s every real caller with IDX10500,
having logged no retrieval attempt at all. Every negative test passed
throughout: a server that authenticates nobody refuses bad tokens perfectly.

Two consequences to preserve. `AccessAuth.VerifySigningKeysAsync` fetches at
startup, logs the URL and the `kid`s, and **refuses to boot on no keys** — don't
make it lazy or best-effort. And `AccessCertsRetriever` **throws** on an empty
key set rather than returning a keyless configuration, so a bad refresh keeps
the last good keys instead of silently degrading to authenticating nobody.

`AccessAuthTests` injects keys via `StaticConfigurationManager` and so cannot
see any of this; `AccessSigningKeyTests` is the file that covers retrieval.
Reverting the URL fails four of its tests — that's the alarm.

`mcpOpts` exists only for wiring that genuinely cannot wait for `Build()` —
Kestrel's listen address, and the `HostGuard` allowlist baked into a middleware
closure. `EnableTrayEndpoints`, `TrayExposureGuard`, and everything under
`Mcp:Access` read `resolvedMcpOpts`. `AccessAuth` goes further and reads
`IOptions<McpOptions>` at *request* time, so even its registration carries no
snapshot.

## MCP transport quirks

- **MCP runs in two transport modes** sharing the same Core wiring (`AddMailvecServices` helper in `Program.cs`):
  - **HTTP (default)**: `MapMcp()` mounted at `/` over Streamable HTTP, **stateless** — `HttpServerTransportOptions.Stateless` defaults to true as of MCP SDK 2.0 (the 2026-07-28 protocol revision), and we take the default. No `initialize` handshake is required, no `Mcp-Session-Id` is issued or expected, the legacy `/sse` endpoint is off, and a bare `POST /` with `tools/list` or `tools/call` works — which is what makes `curl` smoke tests one-liners again. Down-level clients that still send `initialize` (2025-06-18 / 2025-11-25) are answered normally and get `serverInfo` + `ServerInstructions`; they just get no session header back.
  - **Stdio (`--stdio` flag)**: Generic `Host` + `WithStdioServerTransport()`. Used by Claude Desktop because its connector schema only supports stdio. Wired up via `ops/run-mcp-stdio.sh`.
- **Stateless HTTP makes server→client requests impossible**, and that's a design constraint, not a config detail: sampling / elicitation / roots need a channel back to the client, and any response could land on a different process. The SDK deprecates `SampleAsync` / `RequestRootsAsync` / MCP logging (`MCP9005`) accordingly, and `TreatWarningsAsErrors=true` turns using them into a build failure. If a tool ever needs to ask the user something, the 2026-07-28 way is to throw `InputRequiredException` with `InputRequest.ForElicitation(...)` (MRTR), which the client resolves and retries — **not** to flip `Stateless = false` to get the old API back. Stateful is a back-compat-only escape hatch in the SDK's own words: a 2026-07-28 client that sends a session id to a stateful server gets `-32022 UnsupportedProtocolVersion` and downgrades, silently losing the newer codec. Pinned by `ProgramHttpTests.Mcp_endpoint_serves_tools_list_with_no_handshake_and_no_session`.
- **In stdio mode, all logging must go to stderr.** Stdout is the JSON-RPC channel; a single byte on stdout corrupts the protocol. `Program.cs` calls `ClearProviders()` and sets `LogToStandardErrorThreshold = LogLevel.Trace`; `SerilogSetup.Configure(..., stdioMode: true)` passes `standardErrorFromLevel: LogEventLevel.Verbose` to the Console sink so even Verbose/Debug events go to stderr. Don't add any stdout writer in `RunStdio`. Don't use `dotnet run` for stdio launching — its build chatter goes to stdout. `ops/run-mcp-stdio.sh` builds quietly to a log file (`$TMPDIR/mailvec-mcp-stdio-build.log`) and execs the compiled DLL directly.
- **Claude Desktop launches MCP servers with a sanitized environment AND a hard read block on `~/Documents`.** Three quirks:
  1. **`~/Documents` is unreadable for content even with Full Disk Access.** Spawned children can `stat()` files but `open()` returns EPERM (likely a per-app `com.apple.macl` ACL). FDA + Documents toggle in System Settings does not fix this. **Workaround**: don't run anything from inside `~/Documents`. The MCP binary is `dotnet publish`-ed to `~/.local/share/mailvec/mcp/` (by `ops/install-stdio-launcher.sh` for non-Claude stdio clients, by `ops/install.sh` / `ops/redeploy.sh` for the launchd HTTP service). Diagnostic distinction: `ls -l <file>` succeeding doesn't mean Claude can open the file (`ls` is just `lstat()`); have the diag script `head -1 <file>` and check exit status.
  2. **PATH excludes `/usr/local/share/dotnet`.** Spawned PATH is `~/.nvm/.../bin:/opt/homebrew/bin:~/.local/bin:/usr/bin:/bin:/usr/sbin:/sbin`. The Claude Desktop MCPB bundle dodges this by being self-contained (`--self-contained true` in `ops/build-mcpb.sh`). For non-Claude stdio clients (Phase 5 — Gemini CLI, Codex CLI, ChatGPT desktop), `ops/install-stdio-launcher.sh` writes a `~/.local/bin/mailvec-mcp-stdio` shim that exports `DOTNET_ROOT` and prepends it to `PATH` so the framework-dependent binary resolves a runtime.
  3. Use `/bin/bash <script>` form in `claude_desktop_config.json` (not the script directly) — avoids depending on the shebang interpreter being on Claude's allowed-exec list. Diagnostic recipe: have the script `echo` to `>&2`, end with `cat >/dev/null` so stdin stays open and logs land in `~/Library/Logs/Claude/mcp-server-mailvec.log` before SIGTERM.
- **MCP and CLI must use identical Core wiring.** `Mailvec.Mcp/Program.cs` mirrors `CliServices` (same singletons, same HttpClient setup for `OllamaClient`). If you add a search-affecting service, register it in both — drift means CLI debugging stops matching MCP behaviour.
