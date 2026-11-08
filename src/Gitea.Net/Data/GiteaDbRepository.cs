using System.Data;
using System.Data.Common;
using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using Gitea.Net.Models;
using Microsoft.Data.Sqlite;
using Npgsql;

namespace Gitea.Net.Data;

/// <summary>
/// Wraps a DbConnection and DbCommand to count exact SQL statements executed per HTTP request or test scope,
/// providing deterministic programmatic proof of Zero N+1 queries.
/// </summary>
public sealed class SqlQueryCounter
{
    private static readonly AsyncLocal<CounterState?> CurrentState = new();

    private sealed class CounterState
    {
        public int Count;
        public List<string> Statements { get; } = [];
    }

    public static IDisposable BeginScope()
    {
        var state = new CounterState();
        CurrentState.Value = state;
        return new ScopeHandle();
    }

    public static int Count => CurrentState.Value?.Count ?? 0;
    public static IReadOnlyList<string> Statements => CurrentState.Value?.Statements ?? [];

    internal static void Record(string sql)
    {
        var state = CurrentState.Value;
        if (state is not null)
        {
            Interlocked.Increment(ref state.Count);
            lock (state.Statements)
            {
                state.Statements.Add(sql);
            }
        }
    }

    private sealed class ScopeHandle : IDisposable
    {
        public void Dispose() => CurrentState.Value = null;
    }
}

public sealed class CountingDbCommand(DbCommand inner) : DbCommand
{
    [AllowNull]
    public override string CommandText
    {
        get => inner.CommandText;
        set => inner.CommandText = value;
    }

    public override int CommandTimeout
    {
        get => inner.CommandTimeout;
        set => inner.CommandTimeout = value;
    }

    public override CommandType CommandType
    {
        get => inner.CommandType;
        set => inner.CommandType = value;
    }

    public override UpdateRowSource UpdatedRowSource
    {
        get => inner.UpdatedRowSource;
        set => inner.UpdatedRowSource = value;
    }

    protected override DbConnection? DbConnection
    {
        get => inner.Connection;
        set => inner.Connection = value;
    }

    protected override DbParameterCollection DbParameterCollection => inner.Parameters;

    protected override DbTransaction? DbTransaction
    {
        get => inner.Transaction;
        set => inner.Transaction = value;
    }

    public override bool DesignTimeVisible
    {
        get => inner.DesignTimeVisible;
        set => inner.DesignTimeVisible = value;
    }

    public override void Cancel() => inner.Cancel();

    public override int ExecuteNonQuery()
    {
        SqlQueryCounter.Record(inner.CommandText);
        return inner.ExecuteNonQuery();
    }

    public override async Task<int> ExecuteNonQueryAsync(CancellationToken cancellationToken)
    {
        SqlQueryCounter.Record(inner.CommandText);
        return await inner.ExecuteNonQueryAsync(cancellationToken);
    }

    public override object? ExecuteScalar()
    {
        SqlQueryCounter.Record(inner.CommandText);
        return inner.ExecuteScalar();
    }

    public override async Task<object?> ExecuteScalarAsync(CancellationToken cancellationToken)
    {
        SqlQueryCounter.Record(inner.CommandText);
        return await inner.ExecuteScalarAsync(cancellationToken);
    }

    public override void Prepare() => inner.Prepare();

    protected override DbParameter CreateDbParameter() => inner.CreateParameter();

    protected override DbDataReader ExecuteDbDataReader(CommandBehavior behavior)
    {
        SqlQueryCounter.Record(inner.CommandText);
        return inner.ExecuteReader(behavior);
    }

    protected override async Task<DbDataReader> ExecuteDbDataReaderAsync(CommandBehavior behavior, CancellationToken cancellationToken)
    {
        SqlQueryCounter.Record(inner.CommandText);
        return await inner.ExecuteReaderAsync(behavior, cancellationToken);
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing) inner.Dispose();
        base.Dispose(disposing);
    }
}

public sealed class DuplicateResourceException(string message) : Exception(message);

public sealed class GiteaDbRepository
{
    private readonly string _provider;
    private readonly string _connectionString;
    private readonly ILogger<GiteaDbRepository> _logger;

    public GiteaDbRepository(IConfiguration configuration, IHostEnvironment env, ILogger<GiteaDbRepository> logger)
    {
        _provider = configuration["Database:Provider"]
                    ?? Environment.GetEnvironmentVariable("GITEA_TEST_PROVIDER")
                    ?? "Sqlite";
        _logger = logger;

        if (IsPostgres)
        {
            string? pgConn = configuration["Database:PostgresConnectionString"]
                             ?? Environment.GetEnvironmentVariable("GITEA_POSTGRES_CONN");
            if (string.IsNullOrWhiteSpace(pgConn))
            {
                throw new InvalidOperationException(
                    "Database:PostgresConnectionString (or env GITEA_POSTGRES_CONN) is required when Provider=Postgres.");
            }
            _connectionString = pgConn;
        }
        else
        {
            string rawConn = configuration["Database:SqliteConnectionString"] ?? "Data Source=App_Data/gitea_net.db;Mode=ReadWriteCreate;";
            var builder = new SqliteConnectionStringBuilder(rawConn);
            if (!string.IsNullOrWhiteSpace(builder.DataSource) &&
                builder.DataSource != ":memory:" &&
                !Path.IsPathRooted(builder.DataSource))
            {
                builder.DataSource = Path.GetFullPath(Path.Combine(env.ContentRootPath, builder.DataSource));
                Directory.CreateDirectory(Path.GetDirectoryName(builder.DataSource)!);
            }
            builder.Cache = SqliteCacheMode.Default;
            builder.Pooling = true;
            _connectionString = builder.ToString();
        }
    }

    public bool IsPostgres => string.Equals(_provider, "Postgres", StringComparison.OrdinalIgnoreCase)
                           || string.Equals(_provider, "PostgreSQL", StringComparison.OrdinalIgnoreCase);

    public DbConnection CreateConnection()
    {
        if (IsPostgres)
            return new NpgsqlConnection(_connectionString);

        return new SqliteConnection(_connectionString);
    }

    public async Task<bool> CheckHealthAsync(CancellationToken ct = default)
    {
        await using var conn = CreateConnection();
        await conn.OpenAsync(ct);
        await using var cmd = CreateCommand(conn, null, "SELECT 1;");
        var result = await cmd.ExecuteScalarAsync(ct);
        return Convert.ToInt64(result, CultureInfo.InvariantCulture) == 1L;
    }

