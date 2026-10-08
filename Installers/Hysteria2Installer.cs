using System.IO;
using System.Security.Cryptography;
using System.Text;
using VpnProvisioner.Core;
using VpnProvisioner.Ssh;

namespace VpnProvisioner.Installers;

public sealed class Hysteria2Installer : IProtocolInstaller
{
    private const int Port = 443;
    private const string BinaryPath = "/usr/local/bin/hysteria";
    private const string ConfigPath = "/etc/hysteria/config.yaml";
    private const string ServicePath = "/etc/systemd/system/hysteria-server.service";
    private const string CertDir = "/etc/hysteria";

    public string Id => ProtocolRegistry.Hysteria2;
    public string DisplayName => "Hysteria 2";

    public async Task<ProvisionResult> ProvisionAsync(SshSession ssh, ServerInfo info, ProvisionRequest req, IProgress<string> log, CancellationToken ct)
    {
        if (info.Port443Busy)
        {
            log.Report($"Порт 443 занят. Останавливаю конфликтующие сервисы...");
            await ProtocolRegistry.StopConflictingServicesAsync(ssh, "hysteria-server", log);
        }

        await InstallBinaryAsync(ssh, info, log);
        ct.ThrowIfCancellationRequested();

        log.Report("Генерация пароля и TLS-сертификата...");
        var password = GeneratePassword();
        await GenerateSelfSignedCertAsync(ssh, req.Host, log);
        ct.ThrowIfCancellationRequested();

        await WriteConfigAsync(ssh, req, password, log);
        ct.ThrowIfCancellationRequested();

        await InstallServiceAsync(ssh, log);
        await ConfigureFirewallAsync(ssh, log);
        await TryEnableBbrAsync(ssh, log);
        await VerifyRunningAsync(ssh, log);

        var link = BuildLink(req, password);
        return new ProvisionResult(link, "", "", "", req.Sni, Port);
    }

    private static async Task InstallBinaryAsync(SshSession ssh, ServerInfo info, IProgress<string> log)
    {
        var existing = await ssh.RunAsync($"test -x {BinaryPath} && {BinaryPath} version");
        if (existing.Ok)
        {
            log.Report($"Hysteria уже установлен ({existing.Output.Trim()}) — пропускаю загрузку.");
            return;
        }

        var asset = GetAsset(info.Arch);
        const string tmpDir = "/tmp/vpnprov-hysteria";
        await ssh.RunAsync($"rm -rf {tmpDir} && mkdir -p {tmpDir}");

        bool githubOk = await CheckGithubAccessAsync(ssh, log);

        if (githubOk)
        {
            log.Report($"Загрузка Hysteria ({asset}) с GitHub...");
            var url = $"https://github.com/HyNetworks/hysteria/releases/latest/download/{asset}";
            bool hasCurl = await ssh.CommandExistsAsync("curl");
            string downloadCmd = hasCurl
                ? $"curl -fL --retry 3 -o {tmpDir}/hysteria '{url}'"
                : $"wget -q -O {tmpDir}/hysteria '{url}'";

            var dl = await ssh.RunAsync(downloadCmd);
            if (!dl.Ok)
            {
                log.Report("Не удалось скачать с GitHub. Пробую локальную версию...");
                await UploadLocalBinaryAsync(ssh, asset, tmpDir, log);
            }
        }
        else
        {
            await UploadLocalBinaryAsync(ssh, asset, tmpDir, log);
        }

        await ssh.RunAsync("mkdir -p /usr/local/bin");
        var install = await ssh.RunAsync($"install -m 755 {tmpDir}/hysteria {BinaryPath}");
        if (!install.Ok)
        {
            throw new ProvisionException($"Не удалось установить Hysteria: {install.Output} {install.Error}".Trim());
        }
        await ssh.RunAsync($"rm -rf {tmpDir}");

        var ver = await ssh.RunAsync($"{BinaryPath} version");
        log.Report($"Установлено: {ver.Output.Trim()}");
    }

