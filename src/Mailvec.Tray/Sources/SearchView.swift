// SearchView.swift
import AppKit
import SwiftUI

struct SearchView: View {
    @EnvironmentObject var model: TrayModel
    @FocusState private var fieldFocused: Bool

    var body: some View {
        VStack(spacing: 0) {
            SlimHeader()
            SearchInput(text: $model.searchQuery, focused: $fieldFocused)
                .padding(.horizontal, 12).padding(.top, 10).padding(.bottom, 6)
                .onSubmit {
                    // Enter commits the query to recents AND opens the
                    // selected hit (if any) in Fastmail — matches the
                    // footer hint "↩ Open in Fastmail" / "↩ Open".
                    Task {
                        await model.commitSearch()
                        await MainActor.run { model.openSelectedInFastmail() }
                    }
                }
                .onChange(of: model.searchQuery) { _, _ in
                    Task { await model.runSearch() }
                }
                .onChange(of: model.mode) { _, _ in
                    Task { await model.runSearch() }
                }
                .onChange(of: model.folderFilter) { _, _ in
                    Task { await model.runSearch() }
                }
                .onChange(of: model.dateRange) { _, _ in
                    Task { await model.runSearch() }
                }
                // ↑ / ↓ navigate the result list while focus stays in the
                // search field. Returning .handled prevents the TextField
                // from interpreting the keys (a single-line TextField has
                // no use for vertical arrows anyway, but being explicit
                // here documents the intent).
                .onKeyPress(.upArrow) {
                    _ = model.selectPreviousHit()
                    return .handled
                }
                .onKeyPress(.downArrow) {
                    _ = model.selectNextHit()
                    return .handled
                }
            FilterRow()
                .padding(.horizontal, 12).padding(.bottom, 8)

            Group {
                if model.searchQuery.isEmpty {
                    EmptyState()
                } else {
                    ResultsList()
                }
            }
            // Taller search results area — gives room for ~10 visible rows
            // plus an expanded preview without crowding. macOS caps the
            // popover to fit screen, so the outer bound below is a ceiling
            // not a guaranteed size.
            .frame(maxHeight: 640)

            SearchFooter(hasSelection: model.searchSelection != nil)
        }
        .background(Brand.popoverBg)
        .frame(maxHeight: 820)
        .onAppear { fieldFocused = true }
        // The hotkey handler in MailvecTrayApp flips pendingSearchFocus; we
        // watch it here and bring the TextField back into focus even if the
        // popover was already open.
        .onChange(of: model.pendingSearchFocus) { _, want in
            if want {
                fieldFocused = true
                model.pendingSearchFocus = false
            }
        }
        .onExitCommand { model.pane = .dashboard }
    }

}

private struct SlimHeader: View {
    @EnvironmentObject var model: TrayModel
    private var brandIcon: Image {
        NSImage(named: "mailvec.mv-color") != nil
            ? Image("mailvec.mv-color")
            : Image(systemName: "tray.full.fill")
    }
    var body: some View {
        HStack(spacing: 10) {
            brandIcon.resizable().scaledToFit().frame(width: 16)
            Text("Mailvec").font(.system(size: 13, weight: .bold))
            Divider().frame(height: 12)
            Text(caption).font(.system(size: 11.5))
                .foregroundStyle(.white.opacity(0.65))
            Spacer()
            Button("esc") { model.pane = .dashboard }
                .buttonStyle(.borderless)
                .font(.system(size: 10, design: .monospaced))
                .foregroundStyle(.white.opacity(0.7))
        }
        .padding(.horizontal, 14).padding(.vertical, 10)
        .background(LinearGradient(colors: [Brand.bandTop, Brand.bandBottom],
                                   startPoint: .top, endPoint: .bottom))
        .foregroundStyle(Brand.bandText)
    }
    private var caption: String {
        if model.searchQuery.isEmpty {
            return "Search \(model.health?.embedded ?? 0) indexed messages"
        }
        return "\(model.searchHits.count) hits · hybrid · \(model.health?.embedded ?? 0) indexed"
    }
}

private struct SearchInput: View {
    @Binding var text: String
    var focused: FocusState<Bool>.Binding
    var body: some View {
        HStack(spacing: 8) {
            Image(systemName: "magnifyingglass").foregroundStyle(Brand.accent)
            TextField("Search your archive…", text: $text)
                .textFieldStyle(.plain)
                .focused(focused)
                .font(.system(size: 13))
            if !text.isEmpty {
                Button { text = "" } label: { Image(systemName: "xmark.circle.fill") }
                    .buttonStyle(.borderless).foregroundStyle(.secondary)
            }
        }
        .padding(.horizontal, 12).padding(.vertical, 9)
        .background(Brand.cardBg, in: RoundedRectangle(cornerRadius: 10))
        .overlay(RoundedRectangle(cornerRadius: 10)
            .stroke(focused.wrappedValue ? Brand.accent : Brand.hairline,
                    lineWidth: focused.wrappedValue ? 1 : 0.5))
    }
}

