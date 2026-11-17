using Jellyfin.Plugin.Lastfm.Api;
using MediaBrowser.Controller.Library;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Jellyfin.Plugin.Lastfm.Tests;

/// <summary>
/// Protects the login endpoint and the playback event handlers against previously identified issues.
/// </summary>
[Collection(PluginInstanceCollection.Name)]
public sealed class LoginAndHandlerTests
{
    [Fact]
    public async Task LoginHostOverrideDoesNotRedirectOtherUsersScrobbles()
    {
        var context = new ScrobbleTestContext();
        context.Http.LoginGate = new TaskCompletionSource();
        var api = new RestApi(new LastfmApiClient(new FakeFactory(context.Http), NullLogger.Instance), NullLogger<RestApi>.Instance);

        // The login request is held open inside the HTTP call...
        var login = api.CreateMobileSession(new LastFMUser
        {
            Username = "someone",
            Password = "secret",
            ApiHost = "attacker.example"
        });

        // ...while another user's scrobble goes out, carrying their session key.
        var track = context.CreateTrack();
        context.Start(track);
        context.Stop(track, track.RunTimeTicks!.Value);

        context.Http.LoginGate.SetResult();
        await login;

        var requests = context.Http.Requests;
        var loginRequest = Assert.Single(requests, r => r.Body.Contains("method=auth.getMobileSession", StringComparison.Ordinal));
        var scrobbleRequest = Assert.Single(requests, r => r.Body.Contains("method=track.scrobble", StringComparison.Ordinal));

        Assert.Equal("attacker.example", loginRequest.Uri.Host);
        Assert.Equal("ws.audioscrobbler.com", scrobbleRequest.Uri.Host);
        Assert.Equal("ws.audioscrobbler.com", Plugin.Instance!.PluginConfiguration.LastfmApiHost);
    }

    [Fact]
    public void ScrobbleFollowsLastfmClientGuidelines()
    {
        var context = new ScrobbleTestContext();
        var track = context.CreateTrack();
        track.IndexNumber = 7;

        context.Start(track);
        context.Stop(track, track.RunTimeTicks!.Value);

        var scrobble = Assert.Single(context.Http.Requests, r => r.Body.Contains("method=track.scrobble", StringComparison.Ordinal));
        Assert.Equal("https", scrobble.Uri.Scheme);
        Assert.Contains("duration=200", scrobble.Body);
        Assert.Contains("trackNumber=7", scrobble.Body);
        Assert.All(context.Http.UserAgents, ua => Assert.Contains("Jellyfin-Plugin-Lastfm/", ua));
    }

    [Fact]
    public void HandlerExceptionIsLoggedInsteadOfEscaping()
    {
        var context = new ScrobbleTestContext();

        // Users = null makes the handler throw. In an async void method an escaping
        // exception would terminate the process, taking the test host down with it.
        context.Invoke("PlaybackStart", new PlaybackStartEventArgs
        {
            Item = context.CreateTrack(),
            Users = null!
        });

        Assert.Empty(context.Http.Requests);
    }
}
