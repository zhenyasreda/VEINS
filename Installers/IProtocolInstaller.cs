using VpnProvisioner.Core;
using VpnProvisioner.Ssh;

namespace VpnProvisioner.Installers;

public interface IProtocolInstaller
{
    string Id { get; }
    string DisplayName { get; }
    Task<ProvisionResult> ProvisionAsync(SshSession ssh, ServerInfo info, ProvisionRequest req, IProgress<string> log, CancellationToken ct);
}
