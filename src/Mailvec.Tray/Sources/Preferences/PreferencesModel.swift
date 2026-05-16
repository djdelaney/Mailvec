// Preferences/PreferencesModel.swift
import ServiceManagement
import SwiftUI

@MainActor
final class PreferencesModel: ObservableObject {
    @AppStorage("launchAtLogin")       var launchAtLogin: Bool = true
    @AppStorage("notifyEmbedDone")     var notifyEmbedDone: Bool = true
    @AppStorage("notifySyncFailures")  var notifySyncFailures: Bool = true
    @AppStorage("notifyOllamaDown")    var notifyOllamaDown: Bool = true

    @AppStorage("fastmailAccountId")   var fastmailAccountId: String = ""
    @AppStorage("fastmailWebUrl")      var fastmailWebUrl: String = "https://app.fastmail.com"
    @AppStorage("gmailAccountIndex")   var gmailAccountIndex: String = "0"

    @Published var system = SystemSnapshot.placeholder
    @Published var loadError: String?

    /// Refreshes the read-only system snapshot from Mailvec.Mcp /tray/system.
    /// Call from the tab onAppear; on failure we leave the placeholder in
    /// place and surface the error in the UI.
    func loadSystem() async {
        do {
            let s = try await MailvecClient.shared.system()
            system = SystemSnapshot.from(s)
            loadError = nil
        } catch {
            loadError = error.localizedDescription
        }
    }

    func reflectLaunchAtLogin() async {
        do {
            if launchAtLogin { try SMAppService.mainApp.register() }
            else             { try await SMAppService.mainApp.unregister() }
        } catch {
            loadError = "Launch-at-login update failed: \(error.localizedDescription)"
        }
    }

    /// Wipes every UserDefaults key the tray app owns. The Mailvec archive
    /// + embeddings are not touched. Called by AdvancedTab's danger-zone.
    ///
    /// Keep this list in sync with the actual @AppStorage / UserDefaults
    /// keys the tray reads — if you add a new persisted preference,
    /// remember to add it here too or "Reset" silently leaves it behind.
    func resetAllSettings() {
        let defaults = UserDefaults.standard
        let keys = [
            // Preferences
            "launchAtLogin",
            "notifyEmbedDone", "notifySyncFailures", "notifyOllamaDown",
            "fastmailAccountId", "fastmailWebUrl",
            "gmailAccountIndex",
            // Search popover state (TrayModel)
            "mailvec.recentSearches",
            "mailvec.dateRange",
            "mailvec.folderFilter",
        ]
        keys.forEach { defaults.removeObject(forKey: $0) }
    }
}

/// View-friendly mirror of TraySystem with non-optional strings so the
/// SwiftUI tabs (which were authored against the placeholder) compile
/// without nil-coalescing at every callsite. The placeholder is shown
/// before the first /tray/system response arrives.
struct SystemSnapshot: Equatable {
    var maildirRoot: String
    var mbsyncrcPath: String
    var mbsyncSchedule: String
    var imapHost: String
    var imapUser: String
    var lastSyncRelative: String
    var lastSyncDetail: String
    var nextSyncRelative: String

    var dbPath: String
    var dbSize: String
    var schemaVersion: String
    var vecDylibVersion: String

    var ollamaEndpoint: String
    var ollamaReachable: Bool
    var ollamaPingMs: Int
    var embeddingModel: String
    var modelDimensions: Int
    var schemaModelMatches: Bool
    var coverageDone: Int
    var coverageTotal: Int

    var mcpHttpEnabled: Bool
    var mcpBindAddress: String
    var mcpPort: Int
    var mcpbInstalled: Bool
    var mcpbVersion: String
    var attachmentDownloadDir: String

    var softDeletedCount: Int

    static func from(_ t: TraySystem) -> SystemSnapshot {
        SystemSnapshot(
            maildirRoot: t.maildirRoot,
            mbsyncrcPath: t.mbsyncrcPath,
            mbsyncSchedule: t.mbsyncSchedule,
            imapHost: t.imapHost,
            imapUser: t.imapUser,
            lastSyncRelative: t.lastSyncRelative ?? "—",
            lastSyncDetail: t.lastSyncDetail ?? "no recent sync data",
            nextSyncRelative: t.nextSyncRelative ?? "unscheduled",
            dbPath: t.dbPath,
            dbSize: t.dbSize,
            schemaVersion: "v\(t.schemaVersion)",
            vecDylibVersion: t.vecDylibVersion,
            ollamaEndpoint: t.ollamaEndpoint,
            ollamaReachable: t.ollamaReachable,
            ollamaPingMs: t.ollamaPingMs,
            embeddingModel: t.embeddingModel,
            modelDimensions: t.modelDimensions,
            schemaModelMatches: t.schemaModelMatches,
            coverageDone: t.coverageDone,
            coverageTotal: t.coverageTotal,
            mcpHttpEnabled: t.mcpHttpEnabled,
            mcpBindAddress: t.mcpBindAddress,
            mcpPort: t.mcpPort,
            mcpbInstalled: t.mcpbInstalled,
            mcpbVersion: t.mcpbVersion ?? "—",
            attachmentDownloadDir: t.attachmentDownloadDir,
            softDeletedCount: t.softDeletedCount)
    }

    // Shown for the brief window between Preferences-tab open and the
    // first /tray/system response. Rule: fields that vary per install or
    // provider (Maildir root, IMAP host/user) get a neutral em-dash;
    // Mailvec-universal defaults (DB path, Ollama endpoint, model, MCP
    // bind, mbsyncrc convention) keep their real values so the placeholder
    // is informative without presuming Fastmail or any particular account.
    static let placeholder = SystemSnapshot(
        maildirRoot: "—",
        mbsyncrcPath: "~/.mbsyncrc",
        mbsyncSchedule: "—",
        imapHost: "—",
        imapUser: "—",
        lastSyncRelative: "loading…",
        lastSyncDetail: "fetching from /tray/system",
        nextSyncRelative: "—",
        dbPath: "~/Library/Application Support/Mailvec/archive.sqlite",
        dbSize: "—",
        schemaVersion: "—",
        vecDylibVersion: "—",
        ollamaEndpoint: "http://localhost:11434",
        ollamaReachable: false,
        ollamaPingMs: 0,
        embeddingModel: "mxbai-embed-large",
        modelDimensions: 1024,
        schemaModelMatches: true,
        coverageDone: 0,
        coverageTotal: 0,
        mcpHttpEnabled: false,
        mcpBindAddress: "127.0.0.1",
        mcpPort: 3333,
        mcpbInstalled: false,
        mcpbVersion: "—",
        attachmentDownloadDir: "~/Downloads/mailvec",
        softDeletedCount: 0
    )
}
