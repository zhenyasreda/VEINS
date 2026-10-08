namespace VpnProvisioner.Core;

public sealed class ServerInfo
{
    public string OsName { get; set; } = "";
    public string Arch { get; set; } = "";
    public string DistroId { get; set; } = "";
    public bool HasXray { get; set; }
    public bool Port443Busy { get; set; }
    public string Port443Details { get; set; } = "";

    public string PackageManager => DistroId switch
    {
        "ubuntu" or "debian" => "apt",
        "centos" or "rhel" or "fedora" or "almalinux" or "rocky" => "dnf",
        "alpine" => "apk",
        _ => ""
    };

    public string XrayAsset => Arch switch
    {
        "x86_64" => "Xray-linux-64.zip",
        "aarch64" or "arm64" => "Xray-linux-arm64-v8a.zip",
        "armv7l" or "armv7" => "Xray-linux-arm32-v7a.zip",
        "i386" or "i686" => "Xray-linux-32.zip",
        _ => throw new ProvisionException($"Неизвестная архитектура сервера: {Arch}")
    };
}
