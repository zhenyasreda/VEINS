using VpnProvisioner.Installers;

namespace VpnProvisioner.Core;

public sealed class ProvisionRequest
{
    public required string Host { get; init; }
    public int SshPort { get; init; } = 22;
    public string SshUser { get; init; } = "root";
    public string? KeyPath { get; init; }
    public string? KeyPassphrase { get; init; }
    public string? Password { get; init; }
    public string Sni { get; init; } = "www.google.com";
    public bool ForceReconfigure { get; init; }
    public string Protocol { get; init; } = ProtocolRegistry.XrayReality;
}
