# Mailvec documentation

Start with one installation path. The pages below are grouped by the task you are doing; the operation guides are reference material after the first install.

## Install and connect

| Task | Guide |
| --- | --- |
| One-machine Apple Silicon install | [Get started on macOS](getting-started-macos.md) |
| Always-on Linux host | [Get started with Docker](getting-started-docker.md) |
| IMAP credentials and first sync on macOS | [IMAP setup](imap-setup.md) |
| Claude Desktop, Claude Code, and other local MCP clients | [Local client setup](clients/README.md) |
| Remote Claude connector behind Cloudflare Access | [Remote access](remote-access-cloudflare.md) |

## Use and operate

- [Reading and searching attachments](attachments.md) and [Fastmail deep links](fastmail-deep-links.md).
- [macOS operations](install-macos.md): launchd mechanics, backups, migration, and uninstall.
- [Docker deployment](deploy-docker.md): compose layout, image releases, migration, backup, and rollout checks.
- [Logs](logs.md), [Uptime Kuma monitoring](monitoring-uptime-kuma.md), and the [security model](security.md).

## Develop

- [Contributor guide](../CLAUDE.md): architecture, build conventions, and data invariants. Read it before code changes.
- [Architecture notes](../CLAUDE.md#architecture) and [security boundary diagram](security-boundaries.svg).
- [Local development dataset](contributing/local-dev-dataset.md): a repeatable eval corpus and the protected development machine.
- [Synthetic dev corpus](contributing/dev-corpus.md): an invented mailbox to run the services against where there is no real mail, and what each scenario exercises.
- [Cloud development](contributing/cloud-development.md): Claude Code cloud sessions, environment setup, and sqlite-vec initialization.
- [Test database walkthrough](dev-walkthrough.md): use a separate Maildir and SQLite archive.
- Contributor notes: [attachment indexing](contributing/attachment-indexing.md), [OCR](contributing/attachment-ocr.md), [embedding experiments](contributing/embedding-experiments.md), [MCPB](contributing/mcpb.md), [cloud smoke tests](contributing/cloud-smoke-tests.md), and [search performance](contributing/search-performance.md).
- [Eval baselines](../baselines/README.md) and [change history](../CHANGELOG.md).

## Further reading

[Future ideas](future-ideas.md) tracks deferred work; [CHANGELOG](../CHANGELOG.md) records shipped changes.
