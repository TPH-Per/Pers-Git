using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using Gitea.Net.Data;
using Gitea.Net.Git;
using Gitea.Net.Models;
using Gitea.Net.Security;
using Gitea.Net.Services;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Gitea.Net.Tests;

/// <summary>
/// Isolated per-test fixture factory using `builder.UseSetting(...)` so top-level Program.cs
/// rate-limit and database settings are applied before `WebApplication.CreateBuilder` builds the host.
/// Every test creates its own isolated temporary directory and database (zero shared state between tests).
/// </summary>
public sealed class IsolatedGiteaHost : IDisposable
{
    public string TempDir { get; }
    public WebApplicationFactory<Program> Factory { get; }
    public GiteaDbRepository Db { get; }
    public GitEngineService Git { get; }
    public SecurityService Security { get; }
    public MergeReconcilerHostedService Reconciler { get; }
    public CiPipelineRunnerService CiRunner { get; }

    public IsolatedGiteaHost()
    {
        TempDir = Path.Combine(Path.GetTempPath(), $"gitea_net_test_{Guid.NewGuid():N}");
        Directory.CreateDirectory(TempDir);

        string provider = Environment.GetEnvironmentVariable("GITEA_TEST_PROVIDER") ?? "Sqlite";
        string sqliteConn = $"Data Source={Path.Combine(TempDir, "test.db")};Mode=ReadWriteCreate;";
        string pgConn = Environment.GetEnvironmentVariable("GITEA_POSTGRES_CONN") ?? "";

        Factory = new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
        {
            builder.UseEnvironment("Development");
            builder.UseSetting("Database:Provider", provider);
            builder.UseSetting("Database:SqliteConnectionString", sqliteConn);
            if (!string.IsNullOrEmpty(pgConn))
            {
                builder.UseSetting("Database:PostgresConnectionString", pgConn);
            }
            builder.UseSetting("Git:RepositoriesRootPath", Path.Combine(TempDir, "repos"));
            builder.UseSetting("Git:WorkingTempRootPath", Path.Combine(TempDir, "worktrees"));
            builder.UseSetting("Security:TokenSecret", "UnitTest-Production-Ready-32Byte-Minimum-SecretKey-2026!");
            builder.UseSetting("Security:RateLimitPermitLimit", "50000");
            builder.UseSetting("Security:AuthRateLimitPermitLimit", "5000");
            builder.UseSetting("Security:RateLimitWindowSeconds", "60");
        });

        Db = Factory.Services.GetRequiredService<GiteaDbRepository>();
        Git = Factory.Services.GetRequiredService<GitEngineService>();
        Security = Factory.Services.GetRequiredService<SecurityService>();
        Reconciler = Factory.Services.GetRequiredService<MergeReconcilerHostedService>();
        CiRunner = Factory.Services.GetRequiredService<CiPipelineRunnerService>();
    }

    public HttpClient CreateBasicClient(string username, string passwordOrPat)
    {
        var client = Factory.CreateClient();
        string b64 = Convert.ToBase64String(Encoding.UTF8.GetBytes($"{username}:{passwordOrPat}"));
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Basic", b64);
        return client;
    }

    public void Dispose()
    {
        Factory.Dispose();
        try
        {
            if (Directory.Exists(TempDir))
                Directory.Delete(TempDir, recursive: true);
        }
        catch
        {
        }
    }
}

public sealed class ProductionReadinessTests
{
    // ==================== 1. P0-KEYEDLOCKPROVIDER MUTUAL EXCLUSION & TIMEOUT REGRESSION TEST ====================

    [Fact]
    public async Task Regression_P0_KeyedLockProvider_TimeoutDoesNotCorruptRefCountOrBreakMutualExclusion()
    {
        var locks = new KeyedLockProvider();
        const string key = "repo:gitea-admin/critical-repo";

        // Holder 1 acquires lock
        var holder1 = await locks.AcquireAsync(key, TimeSpan.FromSeconds(5));
        Assert.Equal(1, locks.ActiveKeyCount);

        // Waiter 2 times out after 50ms -> must NOT double-decrement RefCount or evict key while holder1 holds it
        await Assert.ThrowsAsync<TimeoutException>(async () =>
        {
            using var _ = await locks.AcquireAsync(key, TimeSpan.FromMilliseconds(50));
        });

        // Key must still be active and held by holder1!
        Assert.Equal(1, locks.ActiveKeyCount);

        // Waiter 3 must ALSO time out because holder1 still holds the lock (mutual exclusion intact!)
        await Assert.ThrowsAsync<TimeoutException>(async () =>
        {
            using var _ = await locks.AcquireAsync(key, TimeSpan.FromMilliseconds(50));
        });

        // Release holder1 -> now RefCount reaches 0 and key is evicted cleanly
        holder1.Dispose();
        Assert.Equal(0, locks.ActiveKeyCount);
    }