private struct FilterRow: View {
    @EnvironmentObject var model: TrayModel
    var body: some View {
        HStack(spacing: 4) {
            ForEach(SearchMode.allCases) { m in
                ModeChip(label: m.label, active: m == model.mode, accent: m == .hybrid)
                    .onTapGesture { model.mode = m }
            }
            Divider().frame(height: 14).padding(.horizontal, 4)
            folderMenu
            dateMenu
            Spacer()
        }
        .task { await model.loadFolders() }
    }

    private var folderMenu: some View {
        Menu {
            Button(action: { model.folderFilter = nil }) {
                if model.folderFilter == nil { Label("All folders", systemImage: "checkmark") }
                else { Text("All folders") }
            }
            if !model.availableFolders.isEmpty { Divider() }
            ForEach(model.availableFolders) { folder in
                Button(action: { model.folderFilter = folder.folder }) {
                    if model.folderFilter == folder.folder {
                        Label("\(folder.folder) (\(folder.messageCount))", systemImage: "checkmark")
                    } else {
                        Text("\(folder.folder) (\(folder.messageCount))")
                    }
                }
            }
        } label: {
            FilterChipContent(label: model.folderFilter ?? "All folders",
                              active: model.folderFilter != nil)
        }
        .menuStyle(.borderlessButton)
        .menuIndicator(.hidden)
        .fixedSize()
    }

    private var dateMenu: some View {
        Menu {
            ForEach(DateRange.allCases) { range in
                Button(action: { model.dateRange = range }) {
                    if model.dateRange == range {
                        Label(range.label, systemImage: "checkmark")
                    } else {
                        Text(range.label)
                    }
                }
            }
        } label: {
            FilterChipContent(label: model.dateRange.label,
                              active: model.dateRange != .allTime)
        }
        .menuStyle(.borderlessButton)
        .menuIndicator(.hidden)
        .fixedSize()
    }
}

/// Tappable replacement for the static FilterChip — same visual, but reads
/// "active" (non-default) state to tint the background and lift the label
/// weight. Used by the folder + date pickers in FilterRow.
private struct FilterChipContent: View {
    let label: String
    let active: Bool
    var body: some View {
        HStack(spacing: 3) {
            Text(label).lineLimit(1)
            Image(systemName: "chevron.down").font(.system(size: 8))
        }
        .font(.system(size: 10.5, weight: active ? .semibold : .regular))
        .padding(.horizontal, 7).padding(.vertical, 3)
        .background(active ? Brand.accent.opacity(0.12) : .black.opacity(0.04),
                    in: RoundedRectangle(cornerRadius: 6))
        .overlay(RoundedRectangle(cornerRadius: 6)
            .stroke(active ? Brand.accent.opacity(0.4) : Brand.hairline))
        .foregroundStyle(active ? Brand.accentDeep : .secondary)
    }
}

// MARK: Empty state

private struct EmptyState: View {
    @EnvironmentObject var model: TrayModel
    @State private var folders: [FolderRow] = []
    var body: some View {
        ScrollView {
            VStack(alignment: .leading, spacing: 10) {
                if !model.recentSearches.isEmpty {
                    Text("Recent searches").sectionHeader()
                        .padding(.horizontal, 14).padding(.top, 8)
                    FlowChips(items: model.recentSearches) { q in
                        model.searchQuery = q
                    }
                    .padding(.horizontal, 12)
                }

                Text("Folders").sectionHeader().padding(.horizontal, 14)
                LazyVGrid(columns: Array(repeating: GridItem(.flexible(), spacing: 4), count: 2),
                          spacing: 4) {
                    ForEach(folders.prefix(8)) { f in
                        FolderTile(name: f.folder, count: f.messageCount)
                    }
                }
                .padding(.horizontal, 12)

                HelperTip()
                    .padding(.horizontal, 12).padding(.bottom, 12)
            }
        }
        .task {
            if let resp = try? await MailvecClient.shared.folders() {
                folders = resp.folders.sorted { $0.messageCount > $1.messageCount }
            }
        }
    }
}

