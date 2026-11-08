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

public sealed class DeepVerificationTests
{
    // =========================================================================
    // 1. SQLITE INJECTION RESILIENCE & PARAMETERIZED COMMAND RIGOR
    // =========================================================================

    public static readonly string[] SqlInjectionPayloads =
    [
        "' OR '1'='1",
        "' OR 1=1 --",
        "admin'--",
        "'; DROP TABLE users; --",
        "'; DROP TABLE repositories; --",
        "' UNION SELECT id, username, password_hash, password_salt, email, full_name, 1, 0, '#000', created_at, 1 FROM users --",
        "' AND 1=0 UNION ALL SELECT 999, 'injected', 'inj@test.local', 'Injected', 'hash', 'salt', 1, 0, '#fff', '2026-01-01', 1 --",
        "/* comment */ ' OR 1=1 /*",
        "1; ATTACH DATABASE ':memory:' AS injected;",
        "admin' /*!50000OR*/ 1=1 --",
        "\\\\' OR 1=1 --",
        "\\\" OR 1=1 --",
        "' OR EXISTS(SELECT * FROM users WHERE is_admin = 1) --",
        "random' OR (SELECT COUNT(*) FROM users) > 0 --",
        "test%27%20OR%201=1--"
    ];

    [Fact]
    public async Task SqlInjection_GetUserByUsernameAsync_ResistsAllAttackPayloadsWithoutSyntaxErrorsOrBypass()
    {
        using var host = new IsolatedGiteaHost();

        foreach (string payload in SqlInjectionPayloads)
        {
            var user = await host.Db.GetUserByUsernameAsync(payload);
            Assert.Null(user); // Must safely return null without throwing SQLite syntax exceptions
        }

        // Verify users table was NOT dropped or damaged by stacked query attempts
        var admin = await host.Db.GetUserByUsernameAsync("gitea-admin");
        Assert.NotNull(admin);
        Assert.Equal("gitea-admin", admin!.Username);
    }

    [Fact]
    public async Task SqlInjection_ListRepositoriesSearch_SafelyEscapesWildcardsAndResistsInjections()
    {
        using var host = new IsolatedGiteaHost();
        var admin = (await host.Db.GetUserByUsernameAsync("gitea-admin"))!;

        // Create one private and one public repo
        await host.Db.CreateRepositoryAsync(admin.Id, "pub-project", "Public project", isPrivate: false, "main");
        await host.Db.CreateRepositoryAsync(admin.Id, "priv-secrets", "Private secret vault", isPrivate: true, "main");

        foreach (string payload in SqlInjectionPayloads)
        {
            // Anonymous search with injection payload: must NOT bypass authorization or expose private repos
            var result = await host.Db.ListRepositoriesAsync(payload, viewerUserId: null, viewerIsAdmin: false, page: 1, pageSize: 50);
            Assert.Empty(result.Items);
        }

        // Test wildcard matching escape: literal '%' and '_' should not match everything
        var wildcardResult = await host.Db.ListRepositoriesAsync("%", viewerUserId: null, viewerIsAdmin: false, page: 1, pageSize: 50);
        Assert.Empty(wildcardResult.Items); // Escaped to literal \% so no matches found
    }

    [Fact]
    public async Task SqlInjection_HttpApiLoginEndpoint_RejectsInjectionWithoutAuthBypass()
    {
        using var host = new IsolatedGiteaHost();
        using var client = host.Factory.CreateClient();
        client.DefaultRequestHeaders.Add("X-Gitea-Csrf", "1");

        foreach (string payload in SqlInjectionPayloads)
        {
            var loginReq = new LoginRequest(payload, payload);
            var resp = await client.PostAsJsonAsync("/api/v1/auth/login", loginReq);
            Assert.Equal(HttpStatusCode.Unauthorized, resp.StatusCode);
        }

        // Legitimate login continues to work flawlessly
        var validReq = new LoginRequest("gitea-admin", "Admin@12345");
        var validResp = await client.PostAsJsonAsync("/api/v1/auth/login", validReq);
        Assert.Equal(HttpStatusCode.OK, validResp.StatusCode);
    }

    // =========================================================================
    // 2. DETERMINISTIC ZERO N+1 DATABASE PERFORMANCE AUDIT (O(1) QUERIES)
    // =========================================================================

