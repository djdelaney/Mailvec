// TrayWireContractTests.swift
//
// Round-trips fixture JSON through the production JSONDecoder to catch
// wire-format drift between the C# records in
// src/Mailvec.Core/Tray/TrayModels.cs and the Swift structs in
// Sources/Models.swift. CLAUDE.md flags this category of bug
// specifically: "Rename either side without the other and Swift silently
// decodes null — there's no compile-time check across the language
// boundary."
//
// The fixtures are hand-written rather than recorded from a live server,
// so we control them tightly. Each field set should match the property
// names + JSON shape that .NET's System.Text.Json produces for the
// corresponding C# record. If a C# record gains/loses a field, the
// matching test here should fail (or surface a nil) before the bug
// reaches a user.
import XCTest
@testable import Mailvec_Tray

final class TrayWireContractTests: XCTestCase {

    private let decoder = WireDecoder.make()

    // MARK: - /tray/status

    func test_decodes_TrayStatus_with_every_field_populated() throws {
        let json = """
        {
            "severity": "ok",
            "messages": 12345,
            "deleted": 6,
            "embedded": 12000,
            "embedTotal": 12345,
            "chunks": 78901,
            "lastIndexedAt": "2026-05-14T19:03:42.9092057+00:00",
            "lastSyncAt": "2026-05-14T18:55:00+00:00",
            "dbSizeBytes": 524288000,
            "schemaVersion": "4",
            "services": [
                { "id": "mbsync", "detail": "idle · 5 runs", "ok": true, "busy": false, "severity": "ok" },
                { "id": "indexer", "detail": "idle", "ok": true, "busy": false, "severity": null }
            ],
            "ollama": { "ok": true, "detail": "mxbai-embed-large", "severity": "ok" },
            "ocr": { "enabled": true, "visionModel": "qwen2.5vl:7b", "modelAvailable": true, "pending": 15, "recovered": 2448, "imagePending": 12, "imageRecovered": 2140, "severity": "syncing" },
            "progress": { "done": 12000, "total": 12345, "ratePerMinute": 50, "etaMinutes": 7 },
            "recentEvents": [
                { "time": "2026-05-14T19:03:42.909+00:00", "kind": "indexed", "text": "Test — alice@example.com", "agent": "indexer", "live": false, "severity": "ok" }
            ],
            "sparkline": [0, 1, 5, 2, 0]
        }
        """.data(using: .utf8)!

        let status = try decoder.decode(TrayHealth.self, from: json)

        XCTAssertEqual(status.severity, .ok)
        XCTAssertEqual(status.messages, 12345)
        XCTAssertEqual(status.embedded, 12000)
        XCTAssertEqual(status.chunks, 78901)
        XCTAssertNotNil(status.lastIndexedAt)
        XCTAssertNotNil(status.lastSyncAt)
        XCTAssertEqual(status.dbSizeBytes, 524288000)
        XCTAssertEqual(status.services.count, 2)
        XCTAssertEqual(status.services[0].id, "mbsync")
        XCTAssertEqual(status.services[1].severity, nil)   // null wire → nil
        XCTAssertEqual(status.ollama.detail, "mxbai-embed-large")
        XCTAssertEqual(status.ocr?.pending, 15)
        XCTAssertEqual(status.ocr?.imagePending, 12)
        XCTAssertEqual(status.ocr?.imageRecovered, 2140)
        XCTAssertEqual(status.ocr?.pdfPendingCount, 3)
        XCTAssertEqual(status.ocr?.pendingSummary, "3 scanned PDFs + 12 images")
        XCTAssertEqual(status.ocr?.imageRecoveredCount, 2140)
        // Locale-safe: assert the image tail is present, not the exact grouped digits.
        XCTAssertTrue(status.ocr?.recoveredLine.contains("recovered") ?? false)
        XCTAssertTrue(status.ocr?.recoveredLine.contains("from images") ?? false)
        XCTAssertEqual(status.progress?.etaMinutes, 7)
        XCTAssertEqual(status.recentEvents[0].kind, .indexed)
        XCTAssertEqual(status.sparkline, [0, 1, 5, 2, 0])
    }

