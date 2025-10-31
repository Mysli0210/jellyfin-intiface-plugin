using System;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;

namespace Jellyfin.TheHandy.Controllers
{
    [ApiController]
    [Route("TheHandy")]
    public class TheHandyController : ControllerBase
    {
        private readonly ILogger<TheHandyController> _logger;

        public TheHandyController(ILogger<TheHandyController> logger)
        {
            _logger = logger;
        }

        // Client-side JS calls this endpoint before playback to provide per-client ws/user/pass.
        // The request should include:
        //   X-Intiface-WS: ws://host:port
        //   X-Intiface-User: username (optional)
        //   X-Intiface-Pass: password (optional)
        // The server will register these headers against a short-lived request id and return 204.
        [HttpPost("IntifaceClientPing/{itemId}")]
        public IActionResult IntifaceClientPing(string itemId)
        {
            try
            {
                // Create request id and store the headers in plugin pending map
                var requestId = Guid.NewGuid().ToString();
                var ws = Request.Headers.ContainsKey("X-Intiface-WS") ? Request.Headers["X-Intiface-WS"].ToString() : null;
                var user = Request.Headers.ContainsKey("X-Intiface-User") ? Request.Headers["X-Intiface-User"].ToString() : null;
                var pass = Request.Headers.ContainsKey("X-Intiface-Pass") ? Request.Headers["X-Intiface-Pass"].ToString() : null;

                // Register in plugin for the next playback request (plugin will look up by X-Intiface-Request-Id)
                if (!string.IsNullOrWhiteSpace(ws) && TheHandyPlugin.Instance != null)
                {
                    TheHandyPlugin.Instance.RegisterPendingRequestHeaders(requestId, ws, user, pass);
                    // Return the request id to the client so it can include X-Intiface-Request-Id on playback calls.
                    Response.Headers["X-Intiface-Request-Id"] = requestId;
                }

                return NoContent();
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "IntifaceClientPing failed");
                return StatusCode(500);
            }
        }
    }
}