    [Fact]
    public async Task ZeroNPlusOne_ListRepositories_ExecutesStrictConstantQueriesRegardlessOfRowCount()
    {
        using var host = new IsolatedGiteaHost();
        var admin = (await host.Db.GetUserByUsernameAsync("gitea-admin"))!;

        // Baseline: 1 repository
        int countFor1;
        using (SqlQueryCounter.BeginScope())
        {
            var r1 = await host.Db.ListRepositoriesAsync(null, admin.Id, true, 1, 50);
            Assert.Single(r1.Items);
            countFor1 = SqlQueryCounter.Count;
        }

        // Seed 25 more repositories (total 26)
        for (int i = 2; i <= 26; i++)
        {
            await host.Db.CreateRepositoryAsync(admin.Id, $"n1-repo-{i}", $"Repo {i}", false, "main");
        }

        // Query with 26 repositories
        int countFor26;
        using (SqlQueryCounter.BeginScope())
        {
            var r26 = await host.Db.ListRepositoriesAsync(null, admin.Id, true, 1, 50);
            Assert.Equal(26, r26.Items.Count);
            countFor26 = SqlQueryCounter.Count;
        }

        // Strict O(1) assertion: 1 repo and 26 repos execute the EXACT same number of database commands!
        Assert.Equal(countFor1, countFor26);
        Assert.Equal(2, countFor26); // Exactly 1 COUNT(*) query + 1 SELECT query with maintained counter columns
    }

    [Fact]
    public async Task ZeroNPlusOne_ListIssuesAndComments_ExecutesStrictConstantQueriesRegardlessOfRowCount()
    {
        using var host = new IsolatedGiteaHost();
        var admin = (await host.Db.GetUserByUsernameAsync("gitea-admin"))!;
        var repo = (await host.Db.GetRepositoryByOwnerAndNameAsync("gitea-admin", "gitea-dotnet-core"))!;

        // Seed 30 issues on the repo
        for (int i = 1; i <= 30; i++)
        {
            var issue = await host.Db.CreateIssueAsync(repo.Id, admin.Id, $"Bench Issue #{i}", $"Body #{i}", "test");
            for (int c = 1; c <= 3; c++)
            {
                await host.Db.AddIssueCommentAsync(repo.Id, issue.Id, admin.Id, $"Comment {c} on issue {i}");
            }
        }

        // Query issues list
        using (SqlQueryCounter.BeginScope())
        {
            var issues = await host.Db.ListIssuesAsync(repo.Id, null, 1, 50);
            Assert.True(issues.Items.Count >= 30);
            // Must be exactly 2 queries (1 COUNT + 1 SELECT), zero per-issue queries for comment counts or author details
            Assert.Equal(2, SqlQueryCounter.Count);
        }

        // Query issue detail bundle
        using (SqlQueryCounter.BeginScope())
        {
            var bundle = await host.Db.GetIssueDetailBundleAsync(repo.Id, 1);
            Assert.NotNull(bundle);
            Assert.NotEmpty(bundle!.Comments);
            // Exactly 2 queries: 1 SELECT for Issue + 1 SELECT for Comments
            Assert.Equal(2, SqlQueryCounter.Count);
        }
    }

    // =========================================================================
    // 3. RACE CONDITIONS & CONCURRENCY INTEGRITY TESTS
    // =========================================================================

    [Fact]
    public async Task Concurrency_AtomicSequenceNumberGeneration_ZeroDuplicatesZeroGapsUnderParallelLoad()
    {
        using var host = new IsolatedGiteaHost();
        var admin = (await host.Db.GetUserByUsernameAsync("gitea-admin"))!;
        var repo = await host.Db.CreateRepositoryAsync(admin.Id, "seq-stress-repo", "Sequence stress", false, "main");

        const int totalTasks = 50;
        var generatedIndices = new ConcurrentBag<long>();

        // 50 threads concurrently racing to insert issues and PRs into repo_counters.last_issue_index
        await Task.WhenAll(Enumerable.Range(1, totalTasks).Select(async i =>
        {
            if (i % 2 == 0)
            {
                var iss = await host.Db.CreateIssueAsync(repo.Id, admin.Id, $"Concurrent Issue {i}", "Body", "concurrency");
                generatedIndices.Add(iss.IssueIndex);
            }
            else
            {
                var pr = await host.Db.CreatePullRequestAsync(repo.Id, admin.Id, $"Concurrent PR {i}", "Body", "main", "main");
                generatedIndices.Add(pr.PullIndex);
            }
        }));

        var sorted = generatedIndices.OrderBy(x => x).ToList();
        var expected = Enumerable.Range(1, totalTasks).Select(x => (long)x).ToList();

        // Must strictly match 1..50 with ZERO collisions and ZERO skipped sequence numbers
        Assert.Equal(expected, sorted);
    }

