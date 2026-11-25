using BeetsMan.Desktop.Application.Dispatch;
using Xunit;
using Xunit.Sdk;

namespace BeetsMan.Desktop.Tests;

/// <summary>Proves the two-collection transaction checks with a standalone mock.</summary>
/// <remarks>2026-11-24: Prepare TEST-RECORDS-001 and TR-RECORDS-ARCH-001#ac-litedb-repositories validation before the adapter.</remarks>
public sealed class RepositoryTransactionMockTests
{
    [Fact]
    public void AtomicWriteAssertionsRejectPartialFailureAndMissingReceipt()
    {
        var ledger = new IndependentLedger();
        Assert.IsType<Outcome<string>.Conflict>(ledger.Apply(accept: false));
        TransactionAssertions.VerifyRejected(ledger.Draft, ledger.Receipt);
        Assert.IsType<Outcome<string>.Success>(ledger.Apply(accept: true));
        TransactionAssertions.VerifyCommitted(ledger.Draft, ledger.Receipt);
        Assert.ThrowsAny<XunitException>(() => TransactionAssertions.VerifyRejected("staged-name", null));
        Assert.ThrowsAny<XunitException>(() => TransactionAssertions.VerifyCommitted("staged-name", null));
    }

    private sealed class IndependentLedger
    {
        public string? Draft { get; private set; }
        public string? Receipt { get; private set; }

        public Outcome<string> Apply(bool accept)
        {
            const string proposedDraft = "staged-name";
            if (!accept)
                return new Outcome<string>.Conflict("revision-changed", 1);
            Draft = proposedDraft;
            Receipt = "accepted";
            return new Outcome<string>.Success(Receipt);
        }
    }
}
