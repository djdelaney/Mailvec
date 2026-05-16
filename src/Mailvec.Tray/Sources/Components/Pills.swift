// Components/Pills.swift
import SwiftUI

struct ModeChip: View {
    let label: String
    let active: Bool
    var body: some View {
        Text(label)
            .font(.system(size: 10.5, weight: active ? .semibold : .medium))
            .padding(.horizontal, 8).padding(.vertical, 3)
            .foregroundStyle(active ? .white : .primary)
            .background(active ? Brand.accent : .black.opacity(0.05),
                        in: Capsule())
    }
}

struct FilterChip: View {
    let label: String
    var body: some View {
        HStack(spacing: 3) {
            Text(label)
            Image(systemName: "chevron.down").font(.system(size: 8))
        }
        .font(.system(size: 10.5))
        .padding(.horizontal, 7).padding(.vertical, 3)
        .background(.black.opacity(0.04), in: RoundedRectangle(cornerRadius: 6))
        .overlay(RoundedRectangle(cornerRadius: 6).stroke(Brand.hairline))
        .foregroundStyle(.secondary)
    }
}

struct FooterHint: View {
    let key: String; let label: String; var primary: Bool = false
    var body: some View {
        HStack(spacing: 4) {
            Text(key)
                .font(.system(size: 9.5, design: .monospaced))
                .padding(.horizontal, 4).padding(.vertical, 1)
                .frame(minWidth: 12)
                .foregroundStyle(primary ? .white : .primary)
                .background(primary ? Brand.accent : .black.opacity(0.06),
                            in: RoundedRectangle(cornerRadius: 3))
                .overlay(RoundedRectangle(cornerRadius: 3)
                    .stroke(primary ? .clear : .black.opacity(0.10)))
            Text(label)
                .font(.system(size: 10.5, weight: primary ? .semibold : .regular))
                .foregroundStyle(primary ? Brand.accentDeep : .primary)
        }
    }
}

struct RecentChip: View {
    let text: String
    var body: some View {
        HStack(spacing: 4) {
            Image(systemName: "clock").font(.system(size: 9))
                .foregroundStyle(.secondary)
            Text(text).font(.system(size: 11.5))
        }
        .padding(.horizontal, 9).padding(.vertical, 4)
        .background(Brand.cardBg, in: Capsule())
        .overlay(Capsule().stroke(Brand.hairline))
    }
}

struct FolderTile: View {
    let name: String; let count: Int
    var body: some View {
        HStack {
            HStack(spacing: 5) {
                Image(systemName: "folder").font(.system(size: 10))
                    .foregroundStyle(.secondary)
                Text(name).font(.system(size: 11, design: .monospaced))
            }
            Spacer()
            Text(count.formatted())
                .font(.system(size: 10.5))
                .monospacedDigit()
                .foregroundStyle(.secondary)
        }
        .padding(.horizontal, 9).padding(.vertical, 5)
        .background(Brand.cardBg, in: RoundedRectangle(cornerRadius: 7))
        .overlay(RoundedRectangle(cornerRadius: 7).stroke(Brand.hairline))
    }
}

struct HelperTip: View {
    var body: some View {
        HStack(alignment: .top, spacing: 8) {
            Image(systemName: "info.circle").font(.system(size: 11))
                .foregroundStyle(.secondary)
            Text("Hybrid search blends BM25 keyword and semantic similarity (RRF, k=60). Narrow results with the folder and date pickers above.")
                .font(.system(size: 11))
                .foregroundStyle(.secondary)
        }
        .padding(.horizontal, 10).padding(.vertical, 8)
        .background(.black.opacity(0.025), in: RoundedRectangle(cornerRadius: 8))
        .overlay(RoundedRectangle(cornerRadius: 8).stroke(Brand.hairline))
    }
}
