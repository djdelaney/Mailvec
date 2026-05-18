// WireDecoderTests.swift
//
// Edge cases for the ISO 8601 fractional-second normaliser. The .NET
// DateTimeOffset.ToString("O") emits anywhere from 0 to 7 fractional digits;
// Foundation's ISO8601DateFormatter only accepts exactly 3 (or 0). Each
// observed case here mirrors a real format the server has emitted.
import XCTest
@testable import Mailvec_Tray

final class WireDecoderTests: XCTestCase {

    // MARK: - normaliseFractionalSeconds edge cases

    func test_normaliseFractionalSeconds_truncates_seven_digits_to_three() {
        XCTAssertEqual(
            WireDecoder.normaliseFractionalSeconds("2026-05-14T19:03:42.9092057+00:00"),
            "2026-05-14T19:03:42.909+00:00")
    }

    func test_normaliseFractionalSeconds_truncates_six_digits_to_three() {
        XCTAssertEqual(
            WireDecoder.normaliseFractionalSeconds("2026-05-14T19:03:42.909205+00:00"),
            "2026-05-14T19:03:42.909+00:00")
    }

    func test_normaliseFractionalSeconds_pads_one_digit_to_three() {
        XCTAssertEqual(
            WireDecoder.normaliseFractionalSeconds("2026-05-14T19:03:42.9+00:00"),
            "2026-05-14T19:03:42.900+00:00")
    }

    func test_normaliseFractionalSeconds_pads_two_digits_to_three() {
        XCTAssertEqual(
            WireDecoder.normaliseFractionalSeconds("2026-05-14T19:03:42.91+00:00"),
            "2026-05-14T19:03:42.910+00:00")
    }

    func test_normaliseFractionalSeconds_leaves_three_digits_unchanged() {
        XCTAssertEqual(
            WireDecoder.normaliseFractionalSeconds("2026-05-14T19:03:42.123+00:00"),
            "2026-05-14T19:03:42.123+00:00")
    }

    func test_normaliseFractionalSeconds_leaves_no_fraction_unchanged() {
        // No dot means no fractional part — return as-is. The decoder's
        // fallback formatter handles parsing this shape.
        XCTAssertEqual(
            WireDecoder.normaliseFractionalSeconds("2026-05-14T19:03:42+00:00"),
            "2026-05-14T19:03:42+00:00")
    }

    func test_normaliseFractionalSeconds_handles_Z_suffix() {
        XCTAssertEqual(
            WireDecoder.normaliseFractionalSeconds("2026-05-14T19:03:42.9092057Z"),
            "2026-05-14T19:03:42.909Z")
    }

    func test_normaliseFractionalSeconds_handles_negative_offset() {
        XCTAssertEqual(
            WireDecoder.normaliseFractionalSeconds("2026-05-14T19:03:42.999999-05:00"),
            "2026-05-14T19:03:42.999-05:00")
    }

    // MARK: - JSONDecoder integration

    func test_decoder_parses_seven_digit_fractional_seconds() throws {
        let json = #"{"t":"2026-05-14T19:03:42.9092057+00:00"}"#.data(using: .utf8)!
        let decoded = try WireDecoder.make().decode(SingleDate.self, from: json)
        XCTAssertNotNil(decoded.t)
        // The C# server's stamp truncated to ms precision lands at 909ms past 19:03:42 UTC.
        // Foundation's Date is a Double seconds-since-2001; round-tripping
        // 909 ms through the string normaliser → Date → nanosecondsOfSecond
        // can land at 908ms-of-second due to FP rounding. Allow ±2 ms.
        let cal = Calendar(identifier: .gregorian)
        let c = cal.dateComponents(in: TimeZone(secondsFromGMT: 0)!, from: decoded.t)
        XCTAssertEqual(c.hour, 19)
        XCTAssertEqual(c.minute, 3)
        XCTAssertEqual(c.second, 42)
        let ms = c.nanosecond! / 1_000_000
        XCTAssertTrue(abs(ms - 909) <= 2, "expected ms ≈ 909, got \(ms)")
    }

    func test_decoder_parses_no_fraction_format() throws {
        let json = #"{"t":"2026-05-14T19:03:42+00:00"}"#.data(using: .utf8)!
        let decoded = try WireDecoder.make().decode(SingleDate.self, from: json)
        XCTAssertNotNil(decoded.t)
    }

    func test_decoder_throws_on_garbage_timestamp() {
        let json = #"{"t":"not-a-date"}"#.data(using: .utf8)!
        XCTAssertThrowsError(try WireDecoder.make().decode(SingleDate.self, from: json)) { error in
            // DecodingError.dataCorrupted is wrapped through the .custom strategy.
            guard case DecodingError.dataCorrupted = error else {
                return XCTFail("Expected dataCorrupted, got \(error)")
            }
        }
    }

    private struct SingleDate: Codable {
        var t: Date
    }
}
