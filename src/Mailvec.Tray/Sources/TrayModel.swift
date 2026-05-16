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
    @Published var searchQuery: String = ""
    @Published var searchHits: [SearchHit] = []
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

    /// Set true by SearchView's hotkey wiring so the TextField grabs focus.
    @Published var pendingSearchFocus: Bool = false

    private var poller: Task<Void, Never>?

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
        } catch {
            lastError = error.localizedDescription
        }
    }

    /// Fires UNUserNotifications on meaningful state transitions — the
    /// proactive surface advertised by the General prefs tab. We only fire
    /// on edge transitions (ok → bad), never on steady states, otherwise
    /// the user would get a banner every 5-second poll while a problem
    /// persists.
    private func detectAndNotifyTransitions(from old: TrayHealth?, to new: TrayHealth) {
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

        // Initial archive embedded: coverage < 100% → coverage = 100%.
        // Only fire when both totals are non-zero (an empty archive would
        // be 100% trivially and shouldn't celebrate).
        let oldDone = old.embedTotal > 0 && old.embedded >= old.embedTotal
        let newDone = new.embedTotal > 0 && new.embedded >= new.embedTotal
        if !oldDone && newDone {
            TrayNotifications.send(
                kind: .embedComplete,
                title: "Mailvec: archive fully embedded",
                body: "All \(new.embedded.formatted()) messages are searchable.")
        }
    }

    /// Live search — called on every keystroke. Does NOT add to history,
    /// so the recents list isn't polluted with every partial query the user
    /// typed. Use `commitSearch()` on Enter to persist a query.
    func runSearch() async {
        await performSearch(commit: false)
    }

    /// Triggered on Enter — runs the search AND persists the query to the
    /// recents list. Idempotent if the query is unchanged from the live run.
    func commitSearch() async {
        await performSearch(commit: true)
    }

    private func performSearch(commit: Bool) async {
        guard !searchQuery.isEmpty else {
            searchHits = []
            searchSelection = nil
            return
        }
        do {
            let trimmed = searchQuery.trimmingCharacters(in: .whitespacesAndNewlines)
            // Result limit hardcoded — anything larger crowds the popover,
            // anything smaller hides relevant hits. Was previously a
            // @AppStorage Stepper but the configurability was never useful.
            searchHits = try await MailvecClient.shared.search(
                trimmed,
                mode: mode,
                limit: 20,
                folder: folderFilter,
                dateFrom: dateRange.dateFrom,
                dateTo: nil)
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
            lastError = error.localizedDescription
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
