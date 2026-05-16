// Preferences/AdvancedTab.swift
import AppKit
import SwiftUI

struct AdvancedTab: View {
    @EnvironmentObject var prefs: PreferencesModel
    @State private var showResetAlert = false

    var body: some View {
        Form {
            Section("Storage") {
                LabeledContent("Database") {
                    VStack(alignment: .trailing, spacing: 2) {
                        PathField(path: prefs.system.dbPath)
                        Text("\(prefs.system.dbSize) · WAL mode")
                            .font(.system(size: 10.5))
                            .foregroundStyle(.secondary)
                    }
                }
                LabeledContent("Schema version") {
                    StatusBadge(tone: .ok,
                                label: "\(prefs.system.schemaVersion) · current")
                }
                LabeledContent("sqlite-vec extension") {
                    StatusBadge(tone: .ok,
                                label: "\(prefs.system.vecDylibVersion) · loaded")
                }
            }

            Section {
                MaintRow(label: "Run doctor",
                         hint: "Full preflight: schema, vec0, Ollama, launchd, MCP /health.",
                         action: "Run…") { runCli(["doctor"]) }
                MaintRow(label: "Rebuild FTS5 index",
                         hint: "Drop + recreate keyword index from messages.body_text. ~1 min / 6k messages.",
                         action: "Rebuild…") { runCli(["rebuild-fts"]) }
                MaintRow(label: "Checkpoint WAL",
                         hint: "Truncate the write-ahead log. Useful after a large reindex.",
                         action: "Checkpoint") { runCli(["checkpoint"]) }
                MaintRow(label: "Audit embeddings",
                         hint: "Sweep the vector index for zero, NaN, or abnormal-norm vectors.",
                         action: "Audit…") { runCli(["audit-embeddings"]) }
                MaintRow(label: "Purge soft-deleted",
                         hint: "\(prefs.system.softDeletedCount) tombstoned messages. Hard-deletes rows + chunks + vectors + attachments.",
                         action: "Preview…") { runCli(["purge-deleted", "--dry-run"]) }
            } header: { Text("Maintenance") } footer: {
                RowHint(text: "All of these have CLI equivalents under `mailvec`. Exposed here for convenience.")
            }

            Section {
                LabeledContent("Log files") {
                    HStack(spacing: 6) {
                        Text("~/Library/Logs/Mailvec/")
                            .font(.system(size: 11, design: .monospaced))
                            .foregroundStyle(.secondary)
                        Button("Open in Finder") {
                            NSWorkspace.shared.open(
                                URL(fileURLWithPath: ("~/Library/Logs/Mailvec/" as NSString).expandingTildeInPath))
                        }
                        .controlSize(.small)
                    }
                }
                LabeledContent("Tray log (live)") {
                    Button("Stream in Console") {
                        // Console.app filters by subsystem when given a URL
                        // with the right scheme. Falling back to a Terminal
                        // `log stream` if Console doesn't accept the URL.
                        let pred = "subsystem == \"com.mailvec.tray\""
                        let script = "tell application \"Console\" to activate\n" +
                            "tell application \"System Events\" to keystroke \"\(pred)\""
                        var err: NSDictionary?
                        if NSAppleScript(source: script)?.executeAndReturnError(&err) == nil {
                            NSWorkspace.shared.open(URL(fileURLWithPath: "/System/Applications/Utilities/Console.app"))
                        }
                    }
                    .controlSize(.small)
                }
                LabeledContent("Copy diagnostics") {
                    Button("Copy") { copyDiagnostics() }.controlSize(.small)
                }
            } header: { Text("Diagnostics") } footer: {
                RowHint(text: "Daily-rolling files · 10 MB cap · 14 retained. Doctor output is redacted before copy.")
            }

            Section {
                LabeledContent("Reset all settings") {
                    Button("Reset…", role: .destructive) { showResetAlert = true }
                        .foregroundStyle(.red)
                }
            } header: { Text("Danger zone") } footer: {
                RowHint(text: "Restores all defaults in this window. Your archive and embeddings stay intact.")
            }
        }
        .confirmationDialog("Reset all settings?",
                            isPresented: $showResetAlert,
                            titleVisibility: .visible) {
            Button("Reset", role: .destructive) { prefs.resetAllSettings() }
            Button("Cancel", role: .cancel) {}
        } message: {
            Text("This only affects Mailvec Tray preferences. The Mailvec archive, embeddings, and launchd agents are untouched.")
        }
    }

    /// Spawn Terminal with `mailvec <args>` — keeps streaming output visible
    /// without us re-rendering it inside Preferences.
    private func runCli(_ args: [String]) {
        CliRunner.runInTerminal(args)
    }

    /// Copies the system snapshot (paths redacted to ~) onto the pasteboard
    /// so the user can paste it into a bug report.
    private func copyDiagnostics() {
        let s = prefs.system
        let home = NSHomeDirectory()
        func tilde(_ p: String) -> String {
            p.hasPrefix(home) ? "~" + p.dropFirst(home.count) : p
        }
        let report = """
        Mailvec diagnostics
        DB:       \(tilde(s.dbPath)) (\(s.dbSize), schema \(s.schemaVersion))
        Maildir:  \(tilde(s.maildirRoot))
        Ollama:   \(s.ollamaEndpoint) reachable=\(s.ollamaReachable) ping=\(s.ollamaPingMs)ms
        Model:    \(s.embeddingModel) (\(s.modelDimensions)d) schemaMatches=\(s.schemaModelMatches)
        Coverage: \(s.coverageDone) / \(s.coverageTotal)
        MCP:      \(s.mcpBindAddress):\(s.mcpPort) enabled=\(s.mcpHttpEnabled)
        MCPB:     installed=\(s.mcpbInstalled) version=\(s.mcpbVersion)
        Soft-deleted: \(s.softDeletedCount)
        """
        let pb = NSPasteboard.general
        pb.clearContents()
        pb.setString(report, forType: .string)
    }
}

private struct MaintRow: View {
    let label: String
    let hint: String
    let action: String
    let onTap: () -> Void

    var body: some View {
        VStack(alignment: .leading, spacing: 3) {
            HStack {
                Text(label)
                Spacer()
                Button(action, action: onTap).controlSize(.small)
            }
            Text(hint).font(.system(size: 11)).foregroundStyle(.secondary)
        }
        .padding(.vertical, 2)
    }
}