    /// <summary>
    /// Initializes schema and applies versioned schema migrations (`v1` -> `v2`),
    /// including upgrading legacy v1 databases that lacked `open_issues_count`, `open_prs_count`,
    /// `comments_count`, `must_change_password`, `last_used_at`, or `repo_collaborators`.
    /// </summary>
    public async Task InitializeSchemaAsync(CancellationToken ct = default)
    {
        await using var conn = CreateConnection();
        await conn.OpenAsync(ct);

        if (!IsPostgres)
        {
            await ExecuteNonQueryAsync(conn, null, """
                PRAGMA journal_mode = WAL;
                PRAGMA synchronous = NORMAL;
                PRAGMA foreign_keys = ON;
                PRAGMA busy_timeout = 10000;
                """, ct);
        }

        if (IsPostgres)
        {
            await ExecuteNonQueryAsync(conn, null, "SELECT pg_advisory_lock(718293041);", ct);
        }

        try
        {
            string idPk = IsPostgres ? "BIGSERIAL PRIMARY KEY" : "INTEGER PRIMARY KEY AUTOINCREMENT";
        string tsType = IsPostgres ? "TIMESTAMPTZ" : "TEXT";
        string nocase = IsPostgres ? "" : "COLLATE NOCASE";

        string ddl = $"""
            CREATE TABLE IF NOT EXISTS schema_migrations (
                version INTEGER PRIMARY KEY,
                description VARCHAR(255) NOT NULL,
                applied_at {tsType} NOT NULL
            );

            CREATE TABLE IF NOT EXISTS users (
                id {idPk},
                username VARCHAR(64) NOT NULL {nocase} UNIQUE,
                email VARCHAR(160) NOT NULL {nocase} UNIQUE,
                full_name VARCHAR(120) NOT NULL,
                password_hash TEXT NOT NULL,
                password_salt TEXT NOT NULL,
                is_admin INTEGER NOT NULL DEFAULT 0,
                must_change_password INTEGER NOT NULL DEFAULT 0,
                avatar_color VARCHAR(16) NOT NULL DEFAULT '#2563eb',
                created_at {tsType} NOT NULL,
                row_version BIGINT NOT NULL DEFAULT 1
            );

            CREATE TABLE IF NOT EXISTS repositories (
                id {idPk},
                owner_id BIGINT NOT NULL,
                name VARCHAR(100) NOT NULL {nocase},
                description TEXT NOT NULL,
                is_private INTEGER NOT NULL DEFAULT 0,
                default_branch VARCHAR(120) NOT NULL DEFAULT 'main',
                stars_count INTEGER NOT NULL DEFAULT 0,
                forks_count INTEGER NOT NULL DEFAULT 0,
                open_issues_count INTEGER NOT NULL DEFAULT 0,
                open_prs_count INTEGER NOT NULL DEFAULT 0,
                created_at {tsType} NOT NULL,
                updated_at {tsType} NOT NULL,
                row_version BIGINT NOT NULL DEFAULT 1,
                UNIQUE(owner_id, name),
                FOREIGN KEY(owner_id) REFERENCES users(id) ON DELETE CASCADE
            );

            CREATE TABLE IF NOT EXISTS repo_collaborators (
                repo_id BIGINT NOT NULL,
                user_id BIGINT NOT NULL,
                permission VARCHAR(16) NOT NULL DEFAULT 'write',
                created_at {tsType} NOT NULL,
                PRIMARY KEY(repo_id, user_id),
                FOREIGN KEY(repo_id) REFERENCES repositories(id) ON DELETE CASCADE,
                FOREIGN KEY(user_id) REFERENCES users(id) ON DELETE CASCADE
            );

            CREATE TABLE IF NOT EXISTS personal_access_tokens (
                id {idPk},
                user_id BIGINT NOT NULL,
                name VARCHAR(100) NOT NULL,
                token_prefix VARCHAR(16) NOT NULL,
                token_hash VARCHAR(128) NOT NULL UNIQUE,
                scopes VARCHAR(160) NOT NULL DEFAULT 'repo:read,repo:write,issue:write,pull:write',
                created_at {tsType} NOT NULL,
                last_used_at {tsType} NULL,
                expires_at {tsType} NULL,
                FOREIGN KEY(user_id) REFERENCES users(id) ON DELETE CASCADE
            );

            CREATE TABLE IF NOT EXISTS revoked_tokens (
                jti VARCHAR(64) PRIMARY KEY,
                expires_at {tsType} NOT NULL
            );

            CREATE TABLE IF NOT EXISTS repo_counters (
                repo_id BIGINT PRIMARY KEY,
                last_issue_index BIGINT NOT NULL DEFAULT 0,
                last_ci_run_index BIGINT NOT NULL DEFAULT 0,
                FOREIGN KEY(repo_id) REFERENCES repositories(id) ON DELETE CASCADE
            );

            CREATE TABLE IF NOT EXISTS ci_workflow_runs (
                id {idPk},
                repo_id BIGINT NOT NULL,
                run_index BIGINT NOT NULL,
                triggered_by_user_id BIGINT NOT NULL,
                workflow_name VARCHAR(160) NOT NULL,
                trigger_event VARCHAR(64) NOT NULL,
                branch VARCHAR(120) NOT NULL,
                commit_sha VARCHAR(64) NOT NULL,
                status VARCHAR(24) NOT NULL DEFAULT 'queued',
                logs_text TEXT NOT NULL DEFAULT '',
                duration_ms DOUBLE PRECISION NOT NULL DEFAULT 0,
                created_at {tsType} NOT NULL,
                completed_at {tsType} NULL,
                row_version BIGINT NOT NULL DEFAULT 1,
                UNIQUE(repo_id, run_index),
                FOREIGN KEY(repo_id) REFERENCES repositories(id) ON DELETE CASCADE,
                FOREIGN KEY(triggered_by_user_id) REFERENCES users(id) ON DELETE CASCADE
            );

            CREATE INDEX IF NOT EXISTS idx_ci_runs_repo_status ON ci_workflow_runs(repo_id, status);

            CREATE TABLE IF NOT EXISTS repo_stars (
                repo_id BIGINT NOT NULL,
                user_id BIGINT NOT NULL,
                created_at {tsType} NOT NULL,
                PRIMARY KEY(repo_id, user_id),
                FOREIGN KEY(repo_id) REFERENCES repositories(id) ON DELETE CASCADE,
                FOREIGN KEY(user_id) REFERENCES users(id) ON DELETE CASCADE
            );

            CREATE TABLE IF NOT EXISTS issues (
                id {idPk},
                repo_id BIGINT NOT NULL,
                issue_index BIGINT NOT NULL,
                author_id BIGINT NOT NULL,
                title VARCHAR(255) NOT NULL,
                body TEXT NOT NULL,
                state VARCHAR(16) NOT NULL DEFAULT 'open',
                labels_csv VARCHAR(255) NOT NULL DEFAULT '',
                comments_count INTEGER NOT NULL DEFAULT 0,
                created_at {tsType} NOT NULL,
                updated_at {tsType} NOT NULL,
                row_version BIGINT NOT NULL DEFAULT 1,
                UNIQUE(repo_id, issue_index),
                FOREIGN KEY(repo_id) REFERENCES repositories(id) ON DELETE CASCADE,
                FOREIGN KEY(author_id) REFERENCES users(id) ON DELETE CASCADE
            );

            CREATE TABLE IF NOT EXISTS issue_comments (
                id {idPk},
                issue_id BIGINT NOT NULL,
                repo_id BIGINT NOT NULL,
                author_id BIGINT NOT NULL,
                body TEXT NOT NULL,
                created_at {tsType} NOT NULL,
                FOREIGN KEY(issue_id) REFERENCES issues(id) ON DELETE CASCADE,
                FOREIGN KEY(repo_id) REFERENCES repositories(id) ON DELETE CASCADE,
                FOREIGN KEY(author_id) REFERENCES users(id) ON DELETE CASCADE
            );

            CREATE TABLE IF NOT EXISTS pull_requests (
                id {idPk},
                repo_id BIGINT NOT NULL,
                pull_index BIGINT NOT NULL,
                author_id BIGINT NOT NULL,
                title VARCHAR(255) NOT NULL,
                body TEXT NOT NULL,
                source_branch VARCHAR(120) NOT NULL,
                target_branch VARCHAR(120) NOT NULL,
                state VARCHAR(16) NOT NULL DEFAULT 'open',
                is_merged INTEGER NOT NULL DEFAULT 0,
                merge_commit_sha VARCHAR(64) NULL,
                merged_by_user_id BIGINT NULL,
                merged_at {tsType} NULL,
                created_at {tsType} NOT NULL,
                updated_at {tsType} NOT NULL,
                row_version BIGINT NOT NULL DEFAULT 1,
                UNIQUE(repo_id, pull_index),
                FOREIGN KEY(repo_id) REFERENCES repositories(id) ON DELETE CASCADE,
                FOREIGN KEY(author_id) REFERENCES users(id) ON DELETE CASCADE
            );

            CREATE TABLE IF NOT EXISTS webhook_deliveries (
                id {idPk},
                webhook_id BIGINT NOT NULL,
                repo_id BIGINT NOT NULL,
                event_name VARCHAR(64) NOT NULL,
                target_url TEXT NOT NULL,
                signature_header VARCHAR(128) NOT NULL,
                status_code INTEGER NOT NULL DEFAULT 0,
                attempt_count INTEGER NOT NULL DEFAULT 1,
                status VARCHAR(32) NOT NULL,
                duration_ms DOUBLE PRECISION NOT NULL DEFAULT 0,
                delivered_at {tsType} NOT NULL,
                FOREIGN KEY(repo_id) REFERENCES repositories(id) ON DELETE CASCADE
            );

            CREATE TABLE IF NOT EXISTS webhooks (
                id {idPk},
                repo_id BIGINT NOT NULL,
                target_url TEXT NOT NULL,
                secret TEXT NOT NULL,
                events_csv VARCHAR(255) NOT NULL,
                is_active INTEGER NOT NULL DEFAULT 1,
                created_at {tsType} NOT NULL,
                FOREIGN KEY(repo_id) REFERENCES repositories(id) ON DELETE CASCADE
            );

            CREATE TABLE IF NOT EXISTS audit_logs (
                id {idPk},
                actor_user_id BIGINT NULL,
                actor_username VARCHAR(64) NOT NULL,
                event_type VARCHAR(64) NOT NULL,
                summary TEXT NOT NULL,
                created_at {tsType} NOT NULL
            );

            CREATE INDEX IF NOT EXISTS idx_repos_owner ON repositories(owner_id);
            CREATE INDEX IF NOT EXISTS idx_issues_repo_state ON issues(repo_id, state);
            CREATE INDEX IF NOT EXISTS idx_comments_issue ON issue_comments(issue_id);
            CREATE INDEX IF NOT EXISTS idx_comments_repo ON issue_comments(repo_id);
            CREATE INDEX IF NOT EXISTS idx_pulls_repo_state ON pull_requests(repo_id, state);
            CREATE INDEX IF NOT EXISTS idx_audit_created ON audit_logs(created_at);
            """;

        await ExecuteNonQueryAsync(conn, null, ddl, ct);

        // Apply Migration v2: Add columns if upgrading from a v1 database
        await TryEnsureColumnExistsAsync(conn, "users", "must_change_password", "INTEGER NOT NULL DEFAULT 0", ct);
        await TryEnsureColumnExistsAsync(conn, "repositories", "open_issues_count", "INTEGER NOT NULL DEFAULT 0", ct);
        await TryEnsureColumnExistsAsync(conn, "repositories", "open_prs_count", "INTEGER NOT NULL DEFAULT 0", ct);
        await TryEnsureColumnExistsAsync(conn, "issues", "comments_count", "INTEGER NOT NULL DEFAULT 0", ct);
        await TryEnsureColumnExistsAsync(conn, "personal_access_tokens", "last_used_at", $"{tsType} NULL", ct);
        await TryEnsureColumnExistsAsync(conn, "repo_counters", "last_ci_run_index", "BIGINT NOT NULL DEFAULT 0", ct);

        if (IsPostgres)
        {
            const string pgIndexes = """
                ALTER TABLE repositories ALTER COLUMN default_branch TYPE VARCHAR(120);
                CREATE UNIQUE INDEX IF NOT EXISTS idx_users_username_lower ON users (LOWER(username));
                CREATE UNIQUE INDEX IF NOT EXISTS idx_users_email_lower ON users (LOWER(email));
                CREATE UNIQUE INDEX IF NOT EXISTS idx_repos_owner_name_lower ON repositories (owner_id, LOWER(name));
                """;
            await ExecuteNonQueryAsync(conn, null, pgIndexes, ct);
        }

        string recordV1Sql = IsPostgres
            ? "INSERT INTO schema_migrations (version, description, applied_at) VALUES (1, 'Initial schema', @now) ON CONFLICT DO NOTHING;"
            : "INSERT OR IGNORE INTO schema_migrations (version, description, applied_at) VALUES (1, 'Initial schema', @now);";
        string recordV2Sql = IsPostgres
            ? "INSERT INTO schema_migrations (version, description, applied_at) VALUES (2, 'Upgrade v2: O(1) counters, PAT scopes/last_used_at, must_change_password, default_branch(120)', @now) ON CONFLICT DO NOTHING;"
            : "INSERT OR IGNORE INTO schema_migrations (version, description, applied_at) VALUES (2, 'Upgrade v2: O(1) counters, PAT scopes/last_used_at, must_change_password, default_branch(120)', @now);";

        await ExecuteNonQueryAsync(conn, null, recordV1Sql, ct, ("@now", NowParam()));
        await ExecuteNonQueryAsync(conn, null, recordV2Sql, ct, ("@now", NowParam()));

            await ReconcileMaintainedCountersAsync(conn, ct);
            _logger.LogInformation("Database schema v2 initialized using provider {Provider}", _provider);
        }
        finally
        {
            if (IsPostgres)
            {
                try
                {
                    await ExecuteNonQueryAsync(conn, null, "SELECT pg_advisory_unlock(718293041);", CancellationToken.None);
                }
                catch (Exception ex)
                {
                    _logger.LogDebug(ex, "Failed to release pg advisory lock");
                }
            }
        }
    }

    private async Task TryEnsureColumnExistsAsync(DbConnection conn, string table, string column, string definition, CancellationToken ct)
    {
        try
        {
            if (IsPostgres)
            {
                await ExecuteNonQueryAsync(conn, null, $"ALTER TABLE {table} ADD COLUMN IF NOT EXISTS {column} {definition};", ct);
            }
            else
            {
                await ExecuteNonQueryAsync(conn, null, $"ALTER TABLE {table} ADD COLUMN {column} {definition};", ct);
            }
        }
        catch
        {
            // Column already exists on SQLite
        }
    }

    /// <summary>
    /// Reconciles maintained counter columns (`open_issues_count`, `open_prs_count`, `comments_count`, `stars_count`)
    /// and shared `repo_counters.last_issue_index` against actual table rows.
    /// </summary>
    public async Task ReconcileMaintainedCountersAsync(DbConnection? existingConn = null, CancellationToken ct = default)
    {
        bool ownsConn = existingConn is null;
        DbConnection conn = existingConn ?? CreateConnection();
        try
        {
            if (ownsConn) await conn.OpenAsync(ct);

            const string syncSql = """
                UPDATE repositories
                SET open_issues_count = (SELECT COUNT(*) FROM issues i WHERE i.repo_id = repositories.id AND i.state = 'open'),
                    open_prs_count = (SELECT COUNT(*) FROM pull_requests p WHERE p.repo_id = repositories.id AND p.state IN ('open', 'merging')),
                    stars_count = (SELECT COUNT(*) FROM repo_stars s WHERE s.repo_id = repositories.id);

                UPDATE issues
                SET comments_count = (SELECT COUNT(*) FROM issue_comments c WHERE c.issue_id = issues.id);
                """;
            await ExecuteNonQueryAsync(conn, null, syncSql, ct);
        }
        finally
        {
            if (ownsConn) await conn.DisposeAsync();
        }
    }

    public async Task CleanupExpiredTokensAndOldAuditLogsAsync(CancellationToken ct = default)
    {
        await using var conn = CreateConnection();
        await conn.OpenAsync(ct);

        object now = NowParam();
        object cutoff = FormatTimestampParam(DateTimeOffset.UtcNow.AddDays(-90));

        await ExecuteNonQueryAsync(conn, null, "DELETE FROM revoked_tokens WHERE expires_at < @now;", ct, ("@now", now));
        await ExecuteNonQueryAsync(conn, null, "DELETE FROM audit_logs WHERE created_at < @cutoff;", ct, ("@cutoff", cutoff));
    }

    // ==================== USER & AUTH OPERATIONS ====================

    public async Task<UserEntity> CreateUserAsync(
        string username,
        string email,
        string fullName,
        string passwordHash,
        string passwordSalt,
        bool isAdmin,
        string avatarColor,
        bool mustChangePassword = false,
        CancellationToken ct = default)
    {
        await using var conn = CreateConnection();
        await conn.OpenAsync(ct);

        object now = NowParam();
        string sql = IsPostgres
            ? """
              INSERT INTO users (username, email, full_name, password_hash, password_salt, is_admin, must_change_password, avatar_color, created_at, row_version)
              VALUES (@username, @email, @fullName, @hash, @salt, @isAdmin, @mustChange, @avatar, @now, 1)
              RETURNING id;
              """
            : """
              INSERT INTO users (username, email, full_name, password_hash, password_salt, is_admin, must_change_password, avatar_color, created_at, row_version)
              VALUES (@username, @email, @fullName, @hash, @salt, @isAdmin, @mustChange, @avatar, @now, 1);
              SELECT last_insert_rowid();
              """;

        try
        {
            await using var cmd = CreateCommand(conn, null, sql,
                ("@username", username),
                ("@email", email),
                ("@fullName", fullName),
                ("@hash", passwordHash),
                ("@salt", passwordSalt),
                ("@isAdmin", isAdmin ? 1 : 0),
                ("@mustChange", mustChangePassword ? 1 : 0),
                ("@avatar", avatarColor),
                ("@now", now));

            long id = Convert.ToInt64(await cmd.ExecuteScalarAsync(ct), CultureInfo.InvariantCulture);
            return new UserEntity
            {
                Id = id,
                Username = username,
                Email = email,
                FullName = fullName,
                PasswordHash = passwordHash,
                PasswordSalt = passwordSalt,
                IsAdmin = isAdmin,
                MustChangePassword = mustChangePassword,
                AvatarColor = avatarColor,
                CreatedAt = DateTimeOffset.UtcNow,
                RowVersion = 1
            };
        }
        catch (Exception ex) when (IsUniqueConstraintViolation(ex))
        {
            throw new DuplicateResourceException("Username or email is already registered.");
        }
    }

    public async Task UpdateUserPasswordAsync(
        long userId, string newHash, string newSalt, bool mustChangePassword, CancellationToken ct = default)
    {
        await using var conn = CreateConnection();
        await conn.OpenAsync(ct);

        const string sql = """
            UPDATE users
            SET password_hash = @hash,
                password_salt = @salt,
                must_change_password = @mustChange,
                row_version = row_version + 1
            WHERE id = @id;
            """;

        await ExecuteNonQueryAsync(conn, null, sql, ct,
            ("@hash", newHash),
            ("@salt", newSalt),
            ("@mustChange", mustChangePassword ? 1 : 0),
            ("@id", userId));
    }

