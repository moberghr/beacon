# Performance

> §6.x — Async, caching, rate limiting. Loaded automatically by Claude Code.

§6.1 **All LLM calls go through `LlmRequestQueue`.** It enforces `MaxConcurrentRequests` from config. NEVER call OpenAI / Azure OpenAI / Anthropic / Bedrock SDK clients directly from a handler or service — go through the queue.

§6.2 **`IMemoryCache` is the standard for hot, rarely-changing reads.** Used for MCP settings, metadata, and app settings. Cache invalidation happens on the corresponding write handler — keep that pairing intact.

§6.3 **Kestrel timeouts are tuned for AI workloads.** Keep-alive and request-header timeouts are 5 minutes. `HttpClient` default timeout is also 5 minutes. Do not lower these without checking what depends on a long-running request.

§6.4 **Hangfire polling is 1 second; automatic retry is disabled (`Attempts = 0`).** Failures are investigated, not retried blindly. If you add a job that legitimately needs retries, configure them on the job, not globally.

§6.5 **Cross-source joins materialize into in-memory SQLite** — see §5.14. Don't try to JOIN across connectors at the SQL layer.
