// Models.swift
//
// Wire-format DTOs returned by Mailvec.Mcp's /tray/* REST endpoints.
// Field names mirror the C# records in src/Mailvec.Core/Tray/TrayModels.cs
// exactly — keep them in lockstep or decoding silently produces nil fields.
import Foundation

/// Mirrors TrayStatus in TrayModels.cs.
struct TrayHealth: Codable, Equatable {
    enum Severity: String, Codable { case ok, syncing, warn, error }

    var severity: Severity
    var messages: Int
    var deleted: Int
    var embedded: Int
    var embedTotal: Int
    var chunks: Int
    var lastIndexedAt: Date?
    var lastSyncAt: Date?
    var dbSizeBytes: Int64
    var schemaVersion: String
    var services: [ServiceStatus]
    var ollama: OllamaStatus
    // Optional so a tray build that's newer than the MCP server (or vice versa)
    // still decodes the rest of the payload instead of failing wholesale.
    var ocr: OCRStatus?
    var progress: EmbedProgress?
    var recentEvents: [TimelineEvent]
    var sparkline: [Int]

    var embedCoverage: Double {
        guard embedTotal > 0 else { return 1.0 }
        if embedded >= embedTotal { return 1.0 }
        // Cap below 1.0 while any message is still embedding so a near-complete
        // fraction (e.g. 74027/74029 = 99.997%) can't be displayed as "100%".
        return min(Double(embedded) / Double(embedTotal), 0.999)
    }
}

struct ServiceStatus: Codable, Equatable, Identifiable {
    var id: String
    var detail: String
    var ok: Bool
    var busy: Bool
    var severity: TrayHealth.Severity?
}

struct OllamaStatus: Codable, Equatable {
    var ok: Bool
    var detail: String
    var severity: TrayHealth.Severity?
}

/// Mirrors TrayOcrStatus in TrayModels.cs — the scanned-PDF OCR stage.
struct OCRStatus: Codable, Equatable {
    var enabled: Bool
    var visionModel: String
    var modelAvailable: Bool?
    var pending: Int
    var recovered: Int
    var severity: TrayHealth.Severity?

    /// Show a dashboard card only when there's something actionable: the model
    /// isn't installed, or there's a live OCR backlog. A healthy idle stage
    /// stays silent (matching the CLI, which only prints "OCR pending" when >0).
    var shouldSurface: Bool {
        guard enabled else { return false }
        return modelAvailable == false || pending > 0
    }

    var modelMissing: Bool { enabled && modelAvailable == false }
}

struct EmbedProgress: Codable, Equatable {
    var done: Int
    var total: Int
    var ratePerMinute: Int
    var etaMinutes: Int
}

struct TimelineEvent: Codable, Equatable, Identifiable {
    enum Kind: String, Codable { case sync, indexed, embed, error }
    // The server doesn't ship an id; synthesise one client-side keyed off
    // (time, agent, text) so ForEach is stable across refreshes.
    var id: String { "\(time.timeIntervalSince1970)-\(agent)-\(text.hashValue)" }
    var time: Date
    var kind: Kind
    var text: String
    var agent: String
    var live: Bool
    var severity: TrayHealth.Severity?
}

// MARK: - Search

/// Mirrors TraySearchHit in TrayModels.cs.
struct SearchHit: Identifiable, Codable, Equatable {
    var id: Int
    var messageId: String
    var folder: String
    var subject: String?
    var fromAddress: String?
    var fromName: String?
    var dateSent: Date?
    var snippet: String
    var score: Double
    var bm25Score: Double?
    var vectorScore: Double?
    var matchedAttachment: MatchedAttachment?
    var webmailUrl: String?

    struct MatchedAttachment: Codable, Equatable {
        var partIndex: Int
        var fileName: String?
        var sizeHint: String?
    }

    // Display helpers used by the SwiftUI views (kept here so views stay
    // declarative).
    var from: String { fromName ?? fromAddress ?? "(unknown)" }
    var fromEmail: String { fromAddress ?? "" }
    var date: Date { dateSent ?? Date.distantPast }
    var displaySubject: String { subject ?? "(no subject)" }
    var matchSource: MatchSource {
        matchedAttachment == nil ? .body : .attachment
    }

    enum MatchSource: String { case body, attachment }
}

