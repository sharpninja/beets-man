using System.Net;
using Xunit;

namespace BeetsMan.Host.Tests;

/// <summary>Checks the private playlist endpoint's authentication and concurrency contract.</summary>
/// <remarks>2026-11-24: TEST-RECORDS-001/002 for FR-RECORDS-001#ac-register and ac-offline.</remarks>
internal static class PlaylistHttpAssertions
{
    public static void Verify(HttpStatusCode missingCredential, HttpStatusCode wrongOrigin, HttpStatusCode wrongLibrary,
        HttpStatusCode matchingReceipt, HttpStatusCode staleWrite)
    {
        Assert.Equal(HttpStatusCode.Unauthorized, missingCredential);
        Assert.Equal(HttpStatusCode.Forbidden, wrongOrigin);
        Assert.Equal(HttpStatusCode.Forbidden, wrongLibrary);
        Assert.Equal(HttpStatusCode.OK, matchingReceipt);
        Assert.Equal(HttpStatusCode.Conflict, staleWrite);
    }
}
