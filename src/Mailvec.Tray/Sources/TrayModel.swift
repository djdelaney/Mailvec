// TrayModel.swift
import AppKit
import Foundation
import SwiftUI

@MainActor
final class TrayModel: ObservableObject {
    /// Process-wide singleton so the `AppDelegate` (which runs the
    /// launch-time prewarm + hotkey registration before SwiftUI evaluates
    /// any view body) and the SwiftUI scene share the same instance.
    /// Bound into `MailvecTrayApp` via `@StateObject(wrappedValue: .shared)`.
    static let shared = TrayModel()

    @Published var health: TrayHealth?
    @Published var lastError: String?

    /// Consecutive /tray/status poll failures. `refresh()` deliberately keeps
    /// the last good `health` on error (so a blip doesn't blank the dashboard),
    /// which used to mean a dead server left frozen counts under a green
    /// "All clear" pill indefinitely — this counter is what lets the UI tell
    /// "stale but momentary" from "the server is gone".
    @Published private(set) var consecutivePollFailures = 0
    /// Wall-clock of the last successful status poll — the "showing data
    /// from Xs ago" timestamp on the disconnected banner.
    @Published private(set) var lastSuccessAt: Date?
    /// Two missed polls (~10s at the 5s cadence) before declaring the server
    /// unreachable: one miss can be a restart (ops/redeploy.sh bounces the
    /// MCP); two in a row is a real outage worth alarming on.
    private let disconnectedAfterFailures = 2
    var isDisconnected: Bool { consecutivePollFailures >= disconnectedAfterFailures }

    /// Severity for the menu-bar icon: server state when we can see it,
    /// error when we've lost the server (stale data must not render a green
    /// icon), syncing-pulse while the first poll is still connecting.
    var effectiveSeverity: TrayHealth.Severity {
        if isDisconnected { return .error }
        return health?.severity ?? .syncing
    }

    /// Error from the most recent (non-superseded) search request, rendered
    /// by SearchView. Distinct from `lastError`: the dashboard's poll errors
    /// and a search failure must not clobber each other's surfaces.
    @Published var searchError: String?
    @Published var searchQuery: String = ""
    @Published var searchHits: [SearchHit] = []
    /// True while a `/tray/search` request is in flight. Drives the spinner
    /// in the search field / header / results area so a multi-second semantic
    /// search doesn't look like the popover ignored the keystroke. Only the
    /// most recent search (see `searchGeneration`) clears it.
    @Published var isSearching = false
    /// Keyboard cursor / highlight target — moved by ↑/↓ and used by
    /// Enter-opens-in-Fastmail. Decoupled from `expandedHit` so arrow-key
    /// navigation doesn't expand each row's inline preview (which would
    /// cascade layout changes and make the scroller chase itself).
    @Published var searchSelection: Int?
    /// Which hit's inline preview is currently shown. Set only by an
    /// explicit click on a row — arrow-key navigation never touches this.
    @Published var expandedHit: Int?
    @Published var mode: SearchMode = .hybrid

    // Filters. Persisted to UserDefaults so the user's "last 30 days +
    // INBOX" choice survives restarts.
    @Published var dateRange: DateRange = .last6Months {
        didSet { UserDefaults.standard.set(dateRange.rawValue, forKey: dateRangeKey) }
    }
    @Published var folderFilter: String? {
        didSet { UserDefaults.standard.set(folderFilter ?? "", forKey: folderFilterKey) }
    }
    @Published var availableFolders: [FolderRow] = []
    private let dateRangeKey = "mailvec.dateRange"
    private let folderFilterKey = "mailvec.folderFilter"

    /// IMAP host reported by /tray/system, used to detect which webmail
    /// provider (Fastmail, Gmail, etc.) the user is on so we can render
    /// "Open in <provider>" buttons correctly and hide them when no
    /// webmail URL scheme is known. Loaded once at app launch by the
    /// AppDelegate prewarm; nil until the first /tray/system response.
    @Published private(set) var imapHost: String?

    /// Derived from imapHost — `.unknown` until /tray/system replies, then
    /// whatever provider `WebmailProvider.detect` matches.
    var webmailProvider: WebmailProvider {
        WebmailProvider.detect(imapHost: imapHost)
    }

    enum Pane: Equatable { case dashboard, search }
    @Published var pane: Pane = .dashboard

    /// Recent search queries persisted to UserDefaults. Capped at 10.
    @Published var recentSearches: [String] = []
    private let recentsKey = "mailvec.recentSearches"
    private let recentsMax = 10

    private var poller: Task<Void, Never>?