    public async Task<UserEntity?> GetUserByIdAsync(long userId, CancellationToken ct = default)
    {
        await using var conn = CreateConnection();
        await conn.OpenAsync(ct);

        const string sql = """
            SELECT id, username, email, full_name, password_hash, password_salt, is_admin, must_change_password, avatar_color, created_at, row_version
            FROM users
            WHERE id = @id
            LIMIT 1;
            """;

        await using var cmd = CreateCommand(conn, null, sql, ("@id", userId));
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        if (!await reader.ReadAsync(ct)) return null;
        return MapUser(reader);
    }

    public async Task<UserEntity?> GetUserByUsernameAsync(string username, CancellationToken ct = default)
    {
        await using var conn = CreateConnection();
        await conn.OpenAsync(ct);

        string sql = IsPostgres
            ? """
              SELECT id, username, email, full_name, password_hash, password_salt, is_admin, must_change_password, avatar_color, created_at, row_version
              FROM users
              WHERE LOWER(username) = LOWER(@username)
              LIMIT 1;
              """
            : """
              SELECT id, username, email, full_name, password_hash, password_salt, is_admin, must_change_password, avatar_color, created_at, row_version
              FROM users
              WHERE username = @username
              LIMIT 1;
              """;

        await using var cmd = CreateCommand(conn, null, sql, ("@username", username));
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        if (!await reader.ReadAsync(ct)) return null;
        return MapUser(reader);
    }

