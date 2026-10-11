using System.Net;
using System.Net.Sockets;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace DualNetwork.Core.Net;

/// <summary>An IPv4 network prefix such as 10.25.30.0/24. Always stored normalised (host bits cleared).</summary>
[JsonConverter(typeof(IPv4PrefixJsonConverter))]
public readonly record struct IPv4Prefix : IComparable<IPv4Prefix>
{
    public uint Network { get; }
    public int Length { get; }

    public IPv4Prefix(uint network, int length)
    {
        if (length is < 0 or > 32) throw new ArgumentOutOfRangeException(nameof(length));
        Length = length;
        Network = network & MaskFor(length);
    }

    public static IPv4Prefix Default { get; } = new(0, 0);

    public uint Mask => MaskFor(Length);
    public uint Broadcast => Network | ~Mask;
    public bool IsDefault => Length == 0;
    public bool IsHost => Length == 32;

    public static uint MaskFor(int length) => length == 0 ? 0u : uint.MaxValue << (32 - length);

    public static IPv4Prefix FromAddress(IPAddress address, int length) => new(IPv4.ToUInt(address), length);

    public static IPv4Prefix FromAddressAndMask(IPAddress address, IPAddress mask)
    {
        var m = IPv4.ToUInt(mask);
        var len = 0;
        while (len < 32 && (m & (0x80000000u >> len)) != 0) len++;
        if (MaskFor(len) != m) throw new FormatException($"Non-contiguous subnet mask {mask}");
        return new IPv4Prefix(IPv4.ToUInt(address), len);
    }

    public static IPv4Prefix Host(IPAddress address) => FromAddress(address, 32);

    public static IPv4Prefix Parse(string text)
    {
        if (!TryParse(text, out var p)) throw new FormatException($"'{text}' is not an IPv4 prefix (expected a.b.c.d/len)");
        return p;
    }

    public static bool TryParse(string? text, out IPv4Prefix prefix)
    {
        prefix = default;
        if (string.IsNullOrWhiteSpace(text)) return false;
        text = text.Trim();
        var slash = text.IndexOf('/');
        var addrPart = slash < 0 ? text : text[..slash];
        var len = 32;
        if (slash >= 0 && !int.TryParse(text[(slash + 1)..], out len)) return false;
        if (len is < 0 or > 32) return false;
        if (!IPAddress.TryParse(addrPart, out var addr) || addr.AddressFamily != AddressFamily.InterNetwork) return false;
        // Reject things like "10.1" that IPAddress.TryParse accepts as shorthand.
        if (addrPart.Count(c => c == '.') != 3) return false;
        prefix = FromAddress(addr, len);
        return true;
    }

    public bool Contains(uint address) => (address & Mask) == Network;
    public bool Contains(IPAddress address) => address.AddressFamily == AddressFamily.InterNetwork && Contains(IPv4.ToUInt(address));

    /// <summary>True when <paramref name="other"/> is equal to or inside this prefix.</summary>
    public bool Contains(IPv4Prefix other) => other.Length >= Length && Contains(other.Network);

    public bool Overlaps(IPv4Prefix other) => Contains(other) || other.Contains(this);

    public IPAddress NetworkAddress => IPv4.FromUInt(Network);
    public IPAddress MaskAddress => IPv4.FromUInt(Mask);

    /// <summary>First usable host (or the network address itself for /31 and /32).</summary>
    public IPAddress FirstHost => IPv4.FromUInt(Length >= 31 ? Network : Network + 1);

    public override string ToString() => $"{IPv4.FromUInt(Network)}/{Length}";

    public int CompareTo(IPv4Prefix other)
    {
        var c = Network.CompareTo(other.Network);
        return c != 0 ? c : Length.CompareTo(other.Length);
    }

    /// <summary>The three RFC 1918 private blocks.</summary>
    public static IReadOnlyList<IPv4Prefix> PrivateBlocks { get; } =
        new[] { Parse("10.0.0.0/8"), Parse("172.16.0.0/12"), Parse("192.168.0.0/16") };

    public static IPv4Prefix LinkLocal { get; } = Parse("169.254.0.0/16");
    public static IPv4Prefix Multicast { get; } = Parse("224.0.0.0/4");
    public static IPv4Prefix Loopback { get; } = Parse("127.0.0.0/8");
    public static IPv4Prefix LowerHalf { get; } = Parse("0.0.0.0/1");
    public static IPv4Prefix UpperHalf { get; } = Parse("128.0.0.0/1");
}

public static class IPv4
{
    public static IPAddress Any { get; } = IPAddress.Any;

    public static uint ToUInt(IPAddress address)
    {
        if (address.AddressFamily != AddressFamily.InterNetwork)
            throw new ArgumentException($"{address} is not an IPv4 address", nameof(address));
        var b = address.GetAddressBytes();
        return ((uint)b[0] << 24) | ((uint)b[1] << 16) | ((uint)b[2] << 8) | b[3];
    }

    public static IPAddress FromUInt(uint value) =>
        new(new[] { (byte)(value >> 24), (byte)(value >> 16), (byte)(value >> 8), (byte)value });

    public static bool IsV4(IPAddress? a) => a is not null && a.AddressFamily == AddressFamily.InterNetwork;
    public static bool IsUnspecified(IPAddress a) => IsV4(a) && ToUInt(a) == 0;
    public static bool IsLinkLocal(IPAddress a) => IsV4(a) && IPv4Prefix.LinkLocal.Contains(a);
    public static bool IsPrivate(IPAddress a) => IsV4(a) && IPv4Prefix.PrivateBlocks.Any(p => p.Contains(a));

    public static IPAddress Parse(string s)
    {
        if (!IPAddress.TryParse(s, out var a) || a.AddressFamily != AddressFamily.InterNetwork || s.Count(c => c == '.') != 3)
            throw new FormatException($"'{s}' is not an IPv4 address");
        return a;
    }
}

public sealed class IPv4PrefixJsonConverter : JsonConverter<IPv4Prefix>
{
    public override IPv4Prefix Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options) =>
        IPv4Prefix.Parse(reader.GetString() ?? throw new JsonException("null prefix"));

    public override void Write(Utf8JsonWriter writer, IPv4Prefix value, JsonSerializerOptions options) =>
        writer.WriteStringValue(value.ToString());
}

public sealed class IPAddressJsonConverter : JsonConverter<IPAddress>
{
    public override IPAddress? Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        var s = reader.GetString();
        return s is null ? null : IPAddress.Parse(s);
    }

    public override void Write(Utf8JsonWriter writer, IPAddress value, JsonSerializerOptions options) =>
        writer.WriteStringValue(value.ToString());
}