    // ==================== 2. P0-AUTHZ, PRIVATE REPO 404 ANTI-ENUMERATION & CSRF TESTS ====================

    [Fact]
    public async Task Regression_P0_P1_AuthZ_PrivateRepoReturns404ToOutsiders_AndCsrfBlocksCrossSiteCookieMutations()
    {
        using var host = new IsolatedGiteaHost();
        using var anonClient = host.Factory.CreateClient();
        using var adminClient = host.CreateBasicClient("gitea-admin", "Admin@12345");
        using var devClient = host.CreateBasicClient("linus-dev", "Dev@12345");

        // 1. Admin creates a PRIVATE repository
        var privCreateResp = await adminClient.PostAsJsonAsync("/api/v1/repos",
            new CreateRepoRequest("stealth-vault", "Private vault", IsPrivate: true, "main", true));
        Assert.Equal(HttpStatusCode.Created, privCreateResp.StatusCode);

        // 2. Both anonymous and non-collaborator receive 404 NotFound (prevents private repo existence enumeration)
        var anonGetPriv = await anonClient.GetAsync("/api/v1/repos/gitea-admin/stealth-vault");
        var devGetPriv = await devClient.GetAsync("/api/v1/repos/gitea-admin/stealth-vault");
        Assert.Equal(HttpStatusCode.NotFound, anonGetPriv.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, devGetPriv.StatusCode);

        // 3. Cookie-authenticated mutating request WITHOUT X-Gitea-Csrf header is rejected with 403 Forbidden
        using var cookieClient = host.Factory.CreateClient();
        var loginResp = await cookieClient.PostAsJsonAsync("/api/v1/auth/login", new LoginRequest("gitea-admin", "Admin@12345"));
        Assert.Equal(HttpStatusCode.OK, loginResp.StatusCode);
        string setCookie = loginResp.Headers.GetValues("Set-Cookie").First();
        string cookiePair = setCookie.Split(';')[0];

        var csrfMissingReq = new HttpRequestMessage(HttpMethod.Post, "/api/v1/repos")
        {
            Content = JsonContent.Create(new CreateRepoRequest("csrf-test", "desc", false, "main", true))
        };
        csrfMissingReq.Headers.Add("Cookie", cookiePair);
        var csrfMissingResp = await cookieClient.SendAsync(csrfMissingReq);
        Assert.Equal(HttpStatusCode.Forbidden, csrfMissingResp.StatusCode);

        // With X-Gitea-Csrf: 1 header, cookie-authenticated mutation succeeds
        var csrfValidReq = new HttpRequestMessage(HttpMethod.Post, "/api/v1/repos")
        {
            Content = JsonContent.Create(new CreateRepoRequest("csrf-ok-repo", "desc", false, "main", true))
        };
        csrfValidReq.Headers.Add("Cookie", cookiePair);
        csrfValidReq.Headers.Add("X-Gitea-Csrf", "1");
        var csrfValidResp = await cookieClient.SendAsync(csrfValidReq);
        Assert.Equal(HttpStatusCode.Created, csrfValidResp.StatusCode);
    }

    // ==================== 3. P0-PAT SCOPES & REVOCATION REGRESSION TEST ====================

