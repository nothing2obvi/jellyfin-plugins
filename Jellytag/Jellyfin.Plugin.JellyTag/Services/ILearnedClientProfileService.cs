using System.Security.Claims;
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
    /// <param name="headers">The request headers.</param>
    /// <param name="user">The authenticated user principal.</param>
    void RecordVariant(BaseItem item, string imageType, IQueryCollection query, IHeaderDictionary headers, ClaimsPrincipal user);

    /// <summary>
    /// Gets learned variants.
    /// </summary>
    /// <returns>The learned variants.</returns>
    IReadOnlyList<LearnedClientVariant> GetVariants();

    /// <summary>
    /// Gets learned variants with read-only source information for the admin UI.
    /// </summary>
    /// <returns>The learned variant details.</returns>
    IReadOnlyList<LearnedClientVariantInfo> GetVariantInfo();

    /// <summary>
    /// Clears all learned variants.
    /// </summary>
    void Clear();
}

/// <summary>
/// A normalized image variant learned from real client traffic.
/// </summary>
public sealed record LearnedClientVariant(string ImageType, string PhaseKey, string Label, IReadOnlyList<KeyValuePair<string, string>> Query);

/// <summary>
/// Read-only details for a normalized image variant learned from real client traffic.
/// </summary>
public sealed record LearnedClientVariantInfo(
    string ImageType,
    string PhaseKey,
    string Label,
    IReadOnlyList<KeyValuePair<string, string>> Query,
    string Client,
    string DeviceName,
    string User,
    string UserId,
    string UserAgent,
    int SeenCount,
    DateTime? FirstSeenUtc,
    DateTime? LastSeenUtc);
