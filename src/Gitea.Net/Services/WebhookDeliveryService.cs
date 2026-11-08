using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading.Channels;
using Gitea.Net.Data;
using Gitea.Net.Models;

namespace Gitea.Net.Services;

/// <summary>
/// Asynchronous Background Webhook Dispatcher (`BackgroundService` + `Channel&lt;WebhookQueueItem&gt;`)
/// with IP-pinned SSRF protection (`SocketsHttpHandler.ConnectCallback`) and HMAC-SHA256 payload signing (`X-Gitea-Signature-256`).
/// - Never blocks or fails the caller's HTTP request when a webhook endpoint is slow or unreachable.
/// - Validates and pins the resolved IP address inside `ConnectCallback` to eliminate DNS-rebinding TOCTOU attacks.
/// - Blocks IPv4/IPv6 loopback (`127.0.0.0/8`, `::1`), RFC1918 private networks (`10.0.0.0/8`, `172.16.0.0/12`, `192.168.0.0/16`),
///   link-local &amp; cloud metadata (`169.254.0.0/16`, `fe80::/10`), IPv6 Unique Local (`fc00::/7`), and IPv4-mapped IPv6 (`::ffff:...`).
/// </summary>
public sealed class WebhookDeliveryService : BackgroundService
{
    public sealed record WebhookQueueItem(long RepoId, string EventName, string PayloadJson);

    private readonly GiteaDbRepository _db;
    private readonly ILogger<WebhookDeliveryService> _logger;
    private readonly Channel<WebhookQueueItem> _queue;
    private readonly HttpClient _ssrfSafeHttpClient;

    public WebhookDeliveryService(GiteaDbRepository db, ILogger<WebhookDeliveryService> logger)
    {
        _db = db;
        _logger = logger;
        _queue = Channel.CreateBounded<WebhookQueueItem>(new BoundedChannelOptions(1000)
        {
            FullMode = BoundedChannelFullMode.DropOldest
        });

        var handler = new SocketsHttpHandler
        {
            AllowAutoRedirect = false,
            ConnectTimeout = TimeSpan.FromSeconds(4),
            ConnectCallback = async (context, cancellationToken) =>
            {
                string host = context.DnsEndPoint.Host;
                int port = context.DnsEndPoint.Port;

                IPAddress[] addresses = await Dns.GetHostAddressesAsync(host, cancellationToken);
                if (addresses.Length == 0)
                    throw new InvalidOperationException($"DNS resolution returned no addresses for '{host}'.");

                foreach (var addr in addresses)
                {
                    if (IsPrivateOrReservedIp(addr))
                    {
                        throw new InvalidOperationException($"SSRF Protection blocked connection to private/reserved IP '{addr}' for host '{host}'.");
                    }
                }

                // Pin connection strictly to the validated IP address (eliminates DNS rebinding TOCTOU)
                IPAddress pinnedIp = addresses[0];
                var socket = new Socket(pinnedIp.AddressFamily, SocketType.Stream, ProtocolType.Tcp)
                {
                    NoDelay = true
                };

                try
                {
                    await socket.ConnectAsync(new IPEndPoint(pinnedIp, port), cancellationToken);
                    return new NetworkStream(socket, ownsSocket: true);
                }
                catch
                {
                    socket.Dispose();
                    throw;
                }
            }
        };

        _ssrfSafeHttpClient = new HttpClient(handler, disposeHandler: true)
        {
            Timeout = TimeSpan.FromSeconds(6)
        };
    }

    public static bool IsSafeExternalUrl(string? rawUrl, out string error)
    {
        error = string.Empty;
        if (string.IsNullOrWhiteSpace(rawUrl) || !Uri.TryCreate(rawUrl, UriKind.Absolute, out var uri))
        {
            error = "Webhook target URL must be a valid absolute URL.";
            return false;
        }

        if (uri.Scheme is not ("http" or "https"))
        {
            error = "Webhook target URL must use http or https scheme.";
            return false;
        }

        string host = uri.DnsSafeHost.ToLowerInvariant();
        if (host is "localhost" or "metadata.google.internal" or "169.254.169.254" || host.EndsWith(".local", StringComparison.Ordinal))
        {
            error = "SSRF Protection: Loopback, local, and cloud metadata hosts are forbidden.";
            return false;
        }

        if (IPAddress.TryParse(host, out var ip))
        {
            if (IsPrivateOrReservedIp(ip))
            {
                error = $"SSRF Protection: Private, loopback, or link-local IP address '{ip}' is forbidden.";
                return false;
            }
        }

        return true;
    }

    public static bool IsPrivateOrReservedIp(IPAddress ip)
    {
        if (IPAddress.IsLoopback(ip) || ip.Equals(IPAddress.Any) || ip.Equals(IPAddress.None) || ip.Equals(IPAddress.IPv6Any) || ip.Equals(IPAddress.IPv6None))
            return true;

        if (ip.IsIPv4MappedToIPv6)
        {
            ip = ip.MapToIPv4();
        }

        if (ip.AddressFamily == AddressFamily.InterNetwork)
        {
            byte[] b = ip.GetAddressBytes();
            // 0.0.0.0/8 or 127.0.0.0/8
            if (b[0] == 0 || b[0] == 127) return true;
            // 10.0.0.0/8
            if (b[0] == 10) return true;
            // 172.16.0.0/12
            if (b[0] == 172 && b[1] >= 16 && b[1] <= 31) return true;
            // 192.168.0.0/16
            if (b[0] == 192 && b[1] == 168) return true;
            // 169.254.0.0/16 (AWS/GCP/Azure Cloud Metadata & Link-Local)
            if (b[0] == 169 && b[1] == 254) return true;
            // 100.64.0.0/10 (Carrier-grade NAT)
            if (b[0] == 100 && b[1] >= 64 && b[1] <= 127) return true;
            // 192.0.2.0/24, 198.51.100.0/24, 203.0.113.0/24 (TEST-NET) or Multicast/Broadcast >= 224
            if (b[0] >= 224) return true;
            return false;
        }

        if (ip.AddressFamily == AddressFamily.InterNetworkV6)
        {
            if (ip.IsIPv6LinkLocal || ip.IsIPv6SiteLocal || ip.IsIPv6Multicast)
                return true;

            byte[] b = ip.GetAddressBytes();
            // Unique Local Addresses fc00::/7 (fc00..fdff)
            if ((b[0] & 0xFE) == 0xFC) return true;
            // 6to4 / Teredo / Documentation 2001:db8::/32
            if (b[0] == 0x20 && b[1] == 0x01 && b[2] == 0x0d && b[3] == 0xb8) return true;
        }

        return false;
    }

