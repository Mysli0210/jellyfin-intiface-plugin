using System;
using System.IO;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;

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
                // Resolve item by id
                BaseItem item = _libraryManager?.GetItemById(itemId);
                if (item == null)
                {
                    _logger?.LogDebug("HasFunscript: item not found for id {ItemId}", itemId);
                    return NotFound();
                }

                // Prefer the explicit Path if present
                string mediaPath = item.Path;

                if (string.IsNullOrWhiteSpace(mediaPath))
                {
                    _logger?.LogDebug("HasFunscript: no local path for item {ItemId}", itemId);
                    return NotFound();
                }

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
