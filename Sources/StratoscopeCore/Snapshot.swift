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
    case running           // requests run, but the engine does not say whether they read or write
}

/// The request that is running right now.
public struct LiveRequest {
    // nil means the engine did not report the value; it is drawn as "–", never as 0.
    public var promptRead: Double?
    public var promptTotal: Double?
    public var generated: Double?
    public var maxTokens: Double?
    public var elapsedSeconds: Double?
    public var prefillTokensPerSecond: Double?
    public var decodeTokensPerSecond: Double?
    /// Prompt tokens served from cache, as far as the engine lets us tell.
    public var reusedEstimate: Double?
    /// How many requests run together (nil for a single request or when the engine does not say). With several, the numbers above are totals.
    public var running: Int?

    public init(promptRead: Double? = nil, promptTotal: Double? = nil, generated: Double? = nil, maxTokens: Double? = nil,
                elapsedSeconds: Double? = nil, prefillTokensPerSecond: Double? = nil,
                decodeTokensPerSecond: Double? = nil, reusedEstimate: Double? = nil, running: Int? = nil) {
        self.promptRead = promptRead; self.promptTotal = promptTotal; self.generated = generated
        self.maxTokens = maxTokens; self.elapsedSeconds = elapsedSeconds
        self.prefillTokensPerSecond = prefillTokensPerSecond
        self.decodeTokensPerSecond = decodeTokensPerSecond; self.reusedEstimate = reusedEstimate
        self.running = running
    }

    public var isParallel: Bool { (running ?? 0) > 1 }