    [Fact]
    public async Task Regression_P0_PatScopes_EnforceReadOnlyAndSupportRevocation()
    {
        using var host = new IsolatedGiteaHost();
        using var adminClient = host.CreateBasicClient("gitea-admin", "Admin@12345");

        // Create a read-only PAT (scope: "repo:read")
        var createPatResp = await adminClient.PostAsJsonAsync("/api/v1/auth/tokens",
            new CreatePatRequest("ReadOnly-CI-Token", "repo:read", 7));
        Assert.Equal(HttpStatusCode.Created, createPatResp.StatusCode);
        var createdPat = await createPatResp.Content.ReadFromJsonAsync<CreatedPatResponse>();
        Assert.NotNull(createdPat);

        using var patClient = host.CreateBasicClient("gitea-admin", createdPat!.RawToken);

        // Read-only PAT CAN read repositories (200 OK)
        var getRepoResp = await patClient.GetAsync("/api/v1/repos/gitea-admin/gitea-dotnet-core");
        Assert.Equal(HttpStatusCode.OK, getRepoResp.StatusCode);

        // Read-only PAT CANNOT create a repository (`repo:write` required -> 403 Forbidden)
        var createRepoResp = await patClient.PostAsJsonAsync("/api/v1/repos",
            new CreateRepoRequest("unauthorized-pat-repo", "desc", false, "main", true));
        Assert.Equal(HttpStatusCode.Forbidden, createRepoResp.StatusCode);

        // Read-only PAT CANNOT mint new PATs (`admin` scope required -> 403 Forbidden)
        var mintPatResp = await patClient.PostAsJsonAsync("/api/v1/auth/tokens",
            new CreatePatRequest("Escalated-Token", "admin", 7));
        Assert.Equal(HttpStatusCode.Forbidden, mintPatResp.StatusCode);

        // Revoke PAT via DELETE /api/v1/auth/tokens/{id}
        var delResp = await adminClient.DeleteAsync($"/api/v1/auth/tokens/{createdPat.Id}");
        Assert.Equal(HttpStatusCode.OK, delResp.StatusCode);

        // Subsequent authenticated calls with revoked PAT fail with 401/404
        var afterRevokeResp = await patClient.GetAsync("/api/v1/auth/me");
        Assert.Equal(HttpStatusCode.Unauthorized, afterRevokeResp.StatusCode);
    }

    // ==================== 4. P0-MERGE RECONCILER & 3-PHASE CONSISTENCY REGRESSION TEST ====================

    [Fact]
    public async Task Regression_P0_MergeReconciler_RecoversStuckMergingPullRequests()
    {
        using var host = new IsolatedGiteaHost();
        var admin = (await host.Db.GetUserByUsernameAsync("gitea-admin"))!;
        var repo = (await host.Db.GetRepositoryByOwnerAndNameAsync("gitea-admin", "gitea-dotnet-core"))!;

        // Create branch, commit, and open PR
        await host.Git.CreateBranchAsync("gitea-admin", "gitea-dotnet-core", "feature/stuck-pr", "main");
        await host.Git.CommitFileAsync("gitea-admin", "gitea-dotnet-core", "feature/stuck-pr", "src/Stuck.cs", "public class Stuck {}", "Add Stuck.cs", admin.FullName, admin.Email);
        var pr = await host.Db.CreatePullRequestAsync(repo.Id, admin.Id, "Stuck PR Test", "Body", "feature/stuck-pr", "main");

        // Simulate crash after Phase 1 ('merging') and Phase 2 (Git merge completed), before Phase 3 (DB finalize)
        bool locked = await host.Db.TryAcquirePullRequestMergeLockAsync(repo.Id, pr.PullIndex, pr.RowVersion);
        Assert.True(locked);

        var (gitOk, sha, _) = await host.Git.MergeBranchesAsync(
            "gitea-admin", "gitea-dotnet-core", "feature/stuck-pr", "main", "merge", "Simulated crash merge", admin.FullName, admin.Email);
        Assert.True(gitOk);
        Assert.NotNull(sha);

        // Run Reconciler -> must detect `feature/stuck-pr` is an ancestor of `main` and finalize PR to 'merged'
        await host.Reconciler.ReconcileStuckPullRequestsAsync(TimeSpan.Zero);

        var reconciledPr = await host.Db.GetPullRequestAsync(repo.Id, pr.PullIndex);
        Assert.NotNull(reconciledPr);
        Assert.True(reconciledPr!.IsMerged);
        Assert.Equal("merged", reconciledPr.State);
    }

    // ==================== 5. P0-ZERO N+1 SQL QUERY COUNTER ENDPOINT & REPOSITORY TESTS ====================