/// Tappable horizontal chip flow — used by the recent-searches list. A
/// LazyHGrid would clip; SwiftUI doesn't have a true flowing layout in pure
/// system API, so we use a simple HStack with truncation.
private struct FlowChips: View {
    let items: [String]
    let onTap: (String) -> Void
    var body: some View {
        ScrollView(.horizontal, showsIndicators: false) {
            HStack(spacing: 5) {
                ForEach(items, id: \.self) { item in
                    Button { onTap(item) } label: { RecentChip(text: item) }
                        .buttonStyle(.plain)
                }
            }
        }
    }
}

// MARK: Results

private struct ResultsList: View {
    @EnvironmentObject var model: TrayModel
    var body: some View {
        // ScrollViewReader wraps the inner ScrollView so arrow-key
        // navigation can call .scrollTo() and keep the selected row in
        // view. Each row's .id() matches its hit.id; the Reader is keyed
        // off model.searchSelection so it re-runs whenever the user moves
        // up/down.
        ScrollViewReader { proxy in
            ScrollView {
                LazyVStack(spacing: 1) {
                    ForEach(model.searchHits) { hit in
                        ResultRow(hit: hit, selected: hit.id == model.searchSelection)
                            .id(hit.id)
                    }
                }
                .padding(.horizontal, 6).padding(.vertical, 2)
            }
            .onChange(of: model.searchSelection) { _, newValue in
                guard let id = newValue else { return }
                withAnimation(.easeOut(duration: 0.15)) {
                    proxy.scrollTo(id, anchor: .center)
                }
            }
        }
    }
}

private struct ResultRow: View {
    @EnvironmentObject var model: TrayModel
    let hit: SearchHit
    let selected: Bool

    var body: some View {
        VStack(alignment: .leading, spacing: 0) {
            // Wrap the summary in a Button so the entire row — including the
            // gaps between text and around padding — is hit-testable. The
            // earlier `.onTapGesture` on an HStack only fired on hits over
            // a non-empty subview, which was the source of the "most of the
            // click area doesn't work" complaint. `.contentShape(Rectangle())`
            // ensures the Button covers the whole frame.
            Button {
                if selected {
                    model.searchSelection = nil
                } else {
                    model.searchSelection = hit.id
                }
            } label: {
                HStack(alignment: .top, spacing: 8) {
                    ScoreBar(score: hit.score)
                    VStack(alignment: .leading, spacing: 1) {
                        HStack(alignment: .firstTextBaseline) {
                            Text(hit.from).font(.system(size: 12, weight: .semibold))
                                .lineLimit(1)
                            Spacer()
                            Text(hit.date.formatted(.dateTime.month(.abbreviated).day()))
                                .font(.system(size: 10.5, design: .monospaced))
                                .foregroundStyle(.secondary)
                        }
                        HStack {
                            Text(hit.displaySubject).font(.system(size: 12))
                                .foregroundStyle(.secondary).lineLimit(1)
                            if hit.matchedAttachment != nil {
                                AttachmentChip()
                            }
                        }
                        HighlightedSnippet(snippet: hit.snippet)
                            .lineLimit(2)
                    }
                }
                .padding(.horizontal, 6).padding(.vertical, 8)
                .frame(maxWidth: .infinity, alignment: .leading)
                .contentShape(Rectangle())
            }
            .buttonStyle(.plain)

            if selected {
                ExpandedPreview(hit: hit)
                    .padding(.horizontal, 6).padding(.bottom, 8)
            }
        }
        .background(
            RoundedRectangle(cornerRadius: 9)
                .fill(selected ? Brand.accent.opacity(0.08) : .clear)
                .overlay(RoundedRectangle(cornerRadius: 9)
                    .stroke(selected ? Brand.accent.opacity(0.33) : .clear)))
    }
}

private struct ScoreBar: View {
    let score: Double
    var body: some View {
        GeometryReader { g in
            VStack(spacing: 0) {
                Rectangle().fill(Brand.accent)
                    .frame(height: g.size.height * score)
                Rectangle().fill(.black.opacity(0.06))
            }
        }
        .frame(width: 3).clipShape(Capsule())
    }
}

private struct HighlightedSnippet: View {
    let snippet: String
    var body: some View {
        Text(attributed)
            .font(.system(size: 11.5))
            .foregroundStyle(.secondary)
    }
    private var attributed: AttributedString {
        var out = AttributedString()
        var s = snippet[...]
        while let openRange = s.range(of: "<mark>") {
            out.append(AttributedString(s[..<openRange.lowerBound]))
            s = s[openRange.upperBound...]
            guard let closeRange = s.range(of: "</mark>") else {
                out.append(AttributedString(s)); break
            }
            var mark = AttributedString(s[..<closeRange.lowerBound])
            mark.backgroundColor = Brand.accent.opacity(0.20)
            mark.foregroundColor = Brand.accentDeep
            mark.font = .system(size: 11.5, weight: .semibold)
            out.append(mark)
            s = s[closeRange.upperBound...]
        }
        return out
    }
}

