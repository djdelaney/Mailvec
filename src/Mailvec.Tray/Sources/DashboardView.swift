// DashboardView.swift
import AppKit
import SwiftUI

struct DashboardView: View {
    @EnvironmentObject var model: TrayModel

    var body: some View {
        VStack(spacing: 0) {
            HeaderBand()
            DashboardSearchField(placeholder:
                "Search \(model.health?.embedded ?? 0) indexed emails…")
                .padding(.horizontal, 12)
                .padding(.top, 10)
                .padding(.bottom, 8)
                .onTapGesture { model.pane = .search }

            if let h = model.health {
                BodyForState(health: h)
            } else {
                LoadingRow()
            }

            FooterActions()
        }
        .background(Brand.popoverBg)
        // Size the popover window to the *actual* content height. The old
        // `.frame(maxHeight: 720)` advertised a flexible max to
        // MenuBarExtra(.window), which sized the host window to 720 and then
        // vertically centered the shorter idle-state body (ThroughputCard, no
        // recent activity) — leaving inflated cream bars above the header and
        // below the footer once embedding finished and the body shrank.
        // fixedSize pins the stack to its ideal height so the window tracks it
        // both ways. Content is bounded (RecentActivity is prefix(4), cards are
        // fixed-height), so no scroll/cap is needed.
        .fixedSize(horizontal: false, vertical: true)
    }
}

// MARK: Header band

private struct HeaderBand: View {
    @EnvironmentObject var model: TrayModel

    /// Coloured inline icon used next to the "Mailvec" wordmark.
    private var brandColorIcon: Image {
        NSImage(named: "mailvec.mv-color") != nil
            ? Image("mailvec.mv-color")
            : Image(systemName: "tray.full.fill")
    }

    var body: some View {
        let h = model.health
        // No decorative watermark behind this band — its faint strokes sat
        // behind the small, tinted "All clear" status pill and made it hard
        // to read on high-contrast displays, so it was removed.
        VStack(alignment: .leading, spacing: 12) {
            HStack(spacing: 9) {
                brandColorIcon
                    .resizable().scaledToFit().frame(width: 18)
                Text("Mailvec").font(.system(size: 14, weight: .bold))
                Text("v\(Bundle.main.infoDictionary?["CFBundleShortVersionString"] as? String ?? "?")")
                    .font(.system(size: 10.5, design: .monospaced))
                    .foregroundStyle(.white.opacity(0.5))
                Spacer()
                StatusPill(severity: h?.severity ?? .ok)
            }
            HStack(spacing: 14) {
                CoverageRing(
                    progress: h?.embedCoverage ?? 0,
                    severity: h?.severity ?? .ok
                )
                .frame(width: 72, height: 72)

                VStack(alignment: .leading, spacing: 6) {
                    HStack(alignment: .firstTextBaseline, spacing: 6) {
                        Text(h?.messages.formatted() ?? "—")
                            .font(.system(size: 28, weight: .bold))
                            .monospacedDigit()
                            .kerning(-0.6)
                        Text("messages")
                            .font(.system(size: 11.5))
                            .foregroundStyle(.white.opacity(0.6))
                    }
                    HStack(spacing: 12) {
                        MiniStat(label: "sync",    value: h?.lastSyncAt.map(relative) ?? "—")
                        MiniStat(label: "indexed", value: h?.lastIndexedAt.map(relative) ?? "—")
                        MiniStat(label: "chunks",  value: h?.chunks.formatted() ?? "—")
                        MiniStat(label: "db",      value: h.map { fmtBytes($0.dbSizeBytes) } ?? "—")
                    }
                }
                Spacer()
            }
        }
        .padding(.horizontal, 16)
        .padding(.vertical, 12)
        .foregroundStyle(Brand.bandText)
        .background(
            LinearGradient(
                colors: [Brand.bandTop, Brand.bandBottom],
                startPoint: .top, endPoint: .bottom)
        )
        // This band is a permanently-dark gradient while the app forces the
        // rest of the popover to `.light`. The status tints on it are now fixed
        // bright colors (Brand.statusOk/Warn/Error) precisely because system
        // `.green`/`.orange`/`.red` dimmed here — but we still pin the subtree
        // to `.dark` so any remaining adaptive chrome (dividers, etc.) resolves
        // for a dark backdrop.
        .environment(\.colorScheme, .dark)
    }
}

