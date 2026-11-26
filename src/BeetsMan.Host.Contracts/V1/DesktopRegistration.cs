using System.Collections.Immutable;
using System.Text.Json.Serialization;

namespace BeetsMan.Host.Contracts.V1;

/// <summary>Identifies the host-assigned access role of one registered desktop.</summary>
/// <remarks>2026-11-25: Project the STB09 Operator and Listener roles for FR-RECORDS-001#ac-register.</remarks>
public enum DesktopRegistrationRole { Listener, Operator }

/// <summary>Describes a registered desktop without credential material.</summary>
/// <remarks>2026-11-25: Expose the latest retained authenticated activity for FR-RECORDS-001#ac-register.</remarks>
public sealed record DesktopRegistrationRow(Guid InstanceId, string Name, DesktopRegistrationRole Role, bool Revoked)
{
    /// <summary>Gets the host observation time, or null for a legacy row without an observation.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public DateTimeOffset? LastSeenUtc { get; init; }
}

/// <summary>Returns a revision-pinned roster and the calling desktop's management permission.</summary>
public sealed record DesktopRegistrationDirectory(Guid LibraryId, long Revision, ImmutableArray<DesktopRegistrationRow> Instances, bool CanManage);

/// <summary>Registers the desktop identified by the credential and expected registry revision.</summary>
public sealed record RegisterDesktopRequest(Guid OriginInstanceId, Guid OperationId, string Name, long ExpectedRevision);

/// <summary>Confirms the accepted desktop identity and registration revision.</summary>
public sealed record RegisterDesktopResponse(Guid LibraryId, Guid OperationId, DesktopRegistrationRow Instance, long Revision);

/// <summary>Revokes the selected desktop using the caller's Operator permission and reviewed revision.</summary>
public sealed record RevokeDesktopRequest(Guid OriginInstanceId, Guid OperationId, Guid InstanceId, long ExpectedRevision);

/// <summary>Confirms the exact revocation operation and resulting roster.</summary>
public sealed record RevokeDesktopResponse(Guid OperationId, Guid InstanceId, DesktopRegistrationDirectory Directory);

/// <summary>Addresses the private roster, registration, and revocation endpoints.</summary>
/// <remarks>2026-11-25: Version the desktop registry separately from catalog revisions for FR-RECORDS-001#ac-register.</remarks>
public static class DesktopRegistrationRoutes
{
    public const string DirectoryPattern = "/api/v1/libraries/{libraryId:guid}/registered-desktops";
    public const string RevokePattern = DirectoryPattern + "/revoke";

    /// <summary>Addresses the configured library's roster and self-enrollment endpoint.</summary>
    public static string DirectoryPath(Guid library) => $"api/v1/libraries/{library:D}/registered-desktops";

    /// <summary>Addresses the configured library's revocation endpoint.</summary>
    public static string RevokePath(Guid library) => DirectoryPath(library) + "/revoke";

    /// <summary>Validates the display name identically at the desktop and host boundaries.</summary>
    public static bool IsValidName(string? value) => value is { Length: > 0 and <= 100 } &&
        !string.IsNullOrWhiteSpace(value) && !value.Any(char.IsControl);
}