private struct ExpandedPreview: View {
    @EnvironmentObject var model: TrayModel
    let hit: SearchHit
    @State private var detail: EmailDetail?
    @State private var loading = false
    @State private var loadError: String?

    var body: some View {
        VStack(alignment: .leading, spacing: 0) {
            VStack(alignment: .leading, spacing: 1) {
                MetaLine(k: "from",   v: "<\(hit.fromEmail)>")
                MetaLine(k: "date",   v: hit.date.formatted(date: .long, time: .shortened))
                MetaLine(k: "folder", v: hit.folder)
                MetaLine(k: "score",
                    v: scoreString(hit: hit),
                    valueAccent: true)
            }
            .padding(.horizontal, 10).padding(.vertical, 7)
            .background(.black.opacity(0.025))
            .font(.system(size: 10.5, design: .monospaced))

            Divider()

            // Body — capped at maxHeight, with truncation rather than an
            // inner ScrollView. Nesting a ScrollView inside the outer
            // ResultsList ScrollView made trackpad gestures ambiguous (the
            // outer wins when you scroll inside the inner). For full-message
            // reading the user clicks "Open in Fastmail".
            bodySection
                .frame(maxHeight: 260, alignment: .topLeading)
                .clipped()

            if let att = hit.matchedAttachment, let file = att.fileName {
                Divider()
                AttachmentRow(hit: hit, attachment: att, file: file,
                              size: att.sizeHint ?? "")
            } else if let attachments = detail?.attachments, !attachments.isEmpty {
                Divider()
                ForEach(attachments) { att in
                    InlineAttachmentRow(hit: hit, attachment: att)
                }
            }

            // Action strip — only Open in Fastmail remains. Preview / Copy
            // link / Actions were never implemented and only existed in the
            // handoff design's mockup.
            HStack(spacing: 6) {
                Button("Open in Fastmail") {
                    FastmailLink.open(messageId: hit.messageId)
                }
                .buttonStyle(.borderedProminent).tint(Brand.accent)
                .controlSize(.small)
                Spacer()
            }
            .padding(.horizontal, 10).padding(.vertical, 6)
        }
        .background(Brand.cardBg, in: RoundedRectangle(cornerRadius: 8))
        .overlay(RoundedRectangle(cornerRadius: 8).stroke(Brand.hairline))
        // Fetch the body each time a row is freshly expanded. The .task
        // modifier re-runs when the view's identity changes (the parent
        // recreates ExpandedPreview for each row), which is exactly what
        // we want — no stale body bleed-through from a previously-selected row.
        .task(id: hit.id) {
            await loadBody()
        }
    }

    @ViewBuilder
    private var bodySection: some View {
        if loading {
            VStack {
                Spacer()
                ProgressView().controlSize(.small)
                Spacer()
            }
            .frame(maxWidth: .infinity)
        } else if let err = loadError {
            Text("Couldn't load preview: \(err)")
                .font(.system(size: 11))
                .foregroundStyle(.red)
                .padding(10)
        } else if let body = detail?.bodyText, !body.isEmpty {
            // No inner ScrollView — the outer ResultsList already scrolls,
            // and SwiftUI gets confused with nested scrollables. Truncate
            // by line count; for the full message the user clicks "Open in
            // Fastmail".
            Text(body)
                .font(.system(size: 11.5))
                .foregroundStyle(.primary)
                .textSelection(.enabled)
                .lineLimit(18, reservesSpace: false)
                .frame(maxWidth: .infinity, alignment: .topLeading)
                .padding(10)
        } else if detail?.hasHtml == true {
            Text("This message is HTML-only — open in Fastmail to view.")
                .font(.system(size: 11)).italic()
                .foregroundStyle(.secondary)
                .padding(10)
        } else {
            Text("(empty body)")
                .font(.system(size: 11)).italic()
                .foregroundStyle(.secondary)
                .padding(10)
        }
    }

    private func loadBody() async {
        loading = true
        loadError = nil
        do {
            detail = try await MailvecClient.shared.email(id: hit.id)
        } catch {
            loadError = error.localizedDescription
        }
        loading = false
    }
}