private struct MiniStat: View {
    let label: String; let value: String
    var body: some View {
        VStack(alignment: .leading, spacing: 1) {
            Text(label).font(.system(size: 9.5, weight: .semibold))
                .textCase(.uppercase).tracking(0.5)
                .foregroundStyle(.white.opacity(0.5))
            Text(value).font(.system(size: 12, weight: .semibold))
                .monospacedDigit()
        }
    }
}

// MARK: Body

private struct BodyForState: View {
    let health: TrayHealth
    var body: some View {
        VStack(spacing: 10) {
            switch health.severity {
            case .error:   ErrorBanner(health: health)
            case .syncing: ProgressCard(health: health)
            default:       ThroughputCard(health: health)
            }
            if let ocr = health.ocr, ocr.shouldSurface {
                OcrCard(ocr: ocr)
            }
            ServicesGrid(services: health.services, ollama: health.ollama)
            RecentActivity(events: Array(health.recentEvents.prefix(4)))
        }
        .padding(.horizontal, 12)
        .padding(.bottom, 10)
    }
}

private struct ProgressCard: View {
    let health: TrayHealth
    var body: some View {
        VStack(alignment: .leading, spacing: 8) {
            HStack {
                Text("Embedding · \(health.embedded.formatted())/\(health.embedTotal.formatted())")
                    .font(.system(size: 11.5, weight: .semibold))
                Spacer()
                if let p = health.progress {
                    Text("\(p.ratePerMinute)/min · \(p.etaMinutes)m left")
                        .font(.system(size: 11)).monospacedDigit()
                        .foregroundStyle(.secondary)
                }
            }
            ProgressView(value: health.embedCoverage)
                .progressViewStyle(.linear)
                .tint(Brand.accent)
            Sparkline(data: health.sparkline, accent: Brand.accent)
                .frame(height: 30)
        }
        .padding(10)
        .background(Brand.cardBg, in: RoundedRectangle(cornerRadius: 10))
        .overlay(RoundedRectangle(cornerRadius: 10).stroke(Brand.hairline))
    }
}

private struct ThroughputCard: View {
    let health: TrayHealth
    var body: some View {
        VStack(alignment: .leading, spacing: 4) {
            HStack {
                // Window matches TrayEventRecorder's 30 one-minute buckets.
                // Bumping BucketCount would mean updating this label.
                Text("Throughput · last \(health.sparkline.count)m")
                    .font(.system(size: 11.5, weight: .semibold))
                Spacer()
                Text(activitySummary)
                    .font(.system(size: 11)).foregroundStyle(.secondary)
            }
            Sparkline(data: health.sparkline, accent: Brand.accent)
                .frame(height: 30)
        }
        .padding(.horizontal, 12).padding(.vertical, 8)
        .background(Brand.cardBg, in: RoundedRectangle(cornerRadius: 10))
        .overlay(RoundedRectangle(cornerRadius: 10).stroke(Brand.hairline))
    }

    /// Derives "idle · last burst Nm ago" / "active · Nn/min" from the
    /// embeddings-per-minute sparkline. Index 0 is oldest, last index is
    /// the bucket that just ended.
    private var activitySummary: String {
        let buckets = health.sparkline
        guard !buckets.isEmpty else { return "idle" }
        let current = buckets.last ?? 0
        if current > 0 {
            return "active · \(current)/min"
        }
        // Find the most recent non-zero bucket walking backwards.
        for i in (0..<buckets.count - 1).reversed() where buckets[i] > 0 {
            let minutesAgo = (buckets.count - 1) - i
            return "idle · last burst \(minutesAgo)m ago"
        }
        return "idle · no recent activity"
    }
}

