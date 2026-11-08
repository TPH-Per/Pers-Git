using System.Collections.Concurrent;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.RegularExpressions;
using Gitea.Net.Data;
using Gitea.Net.Models;
using Microsoft.AspNetCore.Authentication;
using Microsoft.Extensions.Options;

namespace Gitea.Net.Security;

public sealed partial class SecurityService
{
    public const int Pbkdf2Iterations = 600_000;
    public const int LegacyPbkdf2Iterations = 100_000;
    public const string AllScopesCsv = "repo:read,repo:write,issue:write,pull:write,admin";

    private readonly byte[] _secretBytes;
    private readonly string _dummyHash;
    private readonly string _dummySalt;

    private sealed record CachedAuthEntry(long UserId, DateTimeOffset ExpiresAt);
    private sealed record BruteForceEntry(int FailedCount, DateTimeOffset LockedUntil);

    private readonly ConcurrentDictionary<string, CachedAuthEntry> _basicAuthCache = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, BruteForceEntry> _bruteForceTracker = new(StringComparer.OrdinalIgnoreCase);

    // \z matches the absolute end of the string (never before a trailing \n)
    [GeneratedRegex(@"\A[a-zA-Z0-9][a-zA-Z0-9_\-\.]{1,62}\z", RegexOptions.Compiled)]
    private static partial Regex SafeIdentifierRegex();

    [GeneratedRegex(@"\A[^@\s]{1,64}@[^@\s]+\.[^@\s]{2,}\z", RegexOptions.Compiled)]
    private static partial Regex EmailRegex();

