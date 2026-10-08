using System.Text;
using Renci.SshNet;
using VpnProvisioner.Core;
using VpnProvisioner.Installers;
using VpnProvisioner.Ssh;

Console.OutputEncoding = Encoding.UTF8;

if (args.Length == 0)
{
    Console.WriteLine("Usage: Harness install-key <host> <port> <user> <pubkeyFile>");
    Console.WriteLine("       Harness cleanup     <host> <port> <user> <keyFile>");
    Console.WriteLine("       Harness provision   <host> <port> <user> <keyFile> [sni] [--force]");
    return 1;
}

var command = args[0];

switch (command)
{
    case "install-key":
    {
        var host = args[1];
        var port = int.Parse(args[2]);
        var user = args[3];
        var pubKeyFile = args[4];
        var password = Environment.GetEnvironmentVariable("SSH_PASS")
            ?? throw new InvalidOperationException("Set SSH_PASS env var");
        var pubKey = File.ReadAllText(pubKeyFile).Trim();

        var conn = new ConnectionInfo(host, port, user, new PasswordAuthenticationMethod(user, password));
        conn.Timeout = TimeSpan.FromSeconds(25);
        using var ssh = new SshClient(conn);
        ssh.HostKeyReceived += (_, e) =>
        {
            e.CanTrust = true;
            Console.WriteLine($"Host key: {e.FingerPrintSHA256}");
        };
        ssh.Connect();
        Console.WriteLine("Connected with password auth.");

        Run(ssh, "mkdir -p /root/.ssh && chmod 700 /root/.ssh");
        Run(ssh, $"touch /root/.ssh/authorized_keys && chmod 600 /root/.ssh/authorized_keys");
        var add = Run(ssh, $"grep -qF '{pubKey}' /root/.ssh/authorized_keys || printf '%s\\n' '{pubKey}' >> /root/.ssh/authorized_keys");
        Console.WriteLine(add.ExitStatus == 0 ? "Public key installed." : $"Failed to install key: {add.Error}");
        var check = Run(ssh, "grep -c ssh-ed25519 /root/.ssh/authorized_keys");
        Console.WriteLine($"authorized_keys ed25519 entries: {check.Result.Trim()}");
        return add.ExitStatus == 0 ? 0 : 1;
    }

    case "cleanup":
    {
        var host = args[1];
        var port = int.Parse(args[2]);
        var user = args[3];
        var keyFile = args[4];

        var conn = new ConnectionInfo(host, port, user, new PrivateKeyAuthenticationMethod(user, new PrivateKeyFile(keyFile)));
        conn.Timeout = TimeSpan.FromSeconds(25);
        using var ssh = new SshClient(conn);
        ssh.HostKeyReceived += (_, e) =>
        {
            e.CanTrust = true;
            Console.WriteLine($"Host key: {e.FingerPrintSHA256}");
        };
        ssh.Connect();
        Console.WriteLine("Connected with key auth.");

        foreach (var cmd in new[]
        {
            "systemctl stop xray hysteria-server trojan awg-quick@awg0 2>/dev/null; true",
            "systemctl disable xray hysteria-server trojan awg-quick@awg0 2>/dev/null; true",
            "rm -f /etc/systemd/system/xray.service /etc/systemd/system/hysteria-server.service /etc/systemd/system/trojan.service",
            "rm -f /usr/local/bin/xray /usr/local/bin/hysteria /usr/local/bin/trojan",
            "rm -rf /usr/local/etc/xray /usr/local/share/xray /etc/hysteria /etc/trojan",
            "rm -f /etc/amnezia/amneziawg/awg0.conf",
            "awg del awg0 2>/dev/null; true",
            "systemctl daemon-reload",
        })
        {
            var r = Run(ssh, cmd);
            Console.WriteLine($"[{r.ExitStatus}] {cmd} {r.Error}".TrimEnd());
        }

        var port443 = Run(ssh, "ss -tlnp | grep ':443 ' || echo FREE; ss -ulnp | grep ':443 ' || echo FREE-UDP");
        Console.WriteLine($"port 443: {port443.Result.Trim()}");
        return 0;
    }

    case "provision":
    {
        var host = args[1];
        var port = int.Parse(args[2]);
        var user = args[3];
        var keyOrPassword = args[4];
        var sni = args.Length > 5 && !args[5].StartsWith("--") ? args[5] : "www.google.com";
        var force = args.Contains("--force");
        var protocol = ProtocolRegistry.XrayReality;
        var protoIdx = Array.FindIndex(args, a => a == "--protocol");
        if (protoIdx >= 0 && protoIdx + 1 < args.Length)
        {
            protocol = args[protoIdx + 1];
        }

        var progress = new Progress<string>(Console.WriteLine);
        var request = new ProvisionRequest
        {
            Host = host,
            SshPort = port,
            SshUser = user,
            KeyPath = File.Exists(keyOrPassword) ? keyOrPassword : null,
            Password = File.Exists(keyOrPassword) ? null : keyOrPassword,
            Sni = sni,
            ForceReconfigure = force,
            Protocol = protocol,
        };

        using var ssh = SshSession.Connect(request, progress);
        var info = ServerProber.ProbeAsync(ssh, progress).GetAwaiter().GetResult();
        var installer = ProtocolRegistry.Get(request.Protocol);
        var result = installer.ProvisionAsync(ssh, info, request, progress, CancellationToken.None)
            .GetAwaiter().GetResult();

        Console.WriteLine();
        Console.WriteLine("=== RESULT LINK ===");
        Console.WriteLine(result.Link);
        return 0;
    }

    default:
        Console.WriteLine($"Unknown command: {command}");
        return 1;
}

static SshCommand Run(SshClient ssh, string cmd)
{
    var c = ssh.CreateCommand(cmd);
    c.CommandTimeout = TimeSpan.FromMinutes(10);
    c.Execute();
    return c;
}