/// Surfaces the scanned-PDF OCR stage. Two shapes, by severity:
///   • model missing → amber warn card with a one-click "Copy pull command"
///     (running it needs a terminal, but the embedder picks the model up on its
///     next pass automatically once it's pulled — no service restart needed).
///   • backlog only → a neutral info line showing queued / recovered counts.
private struct OcrCard: View {
    let ocr: OCRStatus
    @State private var copied = false

    var body: some View {
        if ocr.modelMissing {
            warnCard
        } else {
            infoCard
        }
    }

    private var warnCard: some View {
        VStack(alignment: .leading, spacing: 10) {
            HStack(alignment: .top, spacing: 8) {
                Image(systemName: "doc.viewfinder.fill")
                    .foregroundStyle(.white, Brand.statusWarn).font(.system(size: 16))
                VStack(alignment: .leading, spacing: 1) {
                    Text("Vision model not installed")
                        .font(.system(size: 13, weight: .semibold))
                    Text("\(ocr.pending) scanned PDF\(ocr.pending == 1 ? "" : "s") won't be searchable until you pull \(ocr.visionModel).")
                        .font(.system(size: 11.5)).foregroundStyle(.secondary)
                }
                Spacer(minLength: 0)
            }
            HStack(spacing: 6) {
                Button(copied ? "Copied!" : "Copy pull command") { copyPullCommand() }
                    .buttonStyle(.borderedProminent).tint(Brand.accent)
                Text("ollama pull \(ocr.visionModel)")
                    .font(.system(size: 10.5, design: .monospaced))
                    .foregroundStyle(.secondary)
                    .textSelection(.enabled)
                Spacer(minLength: 0)
            }
            .controlSize(.small)
        }
        .padding(10)
        .frame(maxWidth: .infinity, alignment: .leading)
        .background(Brand.statusWarn.opacity(0.10), in: RoundedRectangle(cornerRadius: 10))
        .overlay(RoundedRectangle(cornerRadius: 10).stroke(Brand.statusWarn.opacity(0.35)))
    }

    private var infoCard: some View {
        HStack(spacing: 8) {
            Image(systemName: "doc.viewfinder")
                .foregroundStyle(Brand.accent).font(.system(size: 13))
            Text("OCR · \(ocr.pending) scanned PDF\(ocr.pending == 1 ? "" : "s") queued")
                .font(.system(size: 11.5, weight: .semibold))
            Spacer()
            if ocr.recovered > 0 {
                Text("\(ocr.recovered.formatted()) recovered")
                    .font(.system(size: 11)).monospacedDigit()
                    .foregroundStyle(.secondary)
            }
        }
        .padding(.horizontal, 12).padding(.vertical, 8)
        .background(Brand.cardBg, in: RoundedRectangle(cornerRadius: 10))
        .overlay(RoundedRectangle(cornerRadius: 10).stroke(Brand.hairline))
    }

    private func copyPullCommand() {
        NSPasteboard.general.clearContents()
        NSPasteboard.general.setString("ollama pull \(ocr.visionModel)", forType: .string)
        copied = true
        Task {
            try? await Task.sleep(nanoseconds: 2_000_000_000)
            copied = false
        }
    }
}

private struct ErrorBanner: View {
    @EnvironmentObject var model: TrayModel
    let health: TrayHealth

    // Tracks the in-flight retry so the button can show progress + outcome.
    // Without this the kickstart fired silently and the returned bool was
    // discarded, so a working retry was indistinguishable from a no-op.
    @State private var retryPhase: RetryPhase = .idle
    enum RetryPhase: Equatable { case idle, running, succeeded, failed }

