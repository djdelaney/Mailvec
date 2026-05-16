// TrayLog.swift
//
// Lightweight logger for the tray app. Writes to two sinks:
//   1. ~/Library/Logs/Mailvec/mailvec-tray-YYYYMMDD.log — matches the .NET
//      services' Serilog convention so all four Mailvec processes have logs
//      in one folder. Daily-rolling filenames, no in-process rotation
//      (volume is tiny: a few lines per minute even with verbose logging).
//   2. Apple's unified logging system via `os.Logger` — surfaces in
//      Console.app under subsystem `com.mailvec.tray`. Great for live
//      tailing while debugging.
//
// Usage:
//   TrayLog.info("refresh ok", "messages=\(h.messages)")
//   TrayLog.warn("refresh failed", error: err)
//   TrayLog.error("decode failed", error: err)
import Foundation
import os

enum TrayLog {
    enum Level: String { case debug = "DEBUG", info = "INFO", warn = "WARN", error = "ERROR" }

    private static let osLogger = Logger(subsystem: "com.mailvec.tray", category: "tray")
    private static let queue = DispatchQueue(label: "com.mailvec.tray.log", qos: .utility)
    private static let dateFormatter: ISO8601DateFormatter = {
        let f = ISO8601DateFormatter()
        f.formatOptions = [.withInternetDateTime, .withFractionalSeconds]
        return f
    }()

    static func debug(_ event: String, _ detail: String = "") {
        write(.debug, event: event, detail: detail)
    }
    static func info(_ event: String, _ detail: String = "") {
        write(.info, event: event, detail: detail)
    }
    static func warn(_ event: String, _ detail: String = "", error: Error? = nil) {
        write(.warn, event: event, detail: combine(detail, error: error))
    }
    static func error(_ event: String, _ detail: String = "", error: Error? = nil) {
        write(.error, event: event, detail: combine(detail, error: error))
    }

    private static func combine(_ detail: String, error: Error?) -> String {
        guard let e = error else { return detail }
        let msg = (e as? LocalizedError)?.errorDescription ?? "\(e)"
        return detail.isEmpty ? msg : "\(detail) — \(msg)"
    }

    private static func write(_ level: Level, event: String, detail: String) {
        let line = "\(dateFormatter.string(from: Date())) [\(level.rawValue)] \(event)" +
                   (detail.isEmpty ? "" : ": \(detail)")
        // Mirror to unified log so Console.app can tail in real time.
        switch level {
        case .debug: osLogger.debug("\(line, privacy: .public)")
        case .info:  osLogger.info("\(line, privacy: .public)")
        case .warn:  osLogger.warning("\(line, privacy: .public)")
        case .error: osLogger.error("\(line, privacy: .public)")
        }
        // Append to the rolling file. Async via the dedicated queue so the
        // caller doesn't pay the disk-write cost on the main thread.
        queue.async { appendToFile(line) }
    }

    private static func appendToFile(_ line: String) {
        guard let url = currentLogFileURL() else { return }
        let bytes = (line + "\n").data(using: .utf8) ?? Data()
        let dir = url.deletingLastPathComponent()
        try? FileManager.default.createDirectory(at: dir, withIntermediateDirectories: true)
        if FileManager.default.fileExists(atPath: url.path) {
            if let handle = try? FileHandle(forWritingTo: url) {
                defer { try? handle.close() }
                try? handle.seekToEnd()
                try? handle.write(contentsOf: bytes)
            }
        } else {
            try? bytes.write(to: url)
        }
    }

    private static func currentLogFileURL() -> URL? {
        let f = DateFormatter()
        f.dateFormat = "yyyyMMdd"
        f.timeZone = .current
        let stamp = f.string(from: Date())
        let dir = ("~/Library/Logs/Mailvec/" as NSString).expandingTildeInPath
        return URL(fileURLWithPath: dir).appendingPathComponent("mailvec-tray-\(stamp).log")
    }
}
