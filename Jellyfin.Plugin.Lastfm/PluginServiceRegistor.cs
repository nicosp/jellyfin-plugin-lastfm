using System.Net.Http;
using Jellyfin.Plugin.Lastfm.Api;
using MediaBrowser.Controller;
using MediaBrowser.Controller.Plugins;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.Lastfm
{
    /// <summary>
    /// Register Last.fm Scrobbler services
    /// </summary>
    public class PluginServiceRegistrator : IPluginServiceRegistrator
    {
        /// <inheritdoc />
        public void RegisterServices(IServiceCollection serviceCollection, IServerApplicationHost applicationHost)
        {
            // One client for the whole plugin, so there is a single scrobble queue and duplicate guard.
            serviceCollection.AddSingleton(sp => new LastfmApiClient(
                sp.GetRequiredService<IHttpClientFactory>(),
                sp.GetRequiredService<ILogger<LastfmApiClient>>()));
            serviceCollection.AddHostedService<ServerEntryPoint>();
        }
    }
}