    public async Task<PagedResult<UserEntity>> ListUsersAsync(int page = 1, int pageSize = 30, CancellationToken ct = default)
    {
        page = Math.Max(1, page);
        pageSize = Math.Clamp(pageSize, 1, 100);
        int offset = (page - 1) * pageSize;

        await using var conn = CreateConnection();
        await conn.OpenAsync(ct);

        const string countSql = "SELECT COUNT(*) FROM users;";
        int total;
        await using (var countCmd = CreateCommand(conn, null, countSql))
        {
            total = Convert.ToInt32(await countCmd.ExecuteScalarAsync(ct), CultureInfo.InvariantCulture);
        }

        const string sql = """
            SELECT id, username, email, full_name, password_hash, password_salt, is_admin, must_change_password, avatar_color, created_at, row_version
            FROM users
            ORDER BY id ASC
            LIMIT @limit OFFSET @offset;
            """;

        var list = new List<UserEntity>();
        await using var cmd = CreateCommand(conn, null, sql, ("@limit", pageSize), ("@offset", offset));
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            list.Add(MapUser(reader));
        }
        return new PagedResult<UserEntity>(list, page, pageSize, total);
    }

    // ==================== PERSONAL ACCESS TOKENS & REVOCATION ====================

    public async Task<PersonalAccessTokenEntity> CreatePersonalAccessTokenAsync(
        long userId, string name, string tokenPrefix, string tokenHash, string scopes, DateTimeOffset? expiresAt, CancellationToken ct = default)
    {
        await using var conn = CreateConnection();
        await conn.OpenAsync(ct);

        object now = NowParam();
        object exp = expiresAt.HasValue ? FormatTimestampParam(expiresAt.Value) : DBNull.Value;

        string sql = IsPostgres
            ? """
              INSERT INTO personal_access_tokens (user_id, name, token_prefix, token_hash, scopes, created_at, last_used_at, expires_at)
              VALUES (@userId, @name, @prefix, @hash, @scopes, @now, NULL, @exp)
              RETURNING id;
              """
            : """
              INSERT INTO personal_access_tokens (user_id, name, token_prefix, token_hash, scopes, created_at, last_used_at, expires_at)
              VALUES (@userId, @name, @prefix, @hash, @scopes, @now, NULL, @exp);
              SELECT last_insert_rowid();
              """;

        await using var cmd = CreateCommand(conn, null, sql,
            ("@userId", userId),
            ("@name", name),
            ("@prefix", tokenPrefix),
            ("@hash", tokenHash),
            ("@scopes", scopes),
            ("@now", now),
            ("@exp", exp));

        long id = Convert.ToInt64(await cmd.ExecuteScalarAsync(ct), CultureInfo.InvariantCulture);
        return new PersonalAccessTokenEntity
        {
            Id = id,
            UserId = userId,
            Name = name,
            TokenPrefix = tokenPrefix,
            Scopes = scopes,
            CreatedAt = DateTimeOffset.UtcNow,
            LastUsedAt = null,
            ExpiresAt = expiresAt
        };
    }

    public async Task<IReadOnlyList<PersonalAccessTokenEntity>> ListPersonalAccessTokensAsync(long userId, CancellationToken ct = default)
    {
        await using var conn = CreateConnection();
        await conn.OpenAsync(ct);

        const string sql = """
            SELECT id, user_id, name, token_prefix, scopes, created_at, last_used_at, expires_at
            FROM personal_access_tokens
            WHERE user_id = @userId
            ORDER BY id DESC;
            """;

        var list = new List<PersonalAccessTokenEntity>();
        await using var cmd = CreateCommand(conn, null, sql, ("@userId", userId));
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            list.Add(new PersonalAccessTokenEntity
            {
                Id = Convert.ToInt64(reader.GetValue(0), CultureInfo.InvariantCulture),
                UserId = Convert.ToInt64(reader.GetValue(1), CultureInfo.InvariantCulture),
                Name = reader.GetString(2),
                TokenPrefix = reader.GetString(3),
                Scopes = reader.GetString(4),
                CreatedAt = ReadTimestamp(reader, 5),
                LastUsedAt = reader.IsDBNull(6) ? null : ReadTimestamp(reader, 6),
                ExpiresAt = reader.IsDBNull(7) ? null : ReadTimestamp(reader, 7)
            });
        }
        return list;
    }

    public async Task<bool> DeletePersonalAccessTokenAsync(long userId, long tokenId, CancellationToken ct = default)
    {
        await using var conn = CreateConnection();
        await conn.OpenAsync(ct);

        const string sql = "DELETE FROM personal_access_tokens WHERE id = @id AND user_id = @userId;";
        int affected = await ExecuteNonQueryAsync(conn, null, sql, ct, ("@id", tokenId), ("@userId", userId));
        return affected == 1;
    }

    public async Task<(UserEntity User, string ScopesCsv)?> AuthenticatePatByHashAsync(string tokenHash, CancellationToken ct = default)
    {
        await using var conn = CreateConnection();
        await conn.OpenAsync(ct);

        const string sql = """
            SELECT u.id, u.username, u.email, u.full_name, u.password_hash, u.password_salt, u.is_admin, u.must_change_password, u.avatar_color, u.created_at, u.row_version,
                   p.id, p.scopes, p.expires_at
            FROM personal_access_tokens p
            INNER JOIN users u ON u.id = p.user_id
            WHERE p.token_hash = @hash
            LIMIT 1;
            """;

        UserEntity user;
        long patId;
        string scopes;

        await using (var cmd = CreateCommand(conn, null, sql, ("@hash", tokenHash)))
        await using (var reader = await cmd.ExecuteReaderAsync(ct))
        {
            if (!await reader.ReadAsync(ct)) return null;

            if (!reader.IsDBNull(13))
            {
                var exp = ReadTimestamp(reader, 13);
                if (exp < DateTimeOffset.UtcNow) return null;
            }

            user = MapUser(reader);
            patId = Convert.ToInt64(reader.GetValue(11), CultureInfo.InvariantCulture);
            scopes = reader.GetString(12);
        }

        // Update last_used_at
        await ExecuteNonQueryAsync(conn, null,
            "UPDATE personal_access_tokens SET last_used_at = @now WHERE id = @id;",
            ct, ("@now", NowParam()), ("@id", patId));

        return (user, scopes);
    }

    public async Task RevokeTokenAsync(string jti, DateTimeOffset expiresAt, CancellationToken ct = default)
    {
        await using var conn = CreateConnection();
        await conn.OpenAsync(ct);

        string sql = IsPostgres
            ? "INSERT INTO revoked_tokens (jti, expires_at) VALUES (@jti, @exp) ON CONFLICT DO NOTHING;"
            : "INSERT OR IGNORE INTO revoked_tokens (jti, expires_at) VALUES (@jti, @exp);";

        await ExecuteNonQueryAsync(conn, null, sql, ct, ("@jti", jti), ("@exp", FormatTimestampParam(expiresAt)));
    }

    public async Task<bool> IsTokenRevokedAsync(string jti, CancellationToken ct = default)
    {
        if (string.IsNullOrEmpty(jti)) return false;
        await using var conn = CreateConnection();
        await conn.OpenAsync(ct);

        const string sql = "SELECT 1 FROM revoked_tokens WHERE jti = @jti LIMIT 1;";
        await using var cmd = CreateCommand(conn, null, sql, ("@jti", jti));
        var res = await cmd.ExecuteScalarAsync(ct);
        return res is not null;
    }

    // ==================== REPOSITORY & RBAC COLLABORATOR OPERATIONS ====================

    public async Task<RepositoryEntity> CreateRepositoryAsync(
        long ownerId, string name, string description, bool isPrivate, string defaultBranch, CancellationToken ct = default)
    {
        return await ExecuteWithRetryAsync(async () =>
        {
            await using var conn = CreateConnection();
            await conn.OpenAsync(ct);
            await using var tx = await BeginWriteTransactionAsync(conn, ct);

            object now = NowParam();
            string insertRepoSql = IsPostgres
                ? """
                  INSERT INTO repositories (owner_id, name, description, is_private, default_branch, stars_count, forks_count, open_issues_count, open_prs_count, created_at, updated_at, row_version)
                  VALUES (@ownerId, @name, @desc, @isPrivate, @branch, 0, 0, 0, 0, @now, @now, 1)
                  RETURNING id;
                  """
                : """
                  INSERT INTO repositories (owner_id, name, description, is_private, default_branch, stars_count, forks_count, open_issues_count, open_prs_count, created_at, updated_at, row_version)
                  VALUES (@ownerId, @name, @desc, @isPrivate, @branch, 0, 0, 0, 0, @now, @now, 1);
                  SELECT last_insert_rowid();
                  """;

            long repoId;
            try
            {
                await using var cmd = CreateCommand(conn, tx, insertRepoSql,
                    ("@ownerId", ownerId),
                    ("@name", name),
                    ("@desc", description),
                    ("@isPrivate", isPrivate ? 1 : 0),
                    ("@branch", defaultBranch),
                    ("@now", now));

                repoId = Convert.ToInt64(await cmd.ExecuteScalarAsync(ct), CultureInfo.InvariantCulture);
            }
            catch (Exception ex) when (IsUniqueConstraintViolation(ex))
            {
                throw new DuplicateResourceException($"Repository '{name}' already exists for this owner.");
            }

            const string initCounterSql = """
                INSERT INTO repo_counters (repo_id, last_issue_index)
                VALUES (@repoId, 0);
                """;
            await ExecuteNonQueryAsync(conn, tx, initCounterSql, ct, ("@repoId", repoId));

            await tx.CommitAsync(ct);
            return (await GetRepositoryAsync(repoId, ct))!;
        }, ct);
    }

    public async Task TouchRepositoryUpdatedAtAsync(long repoId, CancellationToken ct = default)
    {
        await using var conn = CreateConnection();
        await conn.OpenAsync(ct);
        await ExecuteNonQueryAsync(conn, null,
            "UPDATE repositories SET updated_at = @now, row_version = row_version + 1 WHERE id = @repoId;",
            ct, ("@now", NowParam()), ("@repoId", repoId));
    }

    public async Task DeleteRepositoryAsync(long repoId, CancellationToken ct = default)
    {
        await using var conn = CreateConnection();
        await conn.OpenAsync(ct);
        await ExecuteNonQueryAsync(conn, null, "DELETE FROM repositories WHERE id = @id;", ct, ("@id", repoId));
    }

    public async Task<(bool CanRead, bool CanWrite, bool CanAdmin)> GetRepositoryPermissionsAsync(
        RepositoryEntity repo, long? userId, bool isSiteAdmin, CancellationToken ct = default)
    {
        if (isSiteAdmin || (userId.HasValue && repo.OwnerId == userId.Value))
        {
            return (true, true, true);
        }

        if (!userId.HasValue)
        {
            return (!repo.IsPrivate, false, false);
        }

        await using var conn = CreateConnection();
        await conn.OpenAsync(ct);
        const string sql = "SELECT permission FROM repo_collaborators WHERE repo_id = @repoId AND user_id = @userId LIMIT 1;";
        await using var cmd = CreateCommand(conn, null, sql, ("@repoId", repo.Id), ("@userId", userId.Value));
        string? perm = (await cmd.ExecuteScalarAsync(ct)) as string;

        if (string.IsNullOrEmpty(perm))
        {
            return (!repo.IsPrivate, false, false);
        }

        bool canWrite = string.Equals(perm, "write", StringComparison.OrdinalIgnoreCase)
                     || string.Equals(perm, "admin", StringComparison.OrdinalIgnoreCase);
        bool canAdmin = string.Equals(perm, "admin", StringComparison.OrdinalIgnoreCase);
        return (true, canWrite, canAdmin);
    }

    public async Task AddOrUpdateCollaboratorAsync(long repoId, long userId, string permission, CancellationToken ct = default)
    {
        await using var conn = CreateConnection();
        await conn.OpenAsync(ct);

        string sql = IsPostgres
            ? """
              INSERT INTO repo_collaborators (repo_id, user_id, permission, created_at)
              VALUES (@repoId, @userId, @perm, @now)
              ON CONFLICT (repo_id, user_id) DO UPDATE SET permission = EXCLUDED.permission;
              """
            : """
              INSERT INTO repo_collaborators (repo_id, user_id, permission, created_at)
              VALUES (@repoId, @userId, @perm, @now)
              ON CONFLICT(repo_id, user_id) DO UPDATE SET permission = excluded.permission;
              """;

        await ExecuteNonQueryAsync(conn, null, sql, ct,
            ("@repoId", repoId), ("@userId", userId), ("@perm", permission.ToLowerInvariant()), ("@now", NowParam()));
    }

    public async Task<IReadOnlyList<CollaboratorEntity>> ListCollaboratorsAsync(long repoId, CancellationToken ct = default)
    {
        await using var conn = CreateConnection();
        await conn.OpenAsync(ct);

        const string sql = """
            SELECT c.repo_id, c.user_id, u.username, c.permission, c.created_at
            FROM repo_collaborators c
            INNER JOIN users u ON u.id = c.user_id
            WHERE c.repo_id = @repoId
            ORDER BY u.username ASC;
            """;

        var list = new List<CollaboratorEntity>();
        await using var cmd = CreateCommand(conn, null, sql, ("@repoId", repoId));
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            list.Add(new CollaboratorEntity
            {
                RepoId = Convert.ToInt64(reader.GetValue(0), CultureInfo.InvariantCulture),
                UserId = Convert.ToInt64(reader.GetValue(1), CultureInfo.InvariantCulture),
                Username = reader.GetString(2),
                Permission = reader.GetString(3),
                CreatedAt = ReadTimestamp(reader, 4)
            });
        }
        return list;
    }

    public async Task<PagedResult<RepositoryEntity>> ListRepositoriesAsync(
        string? searchQuery = null,
        long? viewerUserId = null,
        bool viewerIsAdmin = false,
        int page = 1,
        int pageSize = 25,
        CancellationToken ct = default)
    {
        page = Math.Max(1, page);
        pageSize = Math.Clamp(pageSize, 1, 100);
        int offset = (page - 1) * pageSize;

        await using var conn = CreateConnection();
        await conn.OpenAsync(ct);

        string q = (searchQuery ?? string.Empty).Trim();
        string escapedPattern = $"%{EscapeLike(q)}%";

        const string whereClause = """
            WHERE (@isAdmin = 1
                   OR r.is_private = 0
                   OR r.owner_id = @viewerId
                   OR EXISTS (SELECT 1 FROM repo_collaborators rc WHERE rc.repo_id = r.id AND rc.user_id = @viewerId))
              AND (@q = ''
                   OR LOWER(r.name) LIKE LOWER(@qPattern) ESCAPE '\'
                   OR LOWER(r.description) LIKE LOWER(@qPattern) ESCAPE '\'
                   OR LOWER(u.username) LIKE LOWER(@qPattern) ESCAPE '\')
            """;

        string countSql = $"""
            SELECT COUNT(*)
            FROM repositories r
            INNER JOIN users u ON u.id = r.owner_id
            {whereClause};
            """;

        string selectSql = $"""
            SELECT
                r.id,
                r.owner_id,
                u.username AS owner_username,
                r.name,
                r.description,
                r.is_private,
                r.default_branch,
                r.stars_count,
                r.forks_count,
                r.open_issues_count,
                r.open_prs_count,
                r.created_at,
                r.updated_at,
                r.row_version
            FROM repositories r
            INNER JOIN users u ON u.id = r.owner_id
            {whereClause}
            ORDER BY r.updated_at DESC, r.id DESC
            LIMIT @limit OFFSET @offset;
            """;

        var parameters = new (string, object)[]
        {
            ("@isAdmin", viewerIsAdmin ? 1 : 0),
            ("@viewerId", viewerUserId ?? -1L),
            ("@q", q),
            ("@qPattern", escapedPattern),
            ("@limit", pageSize),
            ("@offset", offset)
        };

        int total;
        await using (var countCmd = CreateCommand(conn, null, countSql, parameters))
        {
            total = Convert.ToInt32(await countCmd.ExecuteScalarAsync(ct), CultureInfo.InvariantCulture);
        }

        var list = new List<RepositoryEntity>();
        await using var cmd = CreateCommand(conn, null, selectSql, parameters);
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            list.Add(MapRepository(reader));
        }

        return new PagedResult<RepositoryEntity>(list, page, pageSize, total);
    }

    public async Task<RepositoryEntity?> GetRepositoryByOwnerAndNameAsync(string owner, string name, CancellationToken ct = default)
    {
        await using var conn = CreateConnection();
        await conn.OpenAsync(ct);

        string sql = IsPostgres
            ? """
              SELECT
                  r.id, r.owner_id, u.username, r.name, r.description, r.is_private,
                  r.default_branch, r.stars_count, r.forks_count, r.open_issues_count, r.open_prs_count,
                  r.created_at, r.updated_at, r.row_version
              FROM repositories r
              INNER JOIN users u ON u.id = r.owner_id
              WHERE LOWER(u.username) = LOWER(@owner) AND LOWER(r.name) = LOWER(@name)
              LIMIT 1;
              """
            : """
              SELECT
                  r.id, r.owner_id, u.username, r.name, r.description, r.is_private,
                  r.default_branch, r.stars_count, r.forks_count, r.open_issues_count, r.open_prs_count,
                  r.created_at, r.updated_at, r.row_version
              FROM repositories r
              INNER JOIN users u ON u.id = r.owner_id
              WHERE u.username = @owner AND r.name = @name
              LIMIT 1;
              """;

        await using var cmd = CreateCommand(conn, null, sql, ("@owner", owner), ("@name", name));
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        if (!await reader.ReadAsync(ct)) return null;
        return MapRepository(reader);
    }

    public async Task<RepositoryEntity?> GetRepositoryAsync(long repoId, CancellationToken ct = default)
    {
        await using var conn = CreateConnection();
        await conn.OpenAsync(ct);

        const string sql = """
            SELECT
                r.id, r.owner_id, u.username, r.name, r.description, r.is_private,
                r.default_branch, r.stars_count, r.forks_count, r.open_issues_count, r.open_prs_count,
                r.created_at, r.updated_at, r.row_version
            FROM repositories r
            INNER JOIN users u ON u.id = r.owner_id
            WHERE r.id = @repoId
            LIMIT 1;
            """;

        await using var cmd = CreateCommand(conn, null, sql, ("@repoId", repoId));
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        if (!await reader.ReadAsync(ct)) return null;
        return MapRepository(reader);
    }

    public async Task<(bool Starred, int StarsCount)> ToggleStarAsync(long repoId, long userId, CancellationToken ct = default)
    {
        return await ExecuteWithRetryAsync(async () =>
        {
            await using var conn = CreateConnection();
            await conn.OpenAsync(ct);
            await using var tx = await BeginWriteTransactionAsync(conn, ct);

            object now = NowParam();
            string insertIgnoreSql = IsPostgres
                ? "INSERT INTO repo_stars (repo_id, user_id, created_at) VALUES (@repoId, @userId, @now) ON CONFLICT DO NOTHING;"
                : "INSERT OR IGNORE INTO repo_stars (repo_id, user_id, created_at) VALUES (@repoId, @userId, @now);";

            int inserted = await ExecuteNonQueryAsync(conn, tx, insertIgnoreSql, ct,
                ("@repoId", repoId), ("@userId", userId), ("@now", now));

            bool starred = inserted > 0;
            int delta = starred ? 1 : -1;
            if (!starred)
            {
                const string deleteSql = "DELETE FROM repo_stars WHERE repo_id = @repoId AND user_id = @userId;";
                await ExecuteNonQueryAsync(conn, tx, deleteSql, ct, ("@repoId", repoId), ("@userId", userId));
            }

            string syncCountSql = IsPostgres
                ? "UPDATE repositories SET stars_count = GREATEST(0, stars_count + @delta), row_version = row_version + 1 WHERE id = @repoId RETURNING stars_count;"
                : "UPDATE repositories SET stars_count = MAX(0, stars_count + @delta), row_version = row_version + 1 WHERE id = @repoId; SELECT stars_count FROM repositories WHERE id = @repoId;";

            int newCount;
            await using (var cmd = CreateCommand(conn, tx, syncCountSql, ("@delta", delta), ("@repoId", repoId)))
            {
                newCount = Convert.ToInt32(await cmd.ExecuteScalarAsync(ct), CultureInfo.InvariantCulture);
            }

            await tx.CommitAsync(ct);
            return (starred, newCount);
        }, ct);
    }

    // ==================== ISSUE OPERATIONS ====================

    public async Task<IssueEntity> CreateIssueAsync(
        long repoId, long authorId, string title, string body, string labelsCsv, CancellationToken ct = default)
    {
        return await ExecuteWithRetryAsync(async () =>
        {
            await using var conn = CreateConnection();
            await conn.OpenAsync(ct);
            await using var tx = await BeginWriteTransactionAsync(conn, ct);

            string bumpCounterSql = IsPostgres
                ? "UPDATE repo_counters SET last_issue_index = last_issue_index + 1 WHERE repo_id = @repoId RETURNING last_issue_index;"
                : "UPDATE repo_counters SET last_issue_index = last_issue_index + 1 WHERE repo_id = @repoId; SELECT last_issue_index FROM repo_counters WHERE repo_id = @repoId;";

            long issueIndex;
            await using (var cmd = CreateCommand(conn, tx, bumpCounterSql, ("@repoId", repoId)))
            {
                issueIndex = Convert.ToInt64(await cmd.ExecuteScalarAsync(ct), CultureInfo.InvariantCulture);
            }

            object now = NowParam();
            string insertIssueSql = IsPostgres
                ? """
                  INSERT INTO issues (repo_id, issue_index, author_id, title, body, state, labels_csv, comments_count, created_at, updated_at, row_version)
                  VALUES (@repoId, @issueIndex, @authorId, @title, @body, 'open', @labels, 0, @now, @now, 1)
                  RETURNING id;
                  """
                : """
                  INSERT INTO issues (repo_id, issue_index, author_id, title, body, state, labels_csv, comments_count, created_at, updated_at, row_version)
                  VALUES (@repoId, @issueIndex, @authorId, @title, @body, 'open', @labels, 0, @now, @now, 1);
                  SELECT last_insert_rowid();
                  """;

            await using (var cmd = CreateCommand(conn, tx, insertIssueSql,
                ("@repoId", repoId),
                ("@issueIndex", issueIndex),
                ("@authorId", authorId),
                ("@title", title),
                ("@body", body),
                ("@labels", labelsCsv),
                ("@now", now)))
            {
                _ = await cmd.ExecuteScalarAsync(ct);
            }

            await ExecuteNonQueryAsync(conn, tx,
                "UPDATE repositories SET open_issues_count = open_issues_count + 1, updated_at = @now, row_version = row_version + 1 WHERE id = @repoId;",
                ct, ("@now", now), ("@repoId", repoId));

            await tx.CommitAsync(ct);

            var bundle = await GetIssueDetailBundleAsync(repoId, issueIndex, ct);
            return bundle!.Issue;
        }, ct);
    }

    public async Task<PagedResult<IssueEntity>> ListIssuesAsync(
        long repoId, string? stateFilter = null, int page = 1, int pageSize = 25, CancellationToken ct = default)
    {
        page = Math.Max(1, page);
        pageSize = Math.Clamp(pageSize, 1, 100);
        int offset = (page - 1) * pageSize;
        string state = (stateFilter ?? string.Empty).Trim().ToLowerInvariant();

        await using var conn = CreateConnection();
        await conn.OpenAsync(ct);

        const string countSql = """
            SELECT COUNT(*)
            FROM issues i
            WHERE i.repo_id = @repoId AND (@state = '' OR i.state = @state);
            """;

        int total;
        await using (var countCmd = CreateCommand(conn, null, countSql, ("@repoId", repoId), ("@state", state)))
        {
            total = Convert.ToInt32(await countCmd.ExecuteScalarAsync(ct), CultureInfo.InvariantCulture);
        }

        const string sql = """
            SELECT
                i.id,
                i.repo_id,
                i.issue_index,
                i.author_id,
                u.username AS author_username,
                u.avatar_color AS author_avatar_color,
                i.title,
                i.body,
                i.state,
                i.labels_csv,
                i.comments_count,
                i.created_at,
                i.updated_at,
                i.row_version
            FROM issues i
            INNER JOIN users u ON u.id = i.author_id
            WHERE i.repo_id = @repoId
              AND (@state = '' OR i.state = @state)
            ORDER BY i.issue_index DESC
            LIMIT @limit OFFSET @offset;
            """;

        var list = new List<IssueEntity>();
        await using var cmd = CreateCommand(conn, null, sql,
            ("@repoId", repoId),
            ("@state", state),
            ("@limit", pageSize),
            ("@offset", offset));
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            list.Add(MapIssue(reader));
        }
        return new PagedResult<IssueEntity>(list, page, pageSize, total);
    }

    public async Task<IssueDetailBundleDto?> GetIssueDetailBundleAsync(long repoId, long issueIndex, CancellationToken ct = default)
    {
        await using var conn = CreateConnection();
        await conn.OpenAsync(ct);

        const string issueSql = """
            SELECT
                i.id,
                i.repo_id,
                i.issue_index,
                i.author_id,
                u.username AS author_username,
                u.avatar_color AS author_avatar_color,
                i.title,
                i.body,
                i.state,
                i.labels_csv,
                i.comments_count,
                i.created_at,
                i.updated_at,
                i.row_version
            FROM issues i
            INNER JOIN users u ON u.id = i.author_id
            WHERE i.repo_id = @repoId AND i.issue_index = @issueIndex
            LIMIT 1;
            """;

        IssueEntity? issue;
        await using (var cmd = CreateCommand(conn, null, issueSql, ("@repoId", repoId), ("@issueIndex", issueIndex)))
        await using (var reader = await cmd.ExecuteReaderAsync(ct))
        {
            if (!await reader.ReadAsync(ct)) return null;
            issue = MapIssue(reader);
        }

        const string commentsSql = """
            SELECT
                c.id,
                c.issue_id,
                c.repo_id,
                c.author_id,
                u.username AS author_username,
                u.avatar_color AS author_avatar_color,
                c.body,
                c.created_at
            FROM issue_comments c
            INNER JOIN users u ON u.id = c.author_id
            WHERE c.issue_id = @issueId
            ORDER BY c.id ASC;
            """;

        var comments = new List<IssueCommentEntity>();
        await using (var cmd = CreateCommand(conn, null, commentsSql, ("@issueId", issue.Id)))
        await using (var reader = await cmd.ExecuteReaderAsync(ct))
        {
            while (await reader.ReadAsync(ct))
            {
                comments.Add(new IssueCommentEntity
                {
                    Id = Convert.ToInt64(reader.GetValue(0), CultureInfo.InvariantCulture),
                    IssueId = Convert.ToInt64(reader.GetValue(1), CultureInfo.InvariantCulture),
                    RepoId = Convert.ToInt64(reader.GetValue(2), CultureInfo.InvariantCulture),
                    AuthorId = Convert.ToInt64(reader.GetValue(3), CultureInfo.InvariantCulture),
                    AuthorUsername = reader.GetString(4),
                    AuthorAvatarColor = reader.GetString(5),
                    Body = reader.GetString(6),
                    CreatedAt = ReadTimestamp(reader, 7)
                });
            }
        }

        return new IssueDetailBundleDto(issue, comments);
    }

    public enum StateUpdateOutcome { NotFound, Conflict, Updated }

    public async Task<StateUpdateOutcome> UpdateIssueStateAsync(
        long repoId, long issueIndex, string newState, long expectedRowVersion, CancellationToken ct = default)
    {
        return await ExecuteWithRetryAsync(async () =>
        {
            await using var conn = CreateConnection();
            await conn.OpenAsync(ct);
            await using var tx = await BeginWriteTransactionAsync(conn, ct);

            const string checkSql = "SELECT state, row_version FROM issues WHERE repo_id = @repoId AND issue_index = @issueIndex LIMIT 1;";
            string? oldState;
            long currentRv;

            await using (var checkCmd = CreateCommand(conn, tx, checkSql, ("@repoId", repoId), ("@issueIndex", issueIndex)))
            await using (var reader = await checkCmd.ExecuteReaderAsync(ct))
            {
                if (!await reader.ReadAsync(ct))
                    return StateUpdateOutcome.NotFound;

                oldState = reader.GetString(0);
                currentRv = Convert.ToInt64(reader.GetValue(1), CultureInfo.InvariantCulture);
            }

            if (currentRv != expectedRowVersion)
                return StateUpdateOutcome.Conflict;

            object now = NowParam();
            const string sql = """
                UPDATE issues
                SET state = @state,
                    updated_at = @now,
                    row_version = row_version + 1
                WHERE repo_id = @repoId
                  AND issue_index = @issueIndex
                  AND row_version = @expectedRowVersion;
                """;

            int affected = await ExecuteNonQueryAsync(conn, tx, sql, ct,
                ("@state", newState),
                ("@now", now),
                ("@repoId", repoId),
                ("@issueIndex", issueIndex),
                ("@expectedRowVersion", expectedRowVersion));

            if (affected != 1)
                return StateUpdateOutcome.Conflict;

            if (!string.Equals(oldState, newState, StringComparison.OrdinalIgnoreCase))
            {
                int delta = newState == "open" ? 1 : -1;
                string updateRepoCount = IsPostgres
                    ? "UPDATE repositories SET open_issues_count = GREATEST(0, open_issues_count + @delta), updated_at = @now WHERE id = @repoId;"
                    : "UPDATE repositories SET open_issues_count = MAX(0, open_issues_count + @delta), updated_at = @now WHERE id = @repoId;";
                await ExecuteNonQueryAsync(conn, tx, updateRepoCount, ct, ("@delta", delta), ("@now", now), ("@repoId", repoId));
            }

            await tx.CommitAsync(ct);
            return StateUpdateOutcome.Updated;
        }, ct);
    }

    public async Task<IssueCommentEntity> AddIssueCommentAsync(long repoId, long issueId, long authorId, string body, CancellationToken ct = default)
    {
        return await ExecuteWithRetryAsync(async () =>
        {
            await using var conn = CreateConnection();
            await conn.OpenAsync(ct);
            await using var tx = await BeginWriteTransactionAsync(conn, ct);

            object now = NowParam();
            string insertSql = IsPostgres
                ? """
                  INSERT INTO issue_comments (issue_id, repo_id, author_id, body, created_at)
                  VALUES (@issueId, @repoId, @authorId, @body, @now)
                  RETURNING id;
                  """
                : """
                  INSERT INTO issue_comments (issue_id, repo_id, author_id, body, created_at)
                  VALUES (@issueId, @repoId, @authorId, @body, @now);
                  SELECT last_insert_rowid();
                  """;

            long commentId;
            await using (var cmd = CreateCommand(conn, tx, insertSql,
                ("@issueId", issueId),
                ("@repoId", repoId),
                ("@authorId", authorId),
                ("@body", body),
                ("@now", now)))
            {
                commentId = Convert.ToInt64(await cmd.ExecuteScalarAsync(ct), CultureInfo.InvariantCulture);
            }

            await ExecuteNonQueryAsync(conn, tx,
                "UPDATE issues SET comments_count = comments_count + 1, updated_at = @now, row_version = row_version + 1 WHERE id = @issueId;",
                ct, ("@now", now), ("@issueId", issueId));

            await tx.CommitAsync(ct);

            const string selectSql = """
                SELECT c.id, c.issue_id, c.repo_id, c.author_id, u.username, u.avatar_color, c.body, c.created_at
                FROM issue_comments c
                INNER JOIN users u ON u.id = c.author_id
                WHERE c.id = @id;
                """;

            await using var selectCmd = CreateCommand(conn, null, selectSql, ("@id", commentId));
            await using var reader = await selectCmd.ExecuteReaderAsync(ct);
            await reader.ReadAsync(ct);
            return new IssueCommentEntity
            {
                Id = Convert.ToInt64(reader.GetValue(0), CultureInfo.InvariantCulture),
                IssueId = Convert.ToInt64(reader.GetValue(1), CultureInfo.InvariantCulture),
                RepoId = Convert.ToInt64(reader.GetValue(2), CultureInfo.InvariantCulture),
                AuthorId = Convert.ToInt64(reader.GetValue(3), CultureInfo.InvariantCulture),
                AuthorUsername = reader.GetString(4),
                AuthorAvatarColor = reader.GetString(5),
                Body = reader.GetString(6),
                CreatedAt = ReadTimestamp(reader, 7)
            };
        }, ct);
    }

    // ==================== PULL REQUEST OPERATIONS (3-PHASE ATOMIC MERGE + RECONCILER) ====================

    public async Task<PullRequestEntity> CreatePullRequestAsync(
        long repoId, long authorId, string title, string body, string sourceBranch, string targetBranch, CancellationToken ct = default)
    {
        return await ExecuteWithRetryAsync(async () =>
        {
            await using var conn = CreateConnection();
            await conn.OpenAsync(ct);
            await using var tx = await BeginWriteTransactionAsync(conn, ct);

            string bumpSql = IsPostgres
                ? "UPDATE repo_counters SET last_issue_index = last_issue_index + 1 WHERE repo_id = @repoId RETURNING last_issue_index;"
                : "UPDATE repo_counters SET last_issue_index = last_issue_index + 1 WHERE repo_id = @repoId; SELECT last_issue_index FROM repo_counters WHERE repo_id = @repoId;";

            long pullIndex;
            await using (var cmd = CreateCommand(conn, tx, bumpSql, ("@repoId", repoId)))
            {
                pullIndex = Convert.ToInt64(await cmd.ExecuteScalarAsync(ct), CultureInfo.InvariantCulture);
            }

            object now = NowParam();
            string insertSql = IsPostgres
                ? """
                  INSERT INTO pull_requests (repo_id, pull_index, author_id, title, body, source_branch, target_branch, state, is_merged, created_at, updated_at, row_version)
                  VALUES (@repoId, @pullIndex, @authorId, @title, @body, @source, @target, 'open', 0, @now, @now, 1)
                  RETURNING id;
                  """
                : """
                  INSERT INTO pull_requests (repo_id, pull_index, author_id, title, body, source_branch, target_branch, state, is_merged, created_at, updated_at, row_version)
                  VALUES (@repoId, @pullIndex, @authorId, @title, @body, @source, @target, 'open', 0, @now, @now, 1);
                  SELECT last_insert_rowid();
                  """;

            await using (var cmd = CreateCommand(conn, tx, insertSql,
                ("@repoId", repoId),
                ("@pullIndex", pullIndex),
                ("@authorId", authorId),
                ("@title", title),
                ("@body", body),
                ("@source", sourceBranch),
                ("@target", targetBranch),
                ("@now", now)))
            {
                _ = await cmd.ExecuteScalarAsync(ct);
            }

            await ExecuteNonQueryAsync(conn, tx,
                "UPDATE repositories SET open_prs_count = open_prs_count + 1, updated_at = @now, row_version = row_version + 1 WHERE id = @repoId;",
                ct, ("@now", now), ("@repoId", repoId));

            await tx.CommitAsync(ct);
            return (await GetPullRequestAsync(repoId, pullIndex, ct))!;
        }, ct);
    }

    public async Task<StateUpdateOutcome> UpdatePullRequestStateAsync(
        long repoId, long pullIndex, string newState, long expectedRowVersion, CancellationToken ct = default)
    {
        return await ExecuteWithRetryAsync(async () =>
        {
            await using var conn = CreateConnection();
            await conn.OpenAsync(ct);
            await using var tx = await BeginWriteTransactionAsync(conn, ct);

            const string checkSql = "SELECT state, is_merged, row_version FROM pull_requests WHERE repo_id = @repoId AND pull_index = @pullIndex LIMIT 1;";
            string? oldState;
            bool isMerged;
            long currentRv;

            await using (var checkCmd = CreateCommand(conn, tx, checkSql, ("@repoId", repoId), ("@pullIndex", pullIndex)))
            await using (var reader = await checkCmd.ExecuteReaderAsync(ct))
            {
                if (!await reader.ReadAsync(ct))
                    return StateUpdateOutcome.NotFound;

                oldState = reader.GetString(0);
                isMerged = Convert.ToInt32(reader.GetValue(1), CultureInfo.InvariantCulture) == 1;
                currentRv = Convert.ToInt64(reader.GetValue(2), CultureInfo.InvariantCulture);
            }

            if (isMerged || oldState == "merging" || currentRv != expectedRowVersion)
                return StateUpdateOutcome.Conflict;

            object now = NowParam();
            const string sql = """
                UPDATE pull_requests
                SET state = @state,
                    updated_at = @now,
                    row_version = row_version + 1
                WHERE repo_id = @repoId
                  AND pull_index = @pullIndex
                  AND is_merged = 0
                  AND row_version = @expectedRowVersion;
                """;

            int affected = await ExecuteNonQueryAsync(conn, tx, sql, ct,
                ("@state", newState),
                ("@now", now),
                ("@repoId", repoId),
                ("@pullIndex", pullIndex),
                ("@expectedRowVersion", expectedRowVersion));

            if (affected != 1)
                return StateUpdateOutcome.Conflict;

            if (!string.Equals(oldState, newState, StringComparison.OrdinalIgnoreCase))
            {
                int delta = newState == "open" ? 1 : -1;
                string updateRepoCount = IsPostgres
                    ? "UPDATE repositories SET open_prs_count = GREATEST(0, open_prs_count + @delta), updated_at = @now WHERE id = @repoId;"
                    : "UPDATE repositories SET open_prs_count = MAX(0, open_prs_count + @delta), updated_at = @now WHERE id = @repoId;";
                await ExecuteNonQueryAsync(conn, tx, updateRepoCount, ct, ("@delta", delta), ("@now", now), ("@repoId", repoId));
            }

            await tx.CommitAsync(ct);
            return StateUpdateOutcome.Updated;
        }, ct);
    }

    public async Task<PagedResult<PullRequestEntity>> ListPullRequestsAsync(
        long repoId, string? stateFilter = null, int page = 1, int pageSize = 25, CancellationToken ct = default)
    {
        page = Math.Max(1, page);
        pageSize = Math.Clamp(pageSize, 1, 100);
        int offset = (page - 1) * pageSize;
        string state = (stateFilter ?? string.Empty).Trim().ToLowerInvariant();

        await using var conn = CreateConnection();
        await conn.OpenAsync(ct);

        const string countSql = """
            SELECT COUNT(*)
            FROM pull_requests p
            WHERE p.repo_id = @repoId AND (@state = '' OR p.state = @state);
            """;

        int total;
        await using (var countCmd = CreateCommand(conn, null, countSql, ("@repoId", repoId), ("@state", state)))
        {
            total = Convert.ToInt32(await countCmd.ExecuteScalarAsync(ct), CultureInfo.InvariantCulture);
        }

        const string sql = """
            SELECT
                p.id,
                p.repo_id,
                p.pull_index,
                p.author_id,
                u.username AS author_username,
                u.avatar_color AS author_avatar_color,
                p.title,
                p.body,
                p.source_branch,
                p.target_branch,
                p.state,
                p.is_merged,
                p.merge_commit_sha,
                p.merged_by_user_id,
                mu.username AS merged_by_username,
                p.merged_at,
                p.created_at,
                p.updated_at,
                p.row_version
            FROM pull_requests p
            INNER JOIN users u ON u.id = p.author_id
            LEFT JOIN users mu ON mu.id = p.merged_by_user_id
            WHERE p.repo_id = @repoId
              AND (@state = '' OR p.state = @state)
            ORDER BY p.pull_index DESC
            LIMIT @limit OFFSET @offset;
            """;

        var list = new List<PullRequestEntity>();
        await using var cmd = CreateCommand(conn, null, sql,
            ("@repoId", repoId),
            ("@state", state),
            ("@limit", pageSize),
            ("@offset", offset));
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            list.Add(MapPullRequest(reader));
        }
        return new PagedResult<PullRequestEntity>(list, page, pageSize, total);
    }

    public async Task<PullRequestEntity?> GetPullRequestAsync(long repoId, long pullIndex, CancellationToken ct = default)
    {
        await using var conn = CreateConnection();
        await conn.OpenAsync(ct);

        const string sql = """
            SELECT
                p.id,
                p.repo_id,
                p.pull_index,
                p.author_id,
                u.username AS author_username,
                u.avatar_color AS author_avatar_color,
                p.title,
                p.body,
                p.source_branch,
                p.target_branch,
                p.state,
                p.is_merged,
                p.merge_commit_sha,
                p.merged_by_user_id,
                mu.username AS merged_by_username,
                p.merged_at,
                p.created_at,
                p.updated_at,
                p.row_version
            FROM pull_requests p
            INNER JOIN users u ON u.id = p.author_id
            LEFT JOIN users mu ON mu.id = p.merged_by_user_id
            WHERE p.repo_id = @repoId AND p.pull_index = @pullIndex
            LIMIT 1;
            """;

        await using var cmd = CreateCommand(conn, null, sql, ("@repoId", repoId), ("@pullIndex", pullIndex));
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        if (!await reader.ReadAsync(ct)) return null;
        return MapPullRequest(reader);
    }

    public async Task<bool> TryAcquirePullRequestMergeLockAsync(long repoId, long pullIndex, long expectedRowVersion, CancellationToken ct = default)
    {
        await using var conn = CreateConnection();
        await conn.OpenAsync(ct);

        object now = NowParam();
        const string sql = """
            UPDATE pull_requests
            SET state = 'merging',
                updated_at = @now,
                row_version = row_version + 1
            WHERE repo_id = @repoId
              AND pull_index = @pullIndex
              AND state = 'open'
              AND is_merged = 0
              AND row_version = @expectedRowVersion;
            """;

        int affected = await ExecuteNonQueryAsync(conn, null, sql, ct,
            ("@now", now),
            ("@repoId", repoId),
            ("@pullIndex", pullIndex),
            ("@expectedRowVersion", expectedRowVersion));

        return affected == 1;
    }

    /// <summary>
    /// Idempotent finalization of a merged Pull Request with automatic retries.
    /// Safe to call even if a previous attempt partially succeeded.
    /// </summary>
    public async Task<bool> FinalizePullRequestMergedAsync(
        long repoId, long pullIndex, string mergeCommitSha, long mergedByUserId, CancellationToken ct = default)
    {
        return await ExecuteWithRetryAsync(async () =>
        {
            await using var conn = CreateConnection();
            await conn.OpenAsync(ct);
            await using var tx = await BeginWriteTransactionAsync(conn, ct);

            const string checkSql = "SELECT state, is_merged FROM pull_requests WHERE repo_id = @repoId AND pull_index = @pullIndex LIMIT 1;";
            string? curState;
            bool alreadyMerged;

            await using (var checkCmd = CreateCommand(conn, tx, checkSql, ("@repoId", repoId), ("@pullIndex", pullIndex)))
            await using (var reader = await checkCmd.ExecuteReaderAsync(ct))
            {
                if (!await reader.ReadAsync(ct)) return false;
                curState = reader.GetString(0);
                alreadyMerged = Convert.ToInt32(reader.GetValue(1), CultureInfo.InvariantCulture) == 1;
            }

            if (alreadyMerged && curState == "merged")
            {
                await tx.CommitAsync(ct);
                return true;
            }

            object now = NowParam();
            const string sql = """
                UPDATE pull_requests
                SET state = 'merged',
                    is_merged = 1,
                    merge_commit_sha = @sha,
                    merged_by_user_id = @mergedBy,
                    merged_at = @now,
                    updated_at = @now,
                    row_version = row_version + 1
                WHERE repo_id = @repoId
                  AND pull_index = @pullIndex
                  AND state IN ('merging', 'open')
                  AND is_merged = 0;
                """;

            int affected = await ExecuteNonQueryAsync(conn, tx, sql, ct,
                ("@sha", mergeCommitSha),
                ("@mergedBy", mergedByUserId),
                ("@now", now),
                ("@repoId", repoId),
                ("@pullIndex", pullIndex));

            if (affected == 1)
            {
                string decPrSql = IsPostgres
                    ? "UPDATE repositories SET open_prs_count = GREATEST(0, open_prs_count - 1), updated_at = @now, row_version = row_version + 1 WHERE id = @repoId;"
                    : "UPDATE repositories SET open_prs_count = MAX(0, open_prs_count - 1), updated_at = @now, row_version = row_version + 1 WHERE id = @repoId;";
                await ExecuteNonQueryAsync(conn, tx, decPrSql, ct, ("@now", now), ("@repoId", repoId));
            }

            await tx.CommitAsync(ct);
            return affected == 1;
        }, ct);
    }

    public async Task RollbackPullRequestMergeLockAsync(long repoId, long pullIndex, CancellationToken ct = default)
    {
        await using var conn = CreateConnection();
        await conn.OpenAsync(ct);

        const string sql = """
            UPDATE pull_requests
            SET state = 'open',
                updated_at = @now,
                row_version = row_version + 1
            WHERE repo_id = @repoId
              AND pull_index = @pullIndex
              AND state = 'merging'
              AND is_merged = 0;
            """;

        await ExecuteNonQueryAsync(conn, null, sql, ct, ("@now", NowParam()), ("@repoId", repoId), ("@pullIndex", pullIndex));
    }

    public sealed record StuckMergingPrRecord(
        long RepoId,
        string OwnerUsername,
        string RepoName,
        long PullIndex,
        long AuthorId,
        string SourceBranch,
        string TargetBranch,
        DateTimeOffset UpdatedAt);

    public async Task<IReadOnlyList<StuckMergingPrRecord>> ListStuckMergingPullRequestsAsync(
        DateTimeOffset olderThan, CancellationToken ct = default)
    {
        await using var conn = CreateConnection();
        await conn.OpenAsync(ct);

        const string sql = """
            SELECT p.repo_id, u.username, r.name, p.pull_index, p.author_id, p.source_branch, p.target_branch, p.updated_at
            FROM pull_requests p
            INNER JOIN repositories r ON r.id = p.repo_id
            INNER JOIN users u ON u.id = r.owner_id
            WHERE p.state = 'merging'
              AND p.is_merged = 0
              AND p.updated_at <= @cutoff;
            """;

        var list = new List<StuckMergingPrRecord>();
        await using var cmd = CreateCommand(conn, null, sql, ("@cutoff", FormatTimestampParam(olderThan)));
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            list.Add(new StuckMergingPrRecord(
                RepoId: Convert.ToInt64(reader.GetValue(0), CultureInfo.InvariantCulture),
                OwnerUsername: reader.GetString(1),
                RepoName: reader.GetString(2),
                PullIndex: Convert.ToInt64(reader.GetValue(3), CultureInfo.InvariantCulture),
                AuthorId: Convert.ToInt64(reader.GetValue(4), CultureInfo.InvariantCulture),
                SourceBranch: reader.GetString(5),
                TargetBranch: reader.GetString(6),
                UpdatedAt: ReadTimestamp(reader, 7)));
        }
        return list;
    }

    // ==================== WEBHOOKS & AUDIT LOGS (NO SYNCHRONOUS O(N) DELETE) ====================

    public async Task<WebhookEntity> CreateWebhookAsync(long repoId, string targetUrl, string secret, string eventsCsv, CancellationToken ct = default)
    {
        await using var conn = CreateConnection();
        await conn.OpenAsync(ct);

        object now = NowParam();
        string sql = IsPostgres
            ? """
              INSERT INTO webhooks (repo_id, target_url, secret, events_csv, is_active, created_at)
              VALUES (@repoId, @url, @secret, @events, 1, @now)
              RETURNING id;
              """
            : """
              INSERT INTO webhooks (repo_id, target_url, secret, events_csv, is_active, created_at)
              VALUES (@repoId, @url, @secret, @events, 1, @now);
              SELECT last_insert_rowid();
              """;

        await using var cmd = CreateCommand(conn, null, sql,
            ("@repoId", repoId),
            ("@url", targetUrl),
            ("@secret", secret),
            ("@events", eventsCsv),
            ("@now", now));

        long id = Convert.ToInt64(await cmd.ExecuteScalarAsync(ct), CultureInfo.InvariantCulture);
        return new WebhookEntity
        {
            Id = id,
            RepoId = repoId,
            TargetUrl = targetUrl,
            Secret = secret,
            EventsCsv = eventsCsv,
            IsActive = true,
            CreatedAt = DateTimeOffset.UtcNow
        };
    }

    public async Task<IReadOnlyList<WebhookEntity>> ListWebhooksAsync(long repoId, CancellationToken ct = default)
    {
        await using var conn = CreateConnection();
        await conn.OpenAsync(ct);

        const string sql = """
            SELECT id, repo_id, target_url, secret, events_csv, is_active, created_at
            FROM webhooks
            WHERE repo_id = @repoId
            ORDER BY id DESC;
            """;

        var list = new List<WebhookEntity>();
        await using var cmd = CreateCommand(conn, null, sql, ("@repoId", repoId));
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            list.Add(new WebhookEntity
            {
                Id = Convert.ToInt64(reader.GetValue(0), CultureInfo.InvariantCulture),
                RepoId = Convert.ToInt64(reader.GetValue(1), CultureInfo.InvariantCulture),
                TargetUrl = reader.GetString(2),
                Secret = reader.GetString(3),
                EventsCsv = reader.GetString(4),
                IsActive = Convert.ToInt32(reader.GetValue(5), CultureInfo.InvariantCulture) == 1,
                CreatedAt = ReadTimestamp(reader, 6)
            });
        }
        return list;
    }

    public async Task AppendAuditLogAsync(long? actorUserId, string actorUsername, string eventType, string summary, CancellationToken ct = default)
    {
        await using var conn = CreateConnection();
        await conn.OpenAsync(ct);

        object now = NowParam();
        const string sql = """
            INSERT INTO audit_logs (actor_user_id, actor_username, event_type, summary, created_at)
            VALUES (@actorId, @actorName, @type, @summary, @now);
            """;

        await ExecuteNonQueryAsync(conn, null, sql, ct,
            ("@actorId", (object?)actorUserId ?? DBNull.Value),
            ("@actorName", actorUsername),
            ("@type", eventType),
            ("@summary", summary),
            ("@now", now));
    }

    public async Task<PagedResult<AuditLogEntity>> ListRecentAuditLogsAsync(int page = 1, int pageSize = 25, CancellationToken ct = default)
    {
        page = Math.Max(1, page);
        pageSize = Math.Clamp(pageSize, 1, 100);
        int offset = (page - 1) * pageSize;

        await using var conn = CreateConnection();
        await conn.OpenAsync(ct);

        int total;
        await using (var countCmd = CreateCommand(conn, null, "SELECT COUNT(*) FROM audit_logs;"))
        {
            total = Convert.ToInt32(await countCmd.ExecuteScalarAsync(ct), CultureInfo.InvariantCulture);
        }

        const string sql = """
            SELECT id, actor_user_id, actor_username, event_type, summary, created_at
            FROM audit_logs
            ORDER BY id DESC
            LIMIT @limit OFFSET @offset;
            """;

        var list = new List<AuditLogEntity>();
        await using var cmd = CreateCommand(conn, null, sql, ("@limit", pageSize), ("@offset", offset));
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            list.Add(new AuditLogEntity
            {
                Id = Convert.ToInt64(reader.GetValue(0), CultureInfo.InvariantCulture),
                ActorUserId = reader.IsDBNull(1) ? null : Convert.ToInt64(reader.GetValue(1), CultureInfo.InvariantCulture),
                ActorUsername = reader.GetString(2),
                EventType = reader.GetString(3),
                Summary = reader.GetString(4),
                CreatedAt = ReadTimestamp(reader, 5)
            });
        }
        return new PagedResult<AuditLogEntity>(list, page, pageSize, total);
    }


    public async Task RecordWebhookDeliveryAsync(
        long webhookId, long repoId, string eventName, string targetUrl, string signatureHeader,
        int statusCode, int attemptCount, string status, double durationMs, CancellationToken ct = default)
    {
        await using var conn = CreateConnection();
        await conn.OpenAsync(ct);

        const string sql = """
            INSERT INTO webhook_deliveries (webhook_id, repo_id, event_name, target_url, signature_header, status_code, attempt_count, status, duration_ms, delivered_at)
            VALUES (@whId, @repoId, @ev, @url, @sig, @code, @att, @st, @dur, @now);
            """;

        await ExecuteNonQueryAsync(conn, null, sql, ct,
            ("@whId", webhookId),
            ("@repoId", repoId),
            ("@ev", eventName),
            ("@url", targetUrl),
            ("@sig", signatureHeader),
            ("@code", statusCode),
            ("@att", attemptCount),
            ("@st", status),
            ("@dur", durationMs),
            ("@now", NowParam()));
    }

    public async Task<IReadOnlyList<WebhookDeliveryEntity>> ListWebhookDeliveriesAsync(long repoId, int limit = 25, CancellationToken ct = default)
    {
        await using var conn = CreateConnection();
        await conn.OpenAsync(ct);

        const string sql = """
            SELECT id, webhook_id, repo_id, event_name, target_url, signature_header, status_code, attempt_count, status, duration_ms, delivered_at
            FROM webhook_deliveries
            WHERE repo_id = @repoId
            ORDER BY id DESC
            LIMIT @limit;
            """;

        var list = new List<WebhookDeliveryEntity>();
        await using var cmd = CreateCommand(conn, null, sql, ("@repoId", repoId), ("@limit", Math.Clamp(limit, 1, 100)));
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            list.Add(new WebhookDeliveryEntity
            {
                Id = Convert.ToInt64(reader.GetValue(0), CultureInfo.InvariantCulture),
                WebhookId = Convert.ToInt64(reader.GetValue(1), CultureInfo.InvariantCulture),
                RepoId = Convert.ToInt64(reader.GetValue(2), CultureInfo.InvariantCulture),
                EventName = reader.GetString(3),
                TargetUrl = reader.GetString(4),
                SignatureHeader = reader.GetString(5),
                StatusCode = Convert.ToInt32(reader.GetValue(6), CultureInfo.InvariantCulture),
                AttemptCount = Convert.ToInt32(reader.GetValue(7), CultureInfo.InvariantCulture),
                Status = reader.GetString(8),
                DurationMs = Convert.ToDouble(reader.GetValue(9), CultureInfo.InvariantCulture),
                DeliveredAt = ReadTimestamp(reader, 10)
            });
        }
        return list;
    }

    public async Task<string> CreateSqliteHotBackupAsync(string backupDirectory, CancellationToken ct = default)
    {
        if (IsPostgres)
            throw new InvalidOperationException("VACUUM INTO hot backup is specific to SQLite; use pg_dump for PostgreSQL.");

        Directory.CreateDirectory(backupDirectory);
        string timestamp = DateTimeOffset.UtcNow.ToString("yyyyMMdd_HHmmss", CultureInfo.InvariantCulture);
        string backupFile = Path.GetFullPath(Path.Combine(backupDirectory, $"gitea_backup_{timestamp}.db"));
        string escapedPath = backupFile.Replace("'", "''", StringComparison.Ordinal);

        await using var conn = CreateConnection();
        await conn.OpenAsync(ct);
        await ExecuteNonQueryAsync(conn, null, $"VACUUM INTO '{escapedPath}';", ct);
        return backupFile;
    }


    // ==================== CI/CD ACTIONS WORKFLOW RUNS (ATOMIC SEQUENCE + CAS + REAPER + ZERO N+1) ====================

    public Task<CiWorkflowRunEntity> CreateCiWorkflowRunAsync(
        long repoId,
        string workflowName,
        string triggerEvent,
        string branch,
        string? commitSha,
        long? triggeredByUserId,
        CancellationToken ct = default)
    {
        return CreateCiWorkflowRunAsync(
            repoId,
            triggeredByUserId ?? 1L,
            workflowName,
            triggerEvent,
            branch,
            string.IsNullOrWhiteSpace(commitSha) ? "0000000000000000000000000000000000000000" : commitSha.Trim(),
            ct);
    }

    public async Task<CiWorkflowRunEntity> CreateCiWorkflowRunAsync(
        long repoId,
        long triggeredByUserId,
        string workflowName,
        string triggerEvent,
        string branch,
        string commitSha,
        CancellationToken ct = default)
    {
        return await ExecuteWithRetryAsync(async () =>
        {
            await using var conn = CreateConnection();
            await conn.OpenAsync(ct);
            await using var tx = await BeginWriteTransactionAsync(conn, ct);

            string bumpSql = IsPostgres
                ? "UPDATE repo_counters SET last_ci_run_index = last_ci_run_index + 1 WHERE repo_id = @repoId RETURNING last_ci_run_index;"
                : "UPDATE repo_counters SET last_ci_run_index = last_ci_run_index + 1 WHERE repo_id = @repoId; SELECT last_ci_run_index FROM repo_counters WHERE repo_id = @repoId;";

            long runIndex;
            await using (var cmd = CreateCommand(conn, tx, bumpSql, ("@repoId", repoId)))
            {
                runIndex = Convert.ToInt64(await cmd.ExecuteScalarAsync(ct), CultureInfo.InvariantCulture);
            }

            object now = NowParam();
            string insertSql = IsPostgres
                ? """
                  INSERT INTO ci_workflow_runs (repo_id, run_index, triggered_by_user_id, workflow_name, trigger_event, branch, commit_sha, status, logs_text, duration_ms, created_at, completed_at, row_version)
                  VALUES (@repoId, @runIdx, @userId, @wfName, @ev, @branch, @sha, 'queued', 'Queued for execution...', 0, @now, NULL, 1)
                  RETURNING id;
                  """
                : """
                  INSERT INTO ci_workflow_runs (repo_id, run_index, triggered_by_user_id, workflow_name, trigger_event, branch, commit_sha, status, logs_text, duration_ms, created_at, completed_at, row_version)
                  VALUES (@repoId, @runIdx, @userId, @wfName, @ev, @branch, @sha, 'queued', 'Queued for execution...', 0, @now, NULL, 1);
                  SELECT last_insert_rowid();
                  """;

            long insertedId;
            await using (var cmd = CreateCommand(conn, tx, insertSql,
                ("@repoId", repoId),
                ("@runIdx", runIndex),
                ("@userId", triggeredByUserId),
                ("@wfName", workflowName),
                ("@ev", triggerEvent),
                ("@branch", branch),
                ("@sha", commitSha),
                ("@now", now)))
            {
                insertedId = Convert.ToInt64(await cmd.ExecuteScalarAsync(ct), CultureInfo.InvariantCulture);
            }

            await tx.CommitAsync(ct);
            return (await GetCiWorkflowRunByIdAsync(repoId, insertedId, ct))!;
        }, ct);
    }

    /// <summary>
    /// Atomic CAS worker claim matching strictly by Primary Key `id = @runId` and `status = 'queued'`.
    /// Prevents multi-row ambiguity when `run_index` of one row equals `id` of another row.
    /// </summary>
    public async Task<bool> TryClaimQueuedCiRunAsync(
        long repoId, long runId, CancellationToken ct = default)
    {
        return await ExecuteWithRetryAsync(async () =>
        {
            await using var conn = CreateConnection();
            await conn.OpenAsync(ct);

            const string sql = """
                UPDATE ci_workflow_runs
                SET status = 'running',
                    row_version = row_version + 1
                WHERE repo_id = @repoId
                  AND id = @runId
                  AND status = 'queued';
                """;

            int affected = await ExecuteNonQueryAsync(conn, null, sql, ct,
                ("@repoId", repoId),
                ("@runId", runId));
            return affected == 1;
        }, ct);
    }

    public async Task<bool> TryClaimQueuedCiRunAsync(
        long repoId, long runIndex, long expectedRowVersion, CancellationToken ct = default)
    {
        return await ExecuteWithRetryAsync(async () =>
        {
            await using var conn = CreateConnection();
            await conn.OpenAsync(ct);

            const string sql = """
                UPDATE ci_workflow_runs
                SET status = 'running',
                    row_version = row_version + 1
                WHERE repo_id = @repoId
                  AND run_index = @runIdx
                  AND status = 'queued'
                  AND row_version = @expectedRv;
                """;

            int affected = await ExecuteNonQueryAsync(conn, null, sql, ct,
                ("@repoId", repoId),
                ("@runIdx", runIndex),
                ("@expectedRv", expectedRowVersion));
            return affected == 1;
        }, ct);
    }

    public async Task<bool> CompleteCiWorkflowRunAsync(
        long repoId, long runIndex, string finalStatus, string logsText, double durationMs, CancellationToken ct = default)
    {
        return await ExecuteWithRetryAsync(async () =>
        {
            await using var conn = CreateConnection();
            await conn.OpenAsync(ct);

            const string sql = """
                UPDATE ci_workflow_runs
                SET status = @status,
                    logs_text = @logs,
                    duration_ms = @dur,
                    completed_at = @now,
                    row_version = row_version + 1
                WHERE repo_id = @repoId
                  AND run_index = @runIdx
                  AND status IN ('running', 'queued');
                """;

            int affected = await ExecuteNonQueryAsync(conn, null, sql, ct,
                ("@status", finalStatus),
                ("@logs", logsText),
                ("@dur", durationMs),
                ("@now", NowParam()),
                ("@repoId", repoId),
                ("@runIdx", runIndex));
            return affected == 1;
        }, ct);
    }

    /// <summary>
    /// QA Review Council Member #3 (Lead QA Engineer): Stuck CI/CD Run Reaper.
    /// Automatically recovers orphaned CI runs stuck in `'running'` or `'queued'` state after an unexpected
    /// power outage or process crash by transitioning them atomically to `'failed'`.
    /// </summary>
    public async Task<int> ReapStuckCiWorkflowRunsAsync(TimeSpan stuckThreshold, CancellationToken ct = default)
    {
        return await ExecuteWithRetryAsync(async () =>
        {
            await using var conn = CreateConnection();
            await conn.OpenAsync(ct);

            var cutoff = DateTimeOffset.UtcNow.Subtract(stuckThreshold);
            string sql = IsPostgres
                ? """
                  UPDATE ci_workflow_runs
                  SET status = 'failed',
                      logs_text = logs_text || CHR(10) || '[REAPER] Pipeline finished with status: FAILED (Orphaned/stuck run reaped after unexpected host restart or timeout).',
                      completed_at = @now,
                      row_version = row_version + 1
                  WHERE status IN ('running', 'queued')
                    AND created_at <= @cutoff;
                  """
                : """
                  UPDATE ci_workflow_runs
                  SET status = 'failed',
                      logs_text = logs_text || CHAR(10) || '[REAPER] Pipeline finished with status: FAILED (Orphaned/stuck run reaped after unexpected host restart or timeout).',
                      completed_at = @now,
                      row_version = row_version + 1
                  WHERE status IN ('running', 'queued')
                    AND created_at <= @cutoff;
                  """;

            return await ExecuteNonQueryAsync(conn, null, sql, ct,
                ("@now", NowParam()),
                ("@cutoff", FormatTimestampParam(cutoff)));
        }, ct);
    }

    public async Task<CiWorkflowRunEntity?> GetCiWorkflowRunByIdAsync(long repoId, long runId, CancellationToken ct = default)
    {
        await using var conn = CreateConnection();
        await conn.OpenAsync(ct);

        const string sql = """
            SELECT
                c.id,
                c.repo_id,
                c.run_index,
                c.triggered_by_user_id,
                u.username AS triggered_by_username,
                c.workflow_name,
                c.trigger_event,
                c.branch,
                c.commit_sha,
                c.status,
                c.logs_text,
                c.duration_ms,
                c.created_at,
                c.completed_at,
                c.row_version
            FROM ci_workflow_runs c
            INNER JOIN users u ON u.id = c.triggered_by_user_id
            WHERE c.repo_id = @repoId AND c.id = @runId
            LIMIT 1;
            """;

        await using var cmd = CreateCommand(conn, null, sql, ("@repoId", repoId), ("@runId", runId));
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        if (!await reader.ReadAsync(ct)) return null;
        return MapCiWorkflowRun(reader);
    }

    public async Task<CiWorkflowRunEntity?> GetCiWorkflowRunAsync(long repoId, long runIndex, CancellationToken ct = default)
    {
        await using var conn = CreateConnection();
        await conn.OpenAsync(ct);

        const string sql = """
            SELECT
                c.id,
                c.repo_id,
                c.run_index,
                c.triggered_by_user_id,
                u.username AS triggered_by_username,
                c.workflow_name,
                c.trigger_event,
                c.branch,
                c.commit_sha,
                c.status,
                c.logs_text,
                c.duration_ms,
                c.created_at,
                c.completed_at,
                c.row_version
            FROM ci_workflow_runs c
            INNER JOIN users u ON u.id = c.triggered_by_user_id
            WHERE c.repo_id = @repoId AND c.run_index = @runIdx
            LIMIT 1;
            """;

        await using var cmd = CreateCommand(conn, null, sql, ("@repoId", repoId), ("@runIdx", runIndex));
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        if (!await reader.ReadAsync(ct)) return null;
        return MapCiWorkflowRun(reader);
    }

    /// <summary>
    /// ZERO N+1 QUERY: Lists paginated CI workflow runs with trigger user details in 2 bounded queries (1 COUNT + 1 SELECT INNER JOIN).
    /// </summary>
    public async Task<PagedResult<CiWorkflowRunEntity>> ListCiWorkflowRunsAsync(
        long repoId, int page = 1, int pageSize = 25, CancellationToken ct = default)
    {
        page = Math.Max(1, page);
        pageSize = Math.Clamp(pageSize, 1, 100);
        int offset = (page - 1) * pageSize;

        await using var conn = CreateConnection();
        await conn.OpenAsync(ct);

        int total;
        await using (var countCmd = CreateCommand(conn, null, "SELECT COUNT(*) FROM ci_workflow_runs WHERE repo_id = @repoId;", ("@repoId", repoId)))
        {
            total = Convert.ToInt32(await countCmd.ExecuteScalarAsync(ct), CultureInfo.InvariantCulture);
        }

        const string sql = """
            SELECT
                c.id,
                c.repo_id,
                c.run_index,
                c.triggered_by_user_id,
                u.username AS triggered_by_username,
                c.workflow_name,
                c.trigger_event,
                c.branch,
                c.commit_sha,
                c.status,
                c.logs_text,
                c.duration_ms,
                c.created_at,
                c.completed_at,
                c.row_version
            FROM ci_workflow_runs c
            INNER JOIN users u ON u.id = c.triggered_by_user_id
            WHERE c.repo_id = @repoId
            ORDER BY c.run_index DESC
            LIMIT @limit OFFSET @offset;
            """;

        var list = new List<CiWorkflowRunEntity>();
        await using var cmd = CreateCommand(conn, null, sql, ("@repoId", repoId), ("@limit", pageSize), ("@offset", offset));
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            list.Add(MapCiWorkflowRun(reader));
        }

        return new PagedResult<CiWorkflowRunEntity>(list, page, pageSize, total);
    }

    // ==================== TRANSACTION, RETRY & MAPPING HELPERS ====================

    private async Task<DbTransaction> BeginWriteTransactionAsync(DbConnection conn, CancellationToken ct)
    {
        if (IsPostgres)
            return await conn.BeginTransactionAsync(IsolationLevel.ReadCommitted, ct);

        return await conn.BeginTransactionAsync(IsolationLevel.Serializable, ct);
    }

    private static async Task<T> ExecuteWithRetryAsync<T>(Func<Task<T>> operation, CancellationToken ct)
    {
        const int maxRetries = 5;
        for (int attempt = 1; ; attempt++)
        {
            try
            {
                return await operation();
            }
            catch (PostgresException pgEx) when (pgEx.SqlState is "40001" or "40P01" && attempt < maxRetries)
            {
                await Task.Delay(TimeSpan.FromMilliseconds(15 * attempt), ct);
            }
            catch (SqliteException sqEx) when (sqEx.SqliteErrorCode is 5 or 6 && attempt < maxRetries)
            {
                await Task.Delay(TimeSpan.FromMilliseconds(15 * attempt), ct);
            }
        }
    }

    private static bool IsUniqueConstraintViolation(Exception ex)
    {
        if (ex is PostgresException pgEx && pgEx.SqlState == "23505")
            return true;
        if (ex is SqliteException sqEx && (sqEx.SqliteErrorCode == 19 || sqEx.SqliteExtendedErrorCode is 2067 or 1555))
            return true;
        return false;
    }

    private static string EscapeLike(string value)
    {
        return value
            .Replace("\\", "\\\\", StringComparison.Ordinal)
            .Replace("%", "\\%", StringComparison.Ordinal)
            .Replace("_", "\\_", StringComparison.Ordinal);
    }

    private object NowParam() => FormatTimestampParam(DateTimeOffset.UtcNow);

    private object FormatTimestampParam(DateTimeOffset dto)
    {
        if (IsPostgres)
            return dto.UtcDateTime;
        return dto.UtcDateTime.ToString("O", CultureInfo.InvariantCulture);
    }

    private static DateTimeOffset ReadTimestamp(DbDataReader reader, int ordinal)
    {
        object val = reader.GetValue(ordinal);
        if (val is DateTimeOffset dto) return dto;
        if (val is DateTime dt) return new DateTimeOffset(DateTime.SpecifyKind(dt, DateTimeKind.Utc));
        string str = Convert.ToString(val, CultureInfo.InvariantCulture) ?? string.Empty;
        return DateTimeOffset.Parse(str, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind);
    }

    private static DbCommand CreateCommand(DbConnection conn, DbTransaction? tx, string sql, params (string Name, object Value)[] parameters)
    {
        var rawCmd = conn.CreateCommand();
        rawCmd.Transaction = tx;
        rawCmd.CommandText = sql;
        foreach (var (name, value) in parameters)
        {
            var p = rawCmd.CreateParameter();
            p.ParameterName = name;
            p.Value = value;
            rawCmd.Parameters.Add(p);
        }
        return new CountingDbCommand(rawCmd);
    }

    private static async Task<int> ExecuteNonQueryAsync(DbConnection conn, DbTransaction? tx, string sql, CancellationToken ct, params (string Name, object Value)[] parameters)
    {
        await using var cmd = CreateCommand(conn, tx, sql, parameters);
        return await cmd.ExecuteNonQueryAsync(ct);
    }

    private static UserEntity MapUser(DbDataReader r) => new()
    {
        Id = Convert.ToInt64(r.GetValue(0), CultureInfo.InvariantCulture),
        Username = r.GetString(1),
        Email = r.GetString(2),
        FullName = r.GetString(3),
        PasswordHash = r.GetString(4),
        PasswordSalt = r.GetString(5),
        IsAdmin = Convert.ToInt32(r.GetValue(6), CultureInfo.InvariantCulture) == 1,
        MustChangePassword = Convert.ToInt32(r.GetValue(7), CultureInfo.InvariantCulture) == 1,
        AvatarColor = r.GetString(8),
        CreatedAt = ReadTimestamp(r, 9),
        RowVersion = Convert.ToInt64(r.GetValue(10), CultureInfo.InvariantCulture)
    };

    private static RepositoryEntity MapRepository(DbDataReader r) => new()
    {
        Id = Convert.ToInt64(r.GetValue(0), CultureInfo.InvariantCulture),
        OwnerId = Convert.ToInt64(r.GetValue(1), CultureInfo.InvariantCulture),
        OwnerUsername = r.GetString(2),
        Name = r.GetString(3),
        Description = r.GetString(4),
        IsPrivate = Convert.ToInt32(r.GetValue(5), CultureInfo.InvariantCulture) == 1,
        DefaultBranch = r.GetString(6),
        StarsCount = Convert.ToInt32(r.GetValue(7), CultureInfo.InvariantCulture),
        ForksCount = Convert.ToInt32(r.GetValue(8), CultureInfo.InvariantCulture),
        OpenIssuesCount = Convert.ToInt32(r.GetValue(9), CultureInfo.InvariantCulture),
        OpenPrsCount = Convert.ToInt32(r.GetValue(10), CultureInfo.InvariantCulture),
        CreatedAt = ReadTimestamp(r, 11),
        UpdatedAt = ReadTimestamp(r, 12),
        RowVersion = Convert.ToInt64(r.GetValue(13), CultureInfo.InvariantCulture)
    };

    private static IssueEntity MapIssue(DbDataReader r) => new()
    {
        Id = Convert.ToInt64(r.GetValue(0), CultureInfo.InvariantCulture),
        RepoId = Convert.ToInt64(r.GetValue(1), CultureInfo.InvariantCulture),
        IssueIndex = Convert.ToInt64(r.GetValue(2), CultureInfo.InvariantCulture),
        AuthorId = Convert.ToInt64(r.GetValue(3), CultureInfo.InvariantCulture),
        AuthorUsername = r.GetString(4),
        AuthorAvatarColor = r.GetString(5),
        Title = r.GetString(6),
        Body = r.GetString(7),
        State = r.GetString(8),
        LabelsCsv = r.GetString(9),
        CommentsCount = Convert.ToInt32(r.GetValue(10), CultureInfo.InvariantCulture),
        CreatedAt = ReadTimestamp(r, 11),
        UpdatedAt = ReadTimestamp(r, 12),
        RowVersion = Convert.ToInt64(r.GetValue(13), CultureInfo.InvariantCulture)
    };

    private static PullRequestEntity MapPullRequest(DbDataReader r) => new()
    {
        Id = Convert.ToInt64(r.GetValue(0), CultureInfo.InvariantCulture),
        RepoId = Convert.ToInt64(r.GetValue(1), CultureInfo.InvariantCulture),
        PullIndex = Convert.ToInt64(r.GetValue(2), CultureInfo.InvariantCulture),
        AuthorId = Convert.ToInt64(r.GetValue(3), CultureInfo.InvariantCulture),
        AuthorUsername = r.GetString(4),
        AuthorAvatarColor = r.GetString(5),
        Title = r.GetString(6),
        Body = r.GetString(7),
        SourceBranch = r.GetString(8),
        TargetBranch = r.GetString(9),
        State = r.GetString(10),
        IsMerged = Convert.ToInt32(r.GetValue(11), CultureInfo.InvariantCulture) == 1,
        MergeCommitSha = r.IsDBNull(12) ? null : r.GetString(12),
        MergedByUserId = r.IsDBNull(13) ? null : Convert.ToInt64(r.GetValue(13), CultureInfo.InvariantCulture),
        MergedByUsername = r.IsDBNull(14) ? null : r.GetString(14),
        MergedAt = r.IsDBNull(15) ? null : ReadTimestamp(r, 15),
        CreatedAt = ReadTimestamp(r, 16),
        UpdatedAt = ReadTimestamp(r, 17),
        RowVersion = Convert.ToInt64(r.GetValue(18), CultureInfo.InvariantCulture)
    };

    private static CiWorkflowRunEntity MapCiWorkflowRun(DbDataReader r) => new()
    {
        Id = Convert.ToInt64(r.GetValue(0), CultureInfo.InvariantCulture),
        RepoId = Convert.ToInt64(r.GetValue(1), CultureInfo.InvariantCulture),
        RunIndex = Convert.ToInt64(r.GetValue(2), CultureInfo.InvariantCulture),
        TriggeredByUserId = Convert.ToInt64(r.GetValue(3), CultureInfo.InvariantCulture),
        TriggeredByUsername = r.GetString(4),
        WorkflowName = r.GetString(5),
        TriggerEvent = r.GetString(6),
        Branch = r.GetString(7),
        CommitSha = r.GetString(8),
        Status = r.GetString(9),
        LogsText = r.GetString(10),
        DurationMs = Convert.ToDouble(r.GetValue(11), CultureInfo.InvariantCulture),
        CreatedAt = ReadTimestamp(r, 12),
        CompletedAt = r.IsDBNull(13) ? null : ReadTimestamp(r, 13),
        RowVersion = Convert.ToInt64(r.GetValue(14), CultureInfo.InvariantCulture)
    };
}