    var body: some View {
        let problem = ErrorBanner.diagnose(health)
        VStack(alignment: .leading, spacing: 10) {
            HStack(alignment: .top, spacing: 8) {
                Image(systemName: "exclamationmark.circle.fill")
                    .foregroundStyle(.white, .red).font(.system(size: 16))
                VStack(alignment: .leading, spacing: 1) {
                    Text(problem.title)
                        .font(.system(size: 13, weight: .semibold))
                    Text(problem.body)
                        .font(.system(size: 11.5)).foregroundStyle(.secondary)
                }
                Spacer(minLength: 0)
            }
            HStack(spacing: 6) {
                if let primary = problem.primaryAction {
                    Button(primary.label) { primary.run() }
                        .buttonStyle(.borderedProminent).tint(Brand.accent)
                }
                Button("View logs") { openLogs(for: problem.kind) }
                if let retry = problem.retryService {
                    switch retryPhase {
                    case .idle:
                        Button("Retry now") { runRetry(service: retry) }
                    case .running:
                        HStack(spacing: 5) {
                            ProgressView().controlSize(.small)
                            Text("Restarting…")
                                .font(.system(size: 11)).foregroundStyle(.secondary)
                        }
                    case .succeeded:
                        Label("Restarted", systemImage: "checkmark.circle.fill")
                            .font(.system(size: 11)).foregroundStyle(.green)
                    case .failed:
                        HStack(spacing: 6) {
                            Label("Restart failed", systemImage: "xmark.circle.fill")
                                .font(.system(size: 11)).foregroundStyle(.red)
                            Button("Try again") { runRetry(service: retry) }
                        }
                    }
                }
                Spacer(minLength: 0)
            }
            .controlSize(.small)
        }
        .padding(10)
        // The trailing Spacers inside each HStack push their content to the
        // leading edge, but without `frame(maxWidth: .infinity)` SwiftUI's
        // VStack still hugs the content. Forcing infinity width here makes
        // the banner span the full card area like the other dashboard cards.
        .frame(maxWidth: .infinity, alignment: .leading)
        .background(Color.red.opacity(0.07), in: RoundedRectangle(cornerRadius: 10))
        .overlay(RoundedRectangle(cornerRadius: 10).stroke(Color.red.opacity(0.32)))
    }

    /// Classify the actual failure so the banner text matches reality.
    /// Priority order: model mismatch (most serious — silent corruption
    /// territory) → Ollama unreachable → first failing service.
    static func diagnose(_ h: TrayHealth) -> Problem {
        if !h.ollama.ok {
            return Problem(
                kind: .ollama,
                title: "Ollama is unreachable",
                body: "Embeddings are paused until Ollama responds at \(h.ollama.detail).",
                primaryAction: Action(label: "Start Ollama", run: startOllama),
                retryService: "embedder")
        }
        if let failed = h.services.first(where: { $0.severity == .error }) {
            return Problem(
                kind: .service(failed.id),
                title: "\(failed.id) is in trouble",
                body: failed.detail,
                primaryAction: nil,
                retryService: failed.id)
        }
        // Fall-through — should be rare since severity classification
        // upstream only flags .error when one of the above is true.
        return Problem(kind: .unknown,
                       title: "Attention needed",
                       body: "Mailvec is reporting a degraded state.",
                       primaryAction: nil,
                       retryService: nil)
    }

    /// Fire the kickstart, surface its outcome, then auto-revert to the
    /// button after a few seconds. The `control` call returns false (or
    /// throws) when launchctl exits non-zero, so a failed retry now shows
    /// red instead of looking like nothing happened.
    private func runRetry(service: String) {
        retryPhase = .running
        Task {
            let ok = (try? await MailvecClient.shared.control(
                service: service, action: "kickstart")) ?? false
            await model.refresh()
            retryPhase = ok ? .succeeded : .failed
            // Linger on the outcome, then reset so the button returns —
            // unless another retry is already in flight.
            try? await Task.sleep(nanoseconds: 4_000_000_000)
            if retryPhase == .succeeded || retryPhase == .failed {
                retryPhase = .idle
            }
        }
    }

