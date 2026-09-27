using System.Security.Claims;
using System.Text;
using Microsoft.AspNetCore.Antiforgery;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Authorization.Policy;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.AspNetCore.Http.Metadata;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Beacon.Core.HostEndpoints;
using Beacon.Core.Mcp;

namespace Beacon.MCP.HostEndpoints;

internal enum HostEndpointDispatchOutcome
{
    Completed,
    Forbidden,

    /// <summary>A host dispatch middleware did not call <c>next</c>.</summary>
    Refused,
    TimedOut,
    ResponseTooLarge,
    Failed
}

internal sealed record HostEndpointDispatchResult(
    HostEndpointDispatchOutcome Outcome,
    int StatusCode,
    string? ContentType,
    byte[] Body)
{
    public static HostEndpointDispatchResult Of(HostEndpointDispatchOutcome outcome) => new(outcome, 0, null, []);
}

/// <summary>
/// Runs one host endpoint in-process as the host principal. The request is synthetic: a fresh DI scope, a new
/// <see cref="DefaultHttpContext"/> that carries nothing of the MCP request (no Authorization header, no cookies, no
/// connection info), the endpoint and route values set as routing would, the endpoint's authorization metadata (or
/// the host's fallback policy) evaluated against the host principal BEFORE the endpoint runs, antiforgery tokens
/// minted server-side for that principal, the host's <see cref="IHostEndpointDispatchMiddleware"/> chain, a capped response buffer and a timeout. The endpoint's
/// <see cref="Endpoint.RequestDelegate"/> then runs its own model binding and filter pipeline; host middleware does
/// not run.
/// </summary>
internal sealed class HostEndpointDispatcher(
    IServiceScopeFactory scopeFactory,
    HostEndpointToolOptions options,
    ILogger<HostEndpointDispatcher> logger)
{
    public const string SyntheticHost = "localhost";

    private const string JsonContentType = "application/json; charset=utf-8";
    private const string FormContentType = "application/x-www-form-urlencoded";

    public async Task<HostEndpointDispatchResult> DispatchAsync(
        HostEndpointToolDescriptor tool,
        HostEndpointRequestParts parts,
        ClaimsPrincipal principal,
        McpCaller caller,
        CancellationToken cancellationToken)
    {
        // A fresh execution context: IHttpContextAccessor is AsyncLocal-backed and clears the CURRENT holder when it
        // is reassigned, so pointing it at the synthetic request inside the MCP request's flow would wipe the MCP
        // request's HttpContext (and with it the caller the audit row records). Host code that reads the accessor
        // sees the synthetic request, as it would for a real one.
        Task<HostEndpointDispatchResult> dispatch;
        using (ExecutionContext.SuppressFlow())
        {
            dispatch = Task.Run(() => DispatchIsolatedAsync(tool, parts, principal, caller, cancellationToken), CancellationToken.None);
        }

        return await dispatch;
    }

    private async Task<HostEndpointDispatchResult> DispatchIsolatedAsync(
        HostEndpointToolDescriptor tool,
        HostEndpointRequestParts parts,
        ClaimsPrincipal principal,
        McpCaller caller,
        CancellationToken cancellationToken)
    {
        var scope = scopeFactory.CreateAsyncScope();
        var timeout = new CancellationTokenSource(options.RequestTimeout);
        var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeout.Token);
        var responseBody = new CappedResponseStream(options.MaxResponseBytes);
        var accessor = scope.ServiceProvider.GetService<IHttpContextAccessor>();
        Task? invocation = null;

        try
        {
            var httpContext = CreateHttpContext(tool, parts, principal, scope.ServiceProvider, responseBody, linked.Token);
            if (accessor != null)
            {
                accessor.HttpContext = httpContext;
            }

            if (!await AuthorizeAsync(tool.Endpoint, httpContext, principal, scope.ServiceProvider))
            {
                return HostEndpointDispatchResult.Of(HostEndpointDispatchOutcome.Forbidden);
            }

            await ValidateMinimalApiAntiforgeryAsync(tool.Endpoint, httpContext, scope.ServiceProvider);

            // The host's dispatch middleware, chained in registration order around the endpoint, so ambient state
            // one sets before `await next()` (Thread.CurrentPrincipal) is visible to the endpoint's filters.
            var endpointInvoked = false;
            Func<Task> pipeline = () =>
            {
                endpointInvoked = true;

                return tool.Endpoint.RequestDelegate!(httpContext);
            };

            var middlewares = scope.ServiceProvider.GetServices<IHostEndpointDispatchMiddleware>().ToList();
            for (var i = middlewares.Count - 1; i >= 0; i--)
            {
                var middleware = middlewares[i];
                var next = pipeline;
                pipeline = () => middleware.InvokeAsync(httpContext, caller, next, linked.Token);
            }

            invocation = pipeline();
            await invocation.WaitAsync(linked.Token);

            if (!endpointInvoked)
            {
                return HostEndpointDispatchResult.Of(HostEndpointDispatchOutcome.Refused);
            }

            await httpContext.Response.CompleteAsync();

            if (responseBody.Exceeded)
            {
                return HostEndpointDispatchResult.Of(HostEndpointDispatchOutcome.ResponseTooLarge);
            }

            return new HostEndpointDispatchResult(
                HostEndpointDispatchOutcome.Completed,
                httpContext.Response.StatusCode,
                httpContext.Response.ContentType,
                responseBody.ToArray());
        }
        catch (Exception) when (responseBody.Exceeded)
        {
            return HostEndpointDispatchResult.Of(HostEndpointDispatchOutcome.ResponseTooLarge);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (OperationCanceledException) when (timeout.IsCancellationRequested)
        {
            return HostEndpointDispatchResult.Of(HostEndpointDispatchOutcome.TimedOut);
        }
        catch (Exception ex)
        {
            // §1.11 — the exception can quote arguments or row data; the type is enough to find it in the host's logs.
            logger.LogWarning("Host endpoint tool {Tool} failed with {ExceptionType}", tool.ToolName, ex.GetType().Name);

            return HostEndpointDispatchResult.Of(HostEndpointDispatchOutcome.Failed);
        }
        finally
        {
            if (invocation is { IsCompleted: false })
            {
                // The endpoint ignored its RequestAborted token: keep its scope alive until it finishes.
                _ = invocation.ContinueWith(
                    _ => CleanupAsync(scope, accessor, timeout, linked),
                    CancellationToken.None,
                    TaskContinuationOptions.None,
                    TaskScheduler.Default).Unwrap();
            }
            else
            {
                await CleanupAsync(scope, accessor, timeout, linked);
            }
        }
    }

    private static async Task CleanupAsync(
        AsyncServiceScope scope,
        IHttpContextAccessor? accessor,
        CancellationTokenSource timeout,
        CancellationTokenSource linked)
    {
        if (accessor != null)
        {
            accessor.HttpContext = null;
        }

        linked.Dispose();
        timeout.Dispose();
        await scope.DisposeAsync();
    }

    private static HttpContext CreateHttpContext(
        HostEndpointToolDescriptor tool,
        HostEndpointRequestParts parts,
        ClaimsPrincipal principal,
        IServiceProvider services,
        Stream responseBody,
        CancellationToken requestAborted)
    {
        var httpContext = new DefaultHttpContext
        {
            RequestServices = services,
            User = principal,
            TraceIdentifier = "beacon-mcp-" + Guid.NewGuid().ToString("N"),
            RequestAborted = requestAborted
        };

        var request = httpContext.Request;
        request.Protocol = "HTTP/1.1";
        request.Method = tool.HttpMethod;
        request.Scheme = "https";
        request.Host = new HostString(SyntheticHost);
        request.Path = parts.Path;
        request.QueryString = parts.QueryString;
        request.Headers.Accept = "application/json, text/plain;q=0.9, */*;q=0.1";

        // What a browser grid sends; lets host code that distinguishes AJAX reads (JSON, 401 instead of a login redirect) do so.
        request.Headers["X-Requested-With"] = "XMLHttpRequest";

        foreach (var header in parts.Headers)
        {
            request.Headers.Append(header.Key, header.Value);
        }

        var formFields = parts.FormFields.ToList();
        var tokens = MintAntiforgeryTokens(tool, principal, services);
        if (tokens != null)
        {
            var cookieName = services.GetRequiredService<IOptions<AntiforgeryOptions>>().Value.Cookie.Name;
            request.Headers.Cookie = $"{cookieName}={tokens.CookieToken}";

            if (tokens.HeaderName != null)
            {
                request.Headers[tokens.HeaderName] = tokens.RequestToken;
            }
            else
            {
                formFields.Add(new KeyValuePair<string, string>(tokens.FormFieldName, tokens.RequestToken ?? string.Empty));
            }
        }

        byte[] body = [];
        if (parts.JsonBody != null)
        {
            body = Encoding.UTF8.GetBytes(parts.JsonBody);
            request.ContentType = JsonContentType;
        }
        else if (formFields.Count > 0 || HostEndpointToolDescriptor.HasBody(tool.HttpMethod))
        {
            body = Encoding.UTF8.GetBytes(HostEndpointRequestBuilder.EncodeForm(formFields));
            request.ContentType = FormContentType;
        }

        request.Body = new MemoryStream(body, writable: false);
        request.ContentLength = body.Length;
        httpContext.Features.Set<IHttpRequestBodyDetectionFeature>(new RequestBodyDetectionFeature(body.Length > 0));

        request.RouteValues = new RouteValueDictionary(parts.RouteValues);
        httpContext.SetEndpoint(tool.Endpoint);
        httpContext.Response.Body = responseBody;

        return httpContext;
    }

    /// <summary>
    /// A server-minted antiforgery token pair for the host principal, so the host's own validation
    /// (<c>[ValidateAntiForgeryToken]</c>, <c>AutoValidateAntiforgeryToken</c>, minimal API form endpoints) runs and
    /// passes for this in-process request. CSRF cannot apply to it: it never came from a browser. Real requests are
    /// untouched — nothing is exempted, and the tokens never leave the process.
    /// </summary>
    private static AntiforgeryTokenSet? MintAntiforgeryTokens(HostEndpointToolDescriptor tool, ClaimsPrincipal principal, IServiceProvider services)
    {
        if (!HostEndpointToolDescriptor.HasBody(tool.HttpMethod))
        {
            return null;
        }

        var antiforgery = services.GetService<IAntiforgery>();
        if (antiforgery == null)
        {
            return null;
        }

        var tokenContext = new DefaultHttpContext
        {
            RequestServices = services,
            User = principal
        };
        tokenContext.Request.Scheme = "https";
        tokenContext.Request.Host = new HostString(SyntheticHost);

        return antiforgery.GetTokens(tokenContext);
    }

    /// <summary>
    /// What <c>UseAntiforgery()</c> does for a minimal API endpoint that requires validation: validate, and record
    /// the verdict in <see cref="IAntiforgeryValidationFeature"/> for the endpoint's form binding to honour.
    /// </summary>
    private static async Task ValidateMinimalApiAntiforgeryAsync(Endpoint endpoint, HttpContext httpContext, IServiceProvider services)
    {
        if (endpoint.Metadata.GetMetadata<IAntiforgeryMetadata>() is not { RequiresValidation: true }
            || !HostEndpointToolDescriptor.HasBody(httpContext.Request.Method))
        {
            return;
        }

        var antiforgery = services.GetService<IAntiforgery>();
        if (antiforgery == null)
        {
            return;
        }

        var valid = await antiforgery.IsRequestValidAsync(httpContext);
        httpContext.Features.Set<IAntiforgeryValidationFeature>(new AntiforgeryValidationResult(valid));
    }

    /// <summary>
    /// Evaluates the endpoint's authorization exactly as <c>AuthorizationMiddleware</c> would, but against the host
    /// principal and without re-authenticating through the host's schemes (the synthetic request carries no cookie).
    /// No authorization metadata means the host's fallback policy; no fallback policy either means refused.
    /// </summary>
    internal static async Task<bool> AuthorizeAsync(Endpoint endpoint, HttpContext httpContext, ClaimsPrincipal principal, IServiceProvider services)
    {
        var metadata = endpoint.Metadata;
        if (metadata.GetMetadata<IAllowAnonymous>() != null)
        {
            return true;
        }

        var policyProvider = services.GetService<IAuthorizationPolicyProvider>();
        var policyEvaluator = services.GetService<IPolicyEvaluator>();
        if (policyProvider == null || policyEvaluator == null)
        {
            return false;
        }

        var policy = await AuthorizationPolicy.CombineAsync(
            policyProvider,
            metadata.GetOrderedMetadata<IAuthorizeData>(),
            metadata.GetOrderedMetadata<AuthorizationPolicy>());

        var requirementData = metadata.GetOrderedMetadata<IAuthorizationRequirementData>();
        if (requirementData.Count > 0)
        {
            var builder = new AuthorizationPolicyBuilder();
            if (policy != null)
            {
                builder.Combine(policy);
            }

            foreach (var data in requirementData)
            {
                builder.AddRequirements(data.GetRequirements().ToArray());
            }

            policy = builder.Build();
        }

        policy ??= await policyProvider.GetFallbackPolicyAsync();
        if (policy == null)
        {
            return false;
        }

        var ticket = new AuthenticationTicket(principal, principal.Identity?.AuthenticationType ?? "BeaconMcp");
        var result = await policyEvaluator.AuthorizeAsync(policy, AuthenticateResult.Success(ticket), httpContext, httpContext);

        return result.Succeeded;
    }

    private sealed class RequestBodyDetectionFeature(bool canHaveBody) : IHttpRequestBodyDetectionFeature
    {
        public bool CanHaveBody { get; } = canHaveBody;
    }

    private sealed class AntiforgeryValidationResult(bool isValid) : IAntiforgeryValidationFeature
    {
        public bool IsValid { get; } = isValid;

        public Exception? Error => isValid ? null : new AntiforgeryValidationException("The antiforgery token minted for the Beacon tool call did not validate.");
    }
}