    func test_decodes_TrayStatus_with_nullable_fields_omitted() throws {
        // The .NET serializer emits `null` for nullable record fields whose
        // value is null. Make sure decode doesn't choke on the cold-start
        // shape: no progress, no last sync, empty events.
        let json = """
        {
            "severity": "syncing",
            "messages": 0,
            "deleted": 0,
            "embedded": 0,
            "embedTotal": 0,
            "chunks": 0,
            "lastIndexedAt": null,
            "lastSyncAt": null,
            "dbSizeBytes": 0,
            "schemaVersion": "unknown",
            "services": [],
            "ollama": { "ok": false, "detail": "unreachable", "severity": "error" },
            "progress": null,
            "recentEvents": [],
            "sparkline": []
        }
        """.data(using: .utf8)!

        let status = try decoder.decode(TrayHealth.self, from: json)

        XCTAssertEqual(status.severity, .syncing)
        XCTAssertNil(status.lastIndexedAt)
        XCTAssertNil(status.progress)
        XCTAssertTrue(status.services.isEmpty)
        XCTAssertTrue(status.recentEvents.isEmpty)
    }

    func test_TrayHealth_embedCoverage_handles_zero_total() throws {
        // Computed property used by the dashboard. Zero total messages →
        // coverage shown as 100% (otherwise the UI flashes "0%" on cold
        // start until the first ingest). Verify the guard.
        let json = """
        {
            "severity": "ok", "messages": 0, "deleted": 0, "embedded": 0, "embedTotal": 0,
            "chunks": 0, "lastIndexedAt": null, "lastSyncAt": null, "dbSizeBytes": 0,
            "schemaVersion": "4", "services": [],
            "ollama": { "ok": true, "detail": "ok", "severity": "ok" },
            "progress": null, "recentEvents": [], "sparkline": []
        }
        """.data(using: .utf8)!

        let status = try decoder.decode(TrayHealth.self, from: json)
        XCTAssertEqual(status.embedCoverage, 1.0)
    }

    // MARK: - /tray/system

    func test_decodes_TraySystem() throws {
        let json = """
        {
            "maildirRoot": "/Users/test/Mail",
            "mbsyncrcPath": "/Users/test/.mbsyncrc",
            "mbsyncSchedule": "10 minutes",
            "imapHost": "imap.fastmail.com",
            "imapUser": "test@example.com",
            "lastSyncRelative": "now",
            "lastSyncDetail": "mbsync currently syncing",
            "nextSyncRelative": "in ≤ 10 minutes",
            "dbPath": "/Users/test/.local/share/mailvec/archive.sqlite",
            "dbSize": "512.0 MB",
            "schemaVersion": "4",
            "vecDylibVersion": "v0.1.7-alpha.2",
            "ollamaEndpoint": "http://localhost:11434",
            "ollamaReachable": true,
            "ollamaPingMs": 8,
            "embeddingModel": "mxbai-embed-large",
            "modelDimensions": 1024,
            "schemaModelMatches": true,
            "coverageDone": 12000,
            "coverageTotal": 12345,
            "mcpHttpEnabled": true,
            "mcpBindAddress": "127.0.0.1",
            "mcpPort": 3333,
            "mcpbInstalled": true,
            "mcpbVersion": "0.1.16",
            "attachmentDownloadDir": "/Users/test/Downloads/mailvec",
            "softDeletedCount": 6
        }
        """.data(using: .utf8)!

        let sys = try decoder.decode(TraySystem.self, from: json)

        XCTAssertEqual(sys.maildirRoot, "/Users/test/Mail")
        XCTAssertEqual(sys.mbsyncSchedule, "10 minutes")
        XCTAssertEqual(sys.imapHost, "imap.fastmail.com")
        XCTAssertEqual(sys.vecDylibVersion, "v0.1.7-alpha.2")
        XCTAssertEqual(sys.modelDimensions, 1024)
        XCTAssertTrue(sys.schemaModelMatches)
        XCTAssertEqual(sys.mcpPort, 3333)
        XCTAssertTrue(sys.mcpbInstalled)
        XCTAssertEqual(sys.mcpbVersion, "0.1.16")
        XCTAssertEqual(sys.softDeletedCount, 6)
    }

