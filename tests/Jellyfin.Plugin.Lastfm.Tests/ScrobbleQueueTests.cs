using System.Net;
using Jellyfin.Plugin.Lastfm.Api;
using Jellyfin.Plugin.Lastfm.Models;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Jellyfin.Plugin.Lastfm.Tests;

/// <summary>
/// Retry queue and rate limiting, per https://www.last.fm/api/scrobbling.
/// </summary>
[Collection(PluginInstanceCollection.Name)]
public sealed class ScrobbleQueueTests
{
    private static async Task Scrobble(ScrobbleTestContext context, string name)
    {
        var track = context.CreateTrack();
        track.Name = name;
        await context.Api.Scrobble(track, context.LastfmUser, DateTime.UtcNow);
    }

    private static List<string> ScrobbleBodies(ScrobbleTestContext context) =>
        context.Http.Requests
            .Where(r => r.Body.Contains("method=track.scrobble", StringComparison.Ordinal))
            .Select(r => r.Body)
            .ToList();

    [Fact]
    public async Task TemporaryFailuresAreQueuedAndSentAsOneBatchBeforeNewScrobbles()
    {
        var context = new ScrobbleTestContext();

        context.Http.Failure = "error"; // error 11: service offline
        await Scrobble(context, "first");
        await Scrobble(context, "second");
        Assert.Equal(2, context.Api.Queue.Count);

        context.Http.Failure = null;
        await Scrobble(context, "third");

        var bodies = ScrobbleBodies(context);
        Assert.Equal(4, bodies.Count);
        // The queued scrobbles go first, together, in order...
        Assert.Contains("track[0]=first", bodies[2]);
        Assert.Contains("track[1]=second", bodies[2]);
        // ...then the new one on its own.
        Assert.Contains("track=third", bodies[3]);
        Assert.Equal(0, context.Api.Queue.Count);
    }

    [Fact]
    public async Task QueuedScrobblesTooOldForLastfmAreDroppedInsteadOfSent()
    {
        var context = new ScrobbleTestContext();

        // As if loaded from disk after the server was down for over two weeks.
        foreach (var (name, age) in new[] { ("old", TimeSpan.FromDays(15)), ("recent", TimeSpan.FromDays(13)) })
        {
            context.Api.Queue.Enqueue(new QueuedScrobble
            {
                UserId = context.User.Id,
                Track = name,
                Artist = "artist",
                Timestamp = Utils.Helpers.ToTimestamp(DateTime.UtcNow - age)
            });
        }

        await context.Api.FlushQueueAsync(CancellationToken.None);

        var batch = ScrobbleBodies(context).Last();
        Assert.Contains("track[0]=recent", batch);
        Assert.DoesNotContain("old", batch);
        Assert.Equal(0, context.Api.Queue.Count);
    }

    [Fact]
    public async Task QueuedScrobblesSurviveARestart()
    {
        var context = new ScrobbleTestContext();
        context.Http.Failure = "exception";

        await Scrobble(context, "offline");

        var reloaded = new ScrobbleQueue(NullLogger.Instance, () => context.QueuePath);
        var queued = Assert.Single(reloaded.PeekBatch(context.User.Id, 50));
        Assert.Equal("offline", queued.Track);
    }

    [Fact]
    public async Task PermanentErrorsAreNotQueued()
    {
        var context = new ScrobbleTestContext();
        context.Http.Failure = "rejected"; // error 6

        await Scrobble(context, "malformed");

        Assert.Equal(0, context.Api.Queue.Count);
    }

    [Fact]
    public async Task InvalidSessionKeyHoldsScrobblesUntilTheAccountIsRelinked()
    {
        var context = new ScrobbleTestContext();
        context.Http.Failure = "session"; // error 9
        await Scrobble(context, "held");
        context.Http.Failure = null;

        await context.Api.FlushQueueAsync(CancellationToken.None);
        Assert.Single(ScrobbleBodies(context)); // Same rejected key: nothing resent.
        Assert.Equal(1, context.Api.Queue.Count);

        context.LastfmUser.SessionKey = "relinked";
        await context.Api.FlushQueueAsync(CancellationToken.None);

        Assert.Equal(2, ScrobbleBodies(context).Count);
        Assert.Equal(0, context.Api.Queue.Count);
    }

