// Brand.swift
import SwiftUI

enum Brand {
    static let accent      = Color(red: 0.925, green: 0.396, blue: 0.212)  // #EC6536
    static let accentDeep  = Color(red: 0.816, green: 0.306, blue: 0.133)  // #D04E22

    // Status tints for the dark header band. These are FIXED (not system
    // `.green`/`.orange`/`.red`) on purpose: the system colors are NSColor-
    // backed and resolve against the window's appearance at draw time, so they
    // dim to their light-mode variants against the permanently-dark band. These
    // are the bright dark-mode-tuned values so the "All clear" pill / coverage
    // ring stay legible regardless of system appearance. Brighten here if a
    // future band tweak needs more contrast — don't reach back for `.green`.
    static let statusOk    = Color(red: 0.243, green: 0.886, blue: 0.412)  // #3EE269
    static let statusWarn  = Color(red: 1.000, green: 0.655, blue: 0.149)  // #FFA726
    static let statusError = Color(red: 1.000, green: 0.345, blue: 0.298)  // #FF584C

    static let bandTop     = Color(red: 0.122, green: 0.102, blue: 0.078)  // #1f1a14
    static let bandBottom  = Color(red: 0.078, green: 0.067, blue: 0.051)  // #14110d

    static let popoverBg   = Color(red: 0.984, green: 0.976, blue: 0.961)  // #fbf9f5
    static let cardBg      = Color.white
    static let hairline    = Color.black.opacity(0.08)

    static let bandText    = Color(red: 0.961, green: 0.945, blue: 0.910)

    static let mono = Font.system(size: 11, design: .monospaced)
    static let label = Font.system(size: 11.5, weight: .regular)
    static let sectionLabel = Font.system(size: 10.5, weight: .semibold)
}

extension View {
    /// 10.5pt all-caps section label used across the dashboard.
    func sectionHeader() -> some View {
        self.font(Brand.sectionLabel)
            .textCase(.uppercase)
            .tracking(0.5)
            .foregroundStyle(.secondary)
    }
}
