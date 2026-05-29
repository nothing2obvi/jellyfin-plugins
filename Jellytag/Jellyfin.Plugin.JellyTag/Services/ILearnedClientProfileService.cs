using MediaBrowser.Controller.Entities;
using Microsoft.AspNetCore.Http;

namespace Jellyfin.Plugin.JellyTag.Services;

/// <summary>
/// Stores image variants learned from real client requests for optional cache warming.
/// </summary>
public interface ILearnedClientProfileService
{
    /// <summary>
    /// Records a supported image variant requested by a real client.
    /// </summary>
    /// <param name="item">The requested item.</param>
    /// <param name="imageType">The requested image type.</param>
    /// <param name="query">The request query.</param>
    void RecordVariant(BaseItem item, string imageType, IQueryCollection query);

    /// <summary>
    /// Gets learned variants.
    /// </summary>
    /// <returns>The learned variants.</returns>
    IReadOnlyList<LearnedClientVariant> GetVariants();

    /// <summary>
    /// Clears all learned variants.
    /// </summary>
    void Clear();
}

/// <summary>
/// A normalized image variant learned from real client traffic.
/// </summary>
public sealed record LearnedClientVariant(string ImageType, string PhaseKey, string Label, IReadOnlyList<KeyValuePair<string, string>> Query);
