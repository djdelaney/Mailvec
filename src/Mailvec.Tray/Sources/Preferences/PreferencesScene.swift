// Preferences/PreferencesScene.swift
import SwiftUI

struct PreferencesScene: View {
    // Owned by the scene (not each tab) so all five tabs share the same
    // /tray/system snapshot and a single loadSystem() refreshes them all.
    @StateObject private var prefs = PreferencesModel()

    var body: some View {
        TabView {
            GeneralTab().environmentObject(prefs)
                .tabItem { Label("General", systemImage: "gearshape") }
            SyncTab().environmentObject(prefs)
                .tabItem { Label("Sync", systemImage: "arrow.left.arrow.right") }
            EmbeddingTab().environmentObject(prefs)
                .tabItem { Label("Embedding", systemImage: "brain") }
            SearchAndMCPTab().environmentObject(prefs)
                .tabItem { Label("Search & MCP", systemImage: "magnifyingglass") }
            AdvancedTab().environmentObject(prefs)
                .tabItem { Label("Advanced", systemImage: "wrench.and.screwdriver") }
        }
        .scenePadding()
        // Wider AND taller — the Embedding and Advanced tabs each have
        // ~5 sections, and the original `.frame(width: 640)` left the
        // window at its natural-shrunk height which forced vertical
        // scrolling on almost every tab. 720pt fits Advanced (the tallest
        // tab) without scrolling on a 14" MBP.
        .frame(minWidth: 680, idealWidth: 680, minHeight: 720, idealHeight: 720)
        .formStyle(.grouped)
        .task { await prefs.loadSystem() }
    }
}
