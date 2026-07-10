// CliRunner.swift
//
// Shared helper for tray buttons that shell out to the `mailvec` CLI
// (Doctor, Reindex, Checkpoint, Audit, Purge, etc.). Resolves the binary
// from the install paths set up by ops/install.sh and spawns it inside
// Terminal so the user sees streaming output for long-running commands.
//
// The lookup order matches what ops/install.sh creates:
//   ~/.local/bin/mailvec  ← shim script installed by ops/install.sh
//   /usr/local/bin/mailvec ← user-added symlink if they prefer it on the
//                            default-PATH location (we don't put it there
//                            because doing so cleanly requires sudo).
//
// If neither exists we fall back to "mailvec" (relying on $PATH), so a
// user who installed it elsewhere isn't blocked.
import AppKit
import Foundation

enum CliRunner {
    /// Opens Terminal and runs `mailvec <args>` interactively. Best UX for
    /// long-running destructive operations (Reindex / Rebuild FTS5) where
    /// the user wants to see progress and confirm completion.
    ///
    /// `do script` alone leaves Terminal wherever it was in the window
    /// stack — for menu-bar accessory apps that means the spawned window
    /// often opens *behind* whatever the user was focused on, and the
    /// destructive-action workflow silently appears to do nothing. The
    /// explicit `activate` brings Terminal forward; tested with Terminal
    /// already running, not running, and with multiple windows open.
    static func runInTerminal(_ args: [String]) {
        guard let binary = installedBinary() else {
            // Spawning the bare name "mailvec" here used to open a Terminal
            // window showing only `zsh: command not found: mailvec` — a dead
            // end behind eight tray buttons. Explain instead.
            showAlert(
                title: "mailvec CLI not found",
                text: "Looked in ~/.local/bin and /usr/local/bin. "
                    + "Run ops/install.sh (first install) or ops/redeploy.sh cli from the Mailvec repo to install it. "
                    + "If you keep the CLI somewhere custom, symlink it to /usr/local/bin/mailvec.")
            return
        }
        let cmd = ([binary] + args)
            // Escape backslashes before double-quotes so a literal backslash in an
            // arg survives shell double-quoting instead of consuming the next char.
            .map { "\"\($0.replacingOccurrences(of: "\\", with: "\\\\").replacingOccurrences(of: "\"", with: "\\\""))\"" }
            .joined(separator: " ")
        let script = """
        tell application "Terminal"
            do script \(asAppleScriptString(cmd))
            activate
        end tell
        """
        runAppleScript(script) { failure in
            guard let failure else { return }
            TrayLog.warn("CLI Terminal spawn failed", failure)
            // Surface the failure — previously log-only, so a user who denied
            // the Automation prompt once had every CLI button silently do
            // nothing forever. -1743 = errAEEventNotPermitted.
            if failure.contains("-1743") {
                showAlert(
                    title: "Automation permission needed",
                    text: "macOS blocked Mailvec from opening Terminal. "
                        + "Allow it under System Settings → Privacy & Security → Automation → Mailvec.Tray → Terminal, then try again.")
            } else {
                showAlert(title: "Couldn't open Terminal", text: failure)
            }
        }
    }

    /// Runs an AppleScript source via `/usr/bin/osascript` on a background
    /// queue, calling `completion` on the main queue with nil on success or
    /// the failure text otherwise.
    ///
    /// Why a subprocess and not NSAppleScript: NSAppleScript is
    /// main-thread-only per Apple's thread-safety rules, and
    /// executeAndReturnError is synchronous — from a button handler, a cold
    /// Terminal launch blocked the menu bar for ~1-2s and a wedged target
    /// app beachballed the tray for the full Apple Events timeout (~2 min).
    /// osascript carries the same TCC Automation attribution (the tray is
    /// the responsible process), so the permission prompts are unchanged.
    static func runAppleScript(_ source: String, completion: ((String?) -> Void)? = nil) {
        DispatchQueue.global(qos: .userInitiated).async {
            let proc = Process()
            proc.executableURL = URL(fileURLWithPath: "/usr/bin/osascript")
            proc.arguments = ["-e", source]
            let stderrPipe = Pipe()
            proc.standardError = stderrPipe
            proc.standardOutput = FileHandle.nullDevice
            var failure: String?
            do {
                try proc.run()
                // Read to EOF BEFORE waitUntilExit so stderr can't fill the
                // pipe and deadlock against our wait.
                let errData = stderrPipe.fileHandleForReading.readDataToEndOfFile()
                proc.waitUntilExit()
                if proc.terminationStatus != 0 {
                    let text = String(data: errData, encoding: .utf8)?
                        .trimmingCharacters(in: .whitespacesAndNewlines) ?? ""
                    failure = text.isEmpty ? "osascript exited \(proc.terminationStatus)." : text
                }
            } catch {
                failure = "osascript failed to launch: \(error.localizedDescription)"
            }
            if let completion {
                DispatchQueue.main.async { completion(failure) }
            } else if let failure {
                TrayLog.warn("AppleScript failed", failure)
            }
        }
    }

    /// Absolute path to the `mailvec` shim, or nil when it isn't installed
    /// in either location ops/install.sh (or a user symlink) would put it.
    static func installedBinary() -> String? {
        let candidates = [
            ("~/.local/bin/mailvec" as NSString).expandingTildeInPath,
            "/usr/local/bin/mailvec",
        ]
        return candidates.first { FileManager.default.isExecutableFile(atPath: $0) }
    }

    private static func showAlert(title: String, text: String) {
        NSApp.activate(ignoringOtherApps: true)
        let alert = NSAlert()
        alert.messageText = title
        alert.informativeText = text
        alert.alertStyle = .warning
        alert.runModal()
    }

    private static func asAppleScriptString(_ s: String) -> String {
        // Wrap the inner shell command as an AppleScript string literal.
        // Backslash and double-quote are both special inside an AppleScript
        // string literal, so escape backslashes first, then quotes.
        "\"\(s.replacingOccurrences(of: "\\", with: "\\\\").replacingOccurrences(of: "\"", with: "\\\""))\""
    }
}
