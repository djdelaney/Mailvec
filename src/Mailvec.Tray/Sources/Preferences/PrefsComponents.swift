// Preferences/PrefsComponents.swift
import SwiftUI

struct Banner: View {
    enum Tone { case ok, warn, error }
    let tone: Tone
    let title: String
    // Renamed from `body` to avoid colliding with SwiftUI's View.body
    // requirement. Callsites pass it as `body:` via the explicit init below.
    var caption: String? = nil
    var action: (() -> Void)? = nil
    var actionLabel: String = "Action"

    init(tone: Tone,
         title: String,
         body: String? = nil,
         action: (() -> Void)? = nil,
         actionLabel: String = "Action") {
        self.tone = tone
        self.title = title
        self.caption = body
        self.action = action
        self.actionLabel = actionLabel
    }

    var body: some View {
        HStack(alignment: .top, spacing: 10) {
            iconCircle
            VStack(alignment: .leading, spacing: 2) {
                Text(title).font(.system(size: 13, weight: .semibold))
                if let caption {
                    Text(caption).font(.system(size: 11.5))
                        .foregroundStyle(.secondary)
                }
            }
            Spacer(minLength: 8)
            if let action {
                Button(actionLabel, action: action)
                    .buttonStyle(.borderedProminent).tint(Brand.accent)
                    .controlSize(.small)
            }
        }
        .padding(10)
        .background(toneBg, in: RoundedRectangle(cornerRadius: 9))
        .overlay(RoundedRectangle(cornerRadius: 9).stroke(toneStroke))
    }

    private var iconCircle: some View {
        ZStack {
            Circle().fill(toneFill).frame(width: 18, height: 18)
            switch tone {
            case .ok:    Image(systemName: "checkmark").font(.system(size: 9, weight: .bold))
            case .warn:  Text("!").font(.system(size: 11, weight: .bold))
            case .error: Text("!").font(.system(size: 11, weight: .bold))
            }
        }
        .foregroundStyle(.white)
    }

    private var toneFill: Color {
        switch tone { case .ok: .green; case .warn: .orange; case .error: .red }
    }
    private var toneBg: Color { toneFill.opacity(0.10) }
    private var toneStroke: Color { toneFill.opacity(0.32) }
}

struct StatusBadge: View {
    enum Tone { case ok, warn, error, info }
    let tone: Tone
    let label: String

    var body: some View {
        HStack(spacing: 5) {
            Circle().fill(color).frame(width: 5, height: 5)
            Text(label).font(.system(size: 10.5, weight: .medium))
        }
        .padding(.horizontal, 7).padding(.vertical, 1)
        .background(color.opacity(0.14), in: Capsule())
        .foregroundStyle(color)
    }
    // System colors (adaptive), matching the sibling component's `toneFill`
    // above. The Preferences window follows the system appearance, so fixed
    // dark-for-light RGB values dimmed against the dark grouped-form background
    // in Dark Mode — these brighten automatically instead.
    private var color: Color {
        switch tone {
        case .ok:    .green
        case .warn:  .orange
        case .error: .red
        case .info:  .blue
        }
    }
}

struct PathField: View {
    let path: String

    var body: some View {
        HStack(spacing: 6) {
            Text(path)
                .font(.system(size: 11.5, design: .monospaced))
                .lineLimit(1).truncationMode(.middle)
                .padding(.horizontal, 7).padding(.vertical, 3)
                .frame(maxWidth: .infinity, alignment: .leading)
                // System-adaptive: textBackgroundColor / separatorColor render
                // appropriately in both light and dark modes. Hardcoded
                // Color.white / Color.black would be jarring once we let the
                // Settings window pick up the system appearance.
                .background(Color(nsColor: .textBackgroundColor), in: RoundedRectangle(cornerRadius: 5))
                .overlay(RoundedRectangle(cornerRadius: 5)
                    .stroke(Color(nsColor: .separatorColor)))

            Button { reveal() } label: { Image(systemName: "magnifyingglass") }
                .help("Reveal in Finder").buttonStyle(.borderless)
        }
    }

    private func reveal() {
        let expanded = (path as NSString).expandingTildeInPath
        NSWorkspace.shared.selectFile(expanded, inFileViewerRootedAtPath: "")
    }
}

struct RowHint: View {
    let text: String
    var body: some View {
        Text(text).font(.system(size: 11)).foregroundStyle(.secondary)
    }
}