    [Fact]
    public async Task Regression_P0_ZeroNPlusOne_EndpointsExecuteConstantQueriesRegardlessOfRowCount()
    {
        using var host = new IsolatedGiteaHost();
        var admin = (await host.Db.GetUserByUsernameAsync("gitea-admin"))!;

        for (int i = 1; i <= 15; i++)
        {
            var r = await host.Db.CreateRepositoryAsync(admin.Id, $"scale-repo-{i}", "Scale test", false, "main");
            await host.Db.CreateIssueAsync(r.Id, admin.Id, $"Scale Issue {i}", "Body", "perf");
        }

        using (SqlQueryCounter.BeginScope())
        {
            var repos = await host.Db.ListRepositoriesAsync(null, admin.Id, true, 1, 50);
            Assert.True(repos.Items.Count >= 15);
            Assert.Equal(2, SqlQueryCounter.Count); // 1 COUNT(*) + 1 SELECT
        }

        var firstRepo = (await host.Db.GetRepositoryByOwnerAndNameAsync("gitea-admin", "gitea-dotnet-core"))!;
        using (SqlQueryCounter.BeginScope())
        {
            var issues = await host.Db.ListIssuesAsync(firstRepo.Id, null, 1, 50);
            Assert.NotEmpty(issues.Items);
            Assert.Equal(2, SqlQueryCounter.Count); // 1 COUNT(*) + 1 SELECT
        }
    }

    // ==================== 6. P0-HIGH CONCURRENCY RACE CONDITION TESTS (60 ISSUE/PR SEQ & 50 PR MERGE) ====================

    [Fact]
    public async Task Regression_P0_RaceCondition_60ConcurrentIssuesAndPrs_And_50ConcurrentHttpMerges()
    {
        using var host = new IsolatedGiteaHost();
        var admin = (await host.Db.GetUserByUsernameAsync("gitea-admin"))!;
        var repo = await host.Db.CreateRepositoryAsync(admin.Id, "race-suite-repo", "Race suite", false, "main");
        await host.Git.InitializeBareRepositoryAsync("gitea-admin", "race-suite-repo", "main", true, admin.FullName, admin.Email, "Race suite");

        // Part A: 60 concurrent Issue & PR creations sharing repo_counters.last_issue_index
        const int seqThreads = 60;
        var indices = new ConcurrentBag<long>();
        await Task.WhenAll(Enumerable.Range(1, seqThreads).Select(async i =>
        {
            if (i % 2 == 0)
            {
                var iss = await host.Db.CreateIssueAsync(repo.Id, admin.Id, $"Issue {i}", "Body", "race");
                indices.Add(iss.IssueIndex);
            }
            else
            {
                var pr = await host.Db.CreatePullRequestAsync(repo.Id, admin.Id, $"PR {i}", "Body", "main", "main");
                indices.Add(pr.PullIndex);
            }
        }));

        Assert.Equal(Enumerable.Range(1, seqThreads).Select(x => (long)x).ToList(), indices.OrderBy(x => x).ToList());

        // Part B: 50 concurrent HTTP merge requests on a fresh PR using PAT auth (fast SHA-256 lookup)
        using var setupClient = host.CreateBasicClient("gitea-admin", "Admin@12345");
        var patResp = await setupClient.PostAsJsonAsync("/api/v1/auth/tokens", new CreatePatRequest("MergeTestPat", "repo:write,pull:write", 1));
        var pat = (await patResp.Content.ReadFromJsonAsync<CreatedPatResponse>())!;

        await host.Git.CreateBranchAsync("gitea-admin", "race-suite-repo", "feature/concurrent-merge", "main");
        await host.Git.CommitFileAsync("gitea-admin", "race-suite-repo", "feature/concurrent-merge", "Concurrent.cs", "class C {}", "Add C", admin.FullName, admin.Email);
        var mergePr = await host.Db.CreatePullRequestAsync(repo.Id, admin.Id, "50-Thread Merge Target", "Body", "feature/concurrent-merge", "main");

        const int mergeThreads = 50;
        int winners = 0;
        int conflicts = 0;

        await Task.WhenAll(Enumerable.Range(0, mergeThreads).Select(async _ =>
        {
            using var client = host.CreateBasicClient("gitea-admin", pat.RawToken);
            var resp = await client.PostAsJsonAsync(
                $"/api/v1/repos/gitea-admin/race-suite-repo/pulls/{mergePr.PullIndex}/merge",
                new MergePullRequest("merge", "50-thread atomic merge", mergePr.RowVersion));

            if (resp.StatusCode == HttpStatusCode.OK) Interlocked.Increment(ref winners);
            else if (resp.StatusCode == HttpStatusCode.Conflict) Interlocked.Increment(ref conflicts);
        }));

        Assert.Equal(1, winners);
        Assert.Equal(mergeThreads - 1, conflicts);
    }

