using System.Net;
using Xunit;
using Xunit.Sdk;

namespace BeetsMan.Host.Tests;

/// <summary>Shows the HTTP contract assertions detect a wrong identity or revision acceptance.</summary>
/// <remarks>2026-11-24: Prove the versioned transport assertions for TEST-RECORDS-001 before binding live endpoints.</remarks>
public sealed class PlaylistHttpMockTests
{
    [Fact]
    public void HttpReceiptsDiscriminateAuthenticationAndConcurrencyFailures()
    {
        PlaylistHttpAssertions.Verify(HttpStatusCode.Unauthorized, HttpStatusCode.Forbidden, HttpStatusCode.Forbidden,
            HttpStatusCode.OK, HttpStatusCode.Conflict);
        Assert.ThrowsAny<XunitException>(() => PlaylistHttpAssertions.Verify(HttpStatusCode.OK,
            HttpStatusCode.Forbidden, HttpStatusCode.Forbidden, HttpStatusCode.OK, HttpStatusCode.Conflict));
        Assert.ThrowsAny<XunitException>(() => PlaylistHttpAssertions.Verify(HttpStatusCode.Unauthorized,
            HttpStatusCode.OK, HttpStatusCode.Forbidden, HttpStatusCode.OK, HttpStatusCode.Conflict));
        Assert.ThrowsAny<XunitException>(() => PlaylistHttpAssertions.Verify(HttpStatusCode.Unauthorized,
            HttpStatusCode.Forbidden, HttpStatusCode.OK, HttpStatusCode.OK, HttpStatusCode.Conflict));
        Assert.ThrowsAny<XunitException>(() => PlaylistHttpAssertions.Verify(HttpStatusCode.Unauthorized,
            HttpStatusCode.Forbidden, HttpStatusCode.Forbidden, HttpStatusCode.OK, HttpStatusCode.OK));
    }
}
