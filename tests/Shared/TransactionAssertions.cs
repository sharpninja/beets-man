using Xunit;

namespace BeetsMan.Desktop.Tests;

/// <summary>Shares the same observable transaction assertions across the mock and real-store gates.</summary>
/// <remarks>2026-11-24: Keep TEST-RECORDS-001 and TEST-RECORDS-002 assertions identical for TR-RECORDS-ARCH-001#ac-litedb-repositories.</remarks>
internal static class TransactionAssertions
{
    public static void VerifyRejected(string? draft, string? receipt)
    {
        Assert.Null(draft);
        Assert.Null(receipt);
    }

    public static void VerifyCommitted(string? draft, string? receipt)
    {
        Assert.Equal("staged-name", draft);
        Assert.Equal("accepted", receipt);
    }
}
