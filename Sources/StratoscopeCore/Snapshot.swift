import Foundation

// The engine-neutral data model. Every engine adapter translates its own metrics into an
// `EngineSnapshot`; the UI only ever sees this. Sections an engine cannot provide stay `nil`
// and the matching part of the menu is simply not shown.

public enum EngineStatus: Equatable {
    case loading(String)   // model is starting; the string says why we are waiting
    case idle
    case prefill           // reading the prompt
    case thinking          // generating reasoning tokens
    case decode            // generating the answer
}

/// The request that is running right now.
public struct LiveRequest {
    public var promptRead: Double
    public var promptTotal: Double
    public var generated: Double
    public var maxTokens: Double
    public var elapsedSeconds: Double
    public var prefillTokensPerSecond: Double?
    public var decodeTokensPerSecond: Double?
    /// Prompt tokens served from cache, as far as the engine lets us tell.
    public var reusedEstimate: Double

    public init(promptRead: Double = 0, promptTotal: Double = 0, generated: Double = 0, maxTokens: Double = 0,
                elapsedSeconds: Double = 0, prefillTokensPerSecond: Double? = nil,
                decodeTokensPerSecond: Double? = nil, reusedEstimate: Double = 0) {
        self.promptRead = promptRead; self.promptTotal = promptTotal; self.generated = generated
        self.maxTokens = maxTokens; self.elapsedSeconds = elapsedSeconds
        self.prefillTokensPerSecond = prefillTokensPerSecond
        self.decodeTokensPerSecond = decodeTokensPerSecond; self.reusedEstimate = reusedEstimate
    }

    /// Prefill progress, 0...100.
    public var percent: Int {
        promptTotal > 0 ? min(100, Int(promptRead / promptTotal * 100)) : 0
    }
}

/// A finished request.
public struct RequestStats {
    public var promptTokens: Double
    public var reusedTokens: Double
    public var promptRead: Double
    public var promptMilliseconds: Double
    public var outputTokens: Double
    public var decodeTokensPerSecond: Double?
    public var durationSeconds: Double

    public init(promptTokens: Double = 0, reusedTokens: Double = 0, promptRead: Double = 0,
                promptMilliseconds: Double = 0, outputTokens: Double = 0,
                decodeTokensPerSecond: Double? = nil, durationSeconds: Double = 0) {
        self.promptTokens = promptTokens; self.reusedTokens = reusedTokens; self.promptRead = promptRead
        self.promptMilliseconds = promptMilliseconds; self.outputTokens = outputTokens
        self.decodeTokensPerSecond = decodeTokensPerSecond; self.durationSeconds = durationSeconds
    }

    /// Size of the conversation after this request: the prompt plus the answer.
    public var contextTokens: Double { promptTokens + outputTokens }

    /// Prefill speed, or nil when the prompt was too short for the number to mean anything.
    public func prefillTokensPerSecond(minTokens: Double = 500, minMilliseconds: Double = 500) -> Double? {
        (promptMilliseconds > minMilliseconds && promptRead > minTokens) ? promptRead / (promptMilliseconds / 1000) : nil
    }
}

public struct ContextUsage {
    public var used: Double?
    public var max: Double
    /// Token count where the client summarises or trims the conversation, drawn as a marker on the context bar.
    /// `nil` = nobody knows it: no marker. An adapter fills it from what its engine reports; where the engine
    /// does not (Strata today), from the user's own setting. Never guessed.
    public var compactAt: Double?
    public init(used: Double? = nil, max: Double, compactAt: Double? = nil) {
        self.used = used; self.max = max; self.compactAt = compactAt
    }

    /// Room left below the engine's limit, or nil when either number is unknown.
    public var free: Double? {
        guard max > 0, let used = used else { return nil }
        return Swift.max(0, max - used)
    }
}