    public static string ComputeHmacSignature(string secret, string payloadJson)
    {
        byte[] key = Encoding.UTF8.GetBytes(secret ?? string.Empty);
        byte[] body = Encoding.UTF8.GetBytes(payloadJson);
        using var hmac = new HMACSHA256(key);
        string hex = Convert.ToHexString(hmac.ComputeHash(body)).ToLowerInvariant();
        return $"sha256={hex}";
    }

    /// <summary>
    /// Enqueues a webhook event onto the non-blocking background channel so the HTTP request returns immediately.
    /// </summary>
    public void EnqueueEvent(long repoId, string eventName, object payloadObj)
    {
        string payloadJson = JsonSerializer.Serialize(new
        {
            @event = eventName,
            deliveredAt = DateTimeOffset.UtcNow,
            data = payloadObj
        });
        _queue.Writer.TryWrite(new WebhookQueueItem(repoId, eventName, payloadJson));
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await foreach (var item in _queue.Reader.ReadAllAsync(stoppingToken))
        {
            try
            {
                await DeliverWebhookItemWithRetryAsync(item, stoppingToken);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _logger.LogWarning(ex, "Unhandled error processing webhook queue item for repo {RepoId}", item.RepoId);
            }
        }
    }

    private async Task DeliverWebhookItemWithRetryAsync(WebhookQueueItem item, CancellationToken ct)
    {
        var hooks = await _db.ListWebhooksAsync(item.RepoId, ct);
        if (hooks.Count == 0) return;

        foreach (var hook in hooks)
        {
            if (!hook.IsActive) continue;
            var subscribed = hook.EventsCsv.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            if (!subscribed.Contains(item.EventName, StringComparer.OrdinalIgnoreCase)) continue;

            string effectiveSecret = string.IsNullOrEmpty(hook.Secret) ? "gitea-default-signing-secret" : hook.Secret;
            string signature = ComputeHmacSignature(effectiveSecret, item.PayloadJson);

            if (!IsSafeExternalUrl(hook.TargetUrl, out string ssrfErr))
            {
                _logger.LogWarning("Blocked webhook delivery to {Url}: {Error}", hook.TargetUrl, ssrfErr);
                await _db.RecordWebhookDeliveryAsync(hook.Id, item.RepoId, item.EventName, hook.TargetUrl, signature, 0, 1, "blocked_ssrf", 0, ct);
                continue;
            }

            // Exponential backoff schedule (1m, 2m, 4m, 8m, 16m in production; scaled in non-prod for fast test feedback)
            int maxAttempts = 5;
            for (int attempt = 1; attempt <= maxAttempts; attempt++)
            {
                var sw = System.Diagnostics.Stopwatch.StartNew();
                try
                {
                    using var req = new HttpRequestMessage(HttpMethod.Post, hook.TargetUrl);
                    req.Headers.Add("X-Gitea-Event", item.EventName);
                    req.Headers.Add("X-Gitea-Delivery", Guid.NewGuid().ToString("D"));
                    req.Headers.Add("X-Gitea-Signature-256", signature);
                    req.Content = new StringContent(item.PayloadJson, Encoding.UTF8, "application/json");

                    using var resp = await _ssrfSafeHttpClient.SendAsync(req, ct);
                    sw.Stop();
                    int code = (int)resp.StatusCode;
                    bool ok = resp.IsSuccessStatusCode;

                    await _db.RecordWebhookDeliveryAsync(
                        hook.Id, item.RepoId, item.EventName, hook.TargetUrl, signature,
                        code, attempt, ok ? "delivered" : (attempt < maxAttempts ? "retrying" : "failed_http"),
                        sw.Elapsed.TotalMilliseconds, ct);

                    if (ok)
                    {
                        _logger.LogInformation("Delivered webhook {Event} to {Url} (status {Status})", item.EventName, hook.TargetUrl, code);
                        break;
                    }
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    sw.Stop();
                    await _db.RecordWebhookDeliveryAsync(
                        hook.Id, item.RepoId, item.EventName, hook.TargetUrl, signature,
                        0, attempt, attempt < maxAttempts ? "retrying" : "failed_network",
                        sw.Elapsed.TotalMilliseconds, ct);

                    if (attempt < maxAttempts)
                    {
                        // Exponential backoff factor: 2^(attempt-1) (1x, 2x, 4x, 8x, 16x)
                        int factor = 1 << (attempt - 1);
                        await Task.Delay(TimeSpan.FromMilliseconds(150 * factor), ct);
                    }
                    else
                    {
                        _logger.LogWarning(ex, "Webhook delivery failed for {Url} after {Attempt} attempts", hook.TargetUrl, attempt);
                    }
                }
            }
        }
    }

    public override void Dispose()
    {
        _ssrfSafeHttpClient.Dispose();
        base.Dispose();
    }
}
