# Mailvec site

Notes for the static landing page in [`site/`](../../site/). Plain HTML, CSS and a little JS. No build step, no framework, no third-party requests (fonts are self-hosted).

```
site/
  index.html              the whole page
  styles.css              fonts, theme tokens (light/dark), interactive states, mobile overrides
  script.js               theme toggle, mobile menu, example questions, privacy diagram switch, install tabs, copy button
  favicon.svg / .ico      favicon (SVG follows the OS light/dark setting)
  apple-touch-icon.png    180 × 180
  og-image.png            1200 × 630 social card
  assets/
    claude-desktop-answer.png   S1, the real hero screenshot (synthetic mailbox)
    brand/                      mark and wordmark SVGs: light, dark, currentColor
    fonts/                      IBM Plex Sans, JetBrains Mono, Newsreader (woff2, latin), OFL licences
```

Preview locally with `python3 -m http.server --directory site` from the repo root. Opening `index.html` straight from disk works, but browsers block the font preloads over `file://`.

## Deploying

GitHub Pages, via [`.github/workflows/pages.yml`](../../.github/workflows/pages.yml): every push to `main` that touches `site/` publishes the folder as-is (or run the workflow by hand). The repo's Pages source must be "GitHub Actions". This README lives outside `site/` so it isn't published. `index.html` assumes the site lives at `https://djdelaney.github.io/Mailvec/`. If it ends up somewhere else, update `canonical`, `og:url` and `og:image` in the `<head>`.

## Notes for editing

- Most layout is inline `style="…"` attributes, carried over from the design canvas. Colours always come from the CSS variables in `styles.css`, so both themes stay in sync.
- The theme defaults to the OS setting. The toggle saves the choice to `localStorage` under the `mailvec-theme` key.
- Example questions: each `.askcard` button stores its tool call and result in `data-call` and `data-result`.
- Below 860px wide, diagrams stack vertically and arrows rotate (see the `@media` block in `styles.css`).

## Validation checklist (against the demo mailbox)

All mail content on the page is invented to match the synthetic demo mailbox (`assets/screenshots/demo-mail.py`: Sam Rivera, Bayside Tile & Bath, Keel & Plumb). Anything marked "illustrative" or "placeholder" needs replacing with a real capture or confirming.

Checked against the code at v0.12.0 (schema 14), 2026-11-09. Re-check after any tool-surface change, and recount the stats at launch.

**Verified**

- **Tool and parameter names** in section 05 match the locked surface (`src/Mailvec.Mcp/CLAUDE.md`): `search_emails`, `get_thread`, `get_email_headers`, `get_attachment_text`, `get_attachment_page_image`, and the `fromContains` / `attachmentType` / `dateFrom` / `dateTo` / `folder` / `unread` / `id` / `messageId` / `partIndex` / `page` parameters. "8 read tools" is `ToolSurface.Core`; `mark_read` is the optional ninth, Docker only.
- **S2 JSON field names** match `SearchEmailsResponse` / `EmailHit` / `MailSyncStatus`, including `mailSync.intervalSeconds`. `webmailLink` is `[subject](url)`, built server-side. `mailSync` is present only where the mbsync sidecar writes its marker (the Docker install), and the page says so.
- **Eval numbers** (sections 03 and 09) match `baselines/2026-08-07-post-tray-removal.json`: 70 queries; NDCG@10 0.859 / 0.814 / 0.916; Recall@10 0.940 / 0.913 / 0.977.
- **Pipeline diagram** (section 02): OCR is the embedder's pass, not the indexer's.
- **S3 `mailvec doctor`** (section 09): sections, check names, detail formats, column width and summary line follow `DoctorCommand.cs` for a healthy Mac install. Paths, pids, counts and times are invented. Still not a real capture.
- **Install commands** (section 10) match `docs/getting-started-macos.md` and `docs/getting-started-docker.md`, trimmed. The Docker block includes the `MAILVEC_REQUIRE_SEEDED_DB=0` step and the empty `embedding_api_key` secret that `compose.yml` mounts. The server card says images build locally or can be pinned from GHCR (`docs/deploy-docker.md`).
- **Sync intervals**: 60 s on Docker (`MBSYNC_INTERVAL_SECONDS`), 600 s on a Mac (`ops/launchd/com.mailvec.mbsync.plist`); IMAP push and mark-read are Docker only.
- **Clients**: named clients are trimmed to the ones with guides (Claude Desktop, Claude Code, claude.ai and the Claude apps). Cursor, VS Code, Codex, Gemini CLI, Zed and LM Studio were removed because none has been tested; add one back once it has. The Cloudflare guide only registers claude.ai's OAuth callback, so the page no longer claims other OAuth clients.
- **Stats**: ~1,460 test methods (1,386 xUnit `[Fact]`/`[Theory]` plus 75 Python), shown as 1,400+.
- **Links**: GitHub, docs, licence, both getting-started guides, DevCorpus and Knapper all return 200. The Pages URL itself 404s until the site is deployed.

**Still open**

1. **Example questions** (section 05): the ids (48213, 51207, 39114), the `ledgerlane` sender and the results are invented. Replace them with real ones from the demo mailbox, or add matching demo mail.
2. **Hybrid search diagram** (section 03): the "Ada" reply and the "Home Weekly" newsletter don't exist in the demo yet. Confirm the lanes and fused order against what the demo actually returns.
3. **S2 values** (section 03): field names are right; values still need a dev-server capture.
4. **S3 `mailvec doctor`**: replace with a real capture. The ✓ glyph falls back to a system font (JetBrains Mono's latin subset lacks it).
5. **S5 pet deposit** (section 06): the scanned lease, "section 7", "$500" and `signed-scan.pdf` are invented. Capture a real `get_attachment_page_image` result, or add a scanned lease to the demo.
6. **Attachment flow** (section 06): filenames are invented. Encrypted PDFs are recorded as `extraction_status = encrypted` and surfaced through `get_email`; check the wording against that.