    private func openLogs(for kind: Problem.Kind) {
        let logDir = ("~/Library/Logs/Mailvec/" as NSString).expandingTildeInPath
        // Pick the most relevant log if we know the service; otherwise just
        // open the Mailvec logs folder.
        let stamp: String = {
            let f = DateFormatter(); f.dateFormat = "yyyyMMdd"; return f.string(from: Date())
        }()
        let candidate: String? = {
            switch kind {
            case .ollama:                return "\(logDir)mailvec-embedder-\(stamp).log"
            case .service("embedder"):   return "\(logDir)mailvec-embedder-\(stamp).log"
            case .service("indexer"):    return "\(logDir)mailvec-indexer-\(stamp).log"
            case .service("mcp"):        return "\(logDir)mailvec-mcp-\(stamp).log"
            case .service("mbsync"):     return "\(logDir)mailvec-mbsync.err.log"
            default:                     return nil
            }
        }()
        if let path = candidate, FileManager.default.fileExists(atPath: path) {
            NSWorkspace.shared.open(URL(fileURLWithPath: path))
        } else {
            NSWorkspace.shared.open(URL(fileURLWithPath: logDir))
        }
    }

    /// Best-effort: try the Ollama.app first (installed by `brew install --cask ollama`),
    /// fall back to spawning `ollama serve` if the CLI is on PATH.
    private static func startOllama() {
        if NSWorkspace.shared.urlForApplication(withBundleIdentifier: "com.electron.ollama") != nil
           || FileManager.default.fileExists(atPath: "/Applications/Ollama.app") {
            NSWorkspace.shared.open(URL(fileURLWithPath: "/Applications/Ollama.app"))
            return
        }
        // Spawn `ollama serve` detached. We don't await; the embedder will
        // retry and pick it up on the next poll.
        let candidates = ["/opt/homebrew/bin/ollama", "/usr/local/bin/ollama"]
        guard let bin = candidates.first(where: { FileManager.default.isExecutableFile(atPath: $0) }) else {
            return
        }
        let p = Process()
        p.executableURL = URL(fileURLWithPath: bin)
        p.arguments = ["serve"]
        try? p.run()
    }

    struct Problem {
        enum Kind: Equatable { case ollama, service(String), unknown }
        let kind: Kind
        let title: String
        let body: String
        let primaryAction: Action?
        let retryService: String?
    }

    struct Action {
        let label: String
        let run: () -> Void
    }
}

private struct ServicesGrid: View {
    let services: [ServiceStatus]; let ollama: OllamaStatus
    var body: some View {
        let all = services + [ServiceStatus(
            id: "ollama", detail: ollama.detail, ok: ollama.ok,
            busy: false, severity: ollama.severity)]
        LazyVGrid(columns: Array(repeating: GridItem(.flexible(), spacing: 6), count: 5),
                  spacing: 6) {
            ForEach(all) { ServiceTile(service: $0) }
        }
    }
}

private struct RecentActivity: View {
    let events: [TimelineEvent]
    var body: some View {
        VStack(alignment: .leading, spacing: 2) {
            Text("Recent activity").sectionHeader().padding(.bottom, 2)
            ForEach(events) { TimelineRow(event: $0) }
        }
    }
}

