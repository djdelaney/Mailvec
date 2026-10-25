# Provider traces

The author's own deployment is a Fastmail account in *folder* mode, where
every message lives in exactly one mailbox. Mailvec's multi-copy rules (one
row per Message-ID, folder membership from `sync_state`, read when no copy is
unseen, rename repair, the survivor test in reconciliation) are therefore
exercised daily by almost nothing. Gmail is the opposite: every label is an
IMAP folder, and one message is a file in each folder whose label it carries,
`[Gmail]/All Mail` nearly always among them.

What makes Gmail different is mostly **how the Maildir changes over time**, not
what any one message looks like. Archiving deletes the INBOX copy while the
All Mail copy stays. Removing a label deletes one copy. Trashing takes the
message out of All Mail. One flag change becomes renames across several
folders. A snapshot can't show any of that. A trace can.

Two tools cover it:

| | What | Where |
|---|---|---|
| **Gmail layout** | The dev corpus written as one file per label: the static shape. | `dotnet run --project tools/Mailvec.DevCorpus -- <dir> --layout gmail` ([dev-corpus.md](dev-corpus.md#--layout-gmail)) |
| **Provider traces** | Sequences of Maildir states, replayed through the real scanner: the transitions. | `tests/Mailvec.Indexer.Tests/ProviderTraces/` |

## What a trace is

One directory per trace: `<provider>/<name>/trace.json` plus `blobs/*.eml`.
Each step records:

- **`maildir`**: every message file mbsync has left on disk after the step,
  as Maildir-relative path → blob name.
- **`server`**: what the server itself holds, as Message-ID →
  `{ mailboxes, seen }`.
- **`serverAction`**: what happened, in words.

```json
{
  "version": 1, "provider": "gmail", "source": "hand-written",
  "description": "…",
  "unsynced": ["[Gmail]/All Mail"],
  "steps": [
    {
      "id": "archived",
      "serverAction": "m1 is archived: the INBOX copy is expunged.",
      "maildir": { "[Gmail]/All Mail/cur/1767603600.1001_2.mvtrace,U=501:2,S": "m1.eml" },
      "server": { "m1-receipt@trace.example": { "mailboxes": ["[Gmail]/All Mail"], "seen": true } }
    }
  ]
}
```

`unsynced` lists mailboxes the trace's mbsync `Patterns` leave out. Optional
step fields: `settled` and `divergence`, described below. Unknown fields are
refused, so a typo can't silently turn a check off.

## What the replay checks

`TraceReplayTests` writes each step into one Maildir the way mbsync would. A
path present in both steps is left untouched, mtime included, so the scanner's
fast path sees exactly what it would. A vanished path is deleted, and a new one
is written. It runs `ScanAll`, then holds the index to two oracles:

1. **The Maildir oracle, on every step.** The index has to agree with what
   the files imply under Mailvec's own rules: one live row per Message-ID, a
   member of every folder holding a copy, and read only when no copy is
   unseen. This is the scanner's consistency across transitions.
2. **The server oracle, on settled steps.** The index has to agree with
   `server`, restricted to synced mailboxes. A message left in no synced
   mailbox must not be live. For a recorded trace this is the real question:
   does mbsync plus Mailvec represent the account correctly?

On every step it also checks these:

- No parse failures and no held deletions.
- Each row's attributed folder is one of its copies, and the file it points
  at exists.
- After one convergence scan (a repoint clears the survivor's hash on
  purpose), a further scan upserts nothing and takes no writer lock.

**`settled: false`** marks a state that is real but transient: mid-way
through an `mbsync -a` run, or between the mark-read sidecar's local rename
and the next pull. Only the Maildir oracle applies there, and `server` is
optional.

**`divergence`** marks a settled step where Mailvec is *known* to disagree
with the server, with the reason. The replay requires the disagreement to
still be there, so when a fix lands the note fails instead of going stale.

Verified by mutation (2026-10-24): disabling the survivor test in
reconciliation fails `label-lifecycle` and `all-mail-excluded` (an archived
message is soft-deleted). Disabling rename repair fails `label-lifecycle` and
`read-propagation` (rows point at files that are gone).

## The traces

All four are **hand-written**: `source: "hand-written"`, built from Gmail's
documented IMAP behaviour and never observed. They encode beliefs, and the
replay can only show that Mailvec handles those beliefs correctly.

| Trace | Belief it encodes |
|---|---|
| `gmail/label-lifecycle` | One message through arrive → read in webmail → labelled → archived → unlabelled → trashed → deleted forever. Archive and unlabel are not deletions; trash leaves All Mail. |
| `gmail/all-mail-excluded` | `Patterns * ![Gmail]/All Mail` (the config that halves the download). Archiving is then a deletion; a label move is a deletion followed by a resurrection, with an unsettled step between the two folders' syncs. |
| `gmail/read-propagation` | `mark_read` on a label-mode account (open item 2 in [read-state.md](../proposals/read-state.md)): the INBOX copy is seen, the All Mail copy isn't until the next pull, and the message reads unread in between. |
| `gmail/label-rename` | A server-side label rename. With mbsync's default `Remove None` the old local folder is believed to stay, so the index keeps a ghost label. Pinned as a `divergence`. |

Filenames follow isync 1.5's shape, `<time>.<pid>_<n>.<host>,U=<uid>:2,<flags>`.
Unseen mail sits in `new/` with an empty info suffix, as observed against
Dovecot (see the mark-read applier in the `Dockerfile`).

### What the replay has already shown

- **The attributed folder is whichever copy the walk reaches first, and for
  Gmail that is often `[Gmail]/All Mail`.** Observed 2026-10-24 on macOS
  (APFS): in `label-lifecycle` and `read-propagation`, inbox mail was
  attributed to All Mail. That is first-seen-wins working as designed. But
  `get_email` and search hits then report "[Gmail]/All Mail" for inbox mail,
  which says nothing. The walk order is the filesystem's, so Linux may
  differ. Folder *filters* are unaffected, because they read membership.

## Recording a trace (not built yet)

This is the plan for replacing beliefs with observations. It needs a
throwaway Gmail account that holds only invented mail; an agent can't create
the account.

1. **Seed it** over IMAP `APPEND` with messages from the dev corpus, so every
   byte is already invented and public-safe.
2. **Drive it** with a script that performs one operation per step over IMAP:
   `COPY` into a label folder to add a label, `STORE \Deleted` + `EXPUNGE`
   there to remove it, `STORE \Seen`, a move to `[Gmail]/Trash`.
3. **After each operation,** run real mbsync with a Gmail `mbsyncrc`. Record
   the Maildir tree (cur/new files only; content-addressed blobs) and, from
   the same IMAP session, each message's mailboxes and `\Seen`. That gives
   `maildir` and `server`.
4. **Write it out** with `source: "recorded"` and
   `recorded: { "at", "mbsync", "notes" }` (the loader requires the block
   exactly when the source is `recorded`). Before committing, check the
   blobs' headers for the account address. `APPEND`ed messages carry no
   `Received` chain, but verify that rather than assume it.
5. **Re-record on a schedule**, like the weekly
   [cloud smoke test](cloud-smoke-tests.md), and diff the *shape* against the
   committed trace. That catches Gmail or isync changing behaviour, and it
   exercises authentication (app password or XOAUTH2), which no fixture can.

Credentials go through 1Password, never into the repository or the
transcript. Every trace's blobs must be invented: **this repository is
public.**

## Adding a hand-written trace

Write `trace.json` and its blobs under `ProviderTraces/<provider>/<name>/`.
The csproj copies them to the output directory, and the theory picks the new
trace up automatically. Use reserved example domains. Say in the description
that the trace is a belief, and what would confirm it.