    private static async Task<bool> CheckGithubAccessAsync(SshSession ssh, IProgress<string> log)
    {
        log.Report("Проверка доступности GitHub с сервера...");
        var test = await ssh.RunAsync(
            "curl -sI --max-time 10 -o /dev/null -w '%{http_code}' https://github.com",
            requireRoot: false);
        if (test.Ok && test.Output.Trim() is "200" or "301" or "302")
        {
            log.Report("GitHub доступен.");
            return true;
        }
        log.Report("GitHub недоступен — использую локальную версию.");
        return false;
    }

    private static async Task UploadLocalBinaryAsync(SshSession ssh, string asset, string tmpDir, IProgress<string> log)
    {
        var localPath = FindLocalBinary(asset);
        if (localPath == null)
        {
            throw new ProvisionException(
                $"GitHub недоступен и локальный бинарник Hysteria ({asset}) не найден. " +
                "Убедитесь, что папка binaries/ присутствует рядом с программой.");
        }
        log.Report($"Загрузка локальной версии Hysteria ({Path.GetFileName(localPath)}) на сервер...");
        await ssh.UploadBinaryFileAsync(localPath, $"{tmpDir}/hysteria");
    }

    private static string? FindLocalBinary(string asset)
    {
        var baseDir = AppContext.BaseDirectory;
        var candidates = new[]
        {
            Path.Combine(baseDir, "binaries", "hysteria", asset),
            Path.Combine(baseDir, "..", "binaries", "hysteria", asset),
        };
        foreach (var path in candidates)
        {
            if (File.Exists(path)) return path;
        }
        return null;
    }

    private static string GetAsset(string arch) => arch switch
    {
        "x86_64" => "hysteria-linux-amd64",
        "aarch64" or "arm64" => "hysteria-linux-arm64",
        "armv7l" or "armv7" => "hysteria-linux-arm",
        "i386" or "i686" => "hysteria-linux-386",
        _ => throw new ProvisionException($"Неизвестная архитектура: {arch}")
    };

    private static string GeneratePassword()
    {
        Span<byte> buf = stackalloc byte[16];
        RandomNumberGenerator.Fill(buf);
        return Convert.ToBase64String(buf).Replace('+', '-').Replace('/', '_').TrimEnd('=');
    }

    private static async Task GenerateSelfSignedCertAsync(SshSession ssh, string host, IProgress<string> log)
    {
        await ssh.RunAsync($"mkdir -p {CertDir}");
        var cert = await ssh.RunAsync(
            $"openssl req -x509 -newkey ec -pkeyopt ec_paramgen_curve:prime256v1 -nodes " +
            $"-keyout {CertDir}/key.pem -out {CertDir}/cert.pem -days 3650 " +
            $"-subj '/CN={host}' 2>&1");
        if (!cert.Ok)
        {
            throw new ProvisionException($"Не удалось создать TLS-сертификат: {cert.Output} {cert.Error}".Trim());
        }
        log.Report("TLS-сертификат создан (self-signed, EC P-256).");
    }

    private static async Task WriteConfigAsync(SshSession ssh, ProvisionRequest req, string password, IProgress<string> log)
    {
        log.Report("Запись конфигурации Hysteria 2...");

        var config = $"""
            listen: :{Port}
            auth:
              type: password
              password: {password}
            tls:
              cert: {CertDir}/cert.pem
              key: {CertDir}/key.pem
            quic:
              maxIdleTimeout: 90s
              initStreamFlow: 16777216
              initConnFlow: 8388608
            masquerade:
              type: proxy
              proxy:
                url: https://{req.Sni}
                fallback: true
            """;

        await ssh.UploadTextAsync("/tmp/vpnprov-hysteria.yaml", config);
        await ssh.RunAsync($"mkdir -p {CertDir}");
        var mv = await ssh.RunAsync($"mv /tmp/vpnprov-hysteria.yaml {ConfigPath}");
        if (!mv.Ok)
        {
            throw new ProvisionException($"Не удалось записать конфиг: {mv.Output} {mv.Error}".Trim());
        }
        await ssh.RunAsync($"chmod 600 {ConfigPath}");
        log.Report("Конфигурация записана.");
    }