private struct InlineAttachmentRow: View {
    @EnvironmentObject var model: TrayModel
    let hit: SearchHit
    let attachment: EmailAttachmentRow
    var body: some View {
        HStack(spacing: 8) {
            Image(systemName: "paperclip")
                .foregroundStyle(.secondary).font(.system(size: 11))
            Text(attachment.fileName ?? "(unnamed)")
                .font(.system(size: 11.5)).lineLimit(1)
            Text(formatBytes(attachment.size))
                .font(.system(size: 10, design: .monospaced))
                .foregroundStyle(.secondary)
            Spacer()
            Button("Open") {
                model.openAttachment(messageId: hit.id, partIndex: attachment.partIndex)
            }
            .controlSize(.small)
        }
        .padding(.horizontal, 10).padding(.vertical, 5)
    }
    private func formatBytes(_ n: Int64) -> String {
        ByteCountFormatter.string(fromByteCount: n, countStyle: .file)
    }
}

private struct MetaLine: View {
    let k: String; let v: String; var valueAccent: Bool = false
    var body: some View {
        HStack(alignment: .firstTextBaseline) {
            Text(k).foregroundStyle(.secondary).frame(width: 44, alignment: .leading)
            Text(v).foregroundStyle(valueAccent ? Brand.accentDeep : .primary)
        }
    }
}

private struct AttachmentRow: View {
    @EnvironmentObject var model: TrayModel
    let hit: SearchHit
    let attachment: SearchHit.MatchedAttachment
    let file: String
    let size: String
    var body: some View {
        HStack(spacing: 10) {
            RoundedRectangle(cornerRadius: 3)
                .fill(LinearGradient(colors: [Brand.accent.opacity(0.16),
                                              Brand.accent.opacity(0.06)],
                                     startPoint: .top, endPoint: .bottom))
                .overlay(RoundedRectangle(cornerRadius: 3).stroke(Brand.accent.opacity(0.4)))
                .overlay(Text(extLabel).font(.system(size: 8, weight: .bold, design: .monospaced))
                    .foregroundStyle(Brand.accentDeep))
                .frame(width: 24, height: 30)
            VStack(alignment: .leading, spacing: 1) {
                Text(file).font(.system(size: 11.5)).lineLimit(1)
                Text(size.isEmpty ? "matched in extracted text" : "\(size) · matched in extracted text")
                    .font(.system(size: 10)).foregroundStyle(.secondary)
            }
            Spacer()
            Button("Open") {
                model.openAttachment(messageId: hit.id, partIndex: attachment.partIndex)
            }
            .buttonStyle(.borderedProminent).tint(Brand.accent)
            .controlSize(.small)
        }
        .padding(.horizontal, 10).padding(.vertical, 7)
    }
    private var extLabel: String {
        let ext = (file as NSString).pathExtension.uppercased()
        return ext.isEmpty ? "DOC" : ext
    }
}

/// Renders the bm25/vec score breakdown line for the expanded preview. Both
/// inner scores are optional (only one leg populates them in single-mode
/// searches), so we have to handle each combination.
private func scoreString(hit: SearchHit) -> String {
    switch (hit.bm25Score, hit.vectorScore) {
    case let (b?, v?): return String(format: "%.2f · bm25 %.2f · vec %.2f", hit.score, b, v)
    case let (b?, nil): return String(format: "%.2f · bm25 %.2f", hit.score, b)
    case let (nil, v?): return String(format: "%.2f · vec %.2f", hit.score, v)
    case (nil, nil):    return String(format: "%.2f · hybrid (RRF)", hit.score)
    }
}

private struct AttachmentChip: View {
    var body: some View {
        Text("pdf")
            .font(.system(size: 9.5, design: .monospaced))
            .padding(.horizontal, 5).padding(.vertical, 1)
            .background(Brand.accent.opacity(0.10),
                        in: RoundedRectangle(cornerRadius: 4))
            .overlay(RoundedRectangle(cornerRadius: 4)
                .stroke(Brand.accent.opacity(0.25)))
            .foregroundStyle(Brand.accentDeep)
    }
}

// MARK: Footer

private struct SearchFooter: View {
    let hasSelection: Bool
    var body: some View {
        HStack(spacing: 12) {
            FooterHint(key: "↑↓", label: "Move")
            FooterHint(key: "↩",  label: "Open in Fastmail", primary: hasSelection)
            Spacer()
        }
        .padding(.horizontal, 10).padding(.vertical, 6)
        .background(.black.opacity(0.015))
        .overlay(Rectangle().frame(height: 0.5)
            .foregroundStyle(Brand.hairline), alignment: .top)
    }
}
