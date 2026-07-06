// Preferences/EmbeddingTab.swift
import AppKit
import SwiftUI

struct EmbeddingTab: View {
    @EnvironmentObject var prefs: PreferencesModel
    @State private var showReindexAlert = false

    var body: some View {
        Form {
            Section {
                let s = prefs.system
                Banner(
                    tone: s.schemaModelMatches ? .ok : .error,
                    title: s.schemaModelMatches
                        ? "\(coveragePct(s))% coverage"
                        : "Embedding model mismatch",
                    body: s.schemaModelMatches
                        ? "\(s.coverageDone.formatted()) / \(s.coverageTotal.formatted()) messages embedded · model matches schema"
                        : "Schema recorded one model, config has another. Mailvec refuses to mix vector spaces — reindex required."
                )
                .listRowInsets(.init(top: 0, leading: 0, bottom: 0, trailing: 0))
                .listRowBackground(Color.clear)
            }

            // Model + Chunking sections deliberately omitted. Changing the
            // embedding model invalidates every stored vector (the schema
            // pins the dimension count) and requires a full reindex;
            // changing chunk size/overlap silently shifts retrieval
            // quality in ways that need eval-baseline validation. Both
            // belong in appsettings.json + an intentional ops/redeploy.sh
            // workflow, not a click in a popover. The provider info below
            // stays read-only so the user can confirm what's configured.
            Section {
                LabeledContent("Backend") { Text("Ollama") }
                LabeledContent("Endpoint") {
                    HStack(spacing: 6) {
                        Text(prefs.system.ollamaEndpoint)
                            .font(.system(size: 12, design: .monospaced))
                        if prefs.system.ollamaReachable {
                            StatusBadge(tone: .ok,
                                        label: "reachable · \(prefs.system.ollamaPingMs)ms")
                        } else {
                            StatusBadge(tone: .error, label: "unreachable")
                        }
                    }
                }
                LabeledContent("Model") {
                    Text(prefs.system.embeddingModel)
                        .font(.system(size: 12, design: .monospaced))
                        .foregroundStyle(.secondary)
                }
                LabeledContent("Dimensions") {
                    Text("\(prefs.system.modelDimensions)")
                        .font(.system(size: 12, design: .monospaced))
                        .foregroundStyle(.secondary)
                }
                if prefs.system.ocrEnabled {
                    LabeledContent("OCR model") {
                        HStack(spacing: 6) {
                            Text(prefs.system.visionModel)
                                .font(.system(size: 12, design: .monospaced))
                                .foregroundStyle(.secondary)
                            if prefs.system.visionModelReachable {
                                StatusBadge(tone: .ok, label: "available")
                            } else {
                                StatusBadge(tone: .warn, label: "not pulled")
                            }
                        }
                    }
                    LabeledContent("Scanned PDFs") {
                        Text(prefs.system.ocrPending > 0
                            ? "\(prefs.system.ocrRecovered.formatted()) recovered · \(prefs.system.ocrPending.formatted()) queued"
                            : "\(prefs.system.ocrRecovered.formatted()) recovered")
                            .font(.system(size: 12, design: .monospaced))
                            .foregroundStyle(.secondary)
                    }
                }
            } header: { Text("Provider") } footer: {
                RowHint(text: "Embedding model + chunk size are pinned in appsettings.json. Changing the model requires `mailvec switch-model` (then `ops/redeploy.sh embedder`); changing chunk size requires `mailvec reindex --all`. Scanned-PDF OCR uses the vision model; pull it with `ollama pull \(prefs.system.visionModel)`.")
            }

            Section {
                HStack {
                    Button("Audit vectors") { runCli(["audit-embeddings"]) }
                    Spacer()
                    Button("Reindex all…", role: .destructive) {
                        showReindexAlert = true
                    }
                    .foregroundStyle(.red)
                }
                .listRowInsets(.init(top: 6, leading: 12, bottom: 6, trailing: 12))
            }
        }
        .confirmationDialog("Reindex all messages?",
                            isPresented: $showReindexAlert,
                            titleVisibility: .visible) {
            Button("Reindex (hours of work)", role: .destructive) {
                runCli(["reindex", "--all"])
            }
            Button("Cancel", role: .cancel) {}
        } message: {
            Text("Clears all embeddings and re-embeds every message. On Apple Silicon without a dedicated GPU this is overnight territory for a 6k-message archive. The indexer + FTS5 keep working while it runs.")
        }
    }

    private func coveragePct(_ s: SystemSnapshot) -> Int {
        s.coverageTotal == 0 ? 100 : Int(Double(s.coverageDone) / Double(s.coverageTotal) * 100)
    }

    private func runCli(_ args: [String]) {
        CliRunner.runInTerminal(args)
    }
}
