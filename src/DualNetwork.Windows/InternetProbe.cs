using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using DualNetwork.Core.Model;

namespace DualNetwork.Windows;

/// <summary>
/// Tests Internet reachability *through a specific adapter* by binding the socket to that adapter's
/// IPv4 address. Windows uses the strong host model, so a bound socket can only leave through the
/// adapter that owns the address. Results are cached briefly to keep monitoring cheap.
/// </summary>
public sealed class InternetProbe
{
    private static readonly IPEndPoint[] Targets =
    {
        new(IPAddress.Parse("1.1.1.1"), 443),
        new(IPAddress.Parse("8.8.8.8"), 443),
        new(IPAddress.Parse("9.9.9.9"), 443),
    };

    private readonly ConcurrentDictionary<string, (DateTime At, InternetStatus Status)> _cache = new();
    public TimeSpan CacheFor { get; set; } = TimeSpan.FromSeconds(45);
    public TimeSpan Timeout { get; set; } = TimeSpan.FromSeconds(2.5);

    public void Invalidate() => _cache.Clear();

    public async Task<InternetStatus> ProbeAsync(IPAddress localAddress, string cacheKey, CancellationToken ct = default)
    {
        if (_cache.TryGetValue(cacheKey, out var hit) && DateTime.UtcNow - hit.At < CacheFor) return hit.Status;

        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(Timeout);
        var attempts = Targets.Select(t => TryConnectAsync(localAddress, t, cts.Token)).ToList();
        var status = InternetStatus.NotAvailable;
        while (attempts.Count > 0)
        {
            var done = await Task.WhenAny(attempts);
            attempts.Remove(done);
            if (await done) { status = InternetStatus.Available; break; }
        }
        cts.Cancel();
        _cache[cacheKey] = (DateTime.UtcNow, status);
        return status;
    }

    private static async Task<bool> TryConnectAsync(IPAddress local, IPEndPoint target, CancellationToken ct)
    {
        try
        {
            using var s = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
            s.Bind(new IPEndPoint(local, 0));
            await s.ConnectAsync(target, ct);
            return true;
        }
        catch
        {
            return false;
        }
    }
}
