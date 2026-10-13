using System.Diagnostics;
using System.Net;
using System.Net.Sockets;

namespace DualNetwork.Windows;

/// <summary>
/// Sends one raw DNS query (SOA for a domain) straight to a specific DNS server over UDP.
/// Unlike the system resolver this proves that *this* server is reachable and answers, independent of NRPT
/// and of which other DNS servers Windows might try.
/// </summary>
public static class DnsProbe
{
    public enum Outcome { Answered, NameNotFound, ServerFailure, NoResponse }

    public sealed record Result(Outcome Outcome, long Milliseconds, string Detail);

    public static async Task<Result> QueryAsync(IPAddress server, string domain, int timeoutMs = 2500, CancellationToken ct = default)
    {
        var id = (ushort)Random.Shared.Next(1, ushort.MaxValue);
        var query = BuildQuery(id, domain, qtype: 6); // SOA
        using var udp = new UdpClient(server.AddressFamily);
        var sw = Stopwatch.StartNew();
        try
        {
            await udp.SendAsync(query, query.Length, new IPEndPoint(server, 53)).ConfigureAwait(false);
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(timeoutMs);
            while (true)
            {
                var r = await udp.ReceiveAsync(cts.Token).ConfigureAwait(false);
                var b = r.Buffer;
                if (b.Length < 12 || ((b[0] << 8) | b[1]) != id) continue;
                var rcode = b[3] & 0x0F;
                return rcode switch
                {
                    0 => new Result(Outcome.Answered, sw.ElapsedMilliseconds, $"{server} answered for {domain}"),
                    3 => new Result(Outcome.NameNotFound, sw.ElapsedMilliseconds, $"{server} answered, but does not know {domain}"),
                    _ => new Result(Outcome.ServerFailure, sw.ElapsedMilliseconds, $"{server} answered with error code {rcode}"),
                };
            }
        }
        catch (Exception ex) when (ex is OperationCanceledException or SocketException)
        {
            return new Result(Outcome.NoResponse, sw.ElapsedMilliseconds, $"{server} did not answer DNS queries");
        }
    }

    private static byte[] BuildQuery(ushort id, string domain, ushort qtype)
    {
        var bytes = new List<byte> { (byte)(id >> 8), (byte)id, 0x01, 0x00, 0, 1, 0, 0, 0, 0, 0, 0 }; // RD, 1 question
        foreach (var label in domain.Trim('.').Split('.', StringSplitOptions.RemoveEmptyEntries))
        {
            var l = System.Text.Encoding.ASCII.GetBytes(label);
            bytes.Add((byte)l.Length);
            bytes.AddRange(l);
        }
        bytes.Add(0);
        bytes.AddRange(new byte[] { (byte)(qtype >> 8), (byte)qtype, 0, 1 }); // QTYPE, QCLASS IN
        return bytes.ToArray();
    }
}
