using Microsoft.Extensions.Logging;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Jellyfin.Plugin.Lastfm.Models.Responses;

namespace Jellyfin.Plugin.Lastfm.Api
{

    /// <summary>
    /// Exchanges Last.fm credentials for a session key. Used by the plugin configuration page.
    /// </summary>
    /// <remarks>
    /// Restricted to administrators: the endpoint makes outbound requests to a caller-supplied host,
    /// and only admins can open the configuration page that calls it.
    /// </remarks>
    [ApiController]
    [Authorize(Policy = "RequiresElevation")]
    [Route("Lastfm/Login")]
    public class RestApi : ControllerBase
    {
        private readonly LastfmApiClient _apiClient;
        private readonly ILogger<RestApi> _logger;

        public RestApi(LastfmApiClient apiClient, ILogger<RestApi> logger)
        {
            _apiClient = apiClient;
            _logger = logger;
        }

        [HttpPost]
        [Consumes("application/json")]
        public async Task<ActionResult<MobileSessionResponse>> CreateMobileSession([FromBody] LastFMUser lastFMUser)
        {
            if (lastFMUser == null || string.IsNullOrWhiteSpace(lastFMUser.Username) || string.IsNullOrWhiteSpace(lastFMUser.Password))
            {
                return BadRequest("Username and password are required.");
            }

            _logger.LogInformation("Fetching Last.fm mobilesession auth for Username={Username}", lastFMUser.Username);

            // The host is passed per request rather than written into the shared plugin configuration,
            // so concurrent scrobbles for other users keep going to the configured host.
            return await _apiClient.RequestSession(lastFMUser.Username, lastFMUser.Password, lastFMUser.ApiHost).ConfigureAwait(false);
        }
    }

    public class LastFMUser
    {
        public string Username { get; set; }
        public string Password { get; set; }
        public string ApiHost { get; set; }
    }
}