    func test_decodes_TraySystem_with_unconfigured_mcpb() throws {
        // mcpbInstalled=false, mcpbVersion=null is the common pre-install state.
        let json = #"""
        {
            "maildirRoot": "/Users/test/Mail",
            "mbsyncrcPath": "/Users/test/.mbsyncrc",
            "mbsyncSchedule": "unknown",
            "imapHost": "(see ~/.mbsyncrc)",
            "imapUser": "(see ~/.mbsyncrc)",
            "lastSyncRelative": null,
            "lastSyncDetail": null,
            "nextSyncRelative": null,
            "dbPath": "/Users/test/archive.sqlite",
            "dbSize": "0 B",
            "schemaVersion": "unknown",
            "vecDylibVersion": "—",
            "ollamaEndpoint": "http://localhost:11434",
            "ollamaReachable": false,
            "ollamaPingMs": 0,
            "embeddingModel": "mxbai-embed-large",
            "modelDimensions": 1024,
            "schemaModelMatches": true,
            "coverageDone": 0,
            "coverageTotal": 0,
            "mcpHttpEnabled": false,
            "mcpBindAddress": "127.0.0.1",
            "mcpPort": 3333,
            "mcpbInstalled": false,
            "mcpbVersion": null,
            "attachmentDownloadDir": "/Users/test/Downloads/mailvec",
            "softDeletedCount": 0
        }
        """#.data(using: .utf8)!

        let sys = try decoder.decode(TraySystem.self, from: json)
        XCTAssertFalse(sys.mcpbInstalled)
        XCTAssertNil(sys.mcpbVersion)
        XCTAssertNil(sys.lastSyncRelative)
    }

    // MARK: - /tray/search

    func test_decodes_SearchResponse_with_keyword_hit() throws {
        let json = """
        {
            "query": "ramen",
            "mode": "keyword",
            "count": 1,
            "results": [
                {
                    "id": 42, "messageId": "abc@x", "folder": "INBOX",
                    "subject": "Lunch on Friday", "fromAddress": "alice@example.com", "fromName": "Alice",
                    "dateSent": "2026-01-15T12:30:00+00:00",
                    "snippet": "[ramen] at 12:30",
                    "score": 1.0, "bm25Score": -3.45, "vectorScore": null,
                    "matchedAttachment": null,
                    "webmailUrl": "https://app.fastmail.com/mail/search:msgid:abc%40x?u=u12345678"
                }
            ]
        }
        """.data(using: .utf8)!

        let resp = try decoder.decode(SearchResponse.self, from: json)

        XCTAssertEqual(resp.mode, "keyword")
        XCTAssertEqual(resp.results.count, 1)
        let hit = resp.results[0]
        XCTAssertEqual(hit.id, 42)
        XCTAssertEqual(hit.messageId, "abc@x")
        XCTAssertEqual(hit.bm25Score, -3.45)
        XCTAssertNil(hit.vectorScore)
        XCTAssertNil(hit.matchedAttachment)
        XCTAssertEqual(hit.matchSource, .body)
        XCTAssertEqual(hit.from, "Alice")
        XCTAssertEqual(hit.displaySubject, "Lunch on Friday")
        XCTAssertNotNil(hit.webmailUrl)
    }

    func test_decodes_SearchResponse_with_attachment_match() throws {
        // The matchedAttachment field is what distinguishes a body-text hit
        // from an attachment-text hit in the tray UI.
        let json = """
        {
            "query": "Q3 revenue",
            "mode": "hybrid",
            "count": 1,
            "results": [
                {
                    "id": 99, "messageId": "report@x", "folder": "INBOX",
                    "subject": "Quarterly report", "fromAddress": "ceo@x", "fromName": null,
                    "dateSent": null,
                    "snippet": "Q3 revenue up 12%",
                    "score": 0.95, "bm25Score": null, "vectorScore": null,
                    "matchedAttachment": { "partIndex": 0, "fileName": "Q3.pdf", "sizeHint": null },
                    "webmailUrl": null
                }
            ]
        }
        """.data(using: .utf8)!

        let resp = try decoder.decode(SearchResponse.self, from: json)
        let hit = resp.results[0]

        XCTAssertEqual(hit.matchedAttachment?.fileName, "Q3.pdf")
        XCTAssertEqual(hit.matchedAttachment?.partIndex, 0)
        XCTAssertEqual(hit.matchSource, .attachment)
        XCTAssertNil(hit.webmailUrl)
        XCTAssertEqual(hit.from, "ceo@x")   // falls back to fromAddress when fromName is null
    }

    func test_decodes_empty_SearchResponse() throws {
        let json = #"{ "query": "nothing", "mode": "hybrid", "count": 0, "results": [] }"#.data(using: .utf8)!
        let resp = try decoder.decode(SearchResponse.self, from: json)
        XCTAssertEqual(resp.count, 0)
        XCTAssertTrue(resp.results.isEmpty)
    }

    // MARK: - /tray/email/{id}

    func test_decodes_EmailDetail_with_attachments() throws {
        let json = """
        {
            "id": 42,
            "messageId": "abc@x",
            "folder": "INBOX",
            "subject": "Quote attached",
            "fromAddress": "carol@example.com",
            "fromName": "Carol",
            "to": "alice@example.com",
            "dateSent": "2026-01-15T12:30:00+00:00",
            "bodyText": "See attached.",
            "hasHtml": false,
            "attachments": [
                { "partIndex": 0, "fileName": "quote.pdf", "contentType": "application/pdf", "size": 12345 }
            ],
            "webmailUrl": null
        }
        """.data(using: .utf8)!

        let email = try decoder.decode(EmailDetail.self, from: json)
        XCTAssertEqual(email.id, 42)
        XCTAssertEqual(email.attachments.count, 1)
        XCTAssertEqual(email.attachments[0].fileName, "quote.pdf")
        XCTAssertEqual(email.attachments[0].size, 12345)
    }

    // MARK: - /tray/folders

    func test_decodes_FoldersResponse() throws {
        let json = """
        {
            "count": 3,
            "folders": [
                { "folder": "INBOX", "messageCount": 12000 },
                { "folder": "Archive.2024", "messageCount": 300 },
                { "folder": "Sent", "messageCount": 45 }
            ]
        }
        """.data(using: .utf8)!

        let folders = try decoder.decode(FoldersResponse.self, from: json)
        XCTAssertEqual(folders.count, 3)
        XCTAssertEqual(folders.folders[0].folder, "INBOX")
        XCTAssertEqual(folders.folders[0].id, "INBOX")  // computed Identifiable id
        XCTAssertEqual(folders.folders[2].messageCount, 45)
    }

    // MARK: - /tray/attachment + /tray/control

    func test_decodes_AttachmentResponse() throws {
        let json = #"""
        {
            "path": "/Users/test/Downloads/mailvec/42-0-quote.pdf",
            "bytes": 12345,
            "contentType": "application/pdf",
            "wasReused": false
        }
        """#.data(using: .utf8)!

        let resp = try decoder.decode(AttachmentResponse.self, from: json)
        XCTAssertEqual(resp.bytes, 12345)
        XCTAssertEqual(resp.contentType, "application/pdf")
        XCTAssertFalse(resp.wasReused)
    }

    func test_decodes_ControlResponse() throws {
        let json = #"{ "ok": true, "detail": "kickstart com.mailvec.embedder ok" }"#.data(using: .utf8)!
        let resp = try decoder.decode(ControlResponse.self, from: json)
        XCTAssertTrue(resp.ok)
        XCTAssertTrue(resp.detail.contains("embedder"))
    }

    // MARK: - Forward-compat: unknown enum values

    func test_TimelineEvent_kind_throws_on_unknown_value() {
        // Codable raw-value enums throw on unknown values. The dashboard's
        // ForEach over recentEvents would crash if the server adds a new
        // kind without the tray adopting it. This documents the current
        // (strict) behaviour — if we ever switch to a default-arm enum,
        // this test should be updated to assert the fallback.
        let json = #"""
        {
            "time": "2026-05-14T19:03:42+00:00",
            "kind": "alien-event-type",
            "text": "?",
            "agent": "?",
            "live": false,
            "severity": null
        }
        """#.data(using: .utf8)!

        XCTAssertThrowsError(try WireDecoder.make().decode(TimelineEvent.self, from: json))
    }
}
