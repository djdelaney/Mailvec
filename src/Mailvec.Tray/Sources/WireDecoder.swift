// WireDecoder.swift
//
// Factory for the JSONDecoder the tray uses to decode the .NET server's
// /tray/* responses. Lives in its own file (rather than as a static on
// MailvecClient) so the unit-test target can import the same decoder
// without depending on the URLSession plumbing in MailvecClient.
//
// The .NET server emits DateTimeOffset.ToString("O") which is ISO 8601
// with arbitrary-precision fractional seconds. Foundation's
// ISO8601DateFormatter only accepts millisecond precision, so we
// normalise the fractional part to exactly three digits before parsing.
// Falls through to the no-fraction formatter for timestamps that don't
// have a fractional component at all.
import Foundation

enum WireDecoder {
    /// Builds a JSONDecoder configured for the tray's wire format. Same
    /// instance shape MailvecClient uses in production — tests use this
    /// to round-trip fixture JSON without booting the URLSession actor.
    static func make() -> JSONDecoder {
        let d = JSONDecoder()
        d.dateDecodingStrategy = .custom { dec in
            let s = try dec.singleValueContainer().decode(String.self)
            let normalised = normaliseFractionalSeconds(s)
            if let parsed = isoFormatter.date(from: normalised) { return parsed }
            if let parsed = isoFormatterNoFraction.date(from: s) { return parsed }
            throw DecodingError.dataCorruptedError(
                in: try dec.singleValueContainer(),
                debugDescription: "Unparseable date '\(s)'")
        }
        return d
    }

    /// Trims the fractional-seconds portion of an ISO 8601 timestamp to
    /// exactly three digits, which is what `ISO8601DateFormatter` accepts.
    /// Leaves timestamps without a fractional part untouched. Internal so
    /// the test target can exercise the edge cases directly.
    ///
    /// Examples:
    ///   "2026-05-14T19:03:42.909205+00:00" → "2026-05-14T19:03:42.909+00:00"
    ///   "2026-05-14T19:03:42.9+00:00"       → "2026-05-14T19:03:42.900+00:00"
    ///   "2026-05-14T19:03:42+00:00"         → "2026-05-14T19:03:42+00:00"
    static func normaliseFractionalSeconds(_ s: String) -> String {
        guard let dotRange = s.range(of: ".") else { return s }
        let fracStart = dotRange.upperBound
        var fracEnd = fracStart
        while fracEnd < s.endIndex, s[fracEnd].isNumber {
            fracEnd = s.index(after: fracEnd)
        }
        let fraction = s[fracStart..<fracEnd]
        let normalisedFraction: String
        if fraction.count >= 3 {
            normalisedFraction = String(fraction.prefix(3))
        } else {
            normalisedFraction = fraction + String(repeating: "0", count: 3 - fraction.count)
        }
        return s[..<fracStart] + normalisedFraction + s[fracEnd...]
    }
}

private let isoFormatter: ISO8601DateFormatter = {
    let f = ISO8601DateFormatter()
    f.formatOptions = [.withInternetDateTime, .withFractionalSeconds]
    return f
}()

private let isoFormatterNoFraction: ISO8601DateFormatter = {
    let f = ISO8601DateFormatter()
    f.formatOptions = [.withInternetDateTime]
    return f
}()
