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

Verified by mutation:

- **Survivor test.** Disabling reconciliation's survivor test fails
  `label-lifecycle` and `read-propagation`: an archived or flag-renamed
  message is soft-deleted. Re-checked against the recorded `label-lifecycle`
  on 2026-10-25.
- **Rename repair.** Disabling rename repair fails `label-lifecycle` and
  `read-propagation`: rows point at files that are gone. Checked 2026-10-24,
  against the hand-written version.
- **Single-copy traces don't help here.** `all-mail-excluded` catches
  neither mutation, because without All Mail it never has two copies of one
  message.

## The traces

| Trace | Source | What it shows |
|---|---|---|
| `gmail/label-lifecycle` | **recorded** 2026-10-25, isync 1.5.1 | One message through arrive → read in webmail → labelled → archived → unlabelled → trashed → deleted forever. Archive and unlabel are not deletions; trash leaves All Mail. |
| `gmail/all-mail-excluded` | hand-written | `Patterns * ![Gmail]/All Mail` (the config that halves the download). Archiving is then a deletion; a label move is a deletion followed by a resurrection, with an unsettled step between the two folders' syncs. |
| `gmail/read-propagation` | hand-written | `mark_read` on a label-mode account (open item 2 in [read-state.md](../proposals/read-state.md)): the INBOX copy is seen, the All Mail copy isn't until the next pull, and the message reads unread in between. |
| `gmail/label-rename` | hand-written | A server-side label rename. With mbsync's default `Remove None` the old local folder is believed to stay, so the index keeps a ghost label. Pinned as a `divergence`. |

A hand-written trace encodes a belief, built from Gmail's documented IMAP
behaviour. The replay can only show that Mailvec handles that belief
correctly. A recorded trace is what Gmail and isync actually did.

Filenames follow isync 1.5's shape, `<time>.<pid>_<n>.<host>,U=<uid>:2,<flags>`; recorded ones carry the literal hostname `host`.
Unseen mail sits in `new/` with an empty info suffix.

### What the recordings have shown

Observed 2026-10-25, recording `label-lifecycle` (isync 1.5.1, account
defaults):

- **The hand-written belief held at every step.** The recording matched it
  in every folder set, flag and server state. Only filenames differ, plus
  isync's `X-TUID` header.
- **A server-side `\Seen` moves the file out of `new/`.** mbsync renamed both
  copies into `cur/` with `:2,S` in one run.
- **Gmail added no `[Gmail]/Important` copy** to `APPEND`ed messages, and
  added no headers to them.
- **Every step settled on the first mbsync run.** No intermediate state was
  observed.

From the replay itself (observed 2026-10-24 on macOS / APFS, against the
hand-written traces):

- **The attributed folder is whichever copy the walk reaches first, and for
  Gmail that is often `[Gmail]/All Mail`.** Inbox mail was attributed to All
  Mail. That is first-seen-wins working as designed. But `get_email` and
  search hits then report "[Gmail]/All Mail" for inbox mail, which says
  nothing. The walk order is the filesystem's, so Linux may differ. Folder
  *filters* are unaffected, because they read membership.

## Recording a trace

`ops/record-provider-trace.py` records from a Gmail account that holds only
test mail.

**You don't need this to contribute.** The replay tests run from the
committed traces with no account and no credentials. Recording is for adding
or refreshing a trace, and the maintainer's test account is not shared. To
record, make your own throwaway Gmail account: turn on 2-Step Verification,
then create an app password at myaccount.google.com/apppasswords. The seeded
messages are fixed, so two accounts record the same trace apart from file
names.

The script reads exactly two environment variables, `GMAIL_TRACE_USER` and
`GMAIL_TRACE_PASSWORD` (the app password; spaces are ignored). How you set
them is up to you. It never prints the password or writes it to disk. With a
password manager's CLI (1Password shown):

```bash
GMAIL_TRACE_USER=<test address> \
GMAIL_TRACE_PASSWORD="op://<vault>/<item uuid>/<field>" \
op run -- python3 -I ops/record-provider-trace.py label-lifecycle --work <new dir> --out <new dir>
```

Without one, `read -rs` keeps the password off the screen and out of shell
history:

```bash
read -rs GMAIL_TRACE_PASSWORD && export GMAIL_TRACE_PASSWORD
GMAIL_TRACE_USER=<test address> python3 -I ops/record-provider-trace.py label-lifecycle --work <new dir> --out <new dir>
unset GMAIL_TRACE_PASSWORD
```

Keep the account's address out of this repository: it never needs to be in
a trace, a doc or a commit.

Network failures are retried and nothing else is. Both the recorder's IMAP
session and mbsync time out after 60 s, raised from isync's 20 s default. A
sync that fails on a socket error or timeout is retried up to 3 times, 30 s
apart. A refused login is not retried: retrying only repeats the refusal.

