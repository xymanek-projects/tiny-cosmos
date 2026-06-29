using Microsoft.Data.Sqlite;

namespace TinyCosmos.Core;

public sealed class StateStore
{
    private readonly string _connectionString;

    public StateStore(string databasePath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(databasePath);
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(databasePath))!);
        _connectionString = new SqliteConnectionStringBuilder { DataSource = databasePath }.ToString();
        SQLitePCL.Batteries_V2.Init();
    }

    public async Task InitializeAsync(CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        await ExecuteNonQueryAsync(connection, """
            PRAGMA journal_mode = WAL;
            PRAGMA foreign_keys = ON;
            CREATE TABLE IF NOT EXISTS schema_migrations (
                version INTEGER PRIMARY KEY,
                applied_at TEXT NOT NULL
            );
            CREATE TABLE IF NOT EXISTS groups (
                group_id TEXT PRIMARY KEY,
                owner_uid INTEGER NOT NULL,
                logical_name TEXT NOT NULL,
                host_workspace_path TEXT NULL,
                guest_project_path TEXT NULL,
                archived_at TEXT NULL,
                primary_sandbox_id TEXT NOT NULL,
                created_at TEXT NOT NULL,
                updated_at TEXT NOT NULL,
                UNIQUE(owner_uid, logical_name)
            );
            CREATE TABLE IF NOT EXISTS sandboxes (
                sandbox_id TEXT PRIMARY KEY,
                group_id TEXT NOT NULL REFERENCES groups(group_id) ON DELETE CASCADE,
                lifecycle_state TEXT NOT NULL,
                is_primary INTEGER NOT NULL,
                image_id TEXT NOT NULL,
                vcpu INTEGER NOT NULL,
                memory_mib INTEGER NOT NULL,
                system_disk_gib INTEGER NOT NULL,
                workspace_disk_gib INTEGER NOT NULL,
                cidr TEXT NOT NULL,
                gateway_address TEXT NOT NULL,
                primary_address TEXT NOT NULL,
                system_disk_id TEXT NOT NULL,
                workspace_disk_id TEXT NOT NULL,
                stop_reason TEXT NOT NULL,
                failure_category TEXT NULL,
                failure_guidance TEXT NULL,
                created_at TEXT NOT NULL,
                updated_at TEXT NOT NULL
            );
            CREATE TABLE IF NOT EXISTS sandbox_leases (
                sandbox_id TEXT PRIMARY KEY REFERENCES sandboxes(sandbox_id) ON DELETE CASCADE,
                owner_uid INTEGER NOT NULL,
                lease_owner TEXT NOT NULL,
                expires_at TEXT NOT NULL,
                updated_at TEXT NOT NULL
            );
            INSERT OR IGNORE INTO schema_migrations(version, applied_at) VALUES (1, datetime('now'));
            """, cancellationToken).ConfigureAwait(false);
    }

    public async Task<GroupBundle> GetOrCreateGroupAsync(CreateGroupRequest request, CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);

        var existing = await FindByLogicalNameAsync(connection, request.OwnerUid, request.LogicalName, cancellationToken).ConfigureAwait(false);
        if (existing is not null)
        {
            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
            return existing;
        }

        var bundle = NewBundle(request);
        await InsertBundleAsync(connection, bundle, cancellationToken).ConfigureAwait(false);
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        return bundle;
    }

    public async Task<GroupBundle> CreateGroupAsync(CreateGroupRequest request, CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);

        if (await FindByLogicalNameAsync(connection, request.OwnerUid, request.LogicalName, cancellationToken).ConfigureAwait(false) is not null)
        {
            throw new TinyCosmosException(new TinyCosmosError(TinyCosmosErrorCode.NameConflict, "Logical group name already exists."));
        }

        var bundle = NewBundle(request);
        await InsertBundleAsync(connection, bundle, cancellationToken).ConfigureAwait(false);
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        return bundle;
    }

    public async Task<IReadOnlyList<GroupBundle>> ListGroupsAsync(int ownerUid, CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        var results = new List<GroupBundle>();
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT g.group_id, g.owner_uid, g.logical_name, g.host_workspace_path, g.guest_project_path,
                   g.archived_at, g.primary_sandbox_id, g.created_at, g.updated_at,
                   s.sandbox_id, s.group_id, s.lifecycle_state, s.is_primary, s.image_id, s.vcpu, s.memory_mib,
                   s.system_disk_gib, s.workspace_disk_gib, s.cidr, s.gateway_address,
                   s.primary_address, s.system_disk_id, s.workspace_disk_id, s.stop_reason,
                   s.failure_category, s.failure_guidance, s.created_at, s.updated_at
            FROM groups g JOIN sandboxes s ON g.primary_sandbox_id = s.sandbox_id
            WHERE g.owner_uid = $owner_uid
            ORDER BY g.created_at;
            """;
        command.Parameters.AddWithValue("$owner_uid", ownerUid);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            results.Add(ReadBundle(reader));
        }

        return results;
    }

    public async Task<GroupBundle?> FindByGroupIdAsync(int ownerUid, GroupId groupId, CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        return await FindByGroupIdAsync(connection, ownerUid, groupId, cancellationToken).ConfigureAwait(false);
    }

    public async Task<IReadOnlyList<SandboxRecord>> ListSandboxesForGroupAsync(int ownerUid, GroupId groupId, CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        _ = await FindByGroupIdAsync(connection, ownerUid, groupId, cancellationToken).ConfigureAwait(false)
            ?? throw new TinyCosmosException(new TinyCosmosError(TinyCosmosErrorCode.GroupNotFound, "Group not found."));

        var sandboxes = new List<SandboxRecord>();
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT s.sandbox_id, s.group_id, s.lifecycle_state, s.is_primary, s.image_id, s.vcpu, s.memory_mib,
                   s.system_disk_gib, s.workspace_disk_gib, s.cidr, s.gateway_address, s.primary_address,
                   s.system_disk_id, s.workspace_disk_id, s.stop_reason, s.failure_category, s.failure_guidance,
                   s.created_at, s.updated_at
            FROM sandboxes s JOIN groups g ON s.group_id = g.group_id
            WHERE g.owner_uid = $owner_uid AND s.group_id = $group_id
            ORDER BY s.created_at;
            """;
        command.Parameters.AddWithValue("$owner_uid", ownerUid);
        command.Parameters.AddWithValue("$group_id", groupId.Value);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            sandboxes.Add(ReadSandbox(reader, 0));
        }

        return sandboxes;
    }

    public async Task<GroupBundle> ForkGroupAsync(
        int ownerUid,
        GroupId sourceGroupId,
        LogicalGroupName newLogicalName,
        CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);

        var source = await FindByGroupIdAsync(connection, ownerUid, sourceGroupId, cancellationToken).ConfigureAwait(false)
            ?? throw new TinyCosmosException(new TinyCosmosError(TinyCosmosErrorCode.GroupNotFound, "Source group not found."));
        if (await FindByLogicalNameAsync(connection, ownerUid, newLogicalName, cancellationToken).ConfigureAwait(false) is not null)
        {
            throw new TinyCosmosException(new TinyCosmosError(TinyCosmosErrorCode.NameConflict, "Fork target logical group name already exists."));
        }

        var fork = NewBundle(new CreateGroupRequest(
            newLogicalName,
            ownerUid,
            source.Group.Metadata,
            source.PrimarySandbox.ImageId,
            source.PrimarySandbox.Resources));
        await InsertBundleAsync(connection, fork, cancellationToken).ConfigureAwait(false);
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        return fork;
    }

    public async Task<GroupBundle> ReplacePrimarySandboxAsync(
        int ownerUid,
        GroupId groupId,
        string? imageId,
        ResourceAllocation? resources,
        CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);

        var existing = await FindByGroupIdAsync(connection, ownerUid, groupId, cancellationToken).ConfigureAwait(false)
            ?? throw new TinyCosmosException(new TinyCosmosError(TinyCosmosErrorCode.GroupNotFound, "Group not found."));
        if (existing.PrimarySandbox.LifecycleState is not (LifecycleState.Stopped or LifecycleState.Failed))
        {
            throw new TinyCosmosException(new TinyCosmosError(
                TinyCosmosErrorCode.LifecycleConflict,
                "Primary sandbox must be stopped or failed before replacement."));
        }

        var now = DateTimeOffset.UtcNow;
        var replacement = NewSandbox(
            existing.Group.GroupId,
            imageId ?? existing.PrimarySandbox.ImageId,
            resources ?? existing.PrimarySandbox.Resources,
            now);

        await using (var retire = connection.CreateCommand())
        {
            retire.CommandText = """
                UPDATE sandboxes
                   SET lifecycle_state = 'Retired',
                       is_primary = 0,
                       stop_reason = $stop_reason,
                       failure_category = NULL,
                       failure_guidance = NULL,
                       updated_at = $updated_at
                 WHERE sandbox_id = $sandbox_id;
                """;
            retire.Parameters.AddWithValue("$stop_reason", existing.PrimarySandbox.StopReason.ToString());
            retire.Parameters.AddWithValue("$updated_at", now.ToString("O"));
            retire.Parameters.AddWithValue("$sandbox_id", existing.PrimarySandbox.SandboxId.Value);
            _ = await retire.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }

        await InsertSandboxAsync(connection, replacement, cancellationToken).ConfigureAwait(false);
        await using (var updateGroup = connection.CreateCommand())
        {
            updateGroup.CommandText = """
                UPDATE groups
                   SET primary_sandbox_id = $primary_sandbox_id,
                       updated_at = $updated_at
                 WHERE owner_uid = $owner_uid AND group_id = $group_id;
                """;
            updateGroup.Parameters.AddWithValue("$primary_sandbox_id", replacement.SandboxId.Value);
            updateGroup.Parameters.AddWithValue("$updated_at", now.ToString("O"));
            updateGroup.Parameters.AddWithValue("$owner_uid", ownerUid);
            updateGroup.Parameters.AddWithValue("$group_id", groupId.Value);
            _ = await updateGroup.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }

        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        return (await FindByGroupIdAsync(ownerUid, groupId, cancellationToken).ConfigureAwait(false))!;
    }

    public async Task<GroupBundle> UpdateMetadataAsync(
        int ownerUid,
        GroupId groupId,
        LogicalGroupName? newLogicalName,
        GroupMetadata metadata,
        CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);

        var existing = await FindByGroupIdAsync(connection, ownerUid, groupId, cancellationToken).ConfigureAwait(false)
            ?? throw new TinyCosmosException(new TinyCosmosError(TinyCosmosErrorCode.GroupNotFound, "Group not found."));

        if (newLogicalName is not null && !string.Equals(newLogicalName.Value.Value, existing.Group.LogicalName.Value, StringComparison.Ordinal))
        {
            var conflict = await FindByLogicalNameAsync(connection, ownerUid, newLogicalName.Value, cancellationToken).ConfigureAwait(false);
            if (conflict is not null)
            {
                throw new TinyCosmosException(new TinyCosmosError(TinyCosmosErrorCode.NameConflict, "New logical group name already exists."));
            }
        }

        await using var command = connection.CreateCommand();
        command.CommandText = """
            UPDATE groups
               SET logical_name = $logical_name,
                   host_workspace_path = $host_workspace_path,
                   guest_project_path = $guest_project_path,
                   archived_at = $archived_at,
                   updated_at = $updated_at
             WHERE owner_uid = $owner_uid AND group_id = $group_id;
            """;
        command.Parameters.AddWithValue("$logical_name", (object?)newLogicalName?.Value ?? existing.Group.LogicalName.Value);
        command.Parameters.AddWithValue("$host_workspace_path", (object?)metadata.HostWorkspacePath ?? DBNull.Value);
        command.Parameters.AddWithValue("$guest_project_path", (object?)metadata.GuestProjectPath ?? DBNull.Value);
        command.Parameters.AddWithValue("$archived_at", (object?)metadata.ArchivedAt?.ToString("O") ?? DBNull.Value);
        command.Parameters.AddWithValue("$updated_at", DateTimeOffset.UtcNow.ToString("O"));
        command.Parameters.AddWithValue("$owner_uid", ownerUid);
        command.Parameters.AddWithValue("$group_id", groupId.Value);
        _ = await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);

        return (await FindByGroupIdAsync(ownerUid, groupId, cancellationToken).ConfigureAwait(false))!;
    }

    public async Task TransitionSandboxAsync(
        int ownerUid,
        SandboxId sandboxId,
        LifecycleState next,
        StopReason stopReason = StopReason.None,
        string? failureCategory = null,
        string? failureGuidance = null,
        CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);

        var current = await FindSandboxAsync(connection, ownerUid, sandboxId, cancellationToken).ConfigureAwait(false)
            ?? throw new TinyCosmosException(new TinyCosmosError(TinyCosmosErrorCode.SandboxNotFound, "Sandbox not found."));
        LifecycleRules.RequireTransition(current.LifecycleState, next);

        await using var command = connection.CreateCommand();
        command.CommandText = """
            UPDATE sandboxes
               SET lifecycle_state = $state,
                   stop_reason = $stop_reason,
                   failure_category = $failure_category,
                   failure_guidance = $failure_guidance,
                   updated_at = $updated_at
             WHERE sandbox_id = $sandbox_id;
            """;
        command.Parameters.AddWithValue("$state", next.ToString());
        command.Parameters.AddWithValue("$stop_reason", stopReason.ToString());
        command.Parameters.AddWithValue("$failure_category", (object?)failureCategory ?? DBNull.Value);
        command.Parameters.AddWithValue("$failure_guidance", (object?)failureGuidance ?? DBNull.Value);
        command.Parameters.AddWithValue("$updated_at", DateTimeOffset.UtcNow.ToString("O"));
        command.Parameters.AddWithValue("$sandbox_id", sandboxId.Value);
        _ = await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task TouchSandboxAsync(int ownerUid, SandboxId sandboxId, CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        _ = await FindSandboxAsync(connection, ownerUid, sandboxId, cancellationToken).ConfigureAwait(false)
            ?? throw new TinyCosmosException(new TinyCosmosError(TinyCosmosErrorCode.SandboxNotFound, "Sandbox not found."));

        await using var command = connection.CreateCommand();
        command.CommandText = """
            UPDATE sandboxes
               SET updated_at = $updated_at
             WHERE sandbox_id = $sandbox_id;
            """;
        command.Parameters.AddWithValue("$updated_at", DateTimeOffset.UtcNow.ToString("O"));
        command.Parameters.AddWithValue("$sandbox_id", sandboxId.Value);
        _ = await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task<IReadOnlyList<SandboxReconciliation>> ReconcileInterruptedSandboxesAsync(CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);

        var interrupted = new List<SandboxReconciliation>();
        await using (var select = connection.CreateCommand())
        {
            select.CommandText = """
                SELECT group_id, sandbox_id, lifecycle_state
                FROM sandboxes
                WHERE lifecycle_state IN ('Starting', 'Stopping')
                ORDER BY updated_at;
                """;
            await using var reader = await select.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                interrupted.Add(new SandboxReconciliation(
                    new GroupId(reader.GetString(0)),
                    new SandboxId(reader.GetString(1)),
                    Enum.Parse<LifecycleState>(reader.GetString(2)),
                    LifecycleState.Stopped,
                    StopReason.HostReconcile));
            }
        }

        if (interrupted.Count != 0)
        {
            await using var update = connection.CreateCommand();
            update.CommandText = """
                UPDATE sandboxes
                   SET lifecycle_state = 'Stopped',
                       stop_reason = 'HostReconcile',
                       failure_category = NULL,
                       failure_guidance = NULL,
                       updated_at = $updated_at
                 WHERE lifecycle_state IN ('Starting', 'Stopping');
                """;
            update.Parameters.AddWithValue("$updated_at", DateTimeOffset.UtcNow.ToString("O"));
            _ = await update.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }

        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        return interrupted;
    }

    public async Task<IReadOnlyList<GroupBundle>> ListIdleReadySandboxesAsync(DateTimeOffset idleBefore, CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        var results = new List<GroupBundle>();
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT g.group_id, g.owner_uid, g.logical_name, g.host_workspace_path, g.guest_project_path,
                   g.archived_at, g.primary_sandbox_id, g.created_at, g.updated_at,
                   s.sandbox_id, s.group_id, s.lifecycle_state, s.is_primary, s.image_id, s.vcpu, s.memory_mib,
                   s.system_disk_gib, s.workspace_disk_gib, s.cidr, s.gateway_address,
                   s.primary_address, s.system_disk_id, s.workspace_disk_id, s.stop_reason,
                   s.failure_category, s.failure_guidance, s.created_at, s.updated_at
            FROM groups g JOIN sandboxes s ON g.primary_sandbox_id = s.sandbox_id
            WHERE s.lifecycle_state = 'Ready' AND s.updated_at <= $idle_before
            ORDER BY s.updated_at;
            """;
        command.Parameters.AddWithValue("$idle_before", idleBefore.ToString("O"));
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            results.Add(ReadBundle(reader));
        }

        return results;
    }

    public async Task<SandboxLease> AcquireLeaseAsync(
        int ownerUid,
        SandboxId sandboxId,
        string leaseOwner,
        TimeSpan ttl,
        DateTimeOffset? now = null,
        CancellationToken cancellationToken = default)
    {
        ValidateLease(leaseOwner, ttl);
        var timestamp = now ?? DateTimeOffset.UtcNow;
        var expiresAt = timestamp.Add(ttl);
        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);

        _ = await FindSandboxAsync(connection, ownerUid, sandboxId, cancellationToken).ConfigureAwait(false)
            ?? throw new TinyCosmosException(new TinyCosmosError(TinyCosmosErrorCode.SandboxNotFound, "Sandbox not found."));
        var existing = await FindLeaseAsync(connection, sandboxId, cancellationToken).ConfigureAwait(false);
        if (existing is not null &&
            existing.ExpiresAt > timestamp &&
            (!string.Equals(existing.LeaseOwner, leaseOwner, StringComparison.Ordinal) || existing.OwnerUid != ownerUid))
        {
            throw new TinyCosmosException(new TinyCosmosError(TinyCosmosErrorCode.LifecycleConflict, "Sandbox lease is held by another owner."));
        }

        await using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO sandbox_leases(sandbox_id, owner_uid, lease_owner, expires_at, updated_at)
            VALUES($sandbox_id, $owner_uid, $lease_owner, $expires_at, $updated_at)
            ON CONFLICT(sandbox_id) DO UPDATE SET
                owner_uid = excluded.owner_uid,
                lease_owner = excluded.lease_owner,
                expires_at = excluded.expires_at,
                updated_at = excluded.updated_at;
            """;
        command.Parameters.AddWithValue("$sandbox_id", sandboxId.Value);
        command.Parameters.AddWithValue("$owner_uid", ownerUid);
        command.Parameters.AddWithValue("$lease_owner", leaseOwner);
        command.Parameters.AddWithValue("$expires_at", expiresAt.ToString("O"));
        command.Parameters.AddWithValue("$updated_at", timestamp.ToString("O"));
        _ = await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        return new SandboxLease(sandboxId, ownerUid, leaseOwner, expiresAt, timestamp);
    }

    public async Task<SandboxLease> RenewLeaseAsync(
        int ownerUid,
        SandboxId sandboxId,
        string leaseOwner,
        TimeSpan ttl,
        DateTimeOffset? now = null,
        CancellationToken cancellationToken = default)
    {
        ValidateLease(leaseOwner, ttl);
        var timestamp = now ?? DateTimeOffset.UtcNow;
        var expiresAt = timestamp.Add(ttl);
        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);

        var existing = await FindLeaseAsync(connection, sandboxId, cancellationToken).ConfigureAwait(false)
            ?? throw new TinyCosmosException(new TinyCosmosError(TinyCosmosErrorCode.LifecycleConflict, "Sandbox lease does not exist."));
        if (existing.OwnerUid != ownerUid || !string.Equals(existing.LeaseOwner, leaseOwner, StringComparison.Ordinal) || existing.ExpiresAt <= timestamp)
        {
            throw new TinyCosmosException(new TinyCosmosError(TinyCosmosErrorCode.LifecycleConflict, "Sandbox lease is not renewable by this owner."));
        }

        await using var command = connection.CreateCommand();
        command.CommandText = """
            UPDATE sandbox_leases
               SET expires_at = $expires_at,
                   updated_at = $updated_at
             WHERE sandbox_id = $sandbox_id;
            """;
        command.Parameters.AddWithValue("$expires_at", expiresAt.ToString("O"));
        command.Parameters.AddWithValue("$updated_at", timestamp.ToString("O"));
        command.Parameters.AddWithValue("$sandbox_id", sandboxId.Value);
        _ = await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        return new SandboxLease(sandboxId, ownerUid, leaseOwner, expiresAt, timestamp);
    }

    public async Task ReleaseLeaseAsync(int ownerUid, SandboxId sandboxId, string leaseOwner, CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            DELETE FROM sandbox_leases
            WHERE sandbox_id = $sandbox_id AND owner_uid = $owner_uid AND lease_owner = $lease_owner;
            """;
        command.Parameters.AddWithValue("$sandbox_id", sandboxId.Value);
        command.Parameters.AddWithValue("$owner_uid", ownerUid);
        command.Parameters.AddWithValue("$lease_owner", leaseOwner);
        var rows = await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        if (rows == 0)
        {
            throw new TinyCosmosException(new TinyCosmosError(TinyCosmosErrorCode.LifecycleConflict, "Sandbox lease is not held by this owner."));
        }
    }

    public async Task<int> ExpireLeasesAsync(DateTimeOffset now, CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = "DELETE FROM sandbox_leases WHERE expires_at <= $now;";
        command.Parameters.AddWithValue("$now", now.ToString("O"));
        return await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task DeleteGroupAsync(int ownerUid, GroupId groupId, CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = "DELETE FROM groups WHERE owner_uid = $owner_uid AND group_id = $group_id;";
        command.Parameters.AddWithValue("$owner_uid", ownerUid);
        command.Parameters.AddWithValue("$group_id", groupId.Value);
        var rows = await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        if (rows == 0)
        {
            throw new TinyCosmosException(new TinyCosmosError(TinyCosmosErrorCode.GroupNotFound, "Group not found."));
        }
    }

    private async Task<SqliteConnection> OpenAsync(CancellationToken cancellationToken)
    {
        var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
        await ExecuteNonQueryAsync(connection, "PRAGMA foreign_keys = ON;", cancellationToken).ConfigureAwait(false);
        return connection;
    }

    private static async Task ExecuteNonQueryAsync(SqliteConnection connection, string commandText, CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = commandText;
        _ = await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    private static GroupBundle NewBundle(CreateGroupRequest request)
    {
        var now = DateTimeOffset.UtcNow;
        var groupId = TinyId.NewGroupId();
        var sandbox = NewSandbox(groupId, request.ImageId, request.Resources, now);
        var group = new GroupRecord(groupId, request.LogicalName, request.OwnerUid, request.Metadata, sandbox.SandboxId, now, now);
        return new GroupBundle(group, sandbox);
    }

    private static SandboxRecord NewSandbox(GroupId groupId, string imageId, ResourceAllocation resources, DateTimeOffset now)
    {
        var sandboxId = TinyId.NewSandboxId();
        var subnetOctet = Math.Abs(groupId.Value.GetHashCode(StringComparison.Ordinal)) % 200 + 20;
        var network = new NetworkAllocation($"172.31.{subnetOctet}.0/24", $"172.31.{subnetOctet}.1", $"172.31.{subnetOctet}.2");
        return new SandboxRecord(
            sandboxId,
            groupId,
            LifecycleState.Stopped,
            true,
            imageId,
            resources,
            network,
            "disk_" + sandboxId.Value + "_system",
            "disk_" + sandboxId.Value + "_workspace",
            StopReason.None,
            null,
            null,
            now,
            now);
    }

    private static async Task InsertBundleAsync(SqliteConnection connection, GroupBundle bundle, CancellationToken cancellationToken)
    {
        await using var groupCommand = connection.CreateCommand();
        groupCommand.CommandText = """
            INSERT INTO groups(group_id, owner_uid, logical_name, host_workspace_path, guest_project_path,
                               archived_at, primary_sandbox_id, created_at, updated_at)
            VALUES($group_id, $owner_uid, $logical_name, $host_workspace_path, $guest_project_path,
                   $archived_at, $primary_sandbox_id, $created_at, $updated_at);
            """;
        BindGroup(groupCommand, bundle.Group);
        _ = await groupCommand.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);

        await InsertSandboxAsync(connection, bundle.PrimarySandbox, cancellationToken).ConfigureAwait(false);
    }

    private static async Task InsertSandboxAsync(SqliteConnection connection, SandboxRecord sandbox, CancellationToken cancellationToken)
    {
        await using var sandboxCommand = connection.CreateCommand();
        sandboxCommand.CommandText = """
            INSERT INTO sandboxes(sandbox_id, group_id, lifecycle_state, is_primary, image_id,
                                  vcpu, memory_mib, system_disk_gib, workspace_disk_gib,
                                  cidr, gateway_address, primary_address, system_disk_id, workspace_disk_id,
                                  stop_reason, failure_category, failure_guidance, created_at, updated_at)
            VALUES($sandbox_id, $group_id, $lifecycle_state, $is_primary, $image_id,
                   $vcpu, $memory_mib, $system_disk_gib, $workspace_disk_gib,
                   $cidr, $gateway_address, $primary_address, $system_disk_id, $workspace_disk_id,
                   $stop_reason, $failure_category, $failure_guidance, $created_at, $updated_at);
            """;
        BindSandbox(sandboxCommand, sandbox);
        _ = await sandboxCommand.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    private static void BindGroup(SqliteCommand command, GroupRecord group)
    {
        command.Parameters.AddWithValue("$group_id", group.GroupId.Value);
        command.Parameters.AddWithValue("$owner_uid", group.OwnerUid);
        command.Parameters.AddWithValue("$logical_name", group.LogicalName.Value);
        command.Parameters.AddWithValue("$host_workspace_path", (object?)group.Metadata.HostWorkspacePath ?? DBNull.Value);
        command.Parameters.AddWithValue("$guest_project_path", (object?)group.Metadata.GuestProjectPath ?? DBNull.Value);
        command.Parameters.AddWithValue("$archived_at", (object?)group.Metadata.ArchivedAt?.ToString("O") ?? DBNull.Value);
        command.Parameters.AddWithValue("$primary_sandbox_id", group.PrimarySandboxId.Value);
        command.Parameters.AddWithValue("$created_at", group.CreatedAt.ToString("O"));
        command.Parameters.AddWithValue("$updated_at", group.UpdatedAt.ToString("O"));
    }

    private static void BindSandbox(SqliteCommand command, SandboxRecord sandbox)
    {
        command.Parameters.AddWithValue("$sandbox_id", sandbox.SandboxId.Value);
        command.Parameters.AddWithValue("$group_id", sandbox.GroupId.Value);
        command.Parameters.AddWithValue("$lifecycle_state", sandbox.LifecycleState.ToString());
        command.Parameters.AddWithValue("$is_primary", sandbox.IsPrimary ? 1 : 0);
        command.Parameters.AddWithValue("$image_id", sandbox.ImageId);
        command.Parameters.AddWithValue("$vcpu", sandbox.Resources.Vcpu);
        command.Parameters.AddWithValue("$memory_mib", sandbox.Resources.MemoryMiB);
        command.Parameters.AddWithValue("$system_disk_gib", sandbox.Resources.SystemDiskGiB);
        command.Parameters.AddWithValue("$workspace_disk_gib", sandbox.Resources.WorkspaceDiskGiB);
        command.Parameters.AddWithValue("$cidr", sandbox.Network.Cidr);
        command.Parameters.AddWithValue("$gateway_address", sandbox.Network.GatewayAddress);
        command.Parameters.AddWithValue("$primary_address", sandbox.Network.PrimaryAddress);
        command.Parameters.AddWithValue("$system_disk_id", sandbox.SystemDiskId);
        command.Parameters.AddWithValue("$workspace_disk_id", sandbox.WorkspaceDiskId);
        command.Parameters.AddWithValue("$stop_reason", sandbox.StopReason.ToString());
        command.Parameters.AddWithValue("$failure_category", (object?)sandbox.FailureCategory ?? DBNull.Value);
        command.Parameters.AddWithValue("$failure_guidance", (object?)sandbox.FailureGuidance ?? DBNull.Value);
        command.Parameters.AddWithValue("$created_at", sandbox.CreatedAt.ToString("O"));
        command.Parameters.AddWithValue("$updated_at", sandbox.UpdatedAt.ToString("O"));
    }

    private static async Task<GroupBundle?> FindByLogicalNameAsync(SqliteConnection connection, int ownerUid, LogicalGroupName logicalName, CancellationToken cancellationToken)
    {
        return await FindOneAsync(connection, "g.owner_uid = $owner_uid AND g.logical_name = $logical_name", command =>
        {
            command.Parameters.AddWithValue("$owner_uid", ownerUid);
            command.Parameters.AddWithValue("$logical_name", logicalName.Value);
        }, cancellationToken).ConfigureAwait(false);
    }

    private static async Task<GroupBundle?> FindByGroupIdAsync(SqliteConnection connection, int ownerUid, GroupId groupId, CancellationToken cancellationToken)
    {
        return await FindOneAsync(connection, "g.owner_uid = $owner_uid AND g.group_id = $group_id", command =>
        {
            command.Parameters.AddWithValue("$owner_uid", ownerUid);
            command.Parameters.AddWithValue("$group_id", groupId.Value);
        }, cancellationToken).ConfigureAwait(false);
    }

    private static async Task<SandboxRecord?> FindSandboxAsync(SqliteConnection connection, int ownerUid, SandboxId sandboxId, CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT s.sandbox_id, s.group_id, s.lifecycle_state, s.is_primary, s.image_id, s.vcpu, s.memory_mib,
                   s.system_disk_gib, s.workspace_disk_gib, s.cidr, s.gateway_address, s.primary_address,
                   s.system_disk_id, s.workspace_disk_id, s.stop_reason, s.failure_category, s.failure_guidance,
                   s.created_at, s.updated_at
            FROM sandboxes s JOIN groups g ON s.group_id = g.group_id
            WHERE g.owner_uid = $owner_uid AND s.sandbox_id = $sandbox_id;
            """;
        command.Parameters.AddWithValue("$owner_uid", ownerUid);
        command.Parameters.AddWithValue("$sandbox_id", sandboxId.Value);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        return await reader.ReadAsync(cancellationToken).ConfigureAwait(false) ? ReadSandbox(reader, 0) : null;
    }

    private static async Task<SandboxLease?> FindLeaseAsync(SqliteConnection connection, SandboxId sandboxId, CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT sandbox_id, owner_uid, lease_owner, expires_at, updated_at
            FROM sandbox_leases
            WHERE sandbox_id = $sandbox_id;
            """;
        command.Parameters.AddWithValue("$sandbox_id", sandboxId.Value);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            return null;
        }

        return new SandboxLease(
            new SandboxId(reader.GetString(0)),
            reader.GetInt32(1),
            reader.GetString(2),
            DateTimeOffset.Parse(reader.GetString(3), null, System.Globalization.DateTimeStyles.RoundtripKind),
            DateTimeOffset.Parse(reader.GetString(4), null, System.Globalization.DateTimeStyles.RoundtripKind));
    }

    private static void ValidateLease(string leaseOwner, TimeSpan ttl)
    {
        if (string.IsNullOrWhiteSpace(leaseOwner) || leaseOwner.Length > 128 || leaseOwner.Any(char.IsControl))
        {
            throw new TinyCosmosException(new TinyCosmosError(TinyCosmosErrorCode.InvalidRequest, "Lease owner must be a printable token of at most 128 characters."));
        }

        if (ttl <= TimeSpan.Zero || ttl > TimeSpan.FromHours(24))
        {
            throw new TinyCosmosException(new TinyCosmosError(TinyCosmosErrorCode.InvalidRequest, "Lease ttl must be between zero and 24 hours."));
        }
    }

    private static async Task<GroupBundle?> FindOneAsync(SqliteConnection connection, string predicate, Action<SqliteCommand> bind, CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = $$"""
            SELECT g.group_id, g.owner_uid, g.logical_name, g.host_workspace_path, g.guest_project_path,
                   g.archived_at, g.primary_sandbox_id, g.created_at, g.updated_at,
                   s.sandbox_id, s.group_id, s.lifecycle_state, s.is_primary, s.image_id, s.vcpu, s.memory_mib,
                   s.system_disk_gib, s.workspace_disk_gib, s.cidr, s.gateway_address,
                   s.primary_address, s.system_disk_id, s.workspace_disk_id, s.stop_reason,
                   s.failure_category, s.failure_guidance, s.created_at, s.updated_at
            FROM groups g JOIN sandboxes s ON g.primary_sandbox_id = s.sandbox_id
            WHERE {{predicate}}
            LIMIT 1;
            """;
        bind(command);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        return await reader.ReadAsync(cancellationToken).ConfigureAwait(false) ? ReadBundle(reader) : null;
    }

    private static GroupBundle ReadBundle(SqliteDataReader reader)
    {
        var group = new GroupRecord(
            new GroupId(reader.GetString(0)),
            new LogicalGroupName(reader.GetString(2)),
            reader.GetInt32(1),
            new GroupMetadata(
                reader.IsDBNull(3) ? null : reader.GetString(3),
                reader.IsDBNull(4) ? null : reader.GetString(4),
                reader.IsDBNull(5) ? null : DateTimeOffset.Parse(reader.GetString(5), null, System.Globalization.DateTimeStyles.RoundtripKind)),
            new SandboxId(reader.GetString(6)),
            DateTimeOffset.Parse(reader.GetString(7), null, System.Globalization.DateTimeStyles.RoundtripKind),
            DateTimeOffset.Parse(reader.GetString(8), null, System.Globalization.DateTimeStyles.RoundtripKind));
        var sandbox = ReadSandbox(reader, 9);
        return new GroupBundle(group, sandbox);
    }

    private static SandboxRecord ReadSandbox(SqliteDataReader reader, int offset)
    {
        return new SandboxRecord(
            new SandboxId(reader.GetString(offset + 0)),
            new GroupId(reader.GetString(offset + 1)),
            Enum.Parse<LifecycleState>(reader.GetString(offset + 2)),
            reader.GetInt32(offset + 3) != 0,
            reader.GetString(offset + 4),
            new ResourceAllocation(reader.GetInt32(offset + 5), reader.GetInt64(offset + 6), reader.GetInt64(offset + 7), reader.GetInt64(offset + 8)),
            new NetworkAllocation(reader.GetString(offset + 9), reader.GetString(offset + 10), reader.GetString(offset + 11)),
            reader.GetString(offset + 12),
            reader.GetString(offset + 13),
            Enum.Parse<StopReason>(reader.GetString(offset + 14)),
            reader.IsDBNull(offset + 15) ? null : reader.GetString(offset + 15),
            reader.IsDBNull(offset + 16) ? null : reader.GetString(offset + 16),
            DateTimeOffset.Parse(reader.GetString(offset + 17), null, System.Globalization.DateTimeStyles.RoundtripKind),
            DateTimeOffset.Parse(reader.GetString(offset + 18), null, System.Globalization.DateTimeStyles.RoundtripKind));
    }
}

public sealed class TinyCosmosException(TinyCosmosError error) : Exception(error.Message)
{
    public TinyCosmosError Error { get; } = error;
}
