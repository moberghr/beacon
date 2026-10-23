using Beacon.Core.Authentication;
using FluentAssertions;
using Microsoft.Extensions.Time.Testing;
using NUnit.Framework;

namespace Beacon.Tests.Unit;

[TestFixture]
public class JwksSigningKeyCacheBackOffTests
{
    private const string Endpoint = "https://idp.example/jwks";

    [Test]
    public async Task SlowFailingFetch_BacksOffFromTheFailureNotFromTheStart()
    {
        var time = new FakeTimeProvider();
        var cache = new JwksSigningKeyCache(time);
        var fetches = 0;
        var started = new TaskCompletionSource();
        var release = new TaskCompletionSource();

        async Task<string> SlowFailure(CancellationToken cancellationToken)
        {
            fetches++;
            started.TrySetResult();
            await release.Task;
            time.Advance(JwksSigningKeyCache.FailureRetryInterval + TimeSpan.FromSeconds(5));

            throw new InvalidOperationException("fetch failed");
        }

        var first = cache.GetKeysAsync(Endpoint, SlowFailure, false, CancellationToken.None);
        await started.Task;
        var queued = cache.GetKeysAsync(Endpoint, SlowFailure, false, CancellationToken.None);
        release.SetResult();

        var firstAct = async () => await first;
        await firstAct.Should().ThrowAsync<InvalidOperationException>();
        var queuedAct = async () => await queued;
        await queuedAct.Should().ThrowAsync<JwksSigningKeyCache.RetryNotDueException>();
        fetches.Should().Be(1);

        var laterAct = async () => await cache.GetKeysAsync(Endpoint, SlowFailure, false, CancellationToken.None);
        await laterAct.Should().ThrowAsync<JwksSigningKeyCache.RetryNotDueException>();
        fetches.Should().Be(1);

        time.Advance(JwksSigningKeyCache.FailureRetryInterval);
        var afterAct = async () => await cache.GetKeysAsync(Endpoint, SlowFailure, false, CancellationToken.None);
        await afterAct.Should().ThrowAsync<InvalidOperationException>();
        fetches.Should().Be(2);
    }
}
