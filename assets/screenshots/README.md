# README screenshots

`claude-desktop-answer.png` is Claude Desktop answering a question from a
synthetic mailbox: the [dev corpus](../../docs/contributing/dev-corpus.md) plus
five hand-written messages from [`demo-mail.py`](demo-mail.py) (two contractor
quotes, one with an itemised PDF estimate, a note comparing them, and a
scheduling thread). No real mail is involved at any step, and none may be:
this image and this directory are public.

Nothing here installs an agent or touches the frozen corpus. Every service
runs by hand against the demo directory.

## Recapturing

On the dev Mac, from the repo root. The steps use `~/MailvecDemo`; any new or
empty directory the dev corpus accepts will do.

```sh
dotnet run --project tools/Mailvec.DevCorpus -- ~/MailvecDemo
python3 assets/screenshots/demo-mail.py ~/MailvecDemo/Mail
. ~/MailvecDemo/env.sh
dotnet run --project src/Mailvec.Indexer          # Ctrl-C once it logs "MaildirScanner: seen=…"
ollama serve &                                    # if it isn't already running; needs mxbai-embed-large
Embedder__OcrEnabled=false dotnet run --project src/Mailvec.Embedder   # Ctrl-C once status shows 100%
dotnet run --project src/Mailvec.Cli -- status    # check Database:/Maildir: are the demo paths
dotnet run --project src/Mailvec.Cli -- search --hybrid "how much did the contractor quote for the bathroom"
dotnet publish src/Mailvec.Mcp -c Release -o ~/MailvecDemo/mcp
```

- **Embed it.** Without embeddings, Claude's first search comes back "retry with
  `mode=keyword`", and the visible tool trace gets messier.
- **OCR off.** The corpus has a scanned PDF, and the OCR pass would load the
  vision model to transcribe it. The screenshot doesn't need that.
- **The search check** should put all five demo messages at the top.

Then, with Claude Desktop **quit**, add a local server to
`~/Library/Application Support/Claude/claude_desktop_config.json` (back the
file up first) and relaunch:

```json
"mcpServers": {
  "Mailvec": {
    "command": "/usr/local/share/dotnet/dotnet",
    "args": ["<home>/MailvecDemo/mcp/Mailvec.Mcp.dll", "--stdio"],
    "env": {
      "Archive__DatabasePath": "<home>/MailvecDemo/state/archive.sqlite",
      "Ingest__MaildirRoot": "<home>/MailvecDemo/Mail",
      "Fastmail__AccountId": ""
    }
  }
}
```

- **Point it at the published DLL, never `dotnet run`.** In stdio mode, stdout
  is the JSON-RPC channel, and `dotnet run` prints build output there.
- **The `env` block is what keeps it on the demo mailbox.** Environment variables
  outrank the shared `appsettings.Local.json`, which points at a real corpus.
- **Name the server `Mailvec`**, so the tool trace reads "Used Mailvec
  integration".

In a new chat, **turn the claude.ai Mailvec connector off** before asking. With
both on, Claude can answer from the real mailbox, and that answer ends up in a
public image. Then ask, for example: "Use mailvec, how much did the contractor
quote for the bathroom work?"

Things to know about the answer:

- **Claude may notice the persona.** It knows who you are, so it may point out
  that the mail is addressed to "Sam Rivera", not you. Try an incognito chat
  first. The current image had that paragraph removed afterwards: a band of
  blank rows was cut out, which leaves no seam because paragraphs sit on a
  plain background.
- **Dates are fixed.** The story is dated August 2026, and Claude compares the
  dates in it with today's date. If a deadline in the story has passed, Claude
  may say so. Shift the `datetime(...)` values in `demo-mail.py` if that gets
  in the way.

Capture the window with ⌘⇧4, then Space, then ⌥-click the window. That gives
a window-only PNG with transparent corners and no shadow. A drag-selected
region picks up the desktop around the rounded corners.

The README shows the image at a fixed `width`. If the new capture's pixel
width differs much from the old one, scale that attribute to match, or the
text renders smaller.

## Cleaning up

With Claude Desktop quit, restore the config backup (or remove the
`mcpServers` entry). Then stop Ollama if you started it (`pkill ollama`) and
delete `~/MailvecDemo`.
