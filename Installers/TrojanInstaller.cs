using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using VpnProvisioner.Core;
using VpnProvisioner.Ssh;

namespace VpnProvisioner.Installers;

public sealed class TrojanInstaller : IProtocolInstaller
{
    private const int Port = 443;
    private const string BinaryPath = "/usr/local/bin/trojan";
    private const string ConfigPath = "/etc/trojan/config.json";
    private const string ServicePath = "/etc/systemd/system/trojan.service";
    private const string CertDir = "/etc/trojan";

    public string Id => ProtocolRegistry.Trojan;
    public string DisplayName => "Trojan";

    public async Task<ProvisionResult> ProvisionAsync(SshSession ssh, ServerInfo info, ProvisionRequest req, IProgress<string> log, CancellationToken ct)
    {
        if (info.Port443Busy)
        {
            log.Report($"Порт 443 занят. Останавливаю конфликтующие сервисы...");
            await ProtocolRegistry.StopConflictingServicesAsync(ssh, "trojan", log);
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
        var existing = await ssh.RunAsync($"test -x {BinaryPath} && {BinaryPath} --version");
        if (existing.Ok)
        {
            log.Report($"Trojan уже установлен ({existing.Output.Trim()}) — пропускаю загрузку.");
            return;
        }

        var asset = GetAsset(info.Arch);
        const string tmpDir = "/tmp/vpnprov-trojan";
        await ssh.RunAsync($"rm -rf {tmpDir} && mkdir -p {tmpDir}");

        bool githubOk = await CheckGithubAccessAsync(ssh, log);

        if (githubOk)
        {
            log.Report($"Загрузка Trojan ({asset}) с GitHub...");
            var url = $"https://github.com/trojan-gfw/trojan/releases/download/v1.16.0/{asset}";
            bool hasCurl = await ssh.CommandExistsAsync("curl");
            string downloadCmd = hasCurl
                ? $"curl -fL --retry 3 -o {tmpDir}/trojan.tar.xz '{url}'"
                : $"wget -q -O {tmpDir}/trojan.tar.xz '{url}'";

            var dl = await ssh.RunAsync(downloadCmd);
            if (!dl.Ok)
            {
                log.Report("Не удалось скачать с GitHub. Пробую локальную версию...");
                await UploadLocalArchiveAsync(ssh, asset, tmpDir, log);
            }
        }
        else
        {
            await UploadLocalArchiveAsync(ssh, asset, tmpDir, log);
        }

        var extract = await ssh.RunAsync($"tar -xf {tmpDir}/trojan.tar.xz -C {tmpDir}");
        if (!extract.Ok)
        {
            throw new ProvisionException($"Не удалось распаковать Trojan: {extract.Output} {extract.Error}".Trim());
        }

        await ssh.RunAsync("mkdir -p /usr/local/bin");
        var install = await ssh.RunAsync($"install -m 755 {tmpDir}/trojan/trojan {BinaryPath}");
        if (!install.Ok)
        {
            throw new ProvisionException($"Не удалось установить Trojan: {install.Output} {install.Error}".Trim());
        }
        await ssh.RunAsync($"rm -rf {tmpDir}");

        var ver = await ssh.RunAsync($"{BinaryPath} --version");
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

    private static async Task UploadLocalArchiveAsync(SshSession ssh, string asset, string tmpDir, IProgress<string> log)
    {
        var localPath = FindLocalArchive(asset);
        if (localPath == null)
        {
            throw new ProvisionException(
                $"GitHub недоступен и локальный архив Trojan ({asset}) не найден. " +
                "Убедитесь, что папка binaries/ присутствует рядом с программой.");
        }
        log.Report($"Загрузка локальной версии Trojan ({Path.GetFileName(localPath)}) на сервер...");
        await ssh.UploadBinaryFileAsync(localPath, $"{tmpDir}/trojan.tar.xz");
    }

    private static string? FindLocalArchive(string asset)
    {
        var baseDir = AppContext.BaseDirectory;
        var candidates = new[]
        {
            Path.Combine(baseDir, "binaries", "trojan", asset),
            Path.Combine(baseDir, "..", "binaries", "trojan", asset),
        };
        foreach (var path in candidates)
        {
            if (File.Exists(path)) return path;
        }
        return null;
    }

    private static string GetAsset(string arch) => arch switch
    {
        "x86_64" => "trojan-1.16.0-linux-amd64.tar.xz",
        _ => throw new ProvisionException(
            $"Неизвестная архитектура: {arch}. Trojan официально доступен только для x86_64 Linux.")
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
        log.Report("Запись конфигурации Trojan...");

        var config = new Dictionary<string, object?>
        {
            ["run_as"] = "nobody",
            ["max_open_files"] = 100000,
            ["keep_alive"] = new Dictionary<string, object?>
            {
                ["timeout"] = 600
            },
            ["ssl"] = new Dictionary<string, object?>
            {
                ["cert"] = $"{CertDir}/cert.pem",
                ["key"] = $"{CertDir}/key.pem",
                ["key_password"] = "",
                ["ca"] = "",
                ["ciphers"] = "",
                ["prefer_server_ciphers"] = true,
                ["ecdh_curves"] = "X25519:secp521r1:secp384r1:prime256v1",
                ["minimum_tls_version"] = "",
                ["maximum_tls_version"] = "",
                ["disable_ssl_session_ticket"] = false,
                ["tls_name"] = req.Sni
            },
            ["tcp"] = new Dictionary<string, object?>
            {
                ["bind"] = true,
                ["bind_addr"] = "0.0.0.0",
                ["port"] = Port,
                ["fastopen"] = false,
                ["backlog"] = 128,
                ["accept_timeout"] = 5.0,
                ["connect_timeout"] = 5.0
            },
            ["ipv6"] = new Dictionary<string, object?>
            {
                ["bind"] = false,
                ["bind_addr"] = "::",
                ["port"] = Port,
                ["fastopen"] = false,
                ["backlog"] = 128,
                ["accept_timeout"] = 5.0,
                ["connect_timeout"] = 5.0
            },
            ["tcp_fastopen"] = false,
            ["verify_certificate"] = false,
            ["cipher"] = "TLS_AES_128_GCM_SHA256:TLS_AES_256_GCM_SHA384:TLS_CHACHA20_POLY1305_SHA256:ECDHE-ECDSA-AES128-GCM-SHA256:ECDHE-RSA-AES128-GCM-SHA256:ECDHE-ECDSA-AES256-GCM-SHA384:ECDHE-RSA-AES256-GCM-SHA384:ECDHE-ECDSA-CHACHA20-POLY1305:ECDHE-RSA-CHACHA20-POLY1305",
            ["tls_versions"] = new object?[] { "TLSv1_2", "TLSv1_3" },
            ["sess_timeout"] = 10.0,
            ["ssl_session_size"] = 10,
            ["mysql"] = new Dictionary<string, object?>
            {
                ["ssl_ca"] = "",
                ["ssl_cert"] = "",
                ["ssl_key"] = ""
            },
            ["users"] = new object?[] { password }
        };

        var json = JsonSerializer.Serialize(config, new JsonSerializerOptions { WriteIndented = true });
        await ssh.UploadTextAsync("/tmp/vpnprov-trojan-config.json", json);

        await ssh.RunAsync($"mkdir -p {CertDir}");
        var mv = await ssh.RunAsync($"mv /tmp/vpnprov-trojan-config.json {ConfigPath}");
        if (!mv.Ok)
        {
            throw new ProvisionException($"Не удалось записать конфиг: {mv.Output} {mv.Error}".Trim());
        }
        await ssh.RunAsync($"chmod 600 {ConfigPath}");

        log.Report("Конфигурация Trojan записана.");
    }

    private static async Task InstallServiceAsync(SshSession ssh, IProgress<string> log)
    {
        log.Report("Настройка systemd-сервиса trojan...");

        var unit = """
            [Unit]
            Description=Trojan
            After=network.target nss-lookup.target

            [Service]
            CapabilityBoundingSet=CAP_NET_ADMIN CAP_NET_BIND_SERVICE
            AmbientCapabilities=CAP_NET_ADMIN CAP_NET_BIND_SERVICE
            NoNewPrivileges=true
            ExecStart=/usr/local/bin/trojan -c /etc/trojan/config.json
            Restart=on-failure
            RestartPreventExitStatus=23
            LimitNPROC=10000
            LimitNOFILE=1000000

            [Install]
            WantedBy=multi-user.target
            """;

        await ssh.UploadTextAsync("/tmp/vpnprov-trojan.service", unit);
        var mv = await ssh.RunAsync($"mv /tmp/vpnprov-trojan.service {ServicePath}");
        if (!mv.Ok)
        {
            throw new ProvisionException($"Не удалось записать systemd-юнит: {mv.Output} {mv.Error}".Trim());
        }

        await ssh.RunAsync("systemctl daemon-reload");
        var enable = await ssh.RunAsync("systemctl enable trojan");
        if (!enable.Ok)
        {
            throw new ProvisionException($"Не удалось включить сервис: {enable.Output} {enable.Error}".Trim());
        }
        var restart = await ssh.RunAsync("systemctl restart trojan");
        if (!restart.Ok)
        {
            throw new ProvisionException($"Не удалось запустить сервис: {restart.Output} {restart.Error}".Trim());
        }
    }

    private static async Task ConfigureFirewallAsync(SshSession ssh, IProgress<string> log)
    {
        if (await ssh.CommandExistsAsync("ufw"))
        {
            await ssh.RunAsync($"ufw allow {Port}/tcp");
            await ssh.RunAsync("ufw reload");
            log.Report($"Порт {Port}/tcp открыт в ufw.");
        }
        else if (await ssh.CommandExistsAsync("firewall-cmd"))
        {
            await ssh.RunAsync($"firewall-cmd --permanent --add-port={Port}/tcp");
            await ssh.RunAsync("firewall-cmd --reload");
            log.Report($"Порт {Port}/tcp открыт в firewalld.");
        }
        else
        {
            log.Report($"Менеджер фаервола не найден — убедитесь, что порт {Port}/tcp открыт.");
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
        var active = await ssh.RunAsync("systemctl is-active trojan");
        if (active.Output.Trim() != "active")
        {
            var status = await ssh.RunAsync("systemctl status trojan --no-pager -l");
            throw new ProvisionException($"Сервис trojan не запущен ({active.Output.Trim()}):\n{status.Output}\n{status.Error}".Trim());
        }
        log.Report("Сервис Trojan активен.");
    }

    private static string BuildLink(ProvisionRequest req, string password)
    {
        return $"trojan://{Uri.EscapeDataString(password)}@{req.Host}:{Port}" +
               $"?sni={Uri.EscapeDataString(req.Sni)}" +
               $"#{Uri.EscapeDataString(req.Host)}";
    }
}