    /// High-water mark of pending work (`embedTotal - embedded`) observed
    /// since the archive was last fully caught up. Drives the "significant
    /// batch" gate on the embed-complete notification: a drain to 100% only
    /// fires a banner if the backlog it cleared was large enough to be worth
    /// flagging (an initial backfill or a big sync), not the routine handful
    /// of new messages that arrive between polls. Reset to 0 each time we
    /// reach 100%, so every not-done streak is sized independently.
    private var peakPendingSinceDone = 0

    /// Minimum cleared backlog for the embed-complete banner to fire. Below
    /// this, drain-to-empty is the silent steady state — the case the user
    /// hits constantly as new mail trickles in. Initial archives and large
    /// syncs clear far more than this; a few new mails clear far less.
    private let embedNotifyThreshold = 50

    init() {
        recentSearches = (UserDefaults.standard.array(forKey: recentsKey) as? [String]) ?? []
        if let raw = UserDefaults.standard.string(forKey: dateRangeKey),
           let r = DateRange(rawValue: raw) {
            dateRange = r
        }
        let storedFolder = UserDefaults.standard.string(forKey: folderFilterKey) ?? ""
        folderFilter = storedFolder.isEmpty ? nil : storedFolder
        // Mode always starts on hybrid — the per-session chips in the
        // search popover still let the user pick keyword or semantic for
        // an individual query. Hybrid (BM25 + vector RRF) is the only
        // mode that's eval-benchmarked, so we don't expose a persistent
        // default for the others.
    }

    /// Fetches the folder list once at first popover open so the filter menu
    /// has real names to show. Idempotent.
    func loadFolders() async {
        guard availableFolders.isEmpty else { return }
        if let resp = try? await MailvecClient.shared.folders() {
            availableFolders = resp.folders.sorted { $0.messageCount > $1.messageCount }
        }
    }

    /// Fetches /tray/system once at app launch so `webmailProvider` is
    /// populated before any view renders an "Open in …" button. Cheap
    /// (single round-trip on loopback) but only needed once — Mailvec
    /// doesn't change providers at runtime. Idempotent.
    func loadSystemInfo() async {
        guard imapHost == nil else { return }
        if let sys = try? await MailvecClient.shared.system() {
            imapHost = sys.imapHost
        }
    }

    func start() {
        guard poller == nil else { return }
        poller = Task { [weak self] in
            while !Task.isCancelled {
                await self?.refresh()
                try? await Task.sleep(for: .seconds(5))
            }
        }
    }

    func stop() {
        poller?.cancel()
        poller = nil
    }

    func refresh() async {
        do {
            let before = health
            let next = try await MailvecClient.shared.status()
            detectAndNotifyTransitions(from: before, to: next)
            health = next
            lastError = nil
            consecutivePollFailures = 0
            lastSuccessAt = Date()
        } catch {
            lastError = error.localizedDescription
            consecutivePollFailures += 1
        }
    }

    /// Fires UNUserNotifications on meaningful state transitions — the
    /// proactive surface advertised by the General prefs tab. We only fire
    /// on edge transitions (ok → bad), never on steady states, otherwise
    /// the user would get a banner every 5-second poll while a problem
    /// persists.
    private func detectAndNotifyTransitions(from old: TrayHealth?, to new: TrayHealth) {
        // Track the high-water mark of pending work across the current
        // not-done streak. Done outside the `old` guard so the very first
        // poll after launch seeds it (a big backlog present at startup
        // still counts toward the batch size it eventually clears).
        let pending = max(0, new.embedTotal - new.embedded)
        peakPendingSinceDone = max(peakPendingSinceDone, pending)

        guard let old else { return }   // First poll — no comparison baseline.

        // Ollama: reachable → unreachable
        if old.ollama.ok && !new.ollama.ok {
            TrayNotifications.send(
                kind: .ollamaDown,
                title: "Mailvec: Ollama is unreachable",
                body: "Embedding has paused. Click the menu-bar icon for details.")
        }

        // mbsync: ok → not-ok (warn or error). Looking up by id is robust
        // to service-list reordering.
        let oldMbsync = old.services.first(where: { $0.id == "mbsync" })
        let newMbsync = new.services.first(where: { $0.id == "mbsync" })
        let wasOkMbsync = oldMbsync?.severity == .ok || oldMbsync?.severity == nil
        let isBadMbsync = newMbsync?.severity == .warn || newMbsync?.severity == .error
        if wasOkMbsync && isBadMbsync, let detail = newMbsync?.detail {
            TrayNotifications.send(
                kind: .syncFailure,
                title: "Mailvec: mbsync failed",
                body: detail)
        }

        // Archive caught up: coverage < 100% → coverage = 100%. Only fire
        // when both totals are non-zero (an empty archive is 100% trivially
        // and shouldn't celebrate) AND the backlog we just cleared was large
        // enough to be worth a banner. Without the size gate this fires every
        // time the queue drains after a few new mails arrive — which is the
        // steady state on a live archive, so it became constant noise.
        let oldDone = old.embedTotal > 0 && old.embedded >= old.embedTotal
        let newDone = new.embedTotal > 0 && new.embedded >= new.embedTotal
        if !oldDone && newDone {
            let cleared = peakPendingSinceDone
            peakPendingSinceDone = 0   // Start a fresh streak.
            if cleared >= embedNotifyThreshold {
                TrayNotifications.send(
                    kind: .embedComplete,
                    title: "Mailvec: archive fully embedded",
                    body: "Finished embedding \(cleared.formatted()) messages — "
                        + "all \(new.embedded.formatted()) are now searchable.")
            }
        }
    }

