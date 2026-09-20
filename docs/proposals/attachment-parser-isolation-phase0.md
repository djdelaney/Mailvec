# Phase 0 measurements — what the parsers actually do with hostile PDFs

**Date:** 2026-09-13. Companion to [attachment-parser-isolation.md](attachment-parser-isolation.md).
**Harness:** [`tools/Mailvec.ParserBench`](../../tools/Mailvec.ParserBench) (not in `Mailvec.slnx`).
Every (fixture, op) pair runs in a child process with a 180 s timeout and RSS
polling, so a crash, hang or OOM kill is a row in the table rather than a dead
harness. The cgroup's `oom_kill` counter is read around each child.

The ops are the real code paths: `PdfRenderer.PageCount` / `RenderPageJpeg`
(PDFium, as used by the embedder's OCR pass and `get_attachment_page_image`)
and `AttachmentTextExtractor.Extract` fed through a `MimePart` (PdfPig, the
indexer's path, with the 25 MB size gate raised so the parser is what gets
measured).

## Headline findings

1. **PDFium is where the pipeline can be wedged, and it takes under 1 KB.**
   A 958-byte PDF of 20,000 full-page shading fills is `no_text` to PdfPig in
   186 ms — so the indexer stores it as an OCR candidate — and PDFium was still
   rendering page 1 at the 180 s cut-off. 2,000 fills took 69 s, i.e. ~34 ms
   per operator, linear, and the operator count is free. The render is
   synchronous, takes no cancellation token, and PDFtoImage serialises PDFium,
   so in the embedder this stalls the OCR pass (and therefore the embed pass,
   which runs after it) for as long as the attacker likes; in MCP it stalls
   every subsequent render behind it. Neither process can be stopped
   gracefully while it runs. After a forced restart the OCR cursor resumes
   from 0 and the candidate query is `ORDER BY a.id`, so the same document is
   selected first.
2. **PDFium is also OOM-killed by the cgroup on a 100 KB PDF** (10 M text
   operators: exit 137, `oom_kill +1`), and by a 427 KB path PDF. This is
   native memory, invisible to the .NET heap limit. These particular files are
   `failed` to PdfPig first, so they don't reach the OCR pass — but they do
   reach `get_attachment_page_image` on request, and killing the MCP container
   drops every in-flight tool call for a restart cycle.
3. **PdfPig's memory bombs are caught — by the runtime, not by Mailvec.**
   Inside a `--memory=2g` cgroup .NET caps its managed heap at 75 % of the
   limit and throws a managed `OutOfMemoryException`; `AttachmentTextExtractor`
   catches it and stamps `failed`. Peak RSS sat at ~1.5 GB for 5 M, 10 M and
   20 M operators alike, the process survived three consecutive OOMs in one
   run (`pdfpig-extract-x3`), and no super-linear CPU case was found (5 M text
   ops 5.3 s, 3 M paths 4.6 s, 50 k pages 1.5 s). **So the indexer wedge
   hypothesised in the proposal does not occur for memory in the container.**
   Two caveats: the guard is a runtime default keyed to the cgroup limit, so
   the macOS launchd install has no such limit (natively, 5 M ops peaked at
   2.48 GB and would keep growing); and nothing bounds PdfPig's *time* — none
   of these five shapes found a CPU bomb, but nothing was searched exhaustively.
4. **The image-bomb hypothesis was wrong.** PDFium downsamples on decode: a
   900-megapixel 1-bit page rendered in 750 ms at a 73 MB peak, a 400 MP
   8-bit page in 240 ms at 61 MB. Strike that residual from the proposal.
5. **Malformed input is handled cleanly by both parsers.** Truncated real
   files and a header followed by noise produce `PdfInvalidFormatException`
   from PDFium and `failed` from PdfPig, in milliseconds. A form XObject that
   draws itself is handled by both. No crash (SIGSEGV/SIGABRT) was produced by
   any fixture.

## What this changes in the proposal

- The rasteriser move is now backed by measurement rather than by CVE
  history alone: both the OOM kill and the unbounded render are in PDFium, and
  the `parse` service's "timeout → exit" rule is exactly the control a render
  that ignores cancellation needs.
- The indexer's inclusion loses its availability argument for memory. It
  keeps the exploit-containment argument (MimeKit's `unsafe` parser, native
  zlib), the uniform-enforcement argument (strip every parser from every
  privileged directory), and the fact that PdfPig has no time bound of its
  own. Recommendation in the proposal: keep the end state, make the indexer's
  parse move the last phase, and let it be deferred without invalidating the
  rest.
- `Parser:RequestTimeout` has a measured floor: legitimate renders in this set
  took 25–750 ms; the first hostile one took 69 s. 60 s is a sound default.

## Environment

| | |
| --- | --- |
| Native | macOS 26.6.2, Apple Silicon (24 GB), .NET 10.0.8 — no memory limit |
| Container | `mcr.microsoft.com/dotnet/runtime:10.0` (Ubuntu 24.04, .NET 10.0.12), `docker run --memory=2g --memory-swap=2g --pids-limit=512`, linux/arm64 under Docker Desktop, cgroup v2 |
| Target (for the record) | Docker VM `dk`: Debian 13, kernel 6.12.107, cgroup v2, Docker security options apparmor + builtin seccomp + cgroupns. Landlock network rules (kernel ≥ 6.7) are available there if ever wanted. linux/amd64, so absolute timings will differ from the arm64 numbers below; the memory numbers and failure classes will not. |

## Fixtures

Generated by `dotnet run -- gen <dir>` plus `gen-ops`, `gen-sh`, `gen-paths`.
Every hostile fixture is far under the 25 MB attachment ceiling.

| fixture | size | what it is |
| --- | ---: | --- |
| a-ctrl-{text,table,scanned} | 678 B – 1.8 MB | the repo's own fixtures, untouched — controls |
| b-trunc-* | 339 B – 1.1 MB | those files cut at 50–60 % |
| b-garbage-after-header | 64 KB | `%PDF-1.4` + random bytes |
| c-img-4mp-gray8 / 100mp-rgb8 / 400mp-gray8 / 900mp-1bit | 4 KB – 380 KB | one flat Flate-compressed raster; decoded size 4 MB – 400 MB (900 MP at 1 bpp) |
| d-ops-{1,5,10,20}m | 11 KB – 200 KB | one page, N × `(a) Tj` text operators |
| d-pages-50k | 7.6 MB | 50,000 tiny pages |
| d-form-self-recursion | 646 B | a form XObject whose content draws itself |
| e-paths-{3,10}m | 128 KB / 427 KB | N × stroked diagonal lines |
| e-sh-{2,20}k | 746 B / 958 B | N × full-page axial-gradient `sh` fills |

## Results — Linux container, 2 GB cgroup (the numbers that matter)

Peak RSS is the child's `VmHWM`, polled every 100 ms.

| fixture | size | op | outcome | ms | peak RSS MB | detail |
| --- | ---: | --- | --- | ---: | ---: | --- |
| a-ctrl-scanned.pdf | 1.8 MB | pdfium-count | ok | 11 | 0 | pages=1 |
| a-ctrl-scanned.pdf | 1.8 MB | pdfium-render | ok | 98 | 100 | jpegBytes=253616 |
| a-ctrl-scanned.pdf | 1.8 MB | pdfpig-extract | ok | 176 | 60 | status=no_text |
| a-ctrl-table.pdf | 96 KB | pdfium-render | ok | 35 | 0 | jpegBytes=89119 |
| a-ctrl-table.pdf | 96 KB | pdfpig-extract | ok | 209 | 63 | status=done chars=653 |
| a-ctrl-text.pdf | 678 B | pdfium-render | ok | 25 | 0 | jpegBytes=39656 |
| a-ctrl-text.pdf | 678 B | pdfpig-extract | ok | 169 | 54 | status=done chars=90 |
| b-garbage-after-header.pdf | 64 KB | pdfium-count | managed exception | 5 | 0 | PdfInvalidFormatException |
| b-garbage-after-header.pdf | 64 KB | pdfpig-extract | ok | 35 | 0 | status=failed |
| b-trunc-scanned-60pct.pdf | 1.1 MB | pdfium-count | managed exception | 16 | 0 | PdfInvalidFormatException |
| b-trunc-scanned-60pct.pdf | 1.1 MB | pdfpig-extract | ok | 53 | 0 | status=failed |
| b-trunc-table-50pct.pdf | 48 KB | pdfium-count | managed exception | 5 | 0 | PdfInvalidFormatException |
| b-trunc-table-50pct.pdf | 48 KB | pdfpig-extract | ok | 37 | 0 | status=failed |
| b-trunc-text-50pct.pdf | 339 B | pdfium-count | managed exception | 5 | 0 | PdfInvalidFormatException |
| b-trunc-text-50pct.pdf | 339 B | pdfpig-extract | ok | 27 | 0 | status=failed |
| c-img-4mp-gray8.pdf | 4 KB | pdfium-render | ok | 32 | 0 | |
| c-img-100mp-rgb8.pdf | 285 KB | pdfium-render | ok | 190 | 57 | |
| c-img-400mp-gray8.pdf | 380 KB | pdfium-render | ok | 240 | 61 | |
| c-img-900mp-1bit.pdf | 107 KB | pdfium-render | ok | 747 | 73 | |
| c-img-*.pdf | | pdfpig-extract | ok | 152–160 | 54–55 | status=no_text |
| d-form-self-recursion.pdf | 646 B | pdfium-render | ok | 23 | 0 | |
| d-form-self-recursion.pdf | 646 B | pdfpig-extract | ok | 156 | 54 | status=no_text |
| d-pages-50k.pdf | 7.6 MB | pdfium-count | ok | 44 | 0 | pages=50000 |
| d-pages-50k.pdf | 7.6 MB | pdfium-render | ok | 1738 | 151 | |
| d-pages-50k.pdf | 7.6 MB | pdfpig-extract | ok | 1514 | 170 | status=done chars=299998 |
| d-ops-1m.pdf | 11 KB | pdfium-render | ok | 352 | 357 | |
| d-ops-1m.pdf | 11 KB | pdfpig-extract | ok | 1443 | 507 | status=done chars=1000000 |
| d-ops-5m.pdf | 50 KB | pdfium-render | ok | 1938 | 1636 | |
| d-ops-5m.pdf | 50 KB | pdfpig-extract | ok | 5294 | 1560 | status=failed (managed OOM) |
| d-ops-10m.pdf | 100 KB | pdfium-render | **OOM-KILLED** (exit 137, oom_kill +1) | 2598 | 2057 | |
| d-ops-10m.pdf | 100 KB | pdfpig-extract | ok | 5955 | 1531 | status=failed (managed OOM) |
| d-ops-10m.pdf | 100 KB | pdfpig-extract-x3 | ok | 17771 | 1540 | failed ×3, process survived |
| d-ops-20m.pdf | 200 KB | pdfium-render | **OOM-KILLED** (exit 137, oom_kill +1) | 3187 | 2052 | |
| d-ops-20m.pdf | 200 KB | pdfpig-extract | ok | 5724 | 1425 | status=failed (managed OOM) |
| e-paths-3m.pdf | 128 KB | pdfium-render | **TIMEOUT** (killed at 180 s) | 180140 | 845 | |
| e-paths-3m.pdf | 128 KB | pdfpig-extract | ok | 4589 | 1484 | status=failed (managed OOM) |
| e-paths-10m.pdf | 427 KB | pdfium-render | **OOM-KILLED** (exit 137, oom_kill +1) | 4602 | 2050 | |
| e-paths-10m.pdf | 427 KB | pdfpig-extract | ok | 6501 | 1414 | status=failed (managed OOM) |
| **e-sh-2k.pdf** | **746 B** | pdfium-render | ok | **68635** | 52 | jpegBytes=22525 |
| **e-sh-2k.pdf** | **746 B** | pdfpig-extract | ok | 158 | 54 | **status=no_text** — an OCR candidate |
| **e-sh-20k.pdf** | **958 B** | pdfium-render | **TIMEOUT** (killed at 180 s) | 180071 | 49 | |
| **e-sh-20k.pdf** | **958 B** | pdfpig-extract | ok | 186 | 65 | **status=no_text** — an OCR candidate |

`pdfium-count` was `ok` in ≤ 9 ms for every well-formed fixture and is
omitted where uninteresting.

## Results — native macOS, no memory limit (for contrast)

Same fixtures, same ops. The only rows that differ in kind:

| fixture | op | outcome | ms | peak RSS MB |
| --- | --- | --- | ---: | ---: |
| d-ops-5m.pdf | pdfpig-extract | ok, status=done chars=2000000 (hit `MaxExtractedTextChars`) | 5300 | **2480** |
| d-ops-5m.pdf | pdfium-render | ok | 1720 | 1338 |
| d-ops-1m.pdf | pdfpig-extract | ok, status=done chars=1000000 | 1432 | 609 |

With no heap limit PdfPig simply finishes, having allocated 2.5 GB for a 50 KB
file, and would scale linearly from there. That is the launchd install's
situation; it is not the deployment, but it is why the proposal keeps
`Parser:Mode=inprocess` on macOS labelled as "today's behaviour" rather than
"safe".

## Reproduce

```sh
cd tools/Mailvec.ParserBench
dotnet build -c Release
B="dotnet bin/Release/net10.0/Mailvec.ParserBench.dll"
$B gen /tmp/fx && $B gen-ops /tmp/fx 10000000 && $B gen-sh /tmp/fx 20000 && $B gen-paths /tmp/fx 3000000
$B run /tmp/fx 180                              # native
dotnet publish -c Release -r linux-arm64 --self-contained false -o /tmp/bench   # or linux-x64 on the VM
docker run --rm --memory=2g --memory-swap=2g --pids-limit=512 \
  -v /tmp/bench:/bench:ro -v /tmp/fx:/fx mcr.microsoft.com/dotnet/runtime:10.0 \
  dotnet /bench/Mailvec.ParserBench.dll run /fx 180
```
