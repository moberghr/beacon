# Performance

> §6.x — Async, caching, rate limiting. Loaded automatically by Claude Code.

§6.1 **All LLM calls go through `LlmRequestQueue`.** It enforces `MaxConcurrentRequests` from config. NEVER call OpenAI / Azure OpenAI / Anthropic / Bedrock SDK clients directly from a handler or service — go through the queue.

§6.2 **`IMemoryCache` is the standard for hot, rarely-changing reads.** Used for MCP settings, metadata, and app settings. Cache invalidation happens on the corresponding write handler — keep that pairing intact.

§6.3 **Kestrel timeouts are tuned for AI workloads.** Keep-alive and request-header timeouts are 5 minutes. `HttpClient` default timeout is also 5 minutes. Do not lower these without checking what depends on a long-running request.

§6.4 **Warp polling is 1 second (`opt.PollingInterval`); automatic retry is disabled** — the host does NOT call `opt.AddRetry()`, so failed jobs stay failed. Failures are investigated, not retried blindly. If you add a job that legitimately needs retries, configure it on that job (`[Retry(MaxRetries = n)]`), not globally. Worker count is capped (`opt.WorkerCount = min(2×ProcessorCount, 20)`) to protect the Npgsql pool.

§6.5 **Cross-source joins materialize into in-memory SQLite** — see §5.14. Don't try to JOIN across connectors at the SQL layer.
