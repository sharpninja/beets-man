using BeetsMan.Desktop.Application.Dispatch;
using BeetsMan.Host.Contracts.V1;

namespace BeetsMan.Desktop.Application.Replication;

/// <summary>Publishes a typed record through the authenticated host for this desktop and library.</summary>
/// <remarks>2026-11-25: Preserve the original record operation for FR-RECORDS-001#ac-replicate and ac-offline.</remarks>
public interface ILibraryRecordHost
{
    Task<Outcome<LibraryRecordReceipt>> PublishAsync(LibraryRecordIntent change, Guid operation, CancellationToken token);
}