public struct SystemStats {
    public var ramUsedBytes: Double?
    public var ramTotalBytes: Double?
    public var gpuPercent: Double?
    public var cpuPercent: Double?
    public init(ramUsedBytes: Double? = nil, ramTotalBytes: Double? = nil, gpuPercent: Double? = nil, cpuPercent: Double? = nil) {
        self.ramUsedBytes = ramUsedBytes; self.ramTotalBytes = ramTotalBytes
        self.gpuPercent = gpuPercent; self.cpuPercent = cpuPercent
    }
}

/// One parked conversation in the engine's prompt cache.
public struct CacheEntry {
    public var id: Int
    public var tokens: Int
    public var bytes: Double
    public var parkedAt: Date?
    public init(id: Int, tokens: Int, bytes: Double, parkedAt: Date? = nil) {
        self.id = id; self.tokens = tokens; self.bytes = bytes; self.parkedAt = parkedAt
    }
}

public struct CacheStats {
    public var budgetBytes: Double
    public var usedBytes: Double
    public var slots: Int
    public var parked: Int
    public var requests: Double
    public var requestsReused: Double
    public var evictions: Double
    public var entries: [CacheEntry]
    public init(budgetBytes: Double = 0, usedBytes: Double = 0, slots: Int = 0, parked: Int = 0, requests: Double = 0,
                requestsReused: Double = 0, evictions: Double = 0, entries: [CacheEntry] = []) {
        self.budgetBytes = budgetBytes; self.usedBytes = usedBytes; self.slots = slots; self.parked = parked
        self.requests = requests; self.requestsReused = requestsReused; self.evictions = evictions; self.entries = entries
    }

    /// Slot rows are listed newest first. A row past the known entries but within `parked` is a conversation
    /// the engine reports but we have no details for (for example because its log can't be read): not free.
    public func isParkedWithoutDetails(row: Int) -> Bool { row >= entries.count && row < parked }
}

public struct Totals {
    public var requests: Double
    public var outputTokens: Double
    public var promptTokens: Double
    public var reusedTokens: Double
    public var since: Date?
    public init(requests: Double = 0, outputTokens: Double = 0, promptTokens: Double = 0, reusedTokens: Double = 0, since: Date? = nil) {
        self.requests = requests; self.outputTokens = outputTokens; self.promptTokens = promptTokens
        self.reusedTokens = reusedTokens; self.since = since
    }
}

public struct EngineLink {
    public var title: String
    public var url: URL
    public init(title: String, url: URL) { self.title = title; self.url = url }
}

public struct EngineSnapshot {
    public var engine: String            // "Strata"
    public var model: String
    public var status: EngineStatus
    public var queued: Int
    public var live: LiveRequest?
    public var recent: [RequestStats]    // newest first
    public var context: ContextUsage
    public var system: SystemStats?
    public var cache: CacheStats?
    public var totals: Totals?
    public var links: [EngineLink]
    /// The version the engine reports about itself, or nil when it does not say.
    public var engineVersion: String?

    public init(engine: String, model: String, status: EngineStatus = .idle, queued: Int = 0, live: LiveRequest? = nil,
                recent: [RequestStats] = [], context: ContextUsage, system: SystemStats? = nil,
                cache: CacheStats? = nil, totals: Totals? = nil, links: [EngineLink] = [], engineVersion: String? = nil) {
        self.engine = engine; self.model = model; self.status = status; self.queued = queued; self.live = live
        self.recent = recent; self.context = context; self.system = system; self.cache = cache
        self.totals = totals; self.links = links; self.engineVersion = engineVersion
    }

    /// "Basalt 0.3.2": the engine's name and version, or nil while the version is unknown (then nothing is shown).
    public var engineLabel: String? { engineVersion.map { "\(engine) \($0)" } }

    public var isLoading: Bool { if case .loading = status { return true }; return false }
    public var isBusy: Bool { switch status { case .prefill, .thinking, .decode: return true; default: return false } }
    public var isReading: Bool { status == .prefill }
}