private struct FooterActions: View {
    @EnvironmentObject var model: TrayModel
    @Environment(\.openWindow) private var openWindow
    var body: some View {
        HStack(spacing: 4) {
            // Pause/Resume act on indexer + embedder together. mbsync's timer
            // is left alone so new mail still arrives — only ingestion +
            // embedding pause. Show "Pause" when both are running (the steady
            // state, including idle), "Resume" when either is stopped — the
            // earlier check on severity == .syncing meant the button only
            // ever read "Pause" during an active embed run, which was
            // misleading 99% of the time.
            let running = areCoreServicesRunning()
            if running {
                FooterButton(icon: "pause.fill", label: "Pause") { model.pauseServices() }
            } else {
                FooterButton(icon: "play.fill", label: "Resume",
                             accent: model.health?.severity == .error) { model.resumeServices() }
            }
            FooterButton(icon: "stethoscope",    label: "Doctor") { runDoctor() }
            FooterButton(icon: "folder",         label: "Reveal") { model.revealMaildir() }
            Spacer()
            // Opening Settings from an LSUIElement (menu-bar accessory) app
            // is the most finicky bit of SwiftUI on macOS. Neither
            // `NSApp.sendAction("showSettingsWindow:")` nor `SettingsLink`
            // reliably surfaces the window because the app has no Dock icon
            // to activate from. The workaround is:
            //   1. Bump the activation policy to .regular temporarily so the
            //      Settings window is allowed to come forward.
            //   2. Activate the app explicitly.
            //   3. Call the openSettings environment action.
            // We don't revert to .accessory afterwards — that would re-hide
            // the Settings window we just opened. The Dock icon appears
            // briefly while Settings is open; it's the conventional macOS
            // behaviour for menu-bar apps with a real Preferences window.
            FooterButton(icon: "gearshape") {
                TrayLog.info("preferences opened")
                // `openWindow` on the "preferences" WindowGroup is the
                // canonical macOS 14+ pattern for menu-bar accessory apps.
                // Combined with NSApp.activate(), it surfaces the window
                // reliably without flipping activation policy — which
                // previously corrupted NSStatusItem state after several
                // open/close cycles and left the tray icon unresponsive.
                NSApp.activate(ignoringOtherApps: true)
                openWindow(id: "preferences")
            }
            FooterButton(icon: "power") { NSApp.terminate(nil) }
        }
        .padding(8)
        .background(.black.opacity(0.015))
        .overlay(Rectangle().frame(height: 0.5)
            .foregroundStyle(Brand.hairline), alignment: .top)
    }

    /// True when both indexer and embedder report ok. We only care about
    /// those two — mbsync is timer-driven (its "ok" state is "not running"
    /// between scheduled runs, so it's a misleading signal) and the mcp
    /// service is what's serving this query in the first place.
    private func areCoreServicesRunning() -> Bool {
        guard let services = model.health?.services else { return false }
        let want = Set(["indexer", "embedder"])
        let relevant = services.filter { want.contains($0.id) }
        return relevant.count == want.count && relevant.allSatisfy { $0.ok }
    }

    /// Opens Terminal with `mailvec doctor`. Doctor emits a multi-screen
    /// report that's not worth re-rendering inside the popover.
    private func runDoctor() {
        CliRunner.runInTerminal(["doctor"])
    }
}

private struct FooterButton: View {
    let icon: String; var label: String? = nil; var accent: Bool = false
    let action: () -> Void
    var body: some View {
        Button(action: action) {
            HStack(spacing: 5) {
                Image(systemName: icon)
                if let label { Text(label) }
            }
            .font(.system(size: 12, weight: accent ? .semibold : .regular))
        }
        .buttonStyle(.borderless)
        .foregroundStyle(accent ? Brand.accentDeep : .primary)
    }
}

// MARK: helpers

private func relative(_ date: Date) -> String {
    let f = RelativeDateTimeFormatter(); f.unitsStyle = .abbreviated
    return f.localizedString(for: date, relativeTo: Date())
}
private func fmtBytes(_ b: Int64) -> String {
    ByteCountFormatter.string(fromByteCount: b, countStyle: .file)
}

private struct LoadingRow: View {
    @EnvironmentObject var model: TrayModel
    var body: some View {
        VStack(spacing: 8) {
            ProgressView()
            if let err = model.lastError {
                Text(err)
                    .font(.system(size: 11))
                    .foregroundStyle(.red)
                    .multilineTextAlignment(.center)
                    .padding(.horizontal, 12)
                    .textSelection(.enabled)
            }
        }
        .padding(40)
    }
}

/// Placeholder field used at the bottom of the dashboard — tapping it
/// flips the popover to .search.
struct DashboardSearchField: View {
    let placeholder: String
    var body: some View {
        HStack(spacing: 8) {
            Image(systemName: "magnifyingglass").foregroundStyle(Brand.accent)
            Text(placeholder).foregroundStyle(.secondary)
            Spacer()
        }
        .padding(.horizontal, 12).padding(.vertical, 9)
        .background(Brand.cardBg, in: RoundedRectangle(cornerRadius: 10))
        .overlay(RoundedRectangle(cornerRadius: 10).stroke(Brand.hairline))
    }
}