    [Fact]
    public async Task Concurrency_PullRequestMergeLock_StrictSingleWinnerUnderContention()
    {
        using var host = new IsolatedGiteaHost();
        var admin = (await host.Db.GetUserByUsernameAsync("gitea-admin"))!;
        var repo = (await host.Db.GetRepositoryByOwnerAndNameAsync("gitea-admin", "gitea-dotnet-core"))!;
        var pr = await host.Db.CreatePullRequestAsync(repo.Id, admin.Id, "Lock Contention PR", "Body", "main", "main");

        const int competitors = 25;
        int acquiredCount = 0;
        int failedCount = 0;

        await Task.WhenAll(Enumerable.Range(0, competitors).Select(async _ =>
        {
            bool acquired = await host.Db.TryAcquirePullRequestMergeLockAsync(repo.Id, pr.PullIndex, pr.RowVersion);
            if (acquired) Interlocked.Increment(ref acquiredCount);
            else Interlocked.Increment(ref failedCount);
        }));

        Assert.Equal(1, acquiredCount); // Exactly ONE thread wins the atomic CAS lock
        Assert.Equal(competitors - 1, failedCount); // All other threads cleanly fail without race or corruption
    }

    // =========================================================================
    // 4. GIT MERGE CONFLICT DETECTION & CLEAN STATE RECOVERY
    // =========================================================================

    [Fact]
    public async Task MergeConflict_DivergentLineEdits_Returns422ConflictAndRecoversPointersCleanly()
    {
        using var host = new IsolatedGiteaHost();
        var admin = (await host.Db.GetUserByUsernameAsync("gitea-admin"))!;
        using var client = host.CreateBasicClient("gitea-admin", "Admin@12345");
        client.DefaultRequestHeaders.Add("X-Gitea-Csrf", "1");

        // 1. Create a repository with a shared base file containing 3 lines
        var repo = await host.Db.CreateRepositoryAsync(admin.Id, "conflict-lab", "Merge conflict laboratory", false, "main");
        await host.Git.InitializeBareRepositoryAsync(admin.Username, repo.Name, "main", false, admin.FullName, admin.Email, "Initial commit");

        const string baseContent = "ALPHA\nORIGINAL LINE 2\nOMEGA\n";
        await host.Git.CommitFileAsync(admin.Username, repo.Name, "main", "config.txt", baseContent, "Add config.txt", admin.FullName, admin.Email);

        // 2. Branch A: updates line 2 to 'BRANCH A MODIFICATION'
        await host.Git.CreateBranchAsync(admin.Username, repo.Name, "feature/branch-a", "main");
        const string branchAContent = "ALPHA\nBRANCH A MODIFICATION\nOMEGA\n";
        await host.Git.CommitFileAsync(admin.Username, repo.Name, "feature/branch-a", "config.txt", branchAContent, "Update config from Branch A", admin.FullName, admin.Email);

        // 3. Branch B: updates line 2 to 'BRANCH B MODIFICATION' (conflicts with Branch A!)
        await host.Git.CreateBranchAsync(admin.Username, repo.Name, "feature/branch-b", "main");
        const string branchBContent = "ALPHA\nBRANCH B MODIFICATION\nOMEGA\n";
        await host.Git.CommitFileAsync(admin.Username, repo.Name, "feature/branch-b", "config.txt", branchBContent, "Update config from Branch B", admin.FullName, admin.Email);

        // 4. Create and merge PR #1 (Branch A -> main): must succeed
        var pr1 = await host.Db.CreatePullRequestAsync(repo.Id, admin.Id, "PR 1 from Branch A", "Body", "feature/branch-a", "main");
        var mergePr1Resp = await client.PostAsJsonAsync(
            $"/api/v1/repos/{admin.Username}/{repo.Name}/pulls/{pr1.PullIndex}/merge",
            new MergePullRequest("merge", "Merge Branch A into main", pr1.RowVersion));
        Assert.Equal(HttpStatusCode.OK, mergePr1Resp.StatusCode);

        // 5. Create PR #2 (Branch B -> main): this NOW conflicts with main!
        var pr2 = await host.Db.CreatePullRequestAsync(repo.Id, admin.Id, "PR 2 from Branch B", "Body", "feature/branch-b", "main");

        // Attempt merge of PR #2 -> GitEngineService runs 'git merge-tree --write-tree' and detects conflict
        var mergePr2Resp = await client.PostAsJsonAsync(
            $"/api/v1/repos/{admin.Username}/{repo.Name}/pulls/{pr2.PullIndex}/merge",
            new MergePullRequest("merge", "Merge Branch B into main", pr2.RowVersion));

        // Must reject merge with 422 Unprocessable Entity
        Assert.Equal(HttpStatusCode.UnprocessableEntity, mergePr2Resp.StatusCode);
        var errBody = await mergePr2Resp.Content.ReadAsStringAsync();
        Assert.Contains("Merge conflict detected", errBody);

        // 6. Verify PR #2 was NOT corrupted or left stuck in 'merging': state must be rolled back to 'open'
        var pr2After = await host.Db.GetPullRequestAsync(repo.Id, pr2.PullIndex);
        Assert.NotNull(pr2After);
        Assert.Equal("open", pr2After!.State);
        Assert.False(pr2After.IsMerged);

        // 7. Verify main branch was NOT corrupted: still contains Branch A's content
        var blob = await host.Git.GetBlobContentAsync(admin.Username, repo.Name, "main", "config.txt");
        Assert.NotNull(blob);
        Assert.Equal(branchAContent, blob.Content);

        // 8. Verify a new non-conflicting branch CAN still merge cleanly into main
        await host.Git.CreateBranchAsync(admin.Username, repo.Name, "feature/branch-c", "main");
        await host.Git.CommitFileAsync(admin.Username, repo.Name, "feature/branch-c", "readme.txt", "Independent file", "Add readme", admin.FullName, admin.Email);
        var pr3 = await host.Db.CreatePullRequestAsync(repo.Id, admin.Id, "PR 3 Non-Conflicting", "Body", "feature/branch-c", "main");

        var mergePr3Resp = await client.PostAsJsonAsync(
            $"/api/v1/repos/{admin.Username}/{repo.Name}/pulls/{pr3.PullIndex}/merge",
            new MergePullRequest("merge", "Merge Branch C into main", pr3.RowVersion));
        Assert.Equal(HttpStatusCode.OK, mergePr3Resp.StatusCode);
    }

