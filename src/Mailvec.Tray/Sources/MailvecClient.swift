// MailvecClient.swift
//
// Thin URLSession wrapper around Mailvec.Mcp's /tray/* REST surface on
// 127.0.0.1:3333. We deliberately bypass the MCP protocol (no session id,
// no SSE, no JSON-RPC envelope) — the tray isn't an LLM agent and a plain
// REST contract is much easier to evolve.
import Foundation

actor MailvecClient {
    static let shared = MailvecClient()

    private let base = URL(string: "http://127.0.0.1:3333")!
    private let session: URLSession = {
        let c = URLSessionConfiguration.ephemeral
        // /tray/status pings Ollama (up to 2s) and shells out to launchctl
        // four times in parallel. A 5s ceiling was too tight; 15s gives
        // comfortable headroom without the user-facing wait getting silly.
        c.timeoutIntervalForRequest = 15
        c.waitsForConnectivity = false
        return URLSession(configuration: c)
    }()

    // Shared with the test target — see WireDecoder.swift. Production and
    // tests must use the same decoder shape or the contract test gives
    // false confidence.
    private let decoder: JSONDecoder = WireDecoder.make()

    private let encoder: JSONEncoder = {
        let e = JSONEncoder()
        e.dateEncodingStrategy = .iso8601
        return e
    }()

    func status() async throws -> TrayHealth {
        try await getJSON("/tray/status")
    }

    func system() async throws -> TraySystem {
        try await getJSON("/tray/system")
    }

    func folders() async throws -> FoldersResponse {
        try await getJSON("/tray/folders")
    }

    func email(id: Int) async throws -> EmailDetail {
        try await getJSON("/tray/email/\(id)")
    }

    func search(_ query: String,
                mode: SearchMode = .hybrid,
                limit: Int = 20,
                folder: String? = nil,
                dateFrom: Date? = nil,
                dateTo: Date? = nil) async throws -> [SearchHit] {
        struct Body: Encodable {
            let query: String?
            let mode: String
            let limit: Int
            let folder: String?
            let dateFrom: String?
            let dateTo: String?
            let fromContains: String?
            let fromExact: String?
        }
        let body = Body(
            query: query.isEmpty ? nil : query,
            mode: mode.rawValue,
            limit: limit,
            folder: folder,
            dateFrom: dateFrom.map { ISO8601DateFormatter().string(from: $0) },
            dateTo: dateTo.map { ISO8601DateFormatter().string(from: $0) },
            fromContains: nil,
            fromExact: nil)

        let resp: SearchResponse = try await postJSON("/tray/search", body: body)
        return resp.results
    }

    /// Pause/Resume buttons in the dashboard footer. Acts on indexer +
    /// embedder together (the user's spec — leave mbsync's timer alone).
    /// Returns true if both succeeded.
    @discardableResult
    func pauseServices() async throws -> Bool {
        let a = try await control(service: "indexer", action: "bootout")
        let b = try await control(service: "embedder", action: "bootout")
        return a && b
    }

    @discardableResult
    func resumeServices() async throws -> Bool {
        let a = try await control(service: "indexer", action: "kickstart")
        let b = try await control(service: "embedder", action: "kickstart")
        return a && b
    }

    @discardableResult
    func control(service: String, action: String) async throws -> Bool {
        struct Body: Encodable { let service: String; let action: String }
        let resp: ControlResponse = try await postJSON(
            "/tray/control",
            body: Body(service: service, action: action))
        return resp.ok
    }

    func attachment(messageId: Int, partIndex: Int) async throws -> AttachmentResponse {
        struct Body: Encodable { let messageId: Int; let partIndex: Int }
        return try await postJSON(
            "/tray/attachment",
            body: Body(messageId: messageId, partIndex: partIndex))
    }

    // MARK: - Plumbing

    private func getJSON<T: Decodable>(_ path: String) async throws -> T {
        var req = URLRequest(url: base.appending(path: path))
        req.setValue("application/json", forHTTPHeaderField: "Accept")
        let start = Date()
        do {
            let (data, resp) = try await session.data(for: req)
            try check(resp, data: data)
            let result: T = try decode(T.self, from: data, path: path)
            TrayLog.debug("GET \(path)", "ok in \(ms(start))ms (\(data.count)B)")
            return result
        } catch {
            TrayLog.warn("GET \(path)", "failed in \(ms(start))ms", error: error)
            throw error
        }
    }

    private func postJSON<Body: Encodable, T: Decodable>(_ path: String, body: Body) async throws -> T {
        var req = URLRequest(url: base.appending(path: path))
        req.httpMethod = "POST"
        req.setValue("application/json", forHTTPHeaderField: "Content-Type")
        req.setValue("application/json", forHTTPHeaderField: "Accept")
        req.httpBody = try encoder.encode(body)
        let start = Date()
        do {
            let (data, resp) = try await session.data(for: req)
            try check(resp, data: data)
            let result: T = try decode(T.self, from: data, path: path)
            TrayLog.debug("POST \(path)", "ok in \(ms(start))ms (\(data.count)B)")
            return result
        } catch {
            TrayLog.warn("POST \(path)", "failed in \(ms(start))ms", error: error)
            throw error
        }
    }

    private func ms(_ start: Date) -> Int {
        Int(Date().timeIntervalSince(start) * 1000)
    }

    private func check(_ resp: URLResponse, data: Data) throws {
        guard let http = resp as? HTTPURLResponse else {
            throw MailvecClientError.badResponse("not an HTTP response")
        }
        guard (200..<300).contains(http.statusCode) || http.statusCode == 503 else {
            let body = String(data: data, encoding: .utf8) ?? ""
            throw MailvecClientError.badResponse("HTTP \(http.statusCode): \(body.prefix(200))")
        }
    }

    /// Wraps `decoder.decode` so we get a short, human-readable message
    /// instead of Swift's default unhelpful decode-error dump.
    private func decode<T: Decodable>(_ type: T.Type, from data: Data, path: String) throws -> T {
        do {
            return try decoder.decode(type, from: data)
        } catch let DecodingError.keyNotFound(key, ctx) {
            throw MailvecClientError.decode("\(path): missing key '\(key.stringValue)' at \(ctx.codingPath.map(\.stringValue).joined(separator: "."))")
        } catch let DecodingError.typeMismatch(_, ctx) {
            throw MailvecClientError.decode("\(path): type mismatch at \(ctx.codingPath.map(\.stringValue).joined(separator: ".")): \(ctx.debugDescription)")
        } catch let DecodingError.valueNotFound(_, ctx) {
            throw MailvecClientError.decode("\(path): null at \(ctx.codingPath.map(\.stringValue).joined(separator: "."))")
        } catch let DecodingError.dataCorrupted(ctx) {
            throw MailvecClientError.decode("\(path): corrupted at \(ctx.codingPath.map(\.stringValue).joined(separator: ".")): \(ctx.debugDescription)")
        }
    }
}

enum MailvecClientError: LocalizedError {
    case badResponse(String)
    case decode(String)
    var errorDescription: String? {
        switch self {
        case .badResponse(let s): return "Network: \(s)"
        case .decode(let s):      return "Decode: \(s)"
        }
    }
}

