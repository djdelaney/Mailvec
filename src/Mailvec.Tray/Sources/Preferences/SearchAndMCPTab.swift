// Preferences/SearchAndMCPTab.swift
import AppKit
import SwiftUI

struct SearchAndMCPTab: View {
    @EnvironmentObject var prefs: PreferencesModel

    var body: some View {
        Form {
            // The default-search section was removed: hybrid (BM25 + vector
            // RRF) is the only ranking we ship, and the per-session mode
            // chips in the search popover still let the user override on a
            // single query. Result-limit is hardcoded to 20 in TrayModel
            // — anything larger crowded the popover and anything smaller
            // hid relevant hits.
            // Only surface the Fastmail-specific webmail-link config when
            // the user is actually on Fastmail. Detection happens from
            // /tray/system's imapHost — see WebmailProvider.detect. For
            // any other IMAP host (or before /tray/system has loaded), we
            // hide the section entirely rather than ask the user to
            // configure settings that don't apply.
            if WebmailProvider.detect(imapHost: prefs.system.imapHost) == .fastmail {
                Section {
                    LabeledContent("Account ID") {
                        TextField("", text: $prefs.fastmailAccountId,
                                  prompt: Text("u1234abcd"))
                            .font(.system(size: 12, design: .monospaced))
                            .frame(width: 140)
                            .textFieldStyle(.roundedBorder)
                    }
                    LabeledContent("Open in") {
                        TextField("", text: $prefs.fastmailWebUrl,
                                  prompt: Text("https://app.fastmail.com"))
                            .font(.system(size: 12, design: .monospaced))
                            .frame(width: 240)
                            .textFieldStyle(.roundedBorder)
                    }
                } header: { Text("Fastmail webmail links") } footer: {
                    RowHint(text: "The tray builds Open-in-Fastmail links client-side from these. Account ID is the ?u=… query param visible on any URL at app.fastmail.com — optional, but without it Fastmail redirects to your default account on first open.")
                }
            }

            Section {
                LabeledContent("HTTP transport") {
                    StatusBadge(tone: prefs.system.mcpHttpEnabled ? .ok : .error,
                                label: prefs.system.mcpHttpEnabled ? "running" : "stopped")
                }
                LabeledContent("Bind address") {
                    Text("\(prefs.system.mcpBindAddress):\(prefs.system.mcpPort)")
                        .font(.system(size: 12, design: .monospaced))
                        .foregroundStyle(.secondary)
                }
                LabeledContent("Claude Desktop bundle") {
                    HStack(spacing: 6) {
                        if prefs.system.mcpbInstalled {
                            StatusBadge(tone: .info,
                                        label: "v\(prefs.system.mcpbVersion)")
                        } else {
                            StatusBadge(tone: .warn, label: "not installed")
                        }
                        Button("Reveal…") {
                            // Try the modern "Claude Extensions" folder
                            // first, falling back to the legacy "Connectors"
                            // for older Claude Desktop installs. Whichever
                            // exists gets opened in Finder.
                            let home = ("~/Library/Application Support/Claude" as NSString).expandingTildeInPath
                            let candidates = [
                                home + "/Claude Extensions",
                                home + "/Connectors",
                            ]
                            let target = candidates.first { FileManager.default.fileExists(atPath: $0) }
                                ?? candidates[0]
                            NSWorkspace.shared.open(URL(fileURLWithPath: target))
                        }.controlSize(.small)
                    }
                }
            } header: { Text("MCP server") } footer: {
                RowHint(text: "Transport, bind address, and port are managed by the launchd agent com.mailvec.mcp. To change them, edit src/Mailvec.Mcp/appsettings.json and run ops/redeploy.sh mcp. Default 127.0.0.1:3333 is the trust boundary.")
            }

            Section {
                LabeledContent("Download directory") {
                    PathField(path: prefs.system.attachmentDownloadDir)
                }
            } header: { Text("Attachments") } footer: {
                RowHint(text: "Where get_attachment extracts files. Configured via Mcp:AttachmentDownloadDir in appsettings.json.")
            }
        }
    }
}
