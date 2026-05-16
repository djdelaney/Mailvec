// Preferences/SyncTab.swift
import SwiftUI

struct SyncTab: View {
    @EnvironmentObject var prefs: PreferencesModel

    var body: some View {
        Form {
            Section {
                Banner(
                    tone: .ok,
                    title: "Last sync \(prefs.system.lastSyncRelative)",
                    body: "\(prefs.system.lastSyncDetail) · next run \(prefs.system.nextSyncRelative)",
                    action: {
                        Task {
                            _ = try? await MailvecClient.shared.control(
                                service: "mbsync", action: "kickstart")
                            await prefs.loadSystem()
                        }
                    },
                    actionLabel: "Sync now"
                )
                .listRowInsets(.init(top: 0, leading: 0, bottom: 0, trailing: 0))
                .listRowBackground(Color.clear)
            }

            Section {
                LabeledContent("IMAP via mbsync") {
                    Text(prefs.system.imapHost)
                        .font(.system(size: 11.5, design: .monospaced))
                        .foregroundStyle(.secondary)
                }
                LabeledContent("User") {
                    Text(prefs.system.imapUser)
                        .font(.system(size: 11.5, design: .monospaced))
                }
            } header: { Text("Account") } footer: {
                RowHint(text: "Read-only; changes flow IMAP → local. Password lives in the macOS Keychain under service `mbsync`.")
            }

            Section {
                LabeledContent("Maildir root") {
                    // allowChoose=false until we wire path-rewriting through
                    // the indexer plist. Read-only display + reveal in Finder.
                    PathField(path: prefs.system.maildirRoot, allowChoose: false)
                }
                LabeledContent("mbsync config") {
                    PathField(path: prefs.system.mbsyncrcPath, allowChoose: false)
                }
            } header: { Text("Local files") } footer: {
                RowHint(text: "Paths set at install time via ops/install.sh. Re-run the installer to change them.")
            }

            // The current schedule is surfaced on the banner above
            // ("next run in ≤ 10 minutes"). To change it, edit
            // ops/launchd/com.mailvec.mbsync.plist StartInterval and
            // re-run ops/install.sh — there's no in-app picker because
            // the plist is the source of truth and the .NET services
            // never reread it.
        }
    }
}