    /// Pending debounced keystroke search. Cancelled and replaced on each
    /// keystroke so a burst of typing collapses into one request once the
    /// user pauses — semantic search embeds the query through Ollama, so
    /// firing one per character flooded the server and stacked up multi-second
    /// requests. `performSearch` cancels this too, so Enter / filter changes
    /// (which run immediately) never leave a stale debounce to double-fire.
    private var debounceTask: Task<Void, Never>?
    private let searchDebounceMs: UInt64 = 350

    /// Debounced live search — wired to the query field's `onChange`. Empties
    /// clear results immediately (no point waiting); otherwise the request
    /// fires `searchDebounceMs` after the last keystroke. Does NOT persist to
    /// recents — that's `commitSearch()` on Enter.
    func scheduleSearch() {
        searchGeneration += 1          // supersede anything pending / in flight
        debounceTask?.cancel()
        let gen = searchGeneration
        guard !searchQuery.isEmpty else {
            debounceTask = nil
            isSearching = false
            searchHits = []
            searchSelection = nil
            expandedHit = nil
            searchError = nil
            return
        }
        // Flip the spinner on at the keystroke, not when the debounced request
        // finally fires — otherwise the results pane would flash "No matches"
        // during the debounce window. Cleared by `performSearch`'s defer.
        isSearching = true
        debounceTask = Task { [weak self, gen, ms = searchDebounceMs] in
            try? await Task.sleep(nanoseconds: ms * 1_000_000)
            guard !Task.isCancelled else { return }
            await self?.performSearch(commit: false, generation: gen)
        }
    }

    /// Immediate search for deliberate single actions — Enter and filter/mode
    /// changes. Bumps the generation and cancels any pending keystroke debounce
    /// so it can't fire a duplicate behind this one; an already-awaiting
    /// debounce request is dropped by the generation guard once this bump makes
    /// its token stale. Does NOT add to history unless `commit` is set.
    func runSearchNow(commit: Bool = false) async {
        searchGeneration += 1
        debounceTask?.cancel()
        await performSearch(commit: commit, generation: searchGeneration)
    }

    /// Triggered on Enter — runs the search AND persists the query to the
    /// recents list. Idempotent if the query is unchanged from the live run.
    func commitSearch() async {
        await runSearchNow(commit: true)
    }

    /// Monotonic token bumped at every search *trigger* (keystroke, filter
    /// change, Enter, clear) — the single source of truth for "which search is
    /// current". `performSearch` captures the token it was triggered with and
    /// only applies results / owns the spinner while it still matches. Bumping
    /// at the trigger rather than inside `performSearch` is what lets a new
    /// keystroke immediately invalidate the in-flight request it cancels: that
    /// request's URLError lands with a now-stale token and is dropped silently
    /// instead of clearing a spinner the newer search already owns. (Earlier,
    /// `performSearch` cancelled `debounceTask` itself — which, when the debounce
    /// task WAS the caller, cancelled its own in-flight request and returned
    /// instant empty results. Cancellation now only ever comes from a newer
    /// trigger.)
    private var searchGeneration = 0