/// Mirrors TraySearchResponse in TrayModels.cs.
struct SearchResponse: Codable, Equatable {
    var query: String?
    var mode: String
    var count: Int
    var results: [SearchHit]
}

enum SearchMode: String, CaseIterable, Identifiable, Codable {
    case hybrid, keyword, semantic
    var id: String { rawValue }
    var label: String { rawValue.capitalized }
}

/// Coarse-grained date filter for the search popover. The dashboard's
/// "Hide" button on the date chip maps to `allTime`; specific windows map
/// to a lower bound computed against the current calendar.
enum DateRange: String, CaseIterable, Identifiable, Codable {
    case allTime
    case last7Days
    case last30Days
    case last3Months
    case last6Months
    case last12Months
    case last5Years

    var id: String { rawValue }
    var label: String {
        switch self {
        case .allTime:      return "All time"
        case .last7Days:    return "Last 7 days"
        case .last30Days:   return "Last 30 days"
        case .last3Months:  return "Last 3 months"
        case .last6Months:  return "Last 6 months"
        case .last12Months: return "Last 12 months"
        case .last5Years:   return "Last 5 years"
        }
    }
    /// Lower bound passed to the server as `dateFrom`. nil = unbounded.
    var dateFrom: Date? {
        let cal = Calendar.current
        switch self {
        case .allTime:      return nil
        case .last7Days:    return cal.date(byAdding: .day,   value: -7,  to: Date())
        case .last30Days:   return cal.date(byAdding: .day,   value: -30, to: Date())
        case .last3Months:  return cal.date(byAdding: .month, value: -3,  to: Date())
        case .last6Months:  return cal.date(byAdding: .month, value: -6,  to: Date())
        case .last12Months: return cal.date(byAdding: .year,  value: -1,  to: Date())
        case .last5Years:   return cal.date(byAdding: .year,  value: -5,  to: Date())
        }
    }
}

// MARK: - System / Preferences

/// Mirrors TraySystem in TrayModels.cs. Used by the Preferences tabs.
struct TraySystem: Codable, Equatable {
    var maildirRoot: String
    var mbsyncrcPath: String
    var mbsyncSchedule: String
    var imapHost: String
    var imapUser: String
    var lastSyncRelative: String?
    var lastSyncDetail: String?
    var nextSyncRelative: String?

    var dbPath: String
    var dbSize: String
    var schemaVersion: String
    var vecDylibVersion: String

    var ollamaEndpoint: String
    var ollamaReachable: Bool
    var ollamaPingMs: Int
    var embeddingModel: String
    var modelDimensions: Int
    var schemaModelMatches: Bool
    var coverageDone: Int
    var coverageTotal: Int

    // OCR (vision) stage. Optional for tray/server version skew.
    var ocrEnabled: Bool?
    var visionModel: String?
    var visionModelReachable: Bool?
    var ocrRecovered: Int?
    var ocrPending: Int?

    var mcpHttpEnabled: Bool
    var mcpBindAddress: String
    var mcpPort: Int
    var mcpbInstalled: Bool
    var mcpbVersion: String?
    var attachmentDownloadDir: String

    var softDeletedCount: Int
}

// MARK: - Control

struct ControlResponse: Codable, Equatable {
    var ok: Bool
    var detail: String
}

// MARK: - Attachment

struct AttachmentResponse: Codable, Equatable {
    var path: String
    var bytes: Int64
    var contentType: String
    var wasReused: Bool
}

// MARK: - Email preview

/// Mirrors TrayEmail in TrayModels.cs. Returned by /tray/email/{id} and
/// consumed by SearchView's expanded preview.
struct EmailDetail: Codable, Equatable, Identifiable {
    var id: Int
    var messageId: String
    var folder: String
    var subject: String?
    var fromAddress: String?
    var fromName: String?
    var to: String?
    var dateSent: Date?
    var bodyText: String?
    var hasHtml: Bool
    var attachments: [EmailAttachmentRow]
    var webmailUrl: String?
}

struct EmailAttachmentRow: Codable, Equatable, Identifiable {
    var partIndex: Int
    var fileName: String?
    var contentType: String
    var size: Int64
    var id: Int { partIndex }
}

// MARK: - Folders

struct FolderRow: Codable, Equatable, Identifiable {
    var folder: String
    var messageCount: Int
    var id: String { folder }
}

struct FoldersResponse: Codable, Equatable {
    var count: Int
    var folders: [FolderRow]
}
