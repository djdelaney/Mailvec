# Mailvec.OutlookExport

Standalone Windows console tool that attaches to a **running classic (Win32)
Outlook** over COM and exports mail as `.eml` files in a Maildir-shaped tree
that Mailvec's indexer ingests as-is. It rides the already-authenticated
Outlook session, so it needs **no Graph API access, no app registration, no
IT involvement** — the escape hatch for corporate tenants that block
user-initiated app registrations.

It does not work with "new" Outlook (the WebView2 app) — that client has no
COM object model. Keep the classic client while using this.

## Output layout

```
<out>/<Store Name>/<Folder.Subfolder>/{tmp,new,cur}/<unixtime>.<sha16-of-message-id>.outlookexport.eml
```

- Folder nesting flattens to dots (`Inbox/Projects` → `Inbox.Projects`),
  mirroring mbsync's "Subfolders Verbatim" style. Mailvec's `MaildirScanner`
  walks any directory containing `new/` or `cur/`, so the exact style doesn't
  matter — only the `new`/`cur` leaves do.
- File names are deterministic (derived from the internet `Message-ID`, or a
  hash of the Outlook EntryID when a message has none), which makes re-runs
  **incremental**: existing files are skipped without touching COM bodies.
- Messages are written to `tmp/` and renamed into `cur/` (Maildir delivery
  convention), so sync tools never pick up half-written files.

## One-time check on the work machine

Before building anything, verify the Outlook object model isn't disabled by
policy. In PowerShell (with Outlook running):

```powershell
$o = New-Object -ComObject Outlook.Application
$o.Session.GetDefaultFolder(6).Items.Count   # 6 = Inbox
```

A number means COM access works. A "programmatic access" security prompt may
appear on machines without healthy antivirus state — corporate images with
Defender active normally suppress it. If the `New-Object` itself is blocked by
GPO, this tool won't work; fall back to a manual PST export + offline parsing.

## Build (on any machine with the .NET 10 SDK)

```sh
dotnet publish src/Mailvec.OutlookExport -c Release -r win-x64 \
  --self-contained -p:PublishSingleFile=true
```

Copy the single `Mailvec.OutlookExport.exe` from
`src/Mailvec.OutlookExport/bin/Release/net10.0/win-x64/publish/` to the work
machine. Self-contained means the work machine needs no .NET install.

## Usage

```text
Mailvec.OutlookExport [--out <dir>] [--since yyyy-MM-dd] [--include <folderPath>]...
                      [--exclude <folderName>]... [--all-stores]
                      [--max-attachment-mb <n>] [--dry-run]
```

Typical first run (see what it would touch, then export the recent year):

```powershell
.\Mailvec.OutlookExport.exe --dry-run
.\Mailvec.OutlookExport.exe --since 2025-06-01 --out $env:USERPROFILE\MailvecExport
```

Defaults: exports the default store only; skips Deleted Items, Junk, Drafts,
Outbox, Sync Issues, RSS, and Conversation History; caps attachments at 25 MB
(matching Mailvec's `AttachmentMaxBytes`).

Scheduled incremental runs (Outlook must be running; `--since` bounds the COM
walk for speed — dedup-by-filename keeps the overlap harmless):

```powershell
schtasks /Create /TN MailvecExport /SC DAILY /ST 18:00 `
  /TR "%USERPROFILE%\Mailvec.OutlookExport.exe --since 2025-06-01"
```

## Getting the tree to the Mac

Any file sync works — the tree is plain files with no locks. Options, in order
of preference:

1. A synced folder (OneDrive/Dropbox/Syncthing) as `--out`, with the Mac-side
   replica added under Mailvec's `Ingest:MaildirRoot` (or pointed at directly).
2. Periodic `rsync` over SSH from the Mac, pulling the export dir.

Then the normal pipeline takes over: Indexer parses the `.eml` files (including
attachment text extraction), Embedder vectorises, search works.

## Fidelity notes / limitations

- **Headers are synthesized**, not the original transport headers. From/To/Cc/
  Bcc/Subject/Date plus `Message-ID`, `In-Reply-To`, and `References` (pulled
  from MAPI proptags) are preserved — enough for Mailvec's dedup
  (`Message-ID`) and threading (`get_thread` via the reference chain).
  Received-path and DKIM headers are not.
- Exchange-internal addresses are resolved to SMTP where possible; unresolvable
  X.500 entries fall back to `unknown@unresolved.invalid` rather than dropping
  the message.
- RTF-format messages export their plain-text rendition only.
- OLE-embedded objects are dropped (counted in `attachmentsDropped`).
- **Deletions don't propagate.** Deleting a message in Outlook doesn't remove
  the exported file. The export is an append-only archive; prune the export dir
  manually if that ever matters.
- BCC is included when present (i.e. on your own sent items).
