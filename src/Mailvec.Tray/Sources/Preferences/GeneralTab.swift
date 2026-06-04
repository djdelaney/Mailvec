// Preferences/GeneralTab.swift
import SwiftUI

struct GeneralTab: View {
    @EnvironmentObject var prefs: PreferencesModel

    var body: some View {
        Form {
            Section("Startup") {
                Toggle("Launch at login", isOn: $prefs.launchAtLogin)
                    .onChange(of: prefs.launchAtLogin) { _, _ in
                        Task { await prefs.reflectLaunchAtLogin() }
                    }
            }

            Section {
                Toggle("Large batch embedded", isOn: $prefs.notifyEmbedDone)
                Toggle("Sync failures", isOn: $prefs.notifySyncFailures)
                Toggle("Ollama unreachable", isOn: $prefs.notifyOllamaDown)
            } header: { Text("Notifications") } footer: {
                RowHint(text: "Banners surface only when something needs your attention. None are shown for routine sync activity.")
            }
        }
    }
}
