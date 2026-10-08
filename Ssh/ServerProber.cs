using System.Text.RegularExpressions;
using VpnProvisioner.Core;

namespace VpnProvisioner.Ssh;

public static class ServerProber
{
    public static async Task<ServerInfo> ProbeAsync(SshSession ssh, IProgress<string> log)
    {
        var info = new ServerInfo();

        var os = await ssh.RunAsync("uname -s", requireRoot: false);
        info.OsName = os.Output.Trim();
        if (!info.OsName.Equals("Linux", StringComparison.OrdinalIgnoreCase))
        {
            throw new ProvisionException($"Сервер должен быть на Linux, получено: {info.OsName}");
        }

        var arch = await ssh.RunAsync("uname -m", requireRoot: false);
        info.Arch = arch.Output.Trim();

        var release = await ssh.RunAsync("cat /etc/os-release", requireRoot: false);
        var m = Regex.Match(release.Output, "(?m)^ID=\"?([A-Za-z0-9_.-]+)\"?");
        if (m.Success)
        {
            info.DistroId = m.Groups[1].Value.ToLowerInvariant();
        }

        log.Report($"Сервер: {info.OsName} {info.Arch}, дистрибутив: {(string.IsNullOrEmpty(info.DistroId) ? "не определён" : info.DistroId)}");

        info.HasXray = await ssh.CommandExistsAsync("xray");

        var busyLines = new List<string>();

        var listening = await ssh.RunAsync("ss -tlnp", requireRoot: false);
        if (!listening.Ok)
        {
            listening = await ssh.RunAsync("netstat -tlnp", requireRoot: false);
        }
        busyLines.AddRange(listening.Output
            .Split('\n')
            .Where(l => l.Contains(":443 ") || l.TrimEnd().EndsWith(":443")));

        var udpListening = await ssh.RunAsync("ss -ulnp", requireRoot: false);
        busyLines.AddRange(udpListening.Output
            .Split('\n')
            .Where(l => l.Contains(":443 ") || l.TrimEnd().EndsWith(":443")));

        if (busyLines.Count > 0)
        {
            info.Port443Busy = true;
            info.Port443Details = string.Join("\n", busyLines).Trim();
        }

        return info;
    }
}
