namespace BeetsMan.Desktop.Adapters.Host;

/// <summary>Resolves command paths relative to the registered library host.</summary>
/// <remarks>2026-11-24: Share endpoint validation for FR-RECORDS-001#ac-register and FR-FILESYNC-001#ac-playlist-drop.</remarks>
internal static class RegisteredHostEndpoint
{
    /// <summary>Builds an HTTPS endpoint under the configured host base path and library ID.</summary>
    internal static Uri Resolve(Uri host, Guid libraryId, Func<Guid, string> commandPath)
    {
        if (!host.IsAbsoluteUri || host.Scheme != Uri.UriSchemeHttps || host.UserInfo.Length != 0 ||
            host.Query.Length != 0 || host.Fragment.Length != 0 || libraryId == Guid.Empty)
            throw new ArgumentException("A registered HTTPS host and library are required.", nameof(host));
        return new(new Uri(host.AbsoluteUri.TrimEnd('/') + '/'), commandPath(libraryId));
    }
}