    private static async Task InstallServiceAsync(SshSession ssh, IProgress<string> log)
    {
        log.Report("Настройка systemd-сервиса hysteria...");

        var unit = """
            [Unit]
            Description=Hysteria 2 Server
            After=network.target

            [Service]
            Type=simple
            ExecStart=/usr/local/bin/hysteria server -c /etc/hysteria/config.yaml
            Restart=on-failure
            RestartSec=5
            LimitNOFILE=1048576
            AmbientCapabilities=CAP_NET_BIND_SERVICE

            [Install]
            WantedBy=multi-user.target
            """;

        await ssh.UploadTextAsync("/tmp/vpnprov-hysteria.service", unit);
        var mv = await ssh.RunAsync($"mv /tmp/vpnprov-hysteria.service {ServicePath}");
        if (!mv.Ok)
        {
            throw new ProvisionException($"Не удалось записать systemd-юнит: {mv.Output} {mv.Error}".Trim());
        }

        await ssh.RunAsync("systemctl daemon-reload");
        var enable = await ssh.RunAsync("systemctl enable hysteria-server");
        if (!enable.Ok)
        {
            throw new ProvisionException($"Не удалось включить сервис: {enable.Output} {enable.Error}".Trim());
        }
        var restart = await ssh.RunAsync("systemctl restart hysteria-server");
        if (!restart.Ok)
        {
            throw new ProvisionException($"Не удалось запустить сервис: {restart.Output} {restart.Error}".Trim());
        }
    }

    private static async Task ConfigureFirewallAsync(SshSession ssh, IProgress<string> log)
    {
        if (await ssh.CommandExistsAsync("ufw"))
        {
            await ssh.RunAsync($"ufw allow {Port}/udp");
            await ssh.RunAsync($"ufw allow {Port}/tcp");
            await ssh.RunAsync("ufw reload");
            log.Report($"Порт {Port} (UDP+TCP) открыт в ufw.");
        }
        else if (await ssh.CommandExistsAsync("firewall-cmd"))
        {
            await ssh.RunAsync($"firewall-cmd --permanent --add-port={Port}/udp");
            await ssh.RunAsync($"firewall-cmd --permanent --add-port={Port}/tcp");
            await ssh.RunAsync("firewall-cmd --reload");
            log.Report($"Порт {Port} (UDP+TCP) открыт в firewalld.");
        }
        else
        {
            log.Report($"Менеджер фаервола не найден — убедитесь, что порт {Port} UDP+TCP открыт.");
        }
    }

    private static async Task TryEnableBbrAsync(SshSession ssh, IProgress<string> log)
    {
        var avail = await ssh.RunAsync("cat /proc/sys/net/ipv4/tcp_available_congestion_control", requireRoot: false);
        if (!avail.Ok || !avail.Output.Contains("bbr")) return;

        const string content = "net.core.default_qdisc=fq\nnet.ipv4.tcp_congestion_control=bbr\n";
        await ssh.UploadTextAsync("/tmp/vpnprov-bbr.conf", content);
        var mv = await ssh.RunAsync("mv /tmp/vpnprov-bbr.conf /etc/sysctl.d/99-vpnprov-bbr.conf");
        if (mv.Ok)
        {
            await ssh.RunAsync("sysctl --system");
            log.Report("BBR включён.");
        }
    }

    private static async Task VerifyRunningAsync(SshSession ssh, IProgress<string> log)
    {
        var active = await ssh.RunAsync("systemctl is-active hysteria-server");
        if (active.Output.Trim() != "active")
        {
            var status = await ssh.RunAsync("systemctl status hysteria-server --no-pager -l");
            throw new ProvisionException($"Сервис hysteria не запущен ({active.Output.Trim()}):\n{status.Output}\n{status.Error}".Trim());
        }
        log.Report("Сервис Hysteria 2 активен.");
    }

    private static string BuildLink(ProvisionRequest req, string password)
    {
        return $"hysteria2://{Uri.EscapeDataString(password)}@{req.Host}:{Port}" +
               $"?sni={Uri.EscapeDataString(req.Sni)}&insecure=1" +
               $"#{Uri.EscapeDataString(req.Host)}";
    }
}