    private static readonly HashSet<string> WindowsReservedNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "CON", "PRN", "AUX", "NUL",
        "COM1", "COM2", "COM3", "COM4", "COM5", "COM6", "COM7", "COM8", "COM9",
        "LPT1", "LPT2", "LPT3", "LPT4", "LPT5", "LPT6", "LPT7", "LPT8", "LPT9"
    };

    public static readonly HashSet<string> AllowedPatScopes = new(StringComparer.OrdinalIgnoreCase)
    {
        "repo:read", "repo:write", "issue:write", "pull:write", "admin",
        "repo", "issue", "pull" // Shorthand aliases expanded automatically
    };

    public SecurityService(IConfiguration configuration, IHostEnvironment environment)
    {
        string? secret = Environment.GetEnvironmentVariable("GITEA_NET_TOKEN_SECRET")
                         ?? configuration["Security:TokenSecret"];

        if (string.IsNullOrWhiteSpace(secret) || Encoding.UTF8.GetByteCount(secret) < 32)
        {
            if (environment.IsProduction())
            {
                throw new InvalidOperationException(
                    "FATAL: Security:TokenSecret (or env GITEA_NET_TOKEN_SECRET) must be configured with at least 32 bytes in Production.");
            }
            _secretBytes = RandomNumberGenerator.GetBytes(32);
        }
        else
        {
            _secretBytes = Encoding.UTF8.GetBytes(secret);
        }

        var (dh, ds) = HashPassword("DummyTimingProtectionPassword!2026");
        _dummyHash = dh;
        _dummySalt = ds;
    }

    public static bool IsSafeIdentifier(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return false;
        if (value.EndsWith('.') || value.EndsWith(' ')) return false;
        // Block repository or user names ending in .git to prevent route collision with /{owner}/{repo}.git
        if (value.EndsWith(".git", StringComparison.OrdinalIgnoreCase)) return false;
        if (value.Contains("..", StringComparison.Ordinal) ||
            value.Contains('/') ||
            value.Contains('\\') ||
            value.Contains('\0'))
        {
            return false;
        }

        string baseName = Path.GetFileNameWithoutExtension(value);
        if (WindowsReservedNames.Contains(value) || WindowsReservedNames.Contains(baseName))
            return false;

        return SafeIdentifierRegex().IsMatch(value);
    }

    public static bool IsValidEmail(string? email)
    {
        if (string.IsNullOrWhiteSpace(email) || email.Length > 160) return false;
        return EmailRegex().IsMatch(email);
    }

    /// <summary>
    /// Pure C# implementation of `git check-ref-format` rules (zero process spawns).
    /// </summary>
    public static bool IsSafeGitRefName(string? refName)
    {
        if (string.IsNullOrWhiteSpace(refName) || refName.Length > 120) return false;
        if (refName == "@" || refName.StartsWith('-') || refName.StartsWith('/') || refName.EndsWith('/') || refName.EndsWith('.'))
            return false;
        if (refName.Contains("..", StringComparison.Ordinal) ||
            refName.Contains("@{", StringComparison.Ordinal) ||
            refName.Contains("//", StringComparison.Ordinal) ||
            refName.Contains('\\') ||
            refName.EndsWith(".lock", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        foreach (char c in refName)
        {
            if (c <= 0x20 || c == 0x7F || c is '~' or '^' or ':' or '?' or '*' or '[' or '\0')
                return false;
        }

        var segments = refName.Split('/');
        foreach (string seg in segments)
        {
            if (string.IsNullOrEmpty(seg) || seg.StartsWith('.') || seg.EndsWith('.') ||
                string.Equals(seg, ".git", StringComparison.OrdinalIgnoreCase) ||
                seg.EndsWith(".lock", StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }
        }
        return true;
    }

    /// <summary>
    /// Segment-wise relative path validator that ALLOWS legitimate dot-files such as `.gitignore`,
    /// `.gitattributes`, `.github/workflows/ci.yml`, and filenames with `a..b.txt`,
    /// while strictly blocking `.`, `..`, `.git`, backslashes, control characters, and Windows reserved device names.
    /// </summary>
    public static bool IsSafeRelativeFilePath(string? relativePath)
    {
        if (string.IsNullOrWhiteSpace(relativePath) || relativePath.Length > 260) return false;
        if (relativePath.StartsWith('/') || relativePath.EndsWith('/') || relativePath.Contains('\\'))
            return false;

        foreach (char c in relativePath)
        {
            if (c < 0x20 || c == 0x7F) return false;
        }

        var segments = relativePath.Split('/');
        foreach (string seg in segments)
        {
            if (string.IsNullOrWhiteSpace(seg)) return false;
            if (seg == "." || seg == "..") return false;
            if (string.Equals(seg, ".git", StringComparison.OrdinalIgnoreCase)) return false;
            if (seg.EndsWith('.') || seg.EndsWith(' ')) return false;

            string baseNoExt = Path.GetFileNameWithoutExtension(seg);
            if (WindowsReservedNames.Contains(seg) || (!string.IsNullOrEmpty(baseNoExt) && WindowsReservedNames.Contains(baseNoExt)))
                return false;
        }

        return true;
    }

    public static string NormalizePatScopes(string? rawScopesCsv)
    {
        if (string.IsNullOrWhiteSpace(rawScopesCsv))
            return "repo:read,repo:write,issue:write,pull:write";

        var expanded = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (string token in rawScopesCsv.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            string lower = token.ToLowerInvariant();
            if (lower == "repo")
            {
                expanded.Add("repo:read");
                expanded.Add("repo:write");
            }
            else if (lower == "issue")
            {
                expanded.Add("repo:read");
                expanded.Add("issue:write");
            }
            else if (lower == "pull")
            {
                expanded.Add("repo:read");
                expanded.Add("pull:write");
            }
            else if (AllowedPatScopes.Contains(lower))
            {
                expanded.Add(lower);
            }
        }

        if (expanded.Count == 0)
            expanded.Add("repo:read");

        return string.Join(",", expanded.OrderBy(s => s, StringComparer.Ordinal));
    }

    public static bool HasRequiredScope(ClaimsPrincipal principal, string requiredScope)
    {
        string? scopesCsv = principal.FindFirstValue("gitea_scopes");
        if (string.IsNullOrEmpty(scopesCsv))
            return false;

        var scopes = scopesCsv.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (scopes.Contains("admin", StringComparer.OrdinalIgnoreCase))
            return true;
        if (requiredScope == "repo:read" && scopes.Contains("repo:write", StringComparer.OrdinalIgnoreCase))
            return true;

        return scopes.Contains(requiredScope, StringComparer.OrdinalIgnoreCase);
    }

    public (string Hash, string Salt) HashPassword(string password)
    {
        byte[] saltBytes = RandomNumberGenerator.GetBytes(16);
        byte[] hashBytes = Rfc2898DeriveBytes.Pbkdf2(
            Encoding.UTF8.GetBytes(password),
            saltBytes,
            iterations: Pbkdf2Iterations,
            HashAlgorithmName.SHA256,
            outputLength: 32);

        string saltB64 = Convert.ToBase64String(saltBytes);
        string versionedHash = $"v1${Pbkdf2Iterations}${Convert.ToBase64String(hashBytes)}";
        return (versionedHash, saltB64);
    }

    /// <summary>
    /// Verifies both versioned (`v1$600000$&lt;base64&gt;`) and legacy unprefixed (`100000` iteration) hashes.
    /// Sets `needsUpgrade = true` when a legacy hash matches so the caller can rehash to 600,000 iterations.
    /// </summary>
    public bool VerifyPassword(string? password, string? storedHash, string? storedSaltBase64, out bool needsUpgrade)
    {
        needsUpgrade = false;
        if (string.IsNullOrEmpty(password) || string.IsNullOrEmpty(storedHash) || string.IsNullOrEmpty(storedSaltBase64))
            return false;

        try
        {
            byte[] saltBytes = Convert.FromBase64String(storedSaltBase64);
            int iterations = LegacyPbkdf2Iterations;
            string rawHashB64 = storedHash;
            bool isVersioned = false;

            if (storedHash.StartsWith("v1$", StringComparison.Ordinal))
            {
                string[] parts = storedHash.Split('$');
                if (parts.Length == 3 && int.TryParse(parts[1], out int parsedIter) && parsedIter >= 10_000)
                {
                    iterations = parsedIter;
                    rawHashB64 = parts[2];
                    isVersioned = true;
                }
            }

            byte[] expectedHash = Convert.FromBase64String(rawHashB64);
            byte[] actualHash = Rfc2898DeriveBytes.Pbkdf2(
                Encoding.UTF8.GetBytes(password),
                saltBytes,
                iterations: iterations,
                HashAlgorithmName.SHA256,
                outputLength: expectedHash.Length);

            bool matched = CryptographicOperations.FixedTimeEquals(expectedHash, actualHash);
            if (matched && (!isVersioned || iterations < Pbkdf2Iterations))
            {
                needsUpgrade = true;
            }
            return matched;
        }
        catch (FormatException)
        {
            return false;
        }
    }

    public bool VerifyPassword(string? password, string? storedHash, string? storedSaltBase64)
        => VerifyPassword(password, storedHash, storedSaltBase64, out _);

    public void PerformDummyPasswordVerification(string? password)
    {
        _ = VerifyPassword(password ?? "dummy", _dummyHash, _dummySalt, out _);
    }

    // ==================== BRUTE-FORCE LOCKOUT & BASIC AUTH TTL CACHE ====================

    public bool IsLockedOut(string ipAddress, string username)
    {
        string key = $"{ipAddress}:{username.ToLowerInvariant()}";
        if (_bruteForceTracker.TryGetValue(key, out var entry))
        {
            if (entry.LockedUntil > DateTimeOffset.UtcNow)
                return true;
            _bruteForceTracker.TryRemove(KeyValuePair.Create(key, entry));
        }
        return false;
    }

    public void RecordFailedAuthAttempt(string ipAddress, string username)
    {
        string key = $"{ipAddress}:{username.ToLowerInvariant()}";
        _bruteForceTracker.AddOrUpdate(
            key,
            _ => new BruteForceEntry(1, DateTimeOffset.MinValue),
            (_, existing) =>
            {
                int nextCount = existing.FailedCount + 1;
                DateTimeOffset lockUntil = nextCount >= 10
                    ? DateTimeOffset.UtcNow.AddMinutes(5)
                    : DateTimeOffset.MinValue;
                return new BruteForceEntry(nextCount, lockUntil);
            });
    }

    public void ClearFailedAuthAttempts(string ipAddress, string username)
    {
        string key = $"{ipAddress}:{username.ToLowerInvariant()}";
        _bruteForceTracker.TryRemove(key, out _);
    }

    public bool TryGetCachedBasicAuthUserId(string username, string password, string currentPasswordHash, out long userId)
    {
        userId = 0;
        string cacheKey = ComputeHmac($"{username.ToLowerInvariant()}:{password}:{currentPasswordHash}");
        if (_basicAuthCache.TryGetValue(cacheKey, out var entry))
        {
            if (entry.ExpiresAt > DateTimeOffset.UtcNow)
            {
                userId = entry.UserId;
                return true;
            }
            _basicAuthCache.TryRemove(KeyValuePair.Create(cacheKey, entry));
        }
        return false;
    }

    public void CacheBasicAuthSuccess(string username, string password, string currentPasswordHash, long userId)
    {
        if (_basicAuthCache.Count > 2000)
        {
            _basicAuthCache.Clear();
        }
        string cacheKey = ComputeHmac($"{username.ToLowerInvariant()}:{password}:{currentPasswordHash}");
        _basicAuthCache[cacheKey] = new CachedAuthEntry(userId, DateTimeOffset.UtcNow.AddSeconds(60));
    }

    public (string Token, string Jti, DateTimeOffset ExpiresAt) IssueToken(UserEntity user, TimeSpan? lifetime = null)
    {
        var expiresAt = DateTimeOffset.UtcNow.Add(lifetime ?? TimeSpan.FromHours(12));
        string jti = Convert.ToHexString(RandomNumberGenerator.GetBytes(16)).ToLowerInvariant();
        var payloadObj = new TokenPayload(
            user.Id,
            user.Username,
            user.IsAdmin,
            jti,
            expiresAt.ToUnixTimeSeconds());

        string json = JsonSerializer.Serialize(payloadObj);
        string payloadB64 = Convert.ToBase64String(Encoding.UTF8.GetBytes(json));
        string sigB64 = ComputeHmac(payloadB64);
        return ($"{payloadB64}.{sigB64}", jti, expiresAt);
    }

    public TokenPayload? ValidateToken(string? token)
    {
        if (string.IsNullOrWhiteSpace(token)) return null;
        var parts = token.Split('.');
        if (parts.Length != 2) return null;

        string expectedSig = ComputeHmac(parts[0]);
        if (!CryptographicOperations.FixedTimeEquals(
                Encoding.UTF8.GetBytes(expectedSig),
                Encoding.UTF8.GetBytes(parts[1])))
        {
            return null;
        }

        try
        {
            string json = Encoding.UTF8.GetString(Convert.FromBase64String(parts[0]));
            var payload = JsonSerializer.Deserialize<TokenPayload>(json);
            if (payload is null || payload.ExpUnix < DateTimeOffset.UtcNow.ToUnixTimeSeconds())
                return null;
            return payload;
        }
        catch
        {
            return null;
        }
    }

    public static string ComputeSha256Hex(string input)
    {
        byte[] bytes = SHA256.HashData(Encoding.UTF8.GetBytes(input));
        return Convert.ToHexString(bytes).ToLowerInvariant();
    }

    private string ComputeHmac(string data)
    {
        using var hmac = new HMACSHA256(_secretBytes);
        return Convert.ToBase64String(hmac.ComputeHash(Encoding.UTF8.GetBytes(data)));
    }
}

public sealed record TokenPayload(long UserId, string Username, bool IsAdmin, string Jti, long ExpUnix);

/// <summary>
/// Reference-counted KeyedLockProvider that guarantees mutual exclusion and safe eviction:
/// - Decrements `RefCount` exactly ONCE on timeout or cancellation (never double-decrementing).
/// - Evicts from `_locks` using value-aware `KeyValuePair.Create(key, entry)` inside `_sync`
///   and verifies `entry == current` so an active lock holder is never evicted or replaced.
/// </summary>
public sealed class KeyedLockProvider
{
    private sealed class RefCountedLock
    {
        public SemaphoreSlim Semaphore { get; } = new(1, 1);
        public int RefCount;
    }

    private readonly ConcurrentDictionary<string, RefCountedLock> _locks = new(StringComparer.OrdinalIgnoreCase);
    private readonly object _sync = new();

    public int ActiveKeyCount => _locks.Count;

    public async Task<IDisposable> AcquireAsync(string key, TimeSpan timeout, CancellationToken ct = default)
    {
        RefCountedLock entry;
        lock (_sync)
        {
            while (true)
            {
                entry = _locks.GetOrAdd(key, _ => new RefCountedLock());
                if (_locks.TryGetValue(key, out var current) && ReferenceEquals(entry, current))
                {
                    entry.RefCount++;
                    break;
                }
            }
        }

        bool entered = false;
        try
        {
            entered = await entry.Semaphore.WaitAsync(timeout, ct).ConfigureAwait(false);
        }
        catch
        {
            DecrementAndEvict(key, entry);
            throw;
        }

        if (!entered)
        {
            DecrementAndEvict(key, entry);
            throw new TimeoutException($"Timed out acquiring exclusive lock for resource '{key}'.");
        }

        return new Releaser(this, key, entry);
    }

    private void Release(string key, RefCountedLock entry)
    {
        entry.Semaphore.Release();
        DecrementAndEvict(key, entry);
    }

    private void DecrementAndEvict(string key, RefCountedLock entry)
    {
        lock (_sync)
        {
            entry.RefCount--;
            if (entry.RefCount == 0)
            {
                ((ICollection<KeyValuePair<string, RefCountedLock>>)_locks).Remove(KeyValuePair.Create(key, entry));
            }
        }
    }

    private sealed class Releaser(KeyedLockProvider owner, string key, RefCountedLock entry) : IDisposable
    {
        private int _disposed;
        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) == 0)
            {
                owner.Release(key, entry);
            }
        }
    }
}