    // ==================== 7. P0-SCHEMA V1->V2 UPGRADE & LEGACY PASSWORD HASH REHASH TEST ====================

    [Fact]
    public async Task Regression_P0_SchemaV1Upgrade_And_Legacy100kPasswordRehash_Succeeds()
    {
        string tempDbDir = Path.Combine(Path.GetTempPath(), $"gitea_v1_upgrade_{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempDbDir);
        string dbFile = Path.Combine(tempDbDir, "legacy_v1.db");

        try
        {
            // 1. Create a v1 database lacking `must_change_password`, `open_issues_count`, `open_prs_count`, `comments_count`
            await using (var conn = new SqliteConnection($"Data Source={dbFile};Mode=ReadWriteCreate;"))
            {
                await conn.OpenAsync();
                await using var cmd = conn.CreateCommand();
                cmd.CommandText = """
                    CREATE TABLE users (
                        id INTEGER PRIMARY KEY AUTOINCREMENT,
                        username VARCHAR(64) NOT NULL UNIQUE,
                        email VARCHAR(160) NOT NULL UNIQUE,
                        full_name VARCHAR(120) NOT NULL,
                        password_hash TEXT NOT NULL,
                        password_salt TEXT NOT NULL,
                        is_admin INTEGER NOT NULL DEFAULT 0,
                        avatar_color VARCHAR(16) NOT NULL DEFAULT '#2563eb',
                        created_at TEXT NOT NULL,
                        row_version BIGINT NOT NULL DEFAULT 1
                    );
                    """;
                await cmd.ExecuteNonQueryAsync();
            }

            // 2. Boot IsolatedGiteaHost pointing at the v1 DB -> InitializeSchemaAsync must upgrade it to v2 cleanly
            using var host = new IsolatedGiteaHost();
            byte[] salt = [1, 2, 3, 4, 5, 6, 7, 8, 9, 10, 11, 12, 13, 14, 15, 16];
            byte[] legacyHashBytes = System.Security.Cryptography.Rfc2898DeriveBytes.Pbkdf2(
                Encoding.UTF8.GetBytes("LegacyPass@123"),
                salt,
                SecurityService.LegacyPbkdf2Iterations,
                System.Security.Cryptography.HashAlgorithmName.SHA256,
                32);

            string legacyUnprefixedHash = Convert.ToBase64String(legacyHashBytes);
            string saltB64 = Convert.ToBase64String(salt);

            // Verify legacy 100k hash is accepted and flagged for upgrade
            bool ok = host.Security.VerifyPassword("LegacyPass@123", legacyUnprefixedHash, saltB64, out bool needsUpgrade);
            Assert.True(ok);
            Assert.True(needsUpgrade);
        }
        finally
        {
            try { Directory.Delete(tempDbDir, recursive: true); } catch { }
        }
    }

    // ==================== 8. P1-SSRF IPv4/IPv6/ULA/METADATA & PATH VALIDATION TESTS ====================

    [Fact]
    public void Regression_P1_SsrfAndPathValidators_BlockAllReservedAddressesAndDotGit()
    {
        // SSRF checks including IPv4 loopback, RFC1918, Cloud Metadata, IPv6 loopback, IPv6 ULA (fc00::/7), and IPv4-mapped IPv6
        Assert.False(WebhookDeliveryService.IsSafeExternalUrl("http://127.0.0.1:8080/hook", out _));
        Assert.False(WebhookDeliveryService.IsSafeExternalUrl("http://169.254.169.254/latest/meta-data/", out _));
        Assert.False(WebhookDeliveryService.IsSafeExternalUrl("http://192.168.1.10/internal", out _));
        Assert.False(WebhookDeliveryService.IsSafeExternalUrl("http://[::1]:5000/hook", out _));
        Assert.False(WebhookDeliveryService.IsSafeExternalUrl("http://[fd00::1]/hook", out _));
        Assert.False(WebhookDeliveryService.IsSafeExternalUrl("http://[::ffff:127.0.0.1]/hook", out _));
        Assert.True(WebhookDeliveryService.IsSafeExternalUrl("https://hooks.example.com/gitea", out _));

        // Segment-wise path checks
        Assert.True(SecurityService.IsSafeRelativeFilePath(".gitignore"));
        Assert.True(SecurityService.IsSafeRelativeFilePath(".github/workflows/ci.yml"));
        Assert.True(SecurityService.IsSafeRelativeFilePath("docs/a..b.md"));
        Assert.False(SecurityService.IsSafeRelativeFilePath(".git/config"));
        Assert.False(SecurityService.IsSafeRelativeFilePath("../etc/passwd"));
        Assert.False(SecurityService.IsSafeIdentifier("repo.git"));
        Assert.False(SecurityService.IsSafeIdentifier("CON"));
        Assert.False(SecurityService.IsSafeIdentifier("admin\n"));
    }

    // ==================== 9. P0-CI/CD ACTIONS PIPELINE ZERO N+1 & 50-THREAD CONCURRENT CAS CLAIM RACE TEST ====================

    [Fact]
    public async Task Regression_P0_CiCdPipeline_ZeroNPlusOne_And_50ConcurrentRunCreationAndCasClaimRace()
    {
        using var host = new IsolatedGiteaHost();
        var admin = (await host.Db.GetUserByUsernameAsync("gitea-admin"))!;
        var repo = await host.Db.CreateRepositoryAsync(admin.Id, "ci-cd-race-repo", "CI/CD Race & N+1 Test Repo", false, "main");
        await host.Git.InitializeBareRepositoryAsync("gitea-admin", "ci-cd-race-repo", "main", true, admin.FullName, admin.Email, "CI/CD Race");

        // 1. 50 concurrent CI workflow run creations sharing repo_counters.last_ci_run_index
        const int runThreads = 50;
        var createdRuns = new ConcurrentBag<CiWorkflowRunEntity>();
        await Task.WhenAll(Enumerable.Range(1, runThreads).Select(async i =>
        {
            var run = await host.Db.CreateCiWorkflowRunAsync(
                repo.Id,
                "Per's Git Production CI/CD Pipeline",
                "push",
                "main",
                $"0123456789abcdef0123456789abcdef0000{i:D4}",
                admin.Id);
            createdRuns.Add(run);
        }));

        var sortedIndices = createdRuns.Select(r => r.RunIndex).OrderBy(x => x).ToList();
        Assert.Equal(Enumerable.Range(1, runThreads).Select(x => (long)x).ToList(), sortedIndices);

        // 2. 50 concurrent worker threads racing to claim the exact same queued CI run via CAS (`WHERE status = 'queued'`)
        var targetRun = createdRuns.First(r => r.RunIndex == 1);
        int claimWinners = 0;
        int claimLosers = 0;
        await Task.WhenAll(Enumerable.Range(0, 50).Select(async _ =>
        {
            bool claimed = await host.Db.TryClaimQueuedCiRunAsync(repo.Id, targetRun.Id);
            if (claimed) Interlocked.Increment(ref claimWinners);
            else Interlocked.Increment(ref claimLosers);
        }));

        Assert.Equal(1, claimWinners);
        Assert.Equal(49, claimLosers);

        // 3. Zero N+1 verification: listing all 50 CI workflow runs with trigger user metadata executes in 2 SQL queries (1 COUNT + 1 SELECT JOIN)
        using (SqlQueryCounter.BeginScope())
        {
            var page = await host.Db.ListCiWorkflowRunsAsync(repo.Id, 1, 50);
            Assert.Equal(50, page.Items.Count);
            Assert.All(page.Items, item => Assert.Equal("gitea-admin", item.TriggeredByUsername));
            Assert.Equal(2, SqlQueryCounter.Count);
        }

        // 4. Execute a full end-to-end CI/CD pipeline run on run #2 and verify completion
        var secondRun = createdRuns.First(r => r.RunIndex == 2);
        await host.CiRunner.ExecutePipelineNowAsync(repo.Id, secondRun.Id);
        var completed = await host.Db.GetCiWorkflowRunAsync(repo.Id, 2);
        Assert.NotNull(completed);
        Assert.Equal("success", completed!.Status);
        Assert.Contains("Pipeline finished with status: SUCCESS", completed.LogsText);
    }
}