What a run does:

1. **Refuses** a non-test account: one with more than 5 messages it did not
   seed.
2. **Purges** every message on the seed domain (`@trace.example`) and the
   trace's own labels, so a re-record starts clean.
3. **For each step,** performs one IMAP operation. Then it runs real mbsync
   (`-c` with a generated config: `Patterns *`, `Subfolders Verbatim`, the
   shipped container settings) into `--work` until two consecutive Maildir
   snapshots agree and the server hasn't moved. A first run that differs from
   the settled state becomes a `settled: false` step of its own.
4. **Records** the seeded messages' cur/new files (content-addressed blobs)
   and, per mailbox, each message's membership and `\Seen`. Mail it didn't
   seed, such as Google's welcome email, is never recorded.
5. **Refuses to write** a blob that isn't the seeded bytes, apart from
   isync's own `X-TUID` tracking header: anything else could carry the
   account's identity. It also refuses if `[Gmail]/Sent Mail` changed during
   the run. The recorder never sends, and an app password can, so that check
   is the canary for misuse.
6. **Replaces the hostname** in every recorded file name with `host`. isync
   names files `<time>.<pid>_<n>.<hostname>,U=<uid>:2,<flags>`, and a
   recording machine's name is often its owner's ("Janes-MacBook-Pro"). A
   name it can't parse is refused, never kept.

It writes only into `--out`, never into the repository. Check the output
(for instance `grep -ri <address> <out>` should find nothing), then copy
`trace.json` and `blobs/` over the trace's directory and run the replay.
Both directories must be new or empty, and the recorder refuses anywhere a
real pipeline would ingest them, by the same rules as the dev corpus.
`ops/tests/test_record_provider_trace.py` covers everything but the live
session.

A trace is recordable once its steps are written in the script's `TRACES`
table. `label-lifecycle` is the only one so far.

### Re-recording in CI

`.github/workflows/gmail-traces.yml` re-records every trace in its `TRACES`
list from the live account, weekly (Monday 15:00 UTC) and on demand. For
each trace it checks two things:

1. **Shape.** `--compare` against the committed trace: same steps, copies,
   flags and server state, ignoring file and blob names. A difference means
   Gmail or isync changed behaviour. The job fails, and the recording is
   uploaded as the `gmail-traces` artifact for review.
2. **Replay.** The fresh recording replaces the committed one in the job's
   checkout, and `TraceReplayTests` runs over it. This is the check that
   matters for users: it says whether Mailvec still handles what Gmail does
   *now*, and it runs even when the shape moved.

The recorder runs inside the same pinned Alpine image as the mbsync sidecar
(`Dockerfile`, `AS mbsync`), so it records with the isync version the
deployment ships. Keep the two digests in step.

The guardrails, because the credential can read and send mail for the
account:

- **Triggers:** `schedule` and `workflow_dispatch` only, never anything a
  pull request can trigger.
- **Credential:** in the `gmail-recorder` GitHub Environment, whose
  deployment branches are restricted to `main`. A dispatch from another
  branch can't read it.
- **Output:** the job never commits. A person reviews the artifact and
  commits.
- **Only recordings are uploaded.** Scratch files live in their own
  directory, `trace-work`, never under the uploaded `trace-out`, and are
  deleted on every exit path. They include the generated mbsyncrc, which
  names the account, and the synced Maildir, which holds the account's own
  mail. On 2026-10-26 a failed run had them inside the uploaded directory,
  and only a permission error kept them out of the artifact.
  `ops/tests/test_record_provider_trace.py` (`Workflow`) pins the split.
- **One run at a time.** The concurrency group is `gmail-recorder`, because
  each run starts by purging what the last one seeded. A local recording
  against the same account collides the same way, so don't run one while the
  workflow does.

**One-time setup** (repository settings, done by the maintainer):

1. **Settings → Environments → New environment** `gmail-recorder`.
   Deployment branches: *Selected branches*, `main` only.
2. Add two environment secrets:
   - `GMAIL_TRACE_USER`: the test address.
   - `GMAIL_TRACE_PASSWORD`: its app password.
3. Run the workflow once by hand (**Actions → Gmail traces → Run workflow**)
   and check that it's green.

When rotating the app password, update the environment secret as well as
the password manager.

Every trace's blobs must be invented: **this repository is public.**

## Adding a hand-written trace

Write `trace.json` and its blobs under `ProviderTraces/<provider>/<name>/`.
The csproj copies them to the output directory, and the theory picks the new
trace up automatically. Use reserved example domains. Say in the description
that the trace is a belief, and what would confirm it.
