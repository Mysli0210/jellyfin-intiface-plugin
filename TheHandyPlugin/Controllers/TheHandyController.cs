using System.IO;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;
using Jellyfin.Controller; // adjust if namespace differs
using Jellyfin.Controller.Entities; // adjust
using Jellyfin.Controller.Library; // adjust - for ILibraryManager
using MediaBrowser.Model.Entities; // BaseItem
// NOTE: you might need to adjust using statements to match your project references

namespace Jellyfin.TheHandy.Controllers
{
    [ApiController]
    [Route("TheHandy")]
    public class TheHandyController : ControllerBase
    {
        private readonly ILogger<TheHandyController> _logger;
        private readonly ILibraryManager _libraryManager; // or another service used to resolve items

        public TheHandyController(ILogger<TheHandyController> logger, ILibraryManager libraryManager)
        {
            _logger = logger;
            _libraryManager = libraryManager;
        }

        // GET /TheHandy/HasFunscript/{itemId}
        [HttpGet("HasFunscript/{itemId}")]
        public IActionResult HasFunscript(string itemId)
        {
            if (string.IsNullOrWhiteSpace(itemId))
                return NotFound();

            // Try to fetch the item from the library
            var item = _libraryManager?.GetItemById(itemId); // method name may vary; adjust to actual API
            if (item == null)
            {
                _logger?.LogDebug("HasFunscript: item not found for id {ItemId}", itemId);
                return NotFound();
            }

            // Attempt to get a local path. Many items expose a Path or MediaSources. Adjust as needed.
            string mediaPath = null;
            try
            {
                mediaPath = item.Path; // adjust if property is different; otherwise inspect item.MediaSources to find local path
            }
            catch
            {
                // Fallback: inspect MediaSources
                try
                {
                    var ms = item.GetPrimaryMediaSource(); // pseudo; adjust to real call if available
                    mediaPath = ms?.Path;
                }
                catch { mediaPath = null; }
            }

            if (string.IsNullOrWhiteSpace(mediaPath))
            {
                _logger?.LogDebug("HasFunscript: no local path found for item {ItemId}", itemId);
                return NotFound();
            }

            var funscriptPath = Path.ChangeExtension(mediaPath, ".funscript");
            var exists = System.IO.File.Exists(funscriptPath);
            _logger?.LogInformation("HasFunscript: checking {FunscriptPath} -> exists={Exists}", funscriptPath, exists);
            return exists ? Ok() : NotFound();
        }

        // Optional: implement IntifaceClientPing/{itemId} if you want the server to create/return request id
        // [HttpPost("IntifaceClientPing/{itemId}")]
        // public IActionResult IntifaceClientPing(string itemId) { ... }
    }
}