    [Fact]
    public async Task RejectedBatchIsRetriedIndividuallySoOneBadScrobbleDoesNotBlockTheRest()
    {
        var context = new ScrobbleTestContext();
        context.Http.Failure = "error";
        await Scrobble(context, "good-1");
        await Scrobble(context, "bad");
        await Scrobble(context, "good-2");
        context.Http.Failure = null;

        // Reject any batch containing the bad scrobble.
        context.Http.Responder = body => body.Contains("=bad", StringComparison.Ordinal)
            ? "{\"error\":6,\"message\":\"Invalid parameters\"}"
            : null;

        await context.Api.FlushQueueAsync(CancellationToken.None);

        var resent = ScrobbleBodies(context).Skip(3).ToList();
        Assert.Contains(resent, b => b.Contains("track[0]=good-1", StringComparison.Ordinal) && !b.Contains("track[1]", StringComparison.Ordinal));
        Assert.Contains(resent, b => b.Contains("track[0]=good-2", StringComparison.Ordinal));
        Assert.Equal(0, context.Api.Queue.Count);
    }

    [Fact]
    public async Task Http429PausesAllRequestsUntilRetryAfterThenSendsTheQueue()
    {
        var context = new ScrobbleTestContext();
        context.Http.Failure = "429"; // Retry-After: 120 seconds

        await Scrobble(context, "limited");
        Assert.True(LastfmRateLimit.IsLimited(out var remaining));
        Assert.Equal(TimeSpan.FromSeconds(120), remaining);

        // While paused nothing is sent at all; new scrobbles are queued.
        context.Http.Failure = null;
        var requestsBefore = context.Http.Requests.Count;
        await Scrobble(context, "during-pause");
        await context.Api.FlushQueueAsync(CancellationToken.None);
        Assert.Equal(requestsBefore, context.Http.Requests.Count);
        Assert.Equal(2, context.Api.Queue.Count);

        context.Clock.UtcNow += TimeSpan.FromSeconds(121);
        await context.Api.FlushQueueAsync(CancellationToken.None);

        var batch = ScrobbleBodies(context).Last();
        Assert.Contains("track[0]=limited", batch);
        Assert.Contains("track[1]=during-pause", batch);
        Assert.Equal(0, context.Api.Queue.Count);
    }

    [Fact]
    public async Task Error29WithoutRetryAfterBacksOffExponentially()
    {
        var context = new ScrobbleTestContext();
        context.Http.Failure = "ratelimit";

        await Scrobble(context, "first");
        Assert.True(LastfmRateLimit.IsLimited(out var first));
        Assert.Equal(LastfmRateLimit.InitialBackoff, first);

        context.Clock.UtcNow += first;
        await context.Api.FlushQueueAsync(CancellationToken.None); // Still limited by Last.fm.
        Assert.True(LastfmRateLimit.IsLimited(out var second));
        Assert.Equal(LastfmRateLimit.InitialBackoff * 2, second);

        context.Clock.UtcNow += second;
        context.Http.Failure = null;
        await context.Api.FlushQueueAsync(CancellationToken.None);
        Assert.False(LastfmRateLimit.IsLimited(out _));
        Assert.Equal(0, context.Api.Queue.Count);
    }

    [Fact]
    public void BackoffIsCappedAndRetryAfterIsHonoured()
    {
        LastfmRateLimit.Reset();
        var now = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        LastfmRateLimit.UtcNow = () => now;

        for (var i = 0; i < 10; i++)
        {
            LastfmRateLimit.RegisterLimit(new HttpResponseMessage(HttpStatusCode.TooManyRequests));
            now += LastfmRateLimit.MaxBackoff;
        }

        var response = new HttpResponseMessage(HttpStatusCode.TooManyRequests);
        response.Headers.RetryAfter = new System.Net.Http.Headers.RetryConditionHeaderValue(TimeSpan.FromHours(5));
        Assert.Equal(LastfmRateLimit.MaxBackoff, LastfmRateLimit.RegisterLimit(response));

        LastfmRateLimit.Reset();
    }
}
