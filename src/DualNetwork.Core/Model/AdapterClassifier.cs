namespace DualNetwork.Core.Model;

/// <summary>
/// Decides what kind of adapter something is from its name, description and the OS-reported
/// interface type. VPN and virtual adapters must never be picked as the Ethernet/Wi-Fi role.
/// </summary>
public static class AdapterClassifier
{
    // (keyword in name/description, product name). Order matters: first match wins.
    private static readonly (string Keyword, string Product)[] VpnKeywords =
    {
        ("tailscale", "Tailscale"),
        ("anyconnect", "Cisco AnyConnect"),
        ("cisco secure client", "Cisco Secure Client"),
        ("cisco systems vpn", "Cisco VPN"),
        ("pangp", "GlobalProtect"),
        ("globalprotect", "GlobalProtect"),
        ("palo alto", "GlobalProtect"),
        ("fortinet", "FortiClient"),
        ("forticlient", "FortiClient"),
        ("fortissl", "FortiClient"),
        ("wireguard", "WireGuard"),
        ("wintun", "WireGuard/Wintun"),
        ("openvpn", "OpenVPN"),
        ("tap-windows", "OpenVPN (TAP)"),
        ("tap-win32", "OpenVPN (TAP)"),
        ("zerotier", "ZeroTier"),
        ("nordlynx", "NordVPN"),
        ("nordvpn", "NordVPN"),
        ("expressvpn", "ExpressVPN"),
        ("protonvpn", "ProtonVPN"),
        ("juniper", "Juniper / Pulse"),
        ("pulse secure", "Pulse Secure"),
        ("ivanti", "Ivanti Secure Access"),
        ("check point", "Check Point VPN"),
        ("sonicwall", "SonicWall VPN"),
        ("f5 ", "F5 BIG-IP Edge"),
        ("zscaler", "Zscaler"),
        ("cloudflare warp", "Cloudflare WARP"),
        ("hamachi", "LogMeIn Hamachi"),
        ("wan miniport", "Windows VPN"),
        ("vpn", "VPN"),
    };

    private static readonly (string Keyword, string Product)[] VirtualKeywords =
    {
        ("vmware virtual ethernet adapter", "VMware (host)"),
        ("vmnet", "VMware (host)"),
        ("hyper-v virtual", "Hyper-V"),
        ("vethernet", "Hyper-V"),
        ("virtualbox", "VirtualBox"),
        ("vboxnet", "VirtualBox"),
        ("wsl", "WSL"),
        ("docker", "Docker"),
        ("npcap loopback", "Npcap loopback"),
        ("loopback", "Loopback"),
        ("bluetooth", "Bluetooth PAN"),
        ("kernel debug", "Kernel debugger"),
        ("microsoft wi-fi direct virtual", "Wi-Fi Direct"),
        ("teredo", "Teredo"),
        ("isatap", "ISATAP"),
        ("6to4", "6to4"),
    };

    /// <param name="osType">The OS interface type name (System.Net.NetworkInformation.NetworkInterfaceType), e.g. "Ethernet", "Wireless80211", "Ppp", "Tunnel".</param>
    public static (AdapterKind Kind, string? Product) Classify(string name, string description, string osType)
    {
        var text = $"{name} | {description}".ToLowerInvariant();
        var type = osType.ToLowerInvariant();

        if (type == "loopback") return (AdapterKind.Loopback, "Loopback");

        foreach (var (k, p) in VpnKeywords)
            if (text.Contains(k)) return (AdapterKind.Vpn, p);

        if (type is "ppp" or "tunnel") return (AdapterKind.Vpn, "VPN / tunnel");

        foreach (var (k, p) in VirtualKeywords)
            if (text.Contains(k)) return (AdapterKind.Virtual, p);

        if (type == "wireless80211") return (AdapterKind.WiFi, null);
        if (type.StartsWith("wwanpp") || text.Contains("mobile broadband") || text.Contains("cellular"))
            return (AdapterKind.Cellular, null);
        if (text.Contains("wi-fi") || text.Contains("wireless") || text.Contains("wlan") || text.Contains("802.11"))
            return (AdapterKind.WiFi, null);

        // Note: a VMware *guest* NIC ("vmxnet3 Ethernet Adapter", "Intel PRO/1000 MT") is a real Ethernet from the OS's point of view.
        if (type.Contains("ethernet") || type == "gigabitethernet" || type == "fastethernetfx" || type == "fastethernett")
            return (AdapterKind.Ethernet, null);

        return (AdapterKind.Other, null);
    }
}
