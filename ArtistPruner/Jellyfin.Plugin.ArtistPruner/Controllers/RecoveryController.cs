using Jellyfin.Plugin.ArtistPruner.Services;
using MediaBrowser.Common.Api;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.ArtistPruner.Controllers;

[ApiController]
[Authorize(Policy = Policies.RequiresElevation)]
[Route("ArtistPruner/Backups")]
public sealed class RecoveryController(RecoveryStore store, CleanupService cleanup, ILogger<RecoveryController> logger, PreviewStore preview) : ControllerBase
{
    [HttpGet("/ArtistPruner/Preview")]
    public IActionResult Preview() => Ok(preview.Read());

    [HttpGet]
    public IActionResult List()
    {
        try { return Ok(store.List().Select(p => new { p.Id, p.CreatedUtc, p.State, Albums = p.Changes.Count })); }
        catch (Exception ex) when (ex is InvalidDataException or IOException or System.Text.Json.JsonException or FormatException)
        { logger.LogWarning(ex, "Unable to list recovery points"); return Conflict(new { Message = "A recovery point is unreadable or corrupt. Preserve the files and check the server logs." }); }
    }
    [HttpPost("{id:guid}/Restore")]
    public async Task<IActionResult> Restore(Guid id, CancellationToken cancellationToken)
    {
        try { return Ok(new { Message = await cleanup.Restore(id, cancellationToken) }); }
        catch (Exception ex) when (ex is InvalidOperationException or InvalidDataException or IOException or System.Text.Json.JsonException)
        { logger.LogWarning(ex, "Unable to restore recovery point {Id}", id); return Conflict(new { Message = ex.Message }); }
    }
}
