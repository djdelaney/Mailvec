// CliRunnerTests.swift
//
// CliRunner.runAppleScript executes osascript as a background subprocess
// (NSAppleScript is main-thread-only, and its synchronous execute used to
// beachball the menu bar from button handlers). These tests drive it with
// scripts that need no Automation permission: a pure-value script proves
// the success path off the main thread, and a raised error proves the
// failure text — including the -1743 code the permission-denied alert keys
// off — comes back through the completion.
import XCTest
@testable import Mailvec_Tray

final class CliRunnerTests: XCTestCase {
    func testSuccessfulScriptCompletesWithNilFailure() {
        let done = expectation(description: "completion")
        var failure: String? = "unset"
        CliRunner.runAppleScript("return 1") { result in
            failure = result
            XCTAssertTrue(Thread.isMainThread, "completion must hop back to the main queue")
            done.fulfill()
        }
        wait(for: [done], timeout: 15)
        XCTAssertNil(failure)
    }

    func testFailingScriptSurfacesTheErrorText() {
        let done = expectation(description: "completion")
        var failure: String?
        // The same error-number shape errAEEventNotPermitted produces; the
        // Automation-permission alert branches on "-1743" being present.
        CliRunner.runAppleScript("error \"not authorized\" number -1743") { result in
            failure = result
            done.fulfill()
        }
        wait(for: [done], timeout: 15)
        let text = try? XCTUnwrap(failure)
        XCTAssertNotNil(text, "a failing script must surface non-nil failure text")
        XCTAssertTrue(text?.contains("-1743") ?? false, "error number must survive into the failure text: \(text ?? "nil")")
    }
}
