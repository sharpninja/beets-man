using BeetsMan.Host.Application;
using BeetsMan.Host.Contracts.V1;
using LiteDB;
using JsonSerializer = System.Text.Json.JsonSerializer;

namespace BeetsMan.Host.Adapters.Persistence;

/// <summary>Owns the durable versioned desktop registry and exact enrollment and revocation receipts.</summary>
/// <remarks>2026-11-25: Commit roster changes and their receipts together for FR-RECORDS-001#ac-register and TR-RECORDS-ARCH-001#ac-litedb-repositories.</remarks>
/// <remarks>2026-11-25: Persist bounded authenticated activity without advancing the registry for FR-RECORDS-001#ac-register.</remarks>
public sealed partial class LiteDbHostPlaylistStore : IHostDesktopRegistration, IHostDesktopActivity
{
    private const string DesktopRegistryHeads = "desktop_registry_heads";
    private const string DesktopRegistrationReceipt = "RegisterDesktop";
    private const string DesktopRevocationReceipt = "RevokeDesktop";
    private const string LastSeenField = "LastSeenUtcTicks";

    /// <inheritdoc />
    Task IHostDesktopActivity.ObserveAsync(Guid library, Guid instance, CancellationToken token) => OnLaneAsync<bool>(() =>
    {
        var instances = database.GetCollection<BsonDocument>("registered_instances");
        var row = instances.FindById(instance);
        if (row is null || !IsActiveDesktopRegistration(row, library))
            return new HostOutcome<bool>.Success(false);
        var current = activityClock.GetUtcNow().UtcTicks;
        if (ReadLastSeenTicks(row) is { } previous && current - previous < TimeSpan.TicksPerMinute)
            return new HostOutcome<bool>.Success(false);
        row[LastSeenField] = current;
        return new HostOutcome<bool>.Success(instances.Update(row));
    }, token);

    /// <inheritdoc />
    Task<HostOutcome<DesktopRegistrationDirectory>> IHostDesktopRegistration.ReadAsync(Guid library, AuthenticatedHostInstance caller,
        CancellationToken token) => OnLaneAsync<DesktopRegistrationDirectory>(() =>
    {
        var instances = database.GetCollection<BsonDocument>("registered_instances");
        var row = instances.FindById(caller.InstanceId);
        var revision = ReadDesktopRegistryRevision(library);
        if (row is null)
            return new HostOutcome<DesktopRegistrationDirectory>.Success(new(library, revision, [], false));
        return IsActiveDesktopRegistration(row, library)
            ? new HostOutcome<DesktopRegistrationDirectory>.Success(ProjectDesktopRoster(instances, library, revision, caller.CanManageRegistrations))
            : new HostOutcome<DesktopRegistrationDirectory>.Forbidden();
    }, token);

    /// <inheritdoc />
    Task<HostOutcome<RegisterDesktopResponse>> IHostDesktopRegistration.RegisterAsync(Guid library, AuthenticatedHostInstance caller,
        RegisterDesktopRequest request, CancellationToken token) => ExecuteAsync<RegisterDesktopResponse>(_ =>
    {
        var instances = database.GetCollection<BsonDocument>("registered_instances");
        var row = instances.FindById(caller.InstanceId);
        if (row is not null && !IsActiveDesktopRegistration(row, library))
            return new HostOutcome<RegisterDesktopResponse>.Forbidden();

        return RecordDesktopRegistryOperation<RegisterDesktopResponse, RegisterDesktopRequest>(library, caller, request.OperationId,
            request.ExpectedRevision, DesktopRegistrationReceipt, request, revision =>
        {
            var role = caller.CanManageRegistrations ? DesktopRegistrationRole.Operator : DesktopRegistrationRole.Listener;
            var currentName = Text(row, "Name");
            if (currentName is not null && currentName != request.Name)
                return RegistryConflict<RegisterDesktopResponse>("desktop-already-registered", revision);
            if (row is null || currentName is null || Text(row, "AccessRole") != role.ToString())
            {
                row ??= new BsonDocument
                {
                    ["_id"] = caller.InstanceId, ["LibraryId"] = library, ["Role"] = "Desktop", ["Status"] = "Active",
                    [LastSeenField] = activityClock.GetUtcNow().UtcTicks
                };
                row["Name"] = request.Name;
                row["AccessRole"] = role.ToString();
                instances.Upsert(row);
                revision = AdvanceDesktopRegistry(library, revision);
            }
            return new HostOutcome<RegisterDesktopResponse>.Success(new(library, request.OperationId, ProjectDesktopRegistration(row), revision));
        });
    }, token);