    // =========================================================================
    // 5. STANDARD CORE FEATURES END-TO-END VERIFICATION
    // =========================================================================

    [Fact]
    public async Task StandardFeatures_FullEndToEndLifecycle_PassesAllAcceptanceCriteria()
    {
        using var host = new IsolatedGiteaHost();
        using var client = host.Factory.CreateClient();
        client.DefaultRequestHeaders.Add("X-Gitea-Csrf", "1");

        // A. User Registration & Login
        var regResp = await client.PostAsJsonAsync("/api/v1/auth/register",
            new RegisterRequest("qa-developer", "qa@company.local", "QA Senior Engineer", "TestingPass@123"));
        Assert.Equal(HttpStatusCode.Created, regResp.StatusCode);

        var loginResp = await client.PostAsJsonAsync("/api/v1/auth/login",
            new LoginRequest("qa-developer", "TestingPass@123"));
        Assert.Equal(HttpStatusCode.OK, loginResp.StatusCode);
        var authData = await loginResp.Content.ReadFromJsonAsync<AuthResponse>();
        Assert.NotNull(authData);
        Assert.False(string.IsNullOrEmpty(authData!.Token));

        using var authClient = host.CreateBasicClient("qa-developer", "TestingPass@123");
        authClient.DefaultRequestHeaders.Add("X-Gitea-Csrf", "1");

        // B. Repository Creation & Collaborators
        var createRepoResp = await authClient.PostAsJsonAsync("/api/v1/repos",
            new CreateRepoRequest("microservice-app", "Core microservice", IsPrivate: false, "main", InitializeReadme: true));
        Assert.Equal(HttpStatusCode.Created, createRepoResp.StatusCode);

        // Add admin as collaborator with 'write' access
        var addCollabResp = await authClient.PostAsJsonAsync(
            "/api/v1/repos/qa-developer/microservice-app/collaborators",
            new AddCollaboratorRequest("gitea-admin", "write"));
        Assert.Equal(HttpStatusCode.OK, addCollabResp.StatusCode);

        // C. Branch, Commit, Blob & Unified Diff Inspection
        await host.Git.CreateBranchAsync("qa-developer", "microservice-app", "feature/api-gateway", "main");
        await host.Git.CommitFileAsync("qa-developer", "microservice-app", "feature/api-gateway", "Gateway.cs", "public class Gateway {}", "Add Gateway", "QA Dev", "qa@company.local");

        var blob = await host.Git.GetBlobContentAsync("qa-developer", "microservice-app", "feature/api-gateway", "Gateway.cs");
        Assert.NotNull(blob);
        Assert.Equal("public class Gateway {}", blob.Content);

        var diff = await host.Git.CompareRefsAsync("qa-developer", "microservice-app", "main", "feature/api-gateway");
        Assert.Equal(1, diff.FilesChanged);
        Assert.Contains("+public class Gateway {}", diff.UnifiedDiff);

        // D. Issue Lifecycle: Open, Comment, Close, Reopen
        var issueResp = await authClient.PostAsJsonAsync(
            "/api/v1/repos/qa-developer/microservice-app/issues",
            new CreateIssueRequest("Gateway routing timeout", "Investigate 504 timeouts", "bug,gateway"));
        Assert.Equal(HttpStatusCode.Created, issueResp.StatusCode);
        var issue = await issueResp.Content.ReadFromJsonAsync<IssueEntity>();
        Assert.NotNull(issue);

        var commentResp = await authClient.PostAsJsonAsync(
            $"/api/v1/repos/qa-developer/microservice-app/issues/{issue!.IssueIndex}/comments",
            new CreateCommentRequest("Investigating gateway proxy timeouts."));
        Assert.Equal(HttpStatusCode.Created, commentResp.StatusCode);

        var qaRepo = (await host.Db.GetRepositoryByOwnerAndNameAsync("qa-developer", "microservice-app"))!;
        var bundleBeforeClose = (await host.Db.GetIssueDetailBundleAsync(qaRepo.Id, issue.IssueIndex))!;

        // Close issue using fresh row version
        var closeResp = await authClient.PatchAsJsonAsync(
            $"/api/v1/repos/qa-developer/microservice-app/issues/{issue.IssueIndex}/state",
            new UpdateIssueStateRequest("closed", bundleBeforeClose.Issue.RowVersion));
        Assert.Equal(HttpStatusCode.OK, closeResp.StatusCode);

        // Reopen issue using updated row version
        var bundleBeforeReopen = (await host.Db.GetIssueDetailBundleAsync(qaRepo.Id, issue.IssueIndex))!;
        var reopenResp = await authClient.PatchAsJsonAsync(
            $"/api/v1/repos/qa-developer/microservice-app/issues/{issue.IssueIndex}/state",
            new UpdateIssueStateRequest("open", bundleBeforeReopen.Issue.RowVersion));
        Assert.Equal(HttpStatusCode.OK, reopenResp.StatusCode);

        // E. PR Lifecycle: Open PR and Merge
        var prResp = await authClient.PostAsJsonAsync(
            "/api/v1/repos/qa-developer/microservice-app/pulls",
            new CreatePullRequest("feat: API Gateway", "Introduces microservice gateway", "feature/api-gateway", "main"));
        Assert.Equal(HttpStatusCode.Created, prResp.StatusCode);
        var pr = await prResp.Content.ReadFromJsonAsync<PullRequestEntity>();
        Assert.NotNull(pr);

        var mergeResp = await authClient.PostAsJsonAsync(
            $"/api/v1/repos/qa-developer/microservice-app/pulls/{pr!.PullIndex}/merge",
            new MergePullRequest("merge", "Merge feature/api-gateway into main", pr.RowVersion));
        Assert.Equal(HttpStatusCode.OK, mergeResp.StatusCode);

        var mergedPr = await host.Db.GetPullRequestAsync(qaRepo.Id, pr.PullIndex);
        Assert.NotNull(mergedPr);
        Assert.True(mergedPr!.IsMerged);
        Assert.Equal("merged", mergedPr.State);
        Assert.NotNull(mergedPr.MergeCommitSha);
    }
}