/// <summary>
/// Standard ASP.NET Core AuthenticationHandler supporting:
/// 1. HttpOnly SameSite=Strict session cookie (`gitea_session`)
/// 2. Authorization: Bearer &lt;token&gt; (Session Token or Personal Access Token `pat_...`)
/// 3. Authorization: Basic &lt;base64(username:password_or_pat)&gt; (With 60s HMAC cache &amp; brute-force lockout)
/// Attaches `gitea_scopes` and `auth_method` claims for granular scope and CSRF enforcement.
/// </summary>
public sealed class GiteaAuthHandler(
    IOptionsMonitor<AuthenticationSchemeOptions> options,
    ILoggerFactory logger,
    UrlEncoder encoder,
    SecurityService security,
    GiteaDbRepository db)
    : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
{
    public const string SchemeName = "GiteaAuth";
    public const string CookieName = "gitea_session";

    protected override async Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        CancellationToken ct = Context.RequestAborted;
        string remoteIp = Context.Connection.RemoteIpAddress?.ToString() ?? "local";

        string? authHeader = Request.Headers.Authorization;
        if (!string.IsNullOrWhiteSpace(authHeader))
        {
            if (authHeader.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase))
            {
                string rawToken = authHeader["Bearer ".Length..].Trim();
                return await AuthenticateTokenOrPatAsync(rawToken, "Bearer", ct);
            }

            if (authHeader.StartsWith("Basic ", StringComparison.OrdinalIgnoreCase))
            {
                string b64 = authHeader["Basic ".Length..].Trim();
                try
                {
                    string decoded = Encoding.UTF8.GetString(Convert.FromBase64String(b64));
                    int colonIdx = decoded.IndexOf(':');
                    if (colonIdx <= 0)
                        return AuthenticateResult.Fail("Malformed Basic Authorization header.");

                    string username = decoded[..colonIdx].Trim();
                    string secret = decoded[(colonIdx + 1)..];

                    if (security.IsLockedOut(remoteIp, username))
                        return AuthenticateResult.Fail("Too many failed authentication attempts. Account temporarily locked.");

                    if (secret.StartsWith("pat_", StringComparison.Ordinal))
                    {
                        var patResult = await AuthenticatePatAsync(secret, username, "BasicPat", ct);
                        if (!patResult.Succeeded)
                            security.RecordFailedAuthAttempt(remoteIp, username);
                        else
                            security.ClearFailedAuthAttempts(remoteIp, username);
                        return patResult;
                    }

                    var user = await db.GetUserByUsernameAsync(username, ct);
                    if (user is null)
                    {
                        security.PerformDummyPasswordVerification(secret);
                        security.RecordFailedAuthAttempt(remoteIp, username);
                        return AuthenticateResult.Fail("Invalid Basic credentials.");
                    }

                    // Check 60s HMAC-keyed cache to avoid 600k PBKDF2 DoS on multi-request Git HTTP operations
                    if (!security.TryGetCachedBasicAuthUserId(username, secret, user.PasswordHash, out _))
                    {
                        if (!security.VerifyPassword(secret, user.PasswordHash, user.PasswordSalt, out bool needsUpgrade))
                        {
                            security.RecordFailedAuthAttempt(remoteIp, username);
                            return AuthenticateResult.Fail("Invalid Basic credentials.");
                        }

                        if (needsUpgrade)
                        {
                            var (newHash, newSalt) = security.HashPassword(secret);
                            await db.UpdateUserPasswordAsync(user.Id, newHash, newSalt, user.MustChangePassword, ct);
                            user.PasswordHash = newHash;
                            user.PasswordSalt = newSalt;
                        }

                        security.CacheBasicAuthSuccess(username, secret, user.PasswordHash, user.Id);
                    }

                    security.ClearFailedAuthAttempts(remoteIp, username);
                    return BuildTicket(user, null, SecurityService.AllScopesCsv, "Basic");
                }
                catch
                {
                    return AuthenticateResult.Fail("Invalid Base64 in Basic Authorization header.");
                }
            }
        }

        if (Request.Cookies.TryGetValue(CookieName, out string? cookieToken) && !string.IsNullOrWhiteSpace(cookieToken))
        {
            return await AuthenticateTokenOrPatAsync(cookieToken, "Cookie", ct);
        }

        return AuthenticateResult.NoResult();
    }

    private async Task<AuthenticateResult> AuthenticateTokenOrPatAsync(string rawToken, string authMethod, CancellationToken ct)
    {
        if (rawToken.StartsWith("pat_", StringComparison.Ordinal))
        {
            return await AuthenticatePatAsync(rawToken, null, "Pat", ct);
        }

        var payload = security.ValidateToken(rawToken);
        if (payload is null)
            return AuthenticateResult.Fail("Invalid or expired session token.");

        if (await db.IsTokenRevokedAsync(payload.Jti, ct))
            return AuthenticateResult.Fail("Session token has been revoked.");

        var user = await db.GetUserByIdAsync(payload.UserId, ct);
        if (user is null)
            return AuthenticateResult.Fail("User account no longer exists.");

        return BuildTicket(user, payload.Jti, SecurityService.AllScopesCsv, authMethod);
    }

    private async Task<AuthenticateResult> AuthenticatePatAsync(string rawPat, string? expectedUsername, string authMethod, CancellationToken ct)
    {
        string tokenHash = SecurityService.ComputeSha256Hex(rawPat);
        var patAuth = await db.AuthenticatePatByHashAsync(tokenHash, ct);
        if (patAuth is null)
            return AuthenticateResult.Fail("Invalid, revoked, or expired Personal Access Token.");

        var (user, scopesCsv) = patAuth.Value;
        if (!string.IsNullOrEmpty(expectedUsername) &&
            !string.Equals(user.Username, expectedUsername, StringComparison.OrdinalIgnoreCase))
        {
            return AuthenticateResult.Fail("PAT owner does not match Basic username.");
        }

        return BuildTicket(user, null, scopesCsv, authMethod);
    }

    private AuthenticateResult BuildTicket(UserEntity user, string? jti, string scopesCsv, string authMethod)
    {
        var claims = new List<Claim>
        {
            new(ClaimTypes.NameIdentifier, user.Id.ToString()),
            new(ClaimTypes.Name, user.Username),
            new(ClaimTypes.Email, user.Email),
            new(ClaimTypes.Role, user.IsAdmin ? "Admin" : "User"),
            new("gitea_scopes", scopesCsv),
            new("auth_method", authMethod)
        };
        if (!string.IsNullOrEmpty(jti))
        {
            claims.Add(new Claim("jti", jti));
        }

        var identity = new ClaimsIdentity(claims, Scheme.Name);
        var principal = new ClaimsPrincipal(identity);
        return AuthenticateResult.Success(new AuthenticationTicket(principal, Scheme.Name));
    }
}
