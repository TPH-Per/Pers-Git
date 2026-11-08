using System.Globalization;
using System.IO.Compression;
using System.Runtime.InteropServices;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Threading.RateLimiting;
using Gitea.Net.Data;
using Gitea.Net.Git;
using Gitea.Net.Models;
using Gitea.Net.Security;
using Gitea.Net.Services;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Diagnostics;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;

var builder = WebApplication.CreateBuilder(args);

string appDataDir = Path.Combine(builder.Environment.ContentRootPath, "App_Data");
Directory.CreateDirectory(appDataDir);

builder.Services.AddProblemDetails();
builder.Services.AddSingleton<SecurityService>();
builder.Services.AddSingleton<KeyedLockProvider>();
builder.Services.AddSingleton<GiteaDbRepository>();
builder.Services.AddSingleton<GitEngineService>();
builder.Services.AddSingleton<WebhookDeliveryService>();
builder.Services.AddHostedService(sp => sp.GetRequiredService<WebhookDeliveryService>());
builder.Services.AddSingleton<MergeReconcilerHostedService>();
builder.Services.AddHostedService(sp => sp.GetRequiredService<MergeReconcilerHostedService>());
builder.Services.AddSingleton<CiPipelineRunnerService>();
builder.Services.AddHostedService(sp => sp.GetRequiredService<CiPipelineRunnerService>());

builder.Services.Configure<ForwardedHeadersOptions>(options =>
{
    options.ForwardedHeaders = ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto;
});

builder.Services.AddAuthentication(GiteaAuthHandler.SchemeName)
    .AddScheme<AuthenticationSchemeOptions, GiteaAuthHandler>(GiteaAuthHandler.SchemeName, null);

builder.Services.AddAuthorizationBuilder()
    .AddPolicy("Authenticated", policy => policy.RequireAuthenticatedUser())
    .AddPolicy("AdminOnly", policy => policy.RequireRole("Admin"));

int globalPermitLimit = int.TryParse(builder.Configuration["Security:RateLimitPermitLimit"], NumberStyles.Integer, CultureInfo.InvariantCulture, out int gpl) ? gpl : 5000;
int authPermitLimit = int.TryParse(builder.Configuration["Security:AuthRateLimitPermitLimit"], NumberStyles.Integer, CultureInfo.InvariantCulture, out int apl) ? apl : 20;
int windowSecs = int.TryParse(builder.Configuration["Security:RateLimitWindowSeconds"], NumberStyles.Integer, CultureInfo.InvariantCulture, out int rws) ? Math.Clamp(rws, 1, 3600) : 60;

builder.Services.AddRateLimiter(options =>
{
    options.RejectionStatusCode = StatusCodes.Status429TooManyRequests;

    options.AddPolicy("AuthBruteForcePolicy", httpContext =>
        RateLimitPartition.GetFixedWindowLimiter(
            partitionKey: $"auth:{httpContext.Connection.RemoteIpAddress?.ToString() ?? "local"}",
            factory: _ => new FixedWindowRateLimiterOptions
            {
                AutoReplenishment = true,
                PermitLimit = authPermitLimit,
                QueueLimit = 0,
                Window = TimeSpan.FromSeconds(windowSecs)
            }));

    options.GlobalLimiter = PartitionedRateLimiter.Create<HttpContext, string>(httpContext =>
        RateLimitPartition.GetFixedWindowLimiter(
            partitionKey: httpContext.Connection.RemoteIpAddress?.ToString() ?? "local",
            factory: _ => new FixedWindowRateLimiterOptions
            {
                AutoReplenishment = true,
                PermitLimit = globalPermitLimit,
                QueueLimit = 100,
                Window = TimeSpan.FromSeconds(windowSecs)
            }));
});

var app = builder.Build();

app.UseForwardedHeaders();

// Global Structured Exception Handler: logs internal details and never leaks raw stack traces or OS paths on 5xx
app.UseExceptionHandler(errApp =>
{
    errApp.Run(async context =>
    {
        if (context.RequestAborted.IsCancellationRequested)
        {
            context.Response.StatusCode = 499; // Client Closed Request
            return;
        }

        var ex = context.Features.Get<IExceptionHandlerFeature>()?.Error;
        var logger = context.RequestServices.GetRequiredService<ILoggerFactory>().CreateLogger("ExceptionHandler");

        (int statusCode, string title, string safeDetail) = ex switch
        {
            DuplicateResourceException dre => (StatusCodes.Status409Conflict, "Resource conflict.", dre.Message),
            GitRefNotFoundException grnf => (StatusCodes.Status404NotFound, "Requested Git reference or path was not found.", grnf.Message),
            GitConflictException gce => (StatusCodes.Status409Conflict, "Git conflict detected.", gce.Message),
            GitValidationException gve => (StatusCodes.Status400BadRequest, "Invalid Git parameter.", gve.Message),
            TimeoutException => (StatusCodes.Status503ServiceUnavailable, "Service busy or operation timed out.", "The server timed out waiting for a repository lock or Git process."),
            BadHttpRequestException => (StatusCodes.Status400BadRequest, "Malformed HTTP request.", "The request body or headers could not be parsed."),
            _ => (StatusCodes.Status500InternalServerError, "Internal server error.", "An unexpected server error occurred. Please check server logs.")
        };

        if (statusCode >= 500 && ex is not null)
        {
            logger.LogError(ex, "Unhandled exception on {Method} {Path}", context.Request.Method, context.Request.Path);
        }

        context.Response.StatusCode = statusCode;
        context.Response.ContentType = "application/problem+json";
        var problem = new ProblemDetails
        {
            Status = statusCode,
            Title = title,
            Detail = safeDetail
        };
        await context.Response.WriteAsJsonAsync(problem);
    });
});

if (!app.Environment.IsDevelopment())
{
    app.UseHsts();
}

// Security Headers & Cookie CSRF Defense Middleware
app.Use(async (ctx, next) =>
{
    ctx.Response.Headers["X-Content-Type-Options"] = "nosniff";
    ctx.Response.Headers["X-Frame-Options"] = "DENY";
    ctx.Response.Headers["Referrer-Policy"] = "strict-origin-when-cross-origin";
    ctx.Response.Headers["Content-Security-Policy"] =
        "default-src 'self'; style-src 'self' 'unsafe-inline'; script-src 'self'; img-src 'self' data:; connect-src 'self'; frame-ancestors 'none';";

    // CSRF protection for mutating requests when a session cookie is present (without Bearer/Basic header)
    string method = ctx.Request.Method;
    bool isMutating = HttpMethods.IsPost(method) || HttpMethods.IsPut(method) || HttpMethods.IsPatch(method) || HttpMethods.IsDelete(method);
    if (isMutating &&
        ctx.Request.Cookies.ContainsKey(GiteaAuthHandler.CookieName) &&
        !ctx.Request.Headers.ContainsKey("Authorization") &&
        ctx.Request.Path.StartsWithSegments("/api"))
    {
        string? csrfHeader = ctx.Request.Headers["X-Gitea-Csrf"];
        if (csrfHeader != "1")
        {
            ctx.Response.StatusCode = StatusCodes.Status403Forbidden;
            await ctx.Response.WriteAsJsonAsync(new { error = "CSRF validation failed: missing X-Gitea-Csrf header on cookie-authenticated request." });
            return;
        }

        string? origin = ctx.Request.Headers.Origin;
        if (!string.IsNullOrEmpty(origin) && Uri.TryCreate(origin, UriKind.Absolute, out var originUri))
        {
            if (!string.Equals(originUri.Host, ctx.Request.Host.Host, StringComparison.OrdinalIgnoreCase))
            {
                ctx.Response.StatusCode = StatusCodes.Status403Forbidden;
                await ctx.Response.WriteAsJsonAsync(new { error = "CSRF validation failed: cross-origin request rejected." });
                return;
            }
        }
    }

    await next();
});

app.UseRateLimiter();
app.UseDefaultFiles();
app.UseStaticFiles();
app.UseAuthentication();
app.UseAuthorization();

