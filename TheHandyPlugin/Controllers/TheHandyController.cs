using System;
using System.IO;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;
using MediaBrowser.Controller.Library;
using MediaBrowser.Model.Entities;

namespace Jellyfin.TheHandy.Controllers
{
    [ApiController]
    [Route("TheHandy")]
    public class TheHandyController : ControllerBase
    {
        private readonly ILogger<TheHandyController> _logger;
        private readonly ILibraryManager _libraryManager;

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

            try
            {
                // Try to resolve item by id
                BaseItem item = _libraryManager?.GetItemById(itemId);
                if (item == null)
                {
                    _logger?.LogDebug("HasFunscript: item not found for id {ItemId}", itemId);
                    return NotFound();
                }

                // Prefer the explicit Path if present
                string mediaPath = item.Path;

                // If no Path property, we cannot determine local file; treat as not found
                if (string.IsNullOrWhiteSpace(mediaPath))
                {
                    _logger?.LogDebug("HasFunscript: no local path for item {ItemId}", itemId);
                    return NotFound();
                }

                // Look for a .funscript sidecar with the same base name
                var funscriptPath = Path.ChangeExtension(mediaPath, ".funscript");
                var exists = System.IO.File.Exists(funscriptPath);
                _logger?.LogInformation("HasFunscript: checking {FunscriptPath} -> exists={Exists} for item {ItemId}", funscriptPath, exists, itemId);
                return exists ? Ok() : NotFound();
            }
            catch (Exception ex)
            {
                _logger?.LogError(ex, "HasFunscript: exception checking item {ItemId}", itemId);
                return StatusCode(500);
            }
        }
    }
}