    /// <inheritdoc />
    Task<HostOutcome<RevokeDesktopResponse>> IHostDesktopRegistration.RevokeAsync(Guid library, AuthenticatedHostInstance caller,
        RevokeDesktopRequest request, CancellationToken token) => ExecuteAsync<RevokeDesktopResponse>(_ =>
    {
        var instances = database.GetCollection<BsonDocument>("registered_instances");
        if (!caller.CanManageRegistrations || caller.InstanceId == request.InstanceId ||
            instances.FindById(caller.InstanceId) is not { } actor || !IsActiveDesktopRegistration(actor, library))
            return new HostOutcome<RevokeDesktopResponse>.Forbidden();

        return RecordDesktopRegistryOperation<RevokeDesktopResponse, RevokeDesktopRequest>(library, caller, request.OperationId,
            request.ExpectedRevision, DesktopRevocationReceipt, request, revision =>
        {
            var target = instances.FindById(request.InstanceId);
            if (target is null || !IsDesktopRegistration(target, library) || Text(target, "Status") is not ("Active" or "Revoked"))
                return new HostOutcome<RevokeDesktopResponse>.Rejected("desktop-registration-not-found");
            if (IsActiveDesktopRegistration(target, library))
            {
                target["Status"] = "Revoked";
                instances.Update(target);
                revision = AdvanceDesktopRegistry(library, revision);
            }
            return new HostOutcome<RevokeDesktopResponse>.Success(new(request.OperationId, request.InstanceId,
                ProjectDesktopRoster(instances, library, revision, canManage: true)));
        });
    }, token);

    /// <summary>Resolves an exact authorized replay before checking the caller's reviewed registry revision.</summary>
    /// <remarks>2026-11-25: Use the same operation namespace as other host mutations for FR-RECORDS-001#ac-register.</remarks>
    private HostOutcome<TResult> RecordDesktopRegistryOperation<TResult, TRequest>(Guid library, AuthenticatedHostInstance actor,
        Guid operation, long expected, string type, TRequest request, Func<long, HostOutcome<TResult>> write) where TResult : class
    {
        var key = OperationId(new(actor.InstanceId, operation));
        var fingerprint = HashKey($"desktop-registry/v1/{library:N}/{type}/" + JsonSerializer.Serialize(request));
        var receipts = database.GetCollection<BsonDocument>("operation_receipts");
        if (receipts.FindById(key) is { } prior)
        {
            var stored = prior["Receipt"].AsDocument;
            return Text(prior, "IntentFingerprint") == fingerprint && Text(stored, "Type") == type && stored["Version"].AsInt32 == 1
                ? new HostOutcome<TResult>.Success(ReadStored<TResult>(stored["Result"]))
                : RegistryConflict<TResult>("operation-intent-mismatch");
        }

        var revision = ReadDesktopRegistryRevision(library);
        if (revision != expected)
            return RegistryConflict<TResult>("stale-instance-registry", revision);
        var result = write(revision);
        if (result is HostOutcome<TResult>.Success committed)
        {
            receipts.Insert(new BsonDocument
            {
                ["_id"] = key, ["OriginInstanceId"] = actor.InstanceId, ["OperationId"] = operation, ["IntentFingerprint"] = fingerprint,
                ["Receipt"] = new BsonDocument { ["Type"] = type, ["Version"] = 1, ["Result"] = JsonSerializer.Serialize(committed.Value) }
            });
        }
        return result;
    }

    private long ReadDesktopRegistryRevision(Guid library) =>
        database.GetCollection<BsonDocument>(DesktopRegistryHeads).FindById(library)?["Revision"].AsInt64 ?? 0;

    private long AdvanceDesktopRegistry(Guid library, long revision)
    {
        var next = checked(revision + 1);
        database.GetCollection<BsonDocument>(DesktopRegistryHeads).Upsert(new BsonDocument { ["_id"] = library, ["Revision"] = next });
        return next;
    }

    private static bool IsDesktopRegistration(BsonDocument row, Guid library) =>
        row["LibraryId"].IsGuid && row["LibraryId"].AsGuid == library && Text(row, "Role") == "Desktop";

    private static bool IsActiveDesktopRegistration(BsonDocument row, Guid library) =>
        IsDesktopRegistration(row, library) && Text(row, "Status") == "Active";

    private static DesktopRegistrationDirectory ProjectDesktopRoster(ILiteCollection<BsonDocument> instances, Guid library, long revision,
        bool canManage) => new(library, revision, [.. instances.Find(Query.EQ("LibraryId", library))
            .Where(row => IsDesktopRegistration(row, library) && Text(row, "Status") is "Active" or "Revoked")
            .Select(ProjectDesktopRegistration).OrderBy(row => row.Name, StringComparer.Ordinal).ThenBy(row => row.InstanceId)], canManage);

    private static DesktopRegistrationRow ProjectDesktopRegistration(BsonDocument row) =>
        new(row["_id"].AsGuid, Text(row, "Name") ?? row["_id"].AsGuid.ToString("D"),
            Text(row, "AccessRole") == nameof(DesktopRegistrationRole.Operator) ? DesktopRegistrationRole.Operator : DesktopRegistrationRole.Listener,
            Text(row, "Status") == "Revoked")
        { LastSeenUtc = ReadLastSeenTicks(row) is { } ticks ? new DateTimeOffset(ticks, TimeSpan.Zero) : null };

    private static long? ReadLastSeenTicks(BsonDocument row) => row[LastSeenField] is { IsInt64: true } timestamp &&
        timestamp.AsInt64 >= DateTime.MinValue.Ticks && timestamp.AsInt64 <= DateTime.MaxValue.Ticks ? timestamp.AsInt64 : null;

    private static HostOutcome<T> RegistryConflict<T>(string code, long? revision = null) =>
        new HostOutcome<T>.Rejected(code, revision, Conflict: true);
}
