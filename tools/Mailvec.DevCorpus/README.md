# DevCorpus — a synthetic mailbox for dev runs

Writes an invented, mbsync-shaped Maildir plus an `env.sh` that points every
Mailvec binary at it, a labelled eval query set, and a manifest. For running
the services by hand where there is no real mail (a Claude cloud session) or
where the real mail must not move (the frozen corpus on the dev Mac). Not part
of the shipped product: nothing in `src/` references it.

**The guide is [`docs/contributing/dev-corpus.md`](../../docs/contributing/dev-corpus.md)**
— usage, layout, where it refuses to write, what each scenario exercises,
`--hazards`, and `--embedding fireworks`.

```sh
dotnet run --project tools/Mailvec.DevCorpus -- /tmp/mvdev   # [--filler N] [--hazards] [--embedding fireworks]
. /tmp/mvdev/env.sh
dotnet run --project src/Mailvec.Indexer                      # Ctrl-C once the scan logs
```

## Safety

- **It refuses any target a real pipeline would ingest**: inside a Maildir
  folder or root, under `.frozen-corpus` / `archive.sqlite` / the mbsync
  markers, inside the locations the Mailvec shared config names, or behind a
  symlink to any of those. Enforced in `Guards.cs`, pinned by `WriterTests`.
- **Everything in it is invented**, on reserved example domains. This
  repository is public: never seed it from real mail.
- **`--embedding fireworks` sends the corpus to a hosted provider.** That is
  acceptable only because it is invented. The profile holds no key
  (`Auth:Scheme=none`); in a cloud session the egress proxy attaches it.

## Layout of this project

| File | What |
|---|---|
| `Catalog.cs` | The hand-authored scenarios, each with its `Expect` |
| `Filler.cs` | Seeded background mail |
| `Extras.cs` | The eval query set and the `--hazards` cases |
| `Mime.cs`, `Documents.cs`, `BitmapFont.cs` | Raw MIME, PDF/Office/PNG/TIFF/ICS/vCard, and the font scanned pages are drawn in |
| `HostedEmbedding.cs` | The `--embedding` profiles written into `env.sh` |
| `Guards.cs` | Where it refuses to write |
| `CorpusWriter.cs` | Builds everything in memory, then writes it |

`tests/Mailvec.DevCorpus.Tests` indexes the output with the real scanner and
parser and checks every scenario's expectation; add a scenario in
`Catalog.cs` and the test covers it automatically.