    private func performSearch(commit: Bool, generation gen: Int) async {
        guard !searchQuery.isEmpty else {
            if gen == searchGeneration {
                isSearching = false
                searchHits = []
                searchSelection = nil
                expandedHit = nil
                searchError = nil
            }
            return
        }
        isSearching = true
        // Only the current search owns the spinner; a superseded run (its token
        // now stale) must not flip it off while a newer one is still live.
        defer { if gen == searchGeneration { isSearching = false } }
        do {
            let trimmed = searchQuery.trimmingCharacters(in: .whitespacesAndNewlines)
            // Result limit hardcoded — anything larger crowds the popover,
            // anything smaller hides relevant hits. Was previously a
            // @AppStorage Stepper but the configurability was never useful.
            let hits = try await MailvecClient.shared.search(
                trimmed,
                mode: mode,
                limit: 20,
                folder: folderFilter,
                dateFrom: dateRange.dateFrom,
                dateTo: nil)
            // A newer search started while this one was awaiting (or cancelled
            // it) — drop its results rather than clobbering the fresher query's.
            guard gen == searchGeneration else { return }
            searchError = nil
            searchHits = hits
            // Don't auto-expand the first hit — the user explicitly picks
            // a row to preview. If the previously-selected id is still in
            // the new result set (e.g. they tweaked a filter), keep it
            // selected; otherwise clear. Same goes for the expanded hit.
            if let current = searchSelection, !searchHits.contains(where: { $0.id == current }) {
                searchSelection = nil
            }
            if let expanded = expandedHit, !searchHits.contains(where: { $0.id == expanded }) {
                expandedHit = nil
            }
            if searchHits.isEmpty {
                searchSelection = nil
                expandedHit = nil
            }
            if commit { rememberSearch(trimmed) }
        } catch {
            // Superseded / cancelled request — stay silent so a newer search's
            // spinner and results aren't disturbed by this one's cancellation.
            guard gen == searchGeneration else { return }
            // Clear the stale hits: leaving the previous query's results under
            // a failed search read as "the new search found these", and with
            // the hits pane occupied the failure had no surface at all — a
            // down server showed "No matches", which is actively wrong.
            searchError = error.localizedDescription
            searchHits = []
            searchSelection = nil
            expandedHit = nil
        }
    }

    private func rememberSearch(_ q: String) {
        guard !q.isEmpty else { return }
        var next = recentSearches.filter { $0 != q }
        next.insert(q, at: 0)
        if next.count > recentsMax { next.removeLast(next.count - recentsMax) }
        recentSearches = next
        UserDefaults.standard.set(next, forKey: recentsKey)
    }

    // MARK: - Footer actions

    func pauseServices() {
        Task { _ = try? await MailvecClient.shared.pauseServices(); await refresh() }
    }

    func resumeServices() {
        Task { _ = try? await MailvecClient.shared.resumeServices(); await refresh() }
    }

    func revealMaildir() {
        Task {
            guard let sys = try? await MailvecClient.shared.system() else { return }
            NSWorkspace.shared.selectFile(sys.maildirRoot, inFileViewerRootedAtPath: "")
        }
    }

    func openAttachment(messageId: Int, partIndex: Int) {
        Task {
            do {
                let resp = try await MailvecClient.shared.attachment(
                    messageId: messageId, partIndex: partIndex)
                NSWorkspace.shared.open(URL(fileURLWithPath: resp.path))
            } catch {
                self.lastError = error.localizedDescription
            }
        }
    }

    /// Opens the hit in the detected webmail provider (Fastmail today).
    /// No-op for providers we don't have a URL scheme for — UI callers
    /// hide the affordance in that case so this only fires when we're
    /// confident the link works.
    func openHitInWebmail(_ hit: SearchHit) {
        // Build the URL client-side rather than depending on the server's
        // `webmailUrl` field — the server returns null whenever the
        // Fastmail__AccountId env var isn't set in the launchd plist, and
        // we'd rather work for everyone, account-id or not. See
        // WebmailLink.swift for the URL shape per provider.
        WebmailLink.open(provider: webmailProvider, messageId: hit.messageId)
    }

    // MARK: - Keyboard navigation

    /// Move selection one row down. If nothing is selected yet, select the
    /// first hit; clamp at the last row. Returns the newly-selected id so
    /// callers can scroll it into view.
    @discardableResult
    func selectNextHit() -> Int? {
        guard !searchHits.isEmpty else { return nil }
        let next: SearchHit
        if let current = searchSelection,
           let idx = searchHits.firstIndex(where: { $0.id == current }) {
            next = searchHits[min(idx + 1, searchHits.count - 1)]
        } else {
            next = searchHits[0]
        }
        searchSelection = next.id
        return next.id
    }

    /// Move selection one row up. If nothing is selected, do nothing
    /// (mirrors the convention from Spotlight / Alfred). Clamps at the
    /// first row.
    @discardableResult
    func selectPreviousHit() -> Int? {
        guard !searchHits.isEmpty else { return nil }
        guard let current = searchSelection,
              let idx = searchHits.firstIndex(where: { $0.id == current })
        else { return nil }
        let prev = searchHits[max(idx - 1, 0)]
        searchSelection = prev.id
        return prev.id
    }

    /// Opens the currently-highlighted hit in the detected webmail
    /// provider. No-op if no row is highlighted or the provider is
    /// unknown.
    func openSelectedInWebmail() {
        guard let id = searchSelection,
              let hit = searchHits.first(where: { $0.id == id }) else { return }
        openHitInWebmail(hit)
    }
}