internal sealed class HostEndpointResponseTooLargeException() : IOException("The host endpoint response exceeded the configured size cap.");

/// <summary>A write-only response buffer that fails the write that would take it past the cap.</summary>
internal sealed class CappedResponseStream(int maxBytes) : Stream
{
    private readonly MemoryStream _buffer = new();

    public bool Exceeded { get; private set; }

    public override bool CanRead => false;

    public override bool CanSeek => false;

    public override bool CanWrite => true;

    public override long Length => _buffer.Length;

    public override long Position
    {
        get => _buffer.Length;
        set => throw new NotSupportedException();
    }

    public byte[] ToArray() => _buffer.ToArray();

    public override void Write(byte[] buffer, int offset, int count) => Write(buffer.AsSpan(offset, count));

    public override void Write(ReadOnlySpan<byte> buffer)
    {
        if (_buffer.Length + buffer.Length > maxBytes)
        {
            Exceeded = true;
            throw new HostEndpointResponseTooLargeException();
        }

        _buffer.Write(buffer);
    }

    public override Task WriteAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
    {
        Write(buffer.AsSpan(offset, count));

        return Task.CompletedTask;
    }

    public override ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
    {
        Write(buffer.Span);

        return ValueTask.CompletedTask;
    }

    public override void Flush()
    {
    }

    public override Task FlushAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();

    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

    public override void SetLength(long value) => throw new NotSupportedException();
}