// Initialize DB, verify Git >= 2.38, and run strictly Development-only demo seed
using (var scope = app.Services.CreateScope())
{
    var db = scope.ServiceProvider.GetRequiredService<GiteaDbRepository>();
    var sec = scope.ServiceProvider.GetRequiredService<SecurityService>();
    var git = scope.ServiceProvider.GetRequiredService<GitEngineService>();
    var reconciler = scope.ServiceProvider.GetRequiredService<MergeReconcilerHostedService>();
    var ciRunner = scope.ServiceProvider.GetRequiredService<CiPipelineRunnerService>();
    var logger = scope.ServiceProvider.GetRequiredService<ILoggerFactory>().CreateLogger("Bootstrap");

    await git.EnsureMinimumGitVersionAsync();
    await db.InitializeSchemaAsync();
    await reconciler.ReconcileStuckPullRequestsAsync(MergeReconcilerHostedService.StuckMergeThreshold);
    await ciRunner.ReconcileStuckCiWorkflowRunsAsync(CiPipelineRunnerService.StuckCiRunThreshold);

    var existingAdmin = await db.GetUserByUsernameAsync("gitea-admin");
    if (existingAdmin is null)
    {
        bool isDev = app.Environment.IsDevelopment();
        string? envPassword = Environment.GetEnvironmentVariable("GITEA_ADMIN_INITIAL_PASSWORD");
        bool generatedRandom = false;

        string adminPassword;
        if (!string.IsNullOrWhiteSpace(envPassword))
        {
            adminPassword = envPassword;
        }
        else if (isDev)
        {
            adminPassword = "Admin@12345";
        }
        else
        {
            adminPassword = Convert.ToBase64String(RandomNumberGenerator.GetBytes(20));
            generatedRandom = true;
        }

        var (hash, salt) = sec.HashPassword(adminPassword);
        var admin = await db.CreateUserAsync(
            "gitea-admin",
            "admin@gitea.net.local",
            "Gitea .NET Core Administrator",
            hash,
            salt,
            isAdmin: true,
            avatarColor: "#C96442",
            mustChangePassword: !isDev);

        if (generatedRandom)
        {
            // Never log plaintext passwords to application logs; write to a 0600 root-only file in App_Data
            string bootstrapSecretPath = Path.Combine(appDataDir, "initial_admin_password.txt");
            await File.WriteAllTextAsync(bootstrapSecretPath, $"gitea-admin initial password: {adminPassword}\n");
            if (!RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
            {
                try
                {
                    File.SetUnixFileMode(bootstrapSecretPath, UnixFileMode.UserRead | UnixFileMode.UserWrite);
                }
                catch
                {
                }
            }
            logger.LogWarning("Created initial administrator 'gitea-admin' (must_change_password=true). One-time password written to {Path} (mode 0600).", bootstrapSecretPath);
        }

        // Only seed demo developer account and sample repository in Development environment (never in Staging or Production)
        if (isDev)
        {
            var (devHash, devSalt) = sec.HashPassword("Dev@12345");
            var devUser = await db.CreateUserAsync(
                "linus-dev",
                "linus@gitea.net.local",
                "Linus Torvalds (Staff Engineer)",
                devHash,
                devSalt,
                isAdmin: false,
                avatarColor: "#2E6F4E",
                mustChangePassword: false);

            var repo = await db.CreateRepositoryAsync(
                admin.Id,
                "gitea-dotnet-core",
                "Self-hosted Git service MVP built with .NET 8.0, PostgreSQL/SQLite, Git for Windows CLI, and Vanilla ES6 UI.",
                isPrivate: false,
                defaultBranch: "main");

            await db.AddOrUpdateCollaboratorAsync(repo.Id, devUser.Id, "write");

            await git.InitializeBareRepositoryAsync(
                admin.Username,
                repo.Name,
                "main",
                initializeWithReadme: true,
                admin.FullName,
                admin.Email,
                repo.Description);

            const string kernelSource = """
                namespace Gitea.Net.Sample;

                public static class Kernel
                {
                    public static string Version => "8.0.0-production";
                    public static bool CheckIntegrity() => true;
                }
                """;

            string mainSha = await git.CommitFileAsync(
                admin.Username,
                repo.Name,
                "main",
                "src/Kernel.cs",
                kernelSource,
                "Add core Kernel.cs module",
                admin.FullName,
                admin.Email);

            const string ciWorkflowYaml = """
                name: Per's Git Production CI/CD Pipeline
                on: [push, pull_request, workflow_dispatch]
                jobs:
                  verify-and-test:
                    runs-on: ubuntu-latest
                    steps:
                      - name: Verify C# 12 Lexer & Zero N+1 SQL
                        run: ./verify_all.sh
                """;

            mainSha = await git.CommitFileAsync(
                admin.Username,
                repo.Name,
                "main",
                ".github/workflows/ci-cd.yml",
                ciWorkflowYaml,
                "ci: add automated CI/CD workflow pipeline definition",
                admin.FullName,
                admin.Email);

            await git.CreateBranchAsync(admin.Username, repo.Name, "feature/zero-n-plus-one", "main");

            const string optimizerSource = """
                namespace Gitea.Net.Sample;

                public static class QueryOptimizer
                {
                    public static int MaxQueriesPerRequest => 1;
                }
                """;

            await git.CommitFileAsync(
                admin.Username,
                repo.Name,
                "feature/zero-n-plus-one",
                "src/QueryOptimizer.cs",
                optimizerSource,
                "Implement single-query JOIN optimizer for issues and repositories",
                devUser.FullName,
                devUser.Email);

            var issue1 = await db.CreateIssueAsync(
                repo.Id,
                devUser.Id,
                "Verify O(1) maintained counters and zero N+1 SQL queries",
                "All repository and issue listing endpoints must execute in O(1) without full-table GROUP BY scans.",
                "performance,sql,verified");

            await db.AddIssueCommentAsync(
                repo.Id,
                issue1.Id,
                admin.Id,
                "Confirmed: open_issues_count, open_prs_count, and comments_count are maintained atomically.");

            await db.CreatePullRequestAsync(
                repo.Id,
                devUser.Id,
                "feat(sql): add O(1) maintained counters & 3-phase atomic PR merge",
                "This PR introduces QueryOptimizer.cs and validates 3-phase atomic PR merge semantics.",
                "feature/zero-n-plus-one",
                "main");

            var seededCiRun = await db.CreateCiWorkflowRunAsync(
                repo.Id,
                "Per's Git Production CI/CD Pipeline",
                "push",
                "main",
                mainSha,
                admin.Id);
            await ciRunner.ExecutePipelineNowAsync(repo.Id, seededCiRun.Id);

            await db.AppendAuditLogAsync(admin.Id, admin.Username, "system.seed", "Initialized development workspace, CI/CD workflow, and Git repository.");
        }
    }
}

async Task<UserEntity?> GetAuthenticatedUserAsync(HttpContext ctx, GiteaDbRepository db)
{
    if (ctx.User.Identity?.IsAuthenticated != true)
        return null;

    string? idStr = ctx.User.FindFirstValue(ClaimTypes.NameIdentifier);
    if (!long.TryParse(idStr, NumberStyles.Integer, CultureInfo.InvariantCulture, out long userId))
        return null;

    return await db.GetUserByIdAsync(userId, ctx.RequestAborted);
}

IResult? CheckMustChangePasswordAndScope(HttpContext ctx, UserEntity actor, string requiredScope)
{
    if (actor.MustChangePassword && !ctx.Request.Path.StartsWithSegments("/api/v1/auth/change-password"))
    {
        return Results.Json(
            new { error = "Password change required before performing any other actions.", mustChangePassword = true },
            statusCode: StatusCodes.Status403Forbidden);
    }

    if (!SecurityService.HasRequiredScope(ctx.User, requiredScope))
    {
        return Results.Json(
            new { error = $"Insufficient token scope. Required scope: '{requiredScope}'." },
            statusCode: StatusCodes.Status403Forbidden);
    }

    return null;
}

void SetSessionCookie(HttpContext ctx, string token, DateTimeOffset expiresAt)
{
    ctx.Response.Cookies.Append(GiteaAuthHandler.CookieName, token, new CookieOptions
    {
        HttpOnly = true,
        SameSite = SameSiteMode.Strict,
        Secure = ctx.Request.IsHttps || string.Equals(ctx.Request.Headers["X-Forwarded-Proto"], "https", StringComparison.OrdinalIgnoreCase),
        Expires = expiresAt,
        Path = "/"
    });
}

// ==================== SYSTEM, HEALTH & COUNTER RECONCILIATION API ====================

app.MapGet("/api/v1/health", async (GiteaDbRepository db, GitEngineService git, CancellationToken ct) =>
{
    bool dbOk = await db.CheckHealthAsync(ct);
    if (!dbOk)
        return Results.StatusCode(StatusCodes.Status503ServiceUnavailable);

    return Results.Ok(new
    {
        status = "healthy",
        provider = db.IsPostgres ? "PostgreSQL (Npgsql)" : "SQLite (WAL Mode)",
        gitBinary = git.GitBinaryPath,
        timestamp = DateTimeOffset.UtcNow
    });
});

app.MapGet("/api/v1/audit-logs", async (HttpContext ctx, int? page, int? pageSize, GiteaDbRepository db, CancellationToken ct) =>
{
    var actor = await GetAuthenticatedUserAsync(ctx, db);
    if (actor is null) return Results.Unauthorized();
    var scopeErr = CheckMustChangePasswordAndScope(ctx, actor, "admin");
    if (scopeErr is not null) return scopeErr;

    var logs = await db.ListRecentAuditLogsAsync(page ?? 1, pageSize ?? 25, ct);
    return Results.Ok(logs);
}).RequireAuthorization("AdminOnly");

app.MapPost("/api/v1/admin/reconcile", async (
    HttpContext ctx, GiteaDbRepository db, MergeReconcilerHostedService reconciler, CiPipelineRunnerService ciRunner, CancellationToken ct) =>
{
    var actor = await GetAuthenticatedUserAsync(ctx, db);
    if (actor is null) return Results.Unauthorized();
    var scopeErr = CheckMustChangePasswordAndScope(ctx, actor, "admin");
    if (scopeErr is not null) return scopeErr;

    await db.ReconcileMaintainedCountersAsync(null, ct);
    await reconciler.ReconcileStuckPullRequestsAsync(TimeSpan.Zero, ct);
    int reapedCiRuns = await ciRunner.ReconcileStuckCiWorkflowRunsAsync(TimeSpan.Zero, ct);
    return Results.Ok(new { status = "reconciled", reapedCiRuns });
}).RequireAuthorization("AdminOnly");

app.MapPost("/api/v1/admin/backup", async (HttpContext ctx, IHostEnvironment env, GiteaDbRepository db, CancellationToken ct) =>
{
    var actor = await GetAuthenticatedUserAsync(ctx, db);
    if (actor is null) return Results.Unauthorized();
    var scopeErr = CheckMustChangePasswordAndScope(ctx, actor, "admin");
    if (scopeErr is not null) return scopeErr;

    string backupDir = Path.Combine(env.ContentRootPath, "App_Data", "backups");
    string backupFile = await db.CreateSqliteHotBackupAsync(backupDir, ct);
    await db.AppendAuditLogAsync(actor.Id, actor.Username, "admin.backup", $"Created online SQLite hot backup ({Path.GetFileName(backupFile)})", ct);
    return Results.Ok(new { status = "backed_up", fileName = Path.GetFileName(backupFile), createdAt = DateTimeOffset.UtcNow });
}).RequireAuthorization("AdminOnly");

app.MapGet("/metrics", async (HttpContext ctx, GiteaDbRepository db, KeyedLockProvider locks, CancellationToken ct) =>
{
    var remoteIp = ctx.Connection.RemoteIpAddress;
    bool isInternal = IsInternalOrLoopback(remoteIp);
    if (!isInternal)
    {
        var actor = await GetAuthenticatedUserAsync(ctx, db);
        if (actor is null || !actor.IsAdmin)
            return Results.StatusCode(StatusCodes.Status403Forbidden);
    }

    bool healthy = await db.CheckHealthAsync(ct);
    string metricsText = string.Join("\n",
        "# HELP pers_git_up Whether Per's Git server and database are healthy (1 = up, 0 = down).",
        "# TYPE pers_git_up gauge",
        $"pers_git_up {(healthy ? 1 : 0)}",
        "# HELP pers_git_active_repo_locks Current number of active repository locks in KeyedLockProvider.",
        "# TYPE pers_git_active_repo_locks gauge",
        $"pers_git_active_repo_locks {locks.ActiveKeyCount}",
        "");
    return Results.Text(metricsText, "text/plain; version=0.0.4; charset=utf-8");
});

// ==================== AUTH, SESSION, PASSWORD CHANGE & PAT API ====================

app.MapPost("/api/v1/auth/register", async (HttpContext ctx, RegisterRequest? req, IConfiguration config, GiteaDbRepository db, SecurityService sec, CancellationToken ct) =>
{
    bool allowReg = !bool.TryParse(config["Security:AllowRegistration"], out bool ar) || ar;
    if (!allowReg)
        return Results.Json(new { error = "Public user registration is disabled by the administrator." }, statusCode: StatusCodes.Status403Forbidden);

    if (req is null)
        return Results.BadRequest(new { error = "Request body is required." });

    string username = (req.Username ?? string.Empty).Trim();
    string email = (req.Email ?? string.Empty).Trim();
    string fullName = string.IsNullOrWhiteSpace(req.FullName) ? username : req.FullName.Trim();
    string password = req.Password ?? string.Empty;

    if (!SecurityService.IsSafeIdentifier(username))
        return Results.BadRequest(new { error = "Username must be 2-63 safe alphanumeric, dash, dot, or underscore characters (and cannot end in .git)." });
    if (!SecurityService.IsValidEmail(email))
        return Results.BadRequest(new { error = "A valid email address is required." });
    if (fullName.Length > 120)
        return Results.BadRequest(new { error = "Full name cannot exceed 120 characters." });
    if (password.Length < 8 || password.Length > 128)
        return Results.BadRequest(new { error = "Password must be between 8 and 128 characters." });

    var (hash, salt) = sec.HashPassword(password);
    var user = await db.CreateUserAsync(username, email, fullName, hash, salt, isAdmin: false, avatarColor: "#8C5A3C", mustChangePassword: false, ct);
    await db.AppendAuditLogAsync(user.Id, user.Username, "user.register", $"Registered account @{user.Username}", ct);

    var (token, _, expiresAt) = sec.IssueToken(user);
    SetSessionCookie(ctx, token, expiresAt);
    return Results.Created($"/api/v1/users/{user.Username}", new AuthResponse(token, user.ToDto(), expiresAt));
}).RequireRateLimiting("AuthBruteForcePolicy");

app.MapPost("/api/v1/auth/login", async (HttpContext ctx, LoginRequest? req, GiteaDbRepository db, SecurityService sec, CancellationToken ct) =>
{
    if (req is null || string.IsNullOrWhiteSpace(req.Username) || string.IsNullOrEmpty(req.Password))
        return Results.Json(new { error = "Invalid username or password." }, statusCode: StatusCodes.Status401Unauthorized);

    string username = req.Username.Trim();
    string remoteIp = ctx.Connection.RemoteIpAddress?.ToString() ?? "local";

    if (sec.IsLockedOut(remoteIp, username))
        return Results.Json(new { error = "Too many failed login attempts. Please try again in 5 minutes." }, statusCode: StatusCodes.Status429TooManyRequests);

    var user = await db.GetUserByUsernameAsync(username, ct);
    if (user is null)
    {
        sec.PerformDummyPasswordVerification(req.Password);
        sec.RecordFailedAuthAttempt(remoteIp, username);
        return Results.Json(new { error = "Invalid username or password." }, statusCode: StatusCodes.Status401Unauthorized);
    }

    if (!sec.VerifyPassword(req.Password, user.PasswordHash, user.PasswordSalt, out bool needsUpgrade))
    {
        sec.RecordFailedAuthAttempt(remoteIp, username);
        return Results.Json(new { error = "Invalid username or password." }, statusCode: StatusCodes.Status401Unauthorized);
    }

    sec.ClearFailedAuthAttempts(remoteIp, username);

    // Transparently upgrade legacy 100k password hashes to v1$600000$ on successful login
    if (needsUpgrade)
    {
        var (newHash, newSalt) = sec.HashPassword(req.Password);
        await db.UpdateUserPasswordAsync(user.Id, newHash, newSalt, user.MustChangePassword, ct);
        user.PasswordHash = newHash;
        user.PasswordSalt = newSalt;
    }

    var (token, _, expiresAt) = sec.IssueToken(user);
    SetSessionCookie(ctx, token, expiresAt);
    await db.AppendAuditLogAsync(user.Id, user.Username, "user.login", $"User @{user.Username} signed in", ct);
    return Results.Ok(new AuthResponse(token, user.ToDto(), expiresAt));
}).RequireRateLimiting("AuthBruteForcePolicy");

app.MapPost("/api/v1/auth/change-password", async (HttpContext ctx, ChangePasswordRequest? req, GiteaDbRepository db, SecurityService sec, CancellationToken ct) =>
{
    var actor = await GetAuthenticatedUserAsync(ctx, db);
    if (actor is null) return Results.Unauthorized();
    if (req is null || string.IsNullOrEmpty(req.CurrentPassword) || string.IsNullOrEmpty(req.NewPassword))
        return Results.BadRequest(new { error = "CurrentPassword and NewPassword are required." });
    if (req.NewPassword.Length < 8 || req.NewPassword.Length > 128)
        return Results.BadRequest(new { error = "NewPassword must be between 8 and 128 characters." });

    if (!sec.VerifyPassword(req.CurrentPassword, actor.PasswordHash, actor.PasswordSalt))
        return Results.Json(new { error = "Current password is incorrect." }, statusCode: StatusCodes.Status401Unauthorized);

    var (newHash, newSalt) = sec.HashPassword(req.NewPassword);
    await db.UpdateUserPasswordAsync(actor.Id, newHash, newSalt, mustChangePassword: false, ct);
    await db.AppendAuditLogAsync(actor.Id, actor.Username, "user.password.change", $"User @{actor.Username} changed password", ct);
    return Results.Ok(new { message = "Password updated." });
}).RequireAuthorization("Authenticated");

app.MapPost("/api/v1/auth/logout", async (HttpContext ctx, GiteaDbRepository db, CancellationToken ct) =>
{
    string? jti = ctx.User.FindFirstValue("jti");
    if (!string.IsNullOrEmpty(jti))
    {
        await db.RevokeTokenAsync(jti, DateTimeOffset.UtcNow.AddDays(1), ct);
    }
    ctx.Response.Cookies.Delete(GiteaAuthHandler.CookieName, new CookieOptions { Path = "/" });
    return Results.Ok(new { message = "Logged out and session token revoked." });
}).RequireAuthorization("Authenticated");

app.MapGet("/api/v1/auth/me", async (HttpContext ctx, GiteaDbRepository db) =>
{
    var user = await GetAuthenticatedUserAsync(ctx, db);
    if (user is null) return Results.Unauthorized();
    return Results.Ok(user.ToDto());
}).RequireAuthorization("Authenticated");

app.MapGet("/api/v1/auth/tokens", async (HttpContext ctx, GiteaDbRepository db, CancellationToken ct) =>
{
    var actor = await GetAuthenticatedUserAsync(ctx, db);
    if (actor is null) return Results.Unauthorized();

    var tokens = await db.ListPersonalAccessTokensAsync(actor.Id, ct);
    return Results.Ok(tokens.Select(t => t.ToDto()));
}).RequireAuthorization("Authenticated");

app.MapPost("/api/v1/auth/tokens", async (HttpContext ctx, CreatePatRequest? req, GiteaDbRepository db, CancellationToken ct) =>
{
    var actor = await GetAuthenticatedUserAsync(ctx, db);
    if (actor is null) return Results.Unauthorized();

    // Prevent a PAT from minting additional PATs unless it explicitly has 'admin' scope or the user is authenticated via Cookie/Basic
    string? authMethod = ctx.User.FindFirstValue("auth_method");
    if (authMethod is "Pat" or "BasicPat" && !SecurityService.HasRequiredScope(ctx.User, "admin"))
    {
        return Results.Json(new { error = "Personal Access Tokens without 'admin' scope cannot create new Personal Access Tokens." }, statusCode: StatusCodes.Status403Forbidden);
    }

    string name = (req?.Name ?? string.Empty).Trim();
    if (string.IsNullOrWhiteSpace(name) || name.Length > 100)
        return Results.BadRequest(new { error = "Token name is required (max 100 chars)." });

    string normalizedScopes = SecurityService.NormalizePatScopes(req?.Scopes);
    DateTimeOffset? expiresAt = req?.ExpiresInDays is > 0
        ? DateTimeOffset.UtcNow.AddDays(Math.Min(req.ExpiresInDays.Value, 365))
        : null;

    string rawRandom = Convert.ToHexString(RandomNumberGenerator.GetBytes(24)).ToLowerInvariant();
    string rawToken = $"pat_{rawRandom}";
    string prefix = rawToken[..12];
    string tokenHash = SecurityService.ComputeSha256Hex(rawToken);

    var pat = await db.CreatePersonalAccessTokenAsync(actor.Id, name, prefix, tokenHash, normalizedScopes, expiresAt, ct);
    await db.AppendAuditLogAsync(actor.Id, actor.Username, "auth.pat.create", $"Created PAT '{name}' ({prefix}...) with scopes [{normalizedScopes}]", ct);

    return Results.Created($"/api/v1/auth/tokens/{pat.Id}", new CreatedPatResponse(
        pat.Id, pat.Name, rawToken, pat.TokenPrefix, pat.Scopes, pat.CreatedAt, pat.ExpiresAt));
}).RequireAuthorization("Authenticated");

app.MapDelete("/api/v1/auth/tokens/{id:long}", async (HttpContext ctx, long id, GiteaDbRepository db, CancellationToken ct) =>
{
    var actor = await GetAuthenticatedUserAsync(ctx, db);
    if (actor is null) return Results.Unauthorized();

    bool deleted = await db.DeletePersonalAccessTokenAsync(actor.Id, id, ct);
    if (!deleted) return Results.NotFound(new { error = "Personal Access Token not found." });

    await db.AppendAuditLogAsync(actor.Id, actor.Username, "auth.pat.revoke", $"Revoked PAT #{id}", ct);
    return Results.Ok(new { revoked = true, id });
}).RequireAuthorization("Authenticated");

app.MapGet("/api/v1/users", async (HttpContext ctx, int? page, int? pageSize, GiteaDbRepository db, CancellationToken ct) =>
{
    var actor = await GetAuthenticatedUserAsync(ctx, db);
    if (actor is null) return Results.Unauthorized();

    var paged = await db.ListUsersAsync(page ?? 1, pageSize ?? 30, ct);
    if (actor.IsAdmin)
    {
        return Results.Ok(new PagedResult<UserDto>(
            paged.Items.Select(u => u.ToDto()).ToList(),
            paged.Page,
            paged.PageSize,
            paged.TotalCount));
    }

    return Results.Ok(new PagedResult<PublicUserProfileDto>(
        paged.Items.Select(u => new PublicUserProfileDto(u.Id, u.Username, u.FullName, u.AvatarColor, u.CreatedAt)).ToList(),
        paged.Page,
        paged.PageSize,
        paged.TotalCount));
}).RequireAuthorization("Authenticated");

// ==================== REPOSITORIES, COLLABORATORS & GIT ENGINE API ====================

app.MapGet("/api/v1/repos", async (HttpContext ctx, string? q, int? page, int? pageSize, GiteaDbRepository db, CancellationToken ct) =>
{
    var actor = await GetAuthenticatedUserAsync(ctx, db);
    var repos = await db.ListRepositoriesAsync(q, actor?.Id, actor?.IsAdmin ?? false, page ?? 1, pageSize ?? 25, ct);
    return Results.Ok(repos);
});

app.MapPost("/api/v1/repos", async (HttpContext ctx, CreateRepoRequest? req, GiteaDbRepository db, GitEngineService git, CancellationToken ct) =>
{
    var actor = await GetAuthenticatedUserAsync(ctx, db);
    if (actor is null) return Results.Unauthorized();
    var scopeErr = CheckMustChangePasswordAndScope(ctx, actor, "repo:write");
    if (scopeErr is not null) return scopeErr;

    if (req is null) return Results.BadRequest(new { error = "Request body is required." });

    string name = (req.Name ?? string.Empty).Trim();
    string description = (req.Description ?? string.Empty).Trim();
    string defaultBranch = string.IsNullOrWhiteSpace(req.DefaultBranch) ? "main" : req.DefaultBranch.Trim();

    if (!SecurityService.IsSafeIdentifier(name))
        return Results.BadRequest(new { error = "Invalid repository name. Only safe 2-63 alphanumeric, '-', '_', '.' characters are allowed (cannot end in .git)." });
    if (description.Length > 500)
        return Results.BadRequest(new { error = "Description cannot exceed 500 characters." });
    if (!GitEngineService.ValidateRefFormat(defaultBranch))
        return Results.BadRequest(new { error = "Invalid default branch name." });

    var repo = await db.CreateRepositoryAsync(actor.Id, name, description, req.IsPrivate, defaultBranch, ct);
    try
    {
        await git.InitializeBareRepositoryAsync(
            actor.Username,
            repo.Name,
            defaultBranch,
            req.InitializeReadme,
            actor.FullName,
            actor.Email,
            repo.Description,
            ct);
    }
    catch
    {
        await db.DeleteRepositoryAsync(repo.Id, CancellationToken.None);
        git.DeleteBareRepositoryIfExists(actor.Username, repo.Name);
        throw;
    }

    await db.AppendAuditLogAsync(actor.Id, actor.Username, "repo.create", $"Created repository {actor.Username}/{repo.Name}", ct);
    return Results.Created($"/api/v1/repos/{actor.Username}/{repo.Name}", repo);
}).RequireAuthorization("Authenticated");

// Helper: Return 404 NotFound when a private repository is accessed by an unauthorized user (prevents private repo existence enumeration)
async Task<(RepositoryEntity? Repo, bool CanRead, bool CanWrite, bool CanAdmin, IResult? ErrorResult)> ResolveAuthorizedRepoAsync(
    HttpContext ctx, string owner, string repo, string? requiredScopeForAuthenticated, GiteaDbRepository db, CancellationToken ct)
{
    if (!SecurityService.IsSafeIdentifier(owner) || !SecurityService.IsSafeIdentifier(repo))
        return (null, false, false, false, Results.BadRequest(new { error = "Invalid owner or repository identifier." }));

    var entity = await db.GetRepositoryByOwnerAndNameAsync(owner, repo, ct);
    if (entity is null)
        return (null, false, false, false, Results.NotFound(new { error = "Repository not found." }));

    var actor = await GetAuthenticatedUserAsync(ctx, db);
    if (actor is not null && requiredScopeForAuthenticated is not null)
    {
        var scopeErr = CheckMustChangePasswordAndScope(ctx, actor, requiredScopeForAuthenticated);
        if (scopeErr is not null)
            return (null, false, false, false, scopeErr);
    }

    var (canRead, canWrite, canAdmin) = await db.GetRepositoryPermissionsAsync(entity, actor?.Id, actor?.IsAdmin ?? false, ct);
    if (!canRead)
    {
        // Always return 404 Not Found when caller lacks read permission on a private repository to prevent existence enumeration
        return (null, false, false, false, Results.NotFound(new { error = "Repository not found." }));
    }

    return (entity, canRead, canWrite, canAdmin, null);
}

app.MapGet("/api/v1/repos/{owner}/{repo}", async (HttpContext ctx, string owner, string repo, GiteaDbRepository db, GitEngineService git, CancellationToken ct) =>
{
    var (entity, canRead, canWrite, canAdmin, err) = await ResolveAuthorizedRepoAsync(ctx, owner, repo, "repo:read", db, ct);
    if (err is not null) return err;

    var branches = await git.ListBranchesAsync(entity!.OwnerUsername, entity.Name, ct);
    return Results.Ok(new
    {
        repository = entity,
        branches,
        permissions = new { canRead, canWrite, canAdmin }
    });
});

app.MapGet("/api/v1/repos/{owner}/{repo}/collaborators", async (HttpContext ctx, string owner, string repo, GiteaDbRepository db, CancellationToken ct) =>
{
    var (entity, _, _, _, err) = await ResolveAuthorizedRepoAsync(ctx, owner, repo, "repo:read", db, ct);
    if (err is not null) return err;

    var collabs = await db.ListCollaboratorsAsync(entity!.Id, ct);
    return Results.Ok(collabs);
});

app.MapPost("/api/v1/repos/{owner}/{repo}/collaborators", async (HttpContext ctx, string owner, string repo, AddCollaboratorRequest? req, GiteaDbRepository db, CancellationToken ct) =>
{
    var actor = await GetAuthenticatedUserAsync(ctx, db);
    if (actor is null) return Results.Unauthorized();
    if (req is null || string.IsNullOrWhiteSpace(req.Username))
        return Results.BadRequest(new { error = "Collaborator username is required." });

    string permission = (req.Permission ?? "write").Trim().ToLowerInvariant();
    if (permission is not ("read" or "write" or "admin"))
        return Results.BadRequest(new { error = "Permission must be 'read', 'write', or 'admin'." });

    var (entity, _, _, canAdmin, err) = await ResolveAuthorizedRepoAsync(ctx, owner, repo, "repo:write", db, ct);
    if (err is not null) return err;
    if (!canAdmin) return Results.StatusCode(StatusCodes.Status403Forbidden);

    var targetUser = await db.GetUserByUsernameAsync(req.Username.Trim(), ct);
    if (targetUser is null)
        return Results.NotFound(new { error = $"User '{req.Username}' not found." });

    await db.AddOrUpdateCollaboratorAsync(entity!.Id, targetUser.Id, permission, ct);
    await db.AppendAuditLogAsync(actor.Id, actor.Username, "repo.collaborator.add", $"Granted '{permission}' on {entity.OwnerUsername}/{entity.Name} to @{targetUser.Username}", ct);
    var collabs = await db.ListCollaboratorsAsync(entity.Id, ct);
    return Results.Ok(collabs);
}).RequireAuthorization("Authenticated");

app.MapPost("/api/v1/repos/{owner}/{repo}/star", async (HttpContext ctx, string owner, string repo, GiteaDbRepository db, CancellationToken ct) =>
{
    var actor = await GetAuthenticatedUserAsync(ctx, db);
    if (actor is null) return Results.Unauthorized();

    var (entity, _, _, _, err) = await ResolveAuthorizedRepoAsync(ctx, owner, repo, "repo:read", db, ct);
    if (err is not null) return err;

    var (starred, starsCount) = await db.ToggleStarAsync(entity!.Id, actor.Id, ct);
    return Results.Ok(new { starred, starsCount });
}).RequireAuthorization("Authenticated");

app.MapGet("/api/v1/repos/{owner}/{repo}/tree", async (HttpContext ctx, string owner, string repo, string? @ref, string? path, GiteaDbRepository db, GitEngineService git, CancellationToken ct) =>
{
    var (entity, _, _, _, err) = await ResolveAuthorizedRepoAsync(ctx, owner, repo, "repo:read", db, ct);
    if (err is not null) return err;

    string targetRef = string.IsNullOrWhiteSpace(@ref) ? entity!.DefaultBranch : @ref.Trim();
    var entries = await git.ListTreeAsync(entity!.OwnerUsername, entity.Name, targetRef, path, ct);
    return Results.Ok(new { @ref = targetRef, path = path ?? "", entries });
});

app.MapGet("/api/v1/repos/{owner}/{repo}/blob", async (HttpContext ctx, string owner, string repo, string? @ref, string? path, GiteaDbRepository db, GitEngineService git, CancellationToken ct) =>
{
    if (string.IsNullOrWhiteSpace(path))
        return Results.BadRequest(new { error = "File path query parameter is required." });

    var (entity, _, _, _, err) = await ResolveAuthorizedRepoAsync(ctx, owner, repo, "repo:read", db, ct);
    if (err is not null) return err;

    string targetRef = string.IsNullOrWhiteSpace(@ref) ? entity!.DefaultBranch : @ref.Trim();
    var blob = await git.GetBlobContentAsync(entity!.OwnerUsername, entity.Name, targetRef, path.Trim(), ct);
    return Results.Ok(blob);
});

app.MapPost("/api/v1/repos/{owner}/{repo}/commits", async (
    HttpContext ctx, string owner, string repo, CreateFileCommitRequest? req,
    GiteaDbRepository db, GitEngineService git, WebhookDeliveryService webhooks, CiPipelineRunnerService ciRunner, CancellationToken ct) =>
{
    var actor = await GetAuthenticatedUserAsync(ctx, db);
    if (actor is null) return Results.Unauthorized();
    if (req is null) return Results.BadRequest(new { error = "Request body is required." });

    var (entity, _, canWrite, _, err) = await ResolveAuthorizedRepoAsync(ctx, owner, repo, "repo:write", db, ct);
    if (err is not null) return err;
    if (!canWrite) return Results.StatusCode(StatusCodes.Status403Forbidden);

    string branch = (req.Branch ?? string.Empty).Trim();
    string filePath = (req.FilePath ?? string.Empty).Trim();
    string content = req.Content ?? string.Empty;
    string commitMessage = (req.CommitMessage ?? string.Empty).Trim();

    if (!GitEngineService.ValidateRefFormat(branch) || !SecurityService.IsSafeRelativeFilePath(filePath))
        return Results.BadRequest(new { error = "Invalid branch or file path." });

    string sha = await git.CommitFileAsync(
        entity!.OwnerUsername,
        entity.Name,
        branch,
        filePath,
        content,
        commitMessage,
        actor.FullName,
        actor.Email,
        ct);

    await db.TouchRepositoryUpdatedAtAsync(entity.Id, ct);
    await db.AppendAuditLogAsync(actor.Id, actor.Username, "git.commit", $"Committed {filePath} to {entity.OwnerUsername}/{entity.Name}@{branch} ({sha[..7]})", ct);
    webhooks.EnqueueEvent(entity.Id, "push", new { repository = entity.Name, branch, commitSha = sha, filePath, pusher = actor.Username });
    var ciRun = await ciRunner.EnqueueCiRunAsync(entity.Id, "Per's Git Production CI/CD Pipeline", "push", branch, sha, actor.Id, ct);
    return Results.Ok(new { sha, branch, filePath, ciRunIndex = ciRun.RunIndex });
}).RequireAuthorization("Authenticated");

app.MapGet("/api/v1/repos/{owner}/{repo}/commits", async (HttpContext ctx, string owner, string repo, string? @ref, GiteaDbRepository db, GitEngineService git, CancellationToken ct) =>
{
    var (entity, _, _, _, err) = await ResolveAuthorizedRepoAsync(ctx, owner, repo, "repo:read", db, ct);
    if (err is not null) return err;

    string targetRef = string.IsNullOrWhiteSpace(@ref) ? entity!.DefaultBranch : @ref.Trim();
    var commits = await git.ListCommitsAsync(entity!.OwnerUsername, entity.Name, targetRef, 40, ct);
    return Results.Ok(commits);
});

app.MapPost("/api/v1/repos/{owner}/{repo}/branches", async (HttpContext ctx, string owner, string repo, CreateBranchRequest? req, GiteaDbRepository db, GitEngineService git, CancellationToken ct) =>
{
    var actor = await GetAuthenticatedUserAsync(ctx, db);
    if (actor is null) return Results.Unauthorized();
    if (req is null) return Results.BadRequest(new { error = "Request body is required." });

    var (entity, _, canWrite, _, err) = await ResolveAuthorizedRepoAsync(ctx, owner, repo, "repo:write", db, ct);
    if (err is not null) return err;
    if (!canWrite) return Results.StatusCode(StatusCodes.Status403Forbidden);

    string newBranch = (req.NewBranchName ?? string.Empty).Trim();
    string fromRef = string.IsNullOrWhiteSpace(req.FromRef) ? entity!.DefaultBranch : req.FromRef.Trim();

    await git.CreateBranchAsync(entity!.OwnerUsername, entity.Name, newBranch, fromRef, ct);
    await db.TouchRepositoryUpdatedAtAsync(entity.Id, ct);
    await db.AppendAuditLogAsync(actor.Id, actor.Username, "git.branch", $"Created branch {newBranch} from {fromRef} in {entity.OwnerUsername}/{entity.Name}", ct);
    var branches = await git.ListBranchesAsync(entity.OwnerUsername, entity.Name, ct);
    return Results.Created($"/api/v1/repos/{owner}/{repo}/branches/{newBranch}", new { branches });
}).RequireAuthorization("Authenticated");

// ==================== ISSUES & COMMENTS API ====================

app.MapGet("/api/v1/repos/{owner}/{repo}/issues", async (HttpContext ctx, string owner, string repo, string? state, int? page, int? pageSize, GiteaDbRepository db, CancellationToken ct) =>
{
    var (entity, _, _, _, err) = await ResolveAuthorizedRepoAsync(ctx, owner, repo, "repo:read", db, ct);
    if (err is not null) return err;

    var issues = await db.ListIssuesAsync(entity!.Id, state, page ?? 1, pageSize ?? 25, ct);
    return Results.Ok(issues);
});

app.MapPost("/api/v1/repos/{owner}/{repo}/issues", async (
    HttpContext ctx, string owner, string repo, CreateIssueRequest? req,
    GiteaDbRepository db, WebhookDeliveryService webhooks, CancellationToken ct) =>
{
    var actor = await GetAuthenticatedUserAsync(ctx, db);
    if (actor is null) return Results.Unauthorized();
    if (req is null || string.IsNullOrWhiteSpace(req.Title))
        return Results.BadRequest(new { error = "Issue title is required." });

    string title = req.Title.Trim();
    string body = (req.Body ?? string.Empty).Trim();
    string labels = (req.LabelsCsv ?? string.Empty).Trim();

    if (title.Length > 255) return Results.BadRequest(new { error = "Issue title cannot exceed 255 characters." });
    if (body.Length > 65536) return Results.BadRequest(new { error = "Issue body cannot exceed 65536 characters." });
    if (labels.Length > 255) return Results.BadRequest(new { error = "Labels CSV cannot exceed 255 characters." });

    var (entity, _, _, _, err) = await ResolveAuthorizedRepoAsync(ctx, owner, repo, "issue:write", db, ct);
    if (err is not null) return err;

    var issue = await db.CreateIssueAsync(entity!.Id, actor.Id, title, body, labels, ct);
    await db.AppendAuditLogAsync(actor.Id, actor.Username, "issue.create", $"Opened issue #{issue.IssueIndex} in {entity.OwnerUsername}/{entity.Name}", ct);
    webhooks.EnqueueEvent(entity.Id, "issues", new { action = "opened", issue });
    return Results.Created($"/api/v1/repos/{owner}/{repo}/issues/{issue.IssueIndex}", issue);
}).RequireAuthorization("Authenticated");

app.MapGet("/api/v1/repos/{owner}/{repo}/issues/{index:long}", async (HttpContext ctx, string owner, string repo, long index, GiteaDbRepository db, CancellationToken ct) =>
{
    var (entity, _, _, _, err) = await ResolveAuthorizedRepoAsync(ctx, owner, repo, "repo:read", db, ct);
    if (err is not null) return err;

    var bundle = await db.GetIssueDetailBundleAsync(entity!.Id, index, ct);
    if (bundle is null) return Results.NotFound(new { error = "Issue not found." });
    return Results.Ok(bundle);
});

app.MapPatch("/api/v1/repos/{owner}/{repo}/issues/{index:long}/state", async (
    HttpContext ctx, string owner, string repo, long index, UpdateIssueStateRequest? req,
    GiteaDbRepository db, WebhookDeliveryService webhooks, CancellationToken ct) =>
{
    var actor = await GetAuthenticatedUserAsync(ctx, db);
    if (actor is null) return Results.Unauthorized();
    if (req is null || req.State is not ("open" or "closed"))
        return Results.BadRequest(new { error = "State must be 'open' or 'closed'." });

    var (entity, _, canWrite, _, err) = await ResolveAuthorizedRepoAsync(ctx, owner, repo, "issue:write", db, ct);
    if (err is not null) return err;

    var bundle = await db.GetIssueDetailBundleAsync(entity!.Id, index, ct);
    if (bundle is null) return Results.NotFound(new { error = "Issue not found." });

    if (!canWrite && bundle.Issue.AuthorId != actor.Id)
        return Results.StatusCode(StatusCodes.Status403Forbidden);

    var outcome = await db.UpdateIssueStateAsync(entity.Id, index, req.State, req.ExpectedRowVersion, ct);
    if (outcome == GiteaDbRepository.StateUpdateOutcome.NotFound)
        return Results.NotFound(new { error = "Issue not found." });
    if (outcome == GiteaDbRepository.StateUpdateOutcome.Conflict)
        return Results.Conflict(new { error = "Concurrency conflict detected (RowVersion mismatch)." });

    await db.AppendAuditLogAsync(actor.Id, actor.Username, $"issue.{req.State}", $"Changed issue #{index} state to {req.State} in {entity.OwnerUsername}/{entity.Name}", ct);
    var refreshed = await db.GetIssueDetailBundleAsync(entity.Id, index, ct);
    webhooks.EnqueueEvent(entity.Id, "issues", new { action = req.State, issue = refreshed?.Issue });
    return Results.Ok(refreshed);
}).RequireAuthorization("Authenticated");

app.MapPost("/api/v1/repos/{owner}/{repo}/issues/{index:long}/comments", async (HttpContext ctx, string owner, string repo, long index, CreateCommentRequest? req, GiteaDbRepository db, CancellationToken ct) =>
{
    var actor = await GetAuthenticatedUserAsync(ctx, db);
    if (actor is null) return Results.Unauthorized();
    if (req is null || string.IsNullOrWhiteSpace(req.Body))
        return Results.BadRequest(new { error = "Comment body cannot be empty." });
    if (req.Body.Length > 65536)
        return Results.BadRequest(new { error = "Comment body cannot exceed 65536 characters." });

    var (entity, _, _, _, err) = await ResolveAuthorizedRepoAsync(ctx, owner, repo, "issue:write", db, ct);
    if (err is not null) return err;

    var bundle = await db.GetIssueDetailBundleAsync(entity!.Id, index, ct);
    if (bundle is null) return Results.NotFound(new { error = "Issue not found." });

    var comment = await db.AddIssueCommentAsync(entity.Id, bundle.Issue.Id, actor.Id, req.Body.Trim(), ct);
    return Results.Created($"/api/v1/repos/{owner}/{repo}/issues/{index}", comment);
}).RequireAuthorization("Authenticated");

// ==================== PULL REQUESTS & CRASH-SAFE 3-PHASE ATOMIC GIT MERGE API ====================

app.MapGet("/api/v1/repos/{owner}/{repo}/pulls", async (HttpContext ctx, string owner, string repo, string? state, int? page, int? pageSize, GiteaDbRepository db, CancellationToken ct) =>
{
    var (entity, _, _, _, err) = await ResolveAuthorizedRepoAsync(ctx, owner, repo, "repo:read", db, ct);
    if (err is not null) return err;

    var pulls = await db.ListPullRequestsAsync(entity!.Id, state, page ?? 1, pageSize ?? 25, ct);
    return Results.Ok(pulls);
});

app.MapPost("/api/v1/repos/{owner}/{repo}/pulls", async (
    HttpContext ctx, string owner, string repo, CreatePullRequest? req,
    GiteaDbRepository db, GitEngineService git, WebhookDeliveryService webhooks, CiPipelineRunnerService ciRunner, CancellationToken ct) =>
{
    var actor = await GetAuthenticatedUserAsync(ctx, db);
    if (actor is null) return Results.Unauthorized();
    if (req is null || string.IsNullOrWhiteSpace(req.Title))
        return Results.BadRequest(new { error = "Pull request title is required." });

    string title = req.Title.Trim();
    string body = (req.Body ?? string.Empty).Trim();
    string sourceBranch = (req.SourceBranch ?? string.Empty).Trim();
    string targetBranch = (req.TargetBranch ?? "main").Trim();

    if (title.Length > 255) return Results.BadRequest(new { error = "Pull request title cannot exceed 255 characters." });
    if (body.Length > 65536) return Results.BadRequest(new { error = "Pull request body cannot exceed 65536 characters." });
    if (!GitEngineService.ValidateRefFormat(sourceBranch) || !GitEngineService.ValidateRefFormat(targetBranch))
        return Results.BadRequest(new { error = "Invalid branch names." });
    if (string.Equals(sourceBranch, targetBranch, StringComparison.Ordinal))
        return Results.BadRequest(new { error = "Source and target branches must be different." });

    var (entity, _, canWrite, _, err) = await ResolveAuthorizedRepoAsync(ctx, owner, repo, "pull:write", db, ct);
    if (err is not null) return err;
    if (!canWrite) return Results.StatusCode(StatusCodes.Status403Forbidden);

    // Verify both branches exist in the bare Git repository before creating the PR
    if (!await git.BranchExistsAsync(entity!.OwnerUsername, entity.Name, sourceBranch, ct))
        return Results.BadRequest(new { error = $"Source branch '{sourceBranch}' does not exist." });
    if (!await git.BranchExistsAsync(entity.OwnerUsername, entity.Name, targetBranch, ct))
        return Results.BadRequest(new { error = $"Target branch '{targetBranch}' does not exist." });

    var pr = await db.CreatePullRequestAsync(entity.Id, actor.Id, title, body, sourceBranch, targetBranch, ct);
    await db.AppendAuditLogAsync(actor.Id, actor.Username, "pull.create", $"Opened PR #{pr.PullIndex} ({sourceBranch} -> {targetBranch}) in {entity.OwnerUsername}/{entity.Name}", ct);
    webhooks.EnqueueEvent(entity.Id, "pull_request", new { action = "opened", pullRequest = pr });
    await ciRunner.EnqueueCiRunAsync(entity.Id, "Per's Git Production CI/CD Pipeline", "pull_request", sourceBranch, null, actor.Id, ct);
    return Results.Created($"/api/v1/repos/{owner}/{repo}/pulls/{pr.PullIndex}", pr);
}).RequireAuthorization("Authenticated");

app.MapGet("/api/v1/repos/{owner}/{repo}/pulls/{index:long}", async (HttpContext ctx, string owner, string repo, long index, GiteaDbRepository db, GitEngineService git, CancellationToken ct) =>
{
    var (entity, _, _, _, err) = await ResolveAuthorizedRepoAsync(ctx, owner, repo, "repo:read", db, ct);
    if (err is not null) return err;

    var pr = await db.GetPullRequestAsync(entity!.Id, index, ct);
    if (pr is null) return Results.NotFound(new { error = "Pull request not found." });

    var diff = await git.CompareRefsAsync(entity.OwnerUsername, entity.Name, pr.TargetBranch, pr.SourceBranch, ct);
    return Results.Ok(new { pullRequest = pr, diff });
});

app.MapPatch("/api/v1/repos/{owner}/{repo}/pulls/{index:long}/state", async (
    HttpContext ctx, string owner, string repo, long index, UpdatePullStateRequest? req,
    GiteaDbRepository db, WebhookDeliveryService webhooks, CancellationToken ct) =>
{
    var actor = await GetAuthenticatedUserAsync(ctx, db);
    if (actor is null) return Results.Unauthorized();
    if (req is null || req.State is not ("open" or "closed"))
        return Results.BadRequest(new { error = "State must be 'open' or 'closed'." });

    var (entity, _, canWrite, _, err) = await ResolveAuthorizedRepoAsync(ctx, owner, repo, "pull:write", db, ct);
    if (err is not null) return err;

    var pr = await db.GetPullRequestAsync(entity!.Id, index, ct);
    if (pr is null) return Results.NotFound(new { error = "Pull request not found." });
    if (!canWrite && pr.AuthorId != actor.Id)
        return Results.StatusCode(StatusCodes.Status403Forbidden);

    var outcome = await db.UpdatePullRequestStateAsync(entity.Id, index, req.State, req.ExpectedRowVersion, ct);
    if (outcome == GiteaDbRepository.StateUpdateOutcome.NotFound)
        return Results.NotFound(new { error = "Pull request not found." });
    if (outcome == GiteaDbRepository.StateUpdateOutcome.Conflict)
        return Results.Conflict(new { error = "Cannot change state of a merged PR or RowVersion changed." });

    await db.AppendAuditLogAsync(actor.Id, actor.Username, $"pull.{req.State}", $"Changed PR #{index} state to {req.State} in {entity.OwnerUsername}/{entity.Name}", ct);
    var updated = await db.GetPullRequestAsync(entity.Id, index, ct);
    webhooks.EnqueueEvent(entity.Id, "pull_request", new { action = req.State, pullRequest = updated });
    return Results.Ok(updated);
}).RequireAuthorization("Authenticated");

app.MapPost("/api/v1/repos/{owner}/{repo}/pulls/{index:long}/merge", async (
    HttpContext ctx, string owner, string repo, long index, MergePullRequest? req,
    GiteaDbRepository db, GitEngineService git, WebhookDeliveryService webhooks, CiPipelineRunnerService ciRunner, CancellationToken ct) =>
{
    var actor = await GetAuthenticatedUserAsync(ctx, db);
    if (actor is null) return Results.Unauthorized();
    if (req is null) return Results.BadRequest(new { error = "Request body is required." });

    string strategy = (req.MergeStrategy ?? "merge").Trim().ToLowerInvariant();
    if (strategy is not ("merge" or "squash" or "fast-forward"))
        return Results.BadRequest(new { error = "MergeStrategy must be 'merge', 'squash', or 'fast-forward'." });

    var (entity, _, canWrite, _, err) = await ResolveAuthorizedRepoAsync(ctx, owner, repo, "pull:write", db, ct);
    if (err is not null) return err;
    if (!canWrite) return Results.StatusCode(StatusCodes.Status403Forbidden);

    var pr = await db.GetPullRequestAsync(entity!.Id, index, ct);
    if (pr is null) return Results.NotFound(new { error = "Pull request not found." });
    if (pr.IsMerged || pr.State != "open")
        return Results.Conflict(new { error = $"Pull request is currently '{pr.State}' and cannot be merged." });

    // PHASE 1: Atomic DB CAS transition from 'open' -> 'merging'
    bool claimed = await db.TryAcquirePullRequestMergeLockAsync(entity.Id, index, req.ExpectedRowVersion, ct);
    if (!claimed)
        return Results.Conflict(new { error = "Concurrency conflict: Pull request RowVersion changed or another merge is in progress." });

    bool gitRefUpdated = false;
    string? mergedSha = null;

    try
    {
        string commitMsg = string.IsNullOrWhiteSpace(req.CustomCommitMessage)
            ? $"Merge pull request #{pr.PullIndex} from {pr.SourceBranch} into {pr.TargetBranch}: {pr.Title}"
            : req.CustomCommitMessage.Trim();

        // PHASE 2: Execute bare repository plumbing merge (git merge-tree --write-tree + commit-tree + update-ref)
        var (success, sha, userFacingErr) = await git.MergeBranchesAsync(
            entity.OwnerUsername,
            entity.Name,
            pr.SourceBranch,
            pr.TargetBranch,
            strategy,
            commitMsg,
            actor.FullName,
            actor.Email,
            ct);

        if (!success || string.IsNullOrWhiteSpace(sha))
        {
            await db.RollbackPullRequestMergeLockAsync(entity.Id, index, CancellationToken.None);
            return Results.UnprocessableEntity(new { error = userFacingErr });
        }

        // Git update-ref succeeded! From this point onward, NEVER rollback DB state to 'open'.
        gitRefUpdated = true;
        mergedSha = sha;
    }
    catch
    {
        if (!gitRefUpdated)
        {
            await db.RollbackPullRequestMergeLockAsync(entity.Id, index, CancellationToken.None);
        }
        throw;
    }

    // PHASE 3: Idempotent Finalization ('merging' -> 'merged') with retry; if transient DB failure occurs,
    // PR stays in 'merging' and MergeReconcilerHostedService will finalize it via merge-base --is-ancestor.
    bool finalized = await db.FinalizePullRequestMergedAsync(entity.Id, index, mergedSha!, actor.Id, CancellationToken.None);
    if (!finalized)
    {
        return Results.StatusCode(StatusCodes.Status503ServiceUnavailable);
    }

    await db.AppendAuditLogAsync(actor.Id, actor.Username, "pull.merge", $"Merged PR #{index} into {pr.TargetBranch} ({mergedSha![..7]}) in {entity.OwnerUsername}/{entity.Name}", ct);
    var updatedPr = await db.GetPullRequestAsync(entity.Id, index, ct);
    webhooks.EnqueueEvent(entity.Id, "pull_request", new { action = "merged", pullRequest = updatedPr });
    await ciRunner.EnqueueCiRunAsync(entity.Id, "Per's Git Production CI/CD Pipeline", "push", pr.TargetBranch, mergedSha, actor.Id, ct);
    return Results.Ok(updatedPr);
}).RequireAuthorization("Authenticated");

// ==================== CI/CD ACTIONS PIPELINE API (ZERO N+1, ATOMIC SEQUENCE & CAS RUNNER) ====================

app.MapGet("/api/v1/repos/{owner}/{repo}/actions/runs", async (
    HttpContext ctx, string owner, string repo, int? page, int? pageSize, GiteaDbRepository db, CancellationToken ct) =>
{
    var (entity, _, _, _, err) = await ResolveAuthorizedRepoAsync(ctx, owner, repo, "repo:read", db, ct);
    if (err is not null) return err;

    var runs = await db.ListCiWorkflowRunsAsync(entity!.Id, page ?? 1, pageSize ?? 25, ct);
    return Results.Ok(runs);
});

app.MapGet("/api/v1/repos/{owner}/{repo}/actions/runs/{runIndex:long}", async (
    HttpContext ctx, string owner, string repo, long runIndex, GiteaDbRepository db, CancellationToken ct) =>
{
    var (entity, _, _, _, err) = await ResolveAuthorizedRepoAsync(ctx, owner, repo, "repo:read", db, ct);
    if (err is not null) return err;

    var run = await db.GetCiWorkflowRunAsync(entity!.Id, runIndex, ct);
    if (run is null) return Results.NotFound(new { error = "CI/CD workflow run not found." });
    return Results.Ok(run);
});

app.MapPost("/api/v1/repos/{owner}/{repo}/actions/runs", async (
    HttpContext ctx, string owner, string repo, TriggerCiWorkflowRequest? req,
    GiteaDbRepository db, GitEngineService git, CiPipelineRunnerService ciRunner, CancellationToken ct) =>
{
    var actor = await GetAuthenticatedUserAsync(ctx, db);
    if (actor is null) return Results.Unauthorized();

    var (entity, _, canWrite, _, err) = await ResolveAuthorizedRepoAsync(ctx, owner, repo, "repo:write", db, ct);
    if (err is not null) return err;
    if (!canWrite) return Results.StatusCode(StatusCodes.Status403Forbidden);

    if (!SecurityService.ValidateWorkflowDispatchRequest(
            req,
            entity!.DefaultBranch,
            out string workflowName,
            out string branch,
            out string validationError))
    {
        return Results.BadRequest(new { error = validationError });
    }

    if (!await git.BranchExistsAsync(entity.OwnerUsername, entity.Name, branch, ct))
    {
        return Results.BadRequest(new { error = $"Target branch '{branch}' does not exist in repository." });
    }

    var commits = await git.ListCommitsAsync(entity.OwnerUsername, entity.Name, branch, 1, ct);
    string headSha = commits.Count > 0 ? commits[0].Sha : "0000000000000000000000000000000000000000";

    var queuedRun = await db.CreateCiWorkflowRunAsync(entity.Id, workflowName, "workflow_dispatch", branch, headSha, actor.Id, ct);
    await ciRunner.ExecutePipelineNowAsync(entity.Id, queuedRun.Id, ct);

    var completedRun = await db.GetCiWorkflowRunAsync(entity.Id, queuedRun.RunIndex, ct);
    await db.AppendAuditLogAsync(actor.Id, actor.Username, "ci.workflow.dispatch", $"Triggered CI/CD workflow #{queuedRun.RunIndex} on {entity.OwnerUsername}/{entity.Name}@{branch}", ct);
    return Results.Created($"/api/v1/repos/{owner}/{repo}/actions/runs/{queuedRun.RunIndex}", completedRun ?? queuedRun);
}).RequireAuthorization("Authenticated");

// ==================== WEBHOOKS API (OWNER / ADMIN ONLY, SECRET MANDATORY OR AUTO-GENERATED & REDACTED) ====================

app.MapGet("/api/v1/repos/{owner}/{repo}/webhooks", async (HttpContext ctx, string owner, string repo, GiteaDbRepository db, CancellationToken ct) =>
{
    var actor = await GetAuthenticatedUserAsync(ctx, db);
    if (actor is null) return Results.Unauthorized();

    var (entity, _, _, canAdmin, err) = await ResolveAuthorizedRepoAsync(ctx, owner, repo, "repo:read", db, ct);
    if (err is not null) return err;
    if (!canAdmin) return Results.StatusCode(StatusCodes.Status403Forbidden);

    var hooks = await db.ListWebhooksAsync(entity!.Id, ct);
    return Results.Ok(hooks.Select(h => h.ToDto()));
}).RequireAuthorization("Authenticated");

app.MapPost("/api/v1/repos/{owner}/{repo}/webhooks", async (HttpContext ctx, string owner, string repo, CreateWebhookRequest? req, GiteaDbRepository db, CancellationToken ct) =>
{
    var actor = await GetAuthenticatedUserAsync(ctx, db);
    if (actor is null) return Results.Unauthorized();
    if (req is null) return Results.BadRequest(new { error = "Request body is required." });

    if (!WebhookDeliveryService.IsSafeExternalUrl(req.TargetUrl, out string ssrfError))
        return Results.BadRequest(new { error = ssrfError });

    var (entity, _, _, canAdmin, err) = await ResolveAuthorizedRepoAsync(ctx, owner, repo, "repo:write", db, ct);
    if (err is not null) return err;
    if (!canAdmin) return Results.StatusCode(StatusCodes.Status403Forbidden);

    // Ensure every webhook has a strong HMAC secret (auto-generate if omitted)
    string secret = string.IsNullOrWhiteSpace(req.Secret)
        ? $"whsec_{Convert.ToHexString(RandomNumberGenerator.GetBytes(20)).ToLowerInvariant()}"
        : req.Secret.Trim();

    var hook = await db.CreateWebhookAsync(
        entity!.Id,
        req.TargetUrl!.Trim(),
        secret,
        string.IsNullOrWhiteSpace(req.EventsCsv) ? "push,issues,pull_request" : req.EventsCsv.Trim(),
        ct);

    await db.AppendAuditLogAsync(actor.Id, actor.Username, "webhook.create", $"Added webhook {hook.TargetUrl} on {entity.OwnerUsername}/{entity.Name}", ct);
    return Results.Created($"/api/v1/repos/{owner}/{repo}/webhooks", hook.ToDto());
}).RequireAuthorization("Authenticated");

app.MapGet("/api/v1/repos/{owner}/{repo}/webhooks/deliveries", async (HttpContext ctx, string owner, string repo, GiteaDbRepository db, CancellationToken ct) =>
{
    var actor = await GetAuthenticatedUserAsync(ctx, db);
    if (actor is null) return Results.Unauthorized();

    var (entity, _, _, canAdmin, err) = await ResolveAuthorizedRepoAsync(ctx, owner, repo, "repo:read", db, ct);
    if (err is not null) return err;
    if (!canAdmin) return Results.StatusCode(StatusCodes.Status403Forbidden);

    var deliveries = await db.ListWebhookDeliveriesAsync(entity!.Id, 25, ct);
    return Results.Ok(deliveries);
}).RequireAuthorization("Authenticated");

app.MapPost("/api/v1/repos/{owner}/{repo}/webhooks/test", async (HttpContext ctx, string owner, string repo, GiteaDbRepository db, WebhookDeliveryService webhooks, CancellationToken ct) =>
{
    var actor = await GetAuthenticatedUserAsync(ctx, db);
    if (actor is null) return Results.Unauthorized();

    var (entity, _, _, canAdmin, err) = await ResolveAuthorizedRepoAsync(ctx, owner, repo, "repo:write", db, ct);
    if (err is not null) return err;
    if (!canAdmin) return Results.StatusCode(StatusCodes.Status403Forbidden);

    webhooks.EnqueueEvent(entity!.Id, "push", new
    {
        testDelivery = true,
        repository = $"{entity.OwnerUsername}/{entity.Name}",
        triggeredBy = actor.Username,
        retryPolicy = "Exponential Backoff (1m, 2m, 4m, 8m, 16m)"
    });
    await db.AppendAuditLogAsync(actor.Id, actor.Username, "webhook.test", $"Triggered test webhook dispatch on {entity.OwnerUsername}/{entity.Name}", ct);
    return Results.Accepted($"/api/v1/repos/{owner}/{repo}/webhooks/deliveries", new { status = "enqueued", eventName = "push" });
}).RequireAuthorization("Authenticated");

// ==================== GIT SMART HTTP PROTOCOL ENDPOINTS (WITH RBAC, SCOPE CHECK & PUSH WEBHOOKS) ====================

async Task<(RepositoryEntity? Repo, UserEntity? Actor, IResult? ErrorResult)> AuthorizeGitSmartHttpAsync(
    HttpContext ctx, string owner, string repo, string? service, GiteaDbRepository db, CancellationToken ct)
{
    if (service is not ("git-upload-pack" or "git-receive-pack"))
        return (null, null, Results.BadRequest("Unsupported git service."));

    var entity = await db.GetRepositoryByOwnerAndNameAsync(owner, repo, ct);
    if (entity is null) return (null, null, Results.NotFound("Repository not found."));

    var actor = await GetAuthenticatedUserAsync(ctx, db);
    string requiredScope = service == "git-upload-pack" ? "repo:read" : "repo:write";
    if (actor is not null)
    {
        var scopeErr = CheckMustChangePasswordAndScope(ctx, actor, requiredScope);
        if (scopeErr is not null) return (null, actor, scopeErr);
    }

    var (canRead, canWrite, _) = await db.GetRepositoryPermissionsAsync(entity, actor?.Id, actor?.IsAdmin ?? false, ct);
    bool allowed = service == "git-upload-pack" ? canRead : canWrite;
    if (!allowed)
    {
        if (actor is null)
        {
            ctx.Response.Headers.WWWAuthenticate = "Basic realm=\"Gitea.Net Git Smart HTTP\"";
            return (null, null, Results.StatusCode(StatusCodes.Status401Unauthorized));
        }
        // Return 404 if user cannot even read the private repo, or 403 if they can read but cannot push
        return (null, actor, canRead ? Results.StatusCode(StatusCodes.Status403Forbidden) : Results.NotFound("Repository not found."));
    }

    return (entity, actor, null);
}

app.MapGet("/{owner}/{repo}.git/info/refs", async (HttpContext ctx, string owner, string repo, string? service, GiteaDbRepository db, GitEngineService git, CancellationToken ct) =>
{
    var (_, _, authError) = await AuthorizeGitSmartHttpAsync(ctx, owner, repo, service, db, ct);
    if (authError is not null) return authError;

    ctx.Response.StatusCode = StatusCodes.Status200OK;
    ctx.Response.ContentType = $"application/x-{service}-advertisement";
    ctx.Response.Headers.CacheControl = "no-cache";
    await git.WriteInfoRefsAsync(owner, repo, service!, ctx.Response.Body, ct);
    return Results.Empty;
});

app.MapPost("/{owner}/{repo}.git/{service:regex(^(git-upload-pack|git-receive-pack)$)}", async (
    HttpContext ctx, string owner, string repo, string service,
    GiteaDbRepository db, GitEngineService git, WebhookDeliveryService webhooks, CiPipelineRunnerService ciRunner, CancellationToken ct) =>
{
    // Disable Kestrel 30 MB request body size limit so large (>30MB) Git packfiles can be pushed
    var maxBodyFeature = ctx.Features.Get<IHttpMaxRequestBodySizeFeature>();
    if (maxBodyFeature is { IsReadOnly: false })
    {
        maxBodyFeature.MaxRequestBodySize = null;
    }

    var (entity, actor, authError) = await AuthorizeGitSmartHttpAsync(ctx, owner, repo, service, db, ct);
    if (authError is not null) return authError;

    ctx.Response.StatusCode = StatusCodes.Status200OK;
    ctx.Response.ContentType = $"application/x-{service}-result";
    ctx.Response.Headers.CacheControl = "no-cache";

    Stream inputStream = ctx.Request.Body;
    if (string.Equals(ctx.Request.Headers.ContentEncoding, "gzip", StringComparison.OrdinalIgnoreCase))
    {
        inputStream = new GZipStream(ctx.Request.Body, CompressionMode.Decompress);
    }

    await git.ExecuteStatelessRpcAsync(owner, repo, service, inputStream, ctx.Response.Body, ct);

    if (service == "git-receive-pack" && entity is not null)
    {
        await db.TouchRepositoryUpdatedAtAsync(entity.Id, CancellationToken.None);
        await db.AppendAuditLogAsync(actor?.Id, actor?.Username ?? "git-client", "git.push", $"Pushed packfile over Smart HTTP to {entity.OwnerUsername}/{entity.Name}", CancellationToken.None);
        webhooks.EnqueueEvent(entity.Id, "push", new { repository = entity.Name, owner = entity.OwnerUsername, pusher = actor?.Username ?? "git-client", transport = "smart-http" });
        await ciRunner.EnqueueCiRunAsync(entity.Id, "Per's Git Production CI/CD Pipeline", "push", entity.DefaultBranch, null, actor?.Id, CancellationToken.None);
    }

    return Results.Empty;
});


static bool IsInternalOrLoopback(System.Net.IPAddress? ip)
{
    if (ip is null || System.Net.IPAddress.IsLoopback(ip))
        return true;

    if (ip.IsIPv4MappedToIPv6)
        ip = ip.MapToIPv4();

    Span<byte> bytes = stackalloc byte[16];
    if (!ip.TryWriteBytes(bytes, out int written))
        return false;

    if (written == 4)
    {
        // 10.0.0.0/8
        if (bytes[0] == 10) return true;
        // 172.16.0.0/12 (Docker bridge networks 172.16.0.0 - 172.31.255.255)
        if (bytes[0] == 172 && (bytes[1] & 0xF0) == 0x10) return true;
        // 192.168.0.0/16
        if (bytes[0] == 192 && bytes[1] == 168) return true;
        // 127.0.0.0/8
        if (bytes[0] == 127) return true;
        return false;
    }

    if (written == 16)
    {
        // RFC 4193 ULA fc00::/7
        if ((bytes[0] & 0xFE) == 0xFC) return true;
        // Link-local fe80::/10
        if (bytes[0] == 0xFE && (bytes[1] & 0xC0) == 0x80) return true;
        return false;
    }

    return false;
}

app.Run();

public partial class Program { }
