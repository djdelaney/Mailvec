# IMAP setup (mbsync)

`mbsync` pulls IMAP into a local Maildir the indexer watches. Read-only — changes flow IMAP server → local, never the other way.

```sh
brew install isync                     # the binary is `mbsync`; Homebrew names the formula after the suite
mkdir -p ~/Mail/Fastmail
```

The shipped `ops/mbsyncrc.example` uses Fastmail because that's the author's setup. Any IMAP server works — swap the `Host` / `User` / `PassCmd` lines and the rest of the pipeline (Maildir → SQLite → embeddings → MCP) is unchanged. Gmail and iCloud both require an app-specific password issued from their respective account-security UIs. See `man mbsync` for fancier auth (XOAUTH2, etc.).

## Stash your IMAP password in the macOS Keychain

So it's never on disk. For Fastmail, generate an app-specific password at <https://app.fastmail.com/settings/security/devicekeys> (the password is shown once and can't be retrieved later — only revoked). Add it under service name `mbsync`:

```sh
security add-generic-password -a you@fastmail.com -s mbsync -w
```

The `-a` (account) and `-s` (service) values must match the `PassCmd` line in `~/.mbsyncrc`. The `-w` flag prompts for the password without echoing.

## Configure mbsync

Copy the example and replace `you@fastmail.com` on both the `User` and `PassCmd` lines:

```sh
cp ops/mbsyncrc.example ~/.mbsyncrc
chmod 600 ~/.mbsyncrc
# then edit User + PassCmd in ~/.mbsyncrc
```

## First sync

Run it once manually before scheduling — a multi-year archive can take hours, and you want to see any auth or TLS errors live:

```sh
mbsync -aV       # -a = all channels, -V = verbose
```

Run inside `tmux` / `screen` for a big archive so a closed terminal doesn't kill it. Subsequent syncs are incremental and finish in seconds. The indexer (and embedder) can start against `~/Mail/Fastmail/` while mbsync is still working — they'll pick up new messages as they land.

## Folder filtering (Fastmail labels gotcha)

Fastmail exposes labels as IMAP folders, so a message with two labels lives in two folders. With the example's `Patterns *`, mbsync downloads both copies and the indexer logs spurious `Content changed; cleared embeddings` warnings on the second one — Fastmail's IMAP serializer regenerates the multipart boundary string per folder, so the body bytes hash differently across copies despite being the same email. Final search results are correct, just at the cost of re-embedding every multi-labelled message. To avoid it, narrow `Patterns` (e.g. `Patterns INBOX`); the [example](../ops/mbsyncrc.example) shows the common forms.

## Scheduling

Once the manual sync is working, [`ops/install.sh`](../ops/install.sh) (run by `ops/install-all.sh` in the [README Quickstart](../README.md#quickstart)) installs a launchd plist that runs `mbsync -c ~/.mbsyncrc -a` once at load and every 10 minutes thereafter (the interval lives in the plist's `StartInterval`; see the comment there for why 600s).