    /// Prefill progress, 0...100; nil when the read or the total is unknown.
    public var percent: Int? {
        guard let read = promptRead, let total = promptTotal, total > 0 else { return nil }
        return min(100, Int(read / total * 100))
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
    /// When the request finished, if the engine says.
    public var finishedAt: Date?
    /// How it ended ("stop", "length", ...), as the engine names it.
    public var finish: String?
    /// Expert-cache hit rate of this request, 0...1.
    public var expertHitRate: Double?

    public init(promptTokens: Double = 0, reusedTokens: Double = 0, promptRead: Double = 0,
                promptMilliseconds: Double = 0, outputTokens: Double = 0,
                decodeTokensPerSecond: Double? = nil, durationSeconds: Double = 0,
                finishedAt: Date? = nil, finish: String? = nil, expertHitRate: Double? = nil) {
        self.promptTokens = promptTokens; self.reusedTokens = reusedTokens; self.promptRead = promptRead
        self.promptMilliseconds = promptMilliseconds; self.outputTokens = outputTokens
        self.decodeTokensPerSecond = decodeTokensPerSecond; self.durationSeconds = durationSeconds
        self.finishedAt = finishedAt; self.finish = finish; self.expertHitRate = expertHitRate
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
    public var gpuTemperatureCelsius: Double?
    public var gpuPowerWatts: Double?
    public var gpuPowerLimitWatts: Double?
    public var gpuMemoryUsedBytes: Double?
    public var gpuMemoryTotalBytes: Double?
    public init(ramUsedBytes: Double? = nil, ramTotalBytes: Double? = nil, gpuPercent: Double? = nil, cpuPercent: Double? = nil,
                gpuTemperatureCelsius: Double? = nil, gpuPowerWatts: Double? = nil, gpuPowerLimitWatts: Double? = nil,
                gpuMemoryUsedBytes: Double? = nil, gpuMemoryTotalBytes: Double? = nil) {
        self.ramUsedBytes = ramUsedBytes; self.ramTotalBytes = ramTotalBytes
        self.gpuPercent = gpuPercent; self.cpuPercent = cpuPercent
        self.gpuTemperatureCelsius = gpuTemperatureCelsius; self.gpuPowerWatts = gpuPowerWatts
        self.gpuPowerLimitWatts = gpuPowerLimitWatts
        self.gpuMemoryUsedBytes = gpuMemoryUsedBytes; self.gpuMemoryTotalBytes = gpuMemoryTotalBytes
    }
}

/// Engine settings worth showing (optional; an engine that reports none gets no such section).
public struct EngineInfo {
    public var version: String?
    public var kvFormat: String?
    /// The draft window. Basalt sends a number; it is kept as text.
    public var draftWindow: String?
    public var expertCacheMib: Double?
    public var batchSlots: Double?
    /// Hit rate (0...1) of the newest finished request.
    public var expertHitRate: Double?
    /// The conversation cache's budget and slot count as the engine reports them. nil when the cache is switched off or unreported.
    /// Not a `CacheStats`: Basalt does not say what is parked, and a `CacheStats` would show used and parked as 0.
    public var conversationCacheBudgetBytes: Double?
    public var conversationCacheSlots: Int?
    /// The custom server's free KV pages, pages held only by prefix checkpoints, prefix cache checkpoints, and which admission block holds a
    /// waiting request back ("kv", "speed" or "none"). Carried in the snapshot only; the Mac panel draws none of them.
    public var kvFreePages: Double?
    public var kvCheckpointOnlyPages: Double?
    public var prefixCheckpoints: Double?
    public var admissionBlocked: String?
    /// llama.cpp's counters since the server started (`/metrics`, needs `--metrics`). Kept here and not in `Totals`, whose request count
    /// is not optional and which llama-server does not report. Prompt tokens are those read plus those reused. Carried in the snapshot
    /// only; the Mac panel draws none of them.
    public var totalPromptTokens: Double?
    public var totalReusedTokens: Double?
    public var totalOutputTokens: Double?
    public var totalPromptMilliseconds: Double?
    public var totalDecodeMilliseconds: Double?
    public init(version: String? = nil, kvFormat: String? = nil, draftWindow: String? = nil, expertCacheMib: Double? = nil,
                batchSlots: Double? = nil, expertHitRate: Double? = nil,
                conversationCacheBudgetBytes: Double? = nil, conversationCacheSlots: Int? = nil,
                kvFreePages: Double? = nil, kvCheckpointOnlyPages: Double? = nil, prefixCheckpoints: Double? = nil,
                admissionBlocked: String? = nil,
                totalPromptTokens: Double? = nil, totalReusedTokens: Double? = nil, totalOutputTokens: Double? = nil,
                totalPromptMilliseconds: Double? = nil, totalDecodeMilliseconds: Double? = nil) {
        self.totalPromptTokens = totalPromptTokens; self.totalReusedTokens = totalReusedTokens; self.totalOutputTokens = totalOutputTokens
        self.totalPromptMilliseconds = totalPromptMilliseconds; self.totalDecodeMilliseconds = totalDecodeMilliseconds
        self.kvFreePages = kvFreePages; self.kvCheckpointOnlyPages = kvCheckpointOnlyPages
        self.prefixCheckpoints = prefixCheckpoints; self.admissionBlocked = admissionBlocked
        self.version = version; self.kvFormat = kvFormat; self.draftWindow = draftWindow; self.expertCacheMib = expertCacheMib
        self.batchSlots = batchSlots; self.expertHitRate = expertHitRate
        self.conversationCacheBudgetBytes = conversationCacheBudgetBytes; self.conversationCacheSlots = conversationCacheSlots
    }
}

/// One slot of a batching engine: "reading", "decoding" or "idle".
public struct BatchSlot {
    public var index: Int
    public var state: String?
    /// What an idle slot still holds for a next turn.
    public var heldTokens: Double?
    // The running request of the slot (nil while idle).
    public var promptTokens: Double?
    public var promptRead: Double?
    public var generated: Double?
    public var tokensPerSecond: Double?
    public var maxTokens: Double?
    public init(index: Int, state: String? = nil, heldTokens: Double? = nil, promptTokens: Double? = nil, promptRead: Double? = nil,
                generated: Double? = nil, tokensPerSecond: Double? = nil, maxTokens: Double? = nil, elapsedSeconds: Double? = nil) {
        self.elapsedSeconds = elapsedSeconds
        self.index = index; self.state = state; self.heldTokens = heldTokens; self.promptTokens = promptTokens
        self.promptRead = promptRead; self.generated = generated; self.tokensPerSecond = tokensPerSecond; self.maxTokens = maxTokens
    }

    /// The tokens in the slot's working memory: what an idle slot holds, what a running one holds (its prompt and what it has written).
    public var tokens: Double? { heldTokens ?? promptTokens.map { $0 + (generated ?? 0) } }
    /// Seconds the slot's running request has been going (nil while idle or when the engine does not say).
    public var elapsedSeconds: Double?
    public var isActive: Bool { state != nil && state != "idle" }
    public var isReading: Bool { state == "reading" }
    public var isDecoding: Bool { state == "decoding" }
}

/// One parked conversation in the engine's conversation cache.
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
    /// Engine settings (version, KV format, draft window, expert cache, conversation cache budget); nil when the engine reports none.
    public var info: EngineInfo?
    /// One entry per slot for an engine with several; nil otherwise.
    public var slots: [BatchSlot]?
    /// True when the router refused the last read (HTTP 429) and this is the previous read of the same engine, not a new one; nil otherwise.
    public var routerLimited: Bool?

    public init(engine: String, model: String, status: EngineStatus = .idle, queued: Int = 0, live: LiveRequest? = nil,
                recent: [RequestStats] = [], context: ContextUsage, system: SystemStats? = nil,
                cache: CacheStats? = nil, totals: Totals? = nil, links: [EngineLink] = [], engineVersion: String? = nil,
                info: EngineInfo? = nil, slots: [BatchSlot]? = nil) {
        self.engine = engine; self.model = model; self.status = status; self.queued = queued; self.live = live
        self.recent = recent; self.context = context; self.system = system; self.cache = cache
        self.totals = totals; self.links = links; self.engineVersion = engineVersion
        self.info = info; self.slots = slots
    }

    /// "Basalt 0.3.2": the engine's name and version, or nil while the version is unknown (then nothing is shown).
    public var engineLabel: String? { engineVersion.map { "\(engine) \($0)" } }

    public var isLoading: Bool { if case .loading = status { return true }; return false }
    public var isBusy: Bool { switch status { case .prefill, .thinking, .decode, .running: return true; default: return false } }
    public var isReading: Bool { status == .prefill }
}
