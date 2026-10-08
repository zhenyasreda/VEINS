using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using VpnProvisioner.Core;
using VpnProvisioner.Ssh;

namespace VpnProvisioner.Installers;

public sealed class XrayRealityInstaller : IProtocolInstaller
{
    private const int Port = 443;
    private const string XrayBin = "/usr/local/bin/xray";
    private const string ConfigPath = "/usr/local/etc/xray/config.json";
    private const string ServicePath = "/etc/systemd/system/xray.service";

    public string Id => ProtocolRegistry.XrayReality;
    public string DisplayName => "Xray VLESS/Reality";

    public async Task<ProvisionResult> ProvisionAsync(SshSession ssh, ServerInfo info, ProvisionRequest req, IProgress<string> log, CancellationToken ct)
    {
        if (info.Port443Busy)
        {
            throw new ProvisionException(
                $"Порт 443 на сервере уже занят: \n{info.Port443Details}\n" +
                "Освободите порт (например, остановите nginx/apache) и повторите попытку.");
        }

        bool configExists = (await ssh.RunAsync($"test -f {ConfigPath}")).Ok;
        if (configExists && !req.ForceReconfigure)
        {
            throw new ProvisionException(
                "На сервере уже есть конфиг Xray. Если хотите пересоздать конфиг и ключи " +
                "(старые клиенты перестанут работать), отметьте «Пересоздать, если уже установлен».");
        }

        await InstallXrayBinaryAsync(ssh, info, log);
        ct.ThrowIfCancellationRequested();

        await EnsureXrayUserAsync(ssh, log);

        log.Report("Генерация UUID и ключей X25519 на сервере...");
        var uuid = await GenerateUuidAsync(ssh);
        var (privateKey, publicKey) = await GenerateX25519Async(ssh);
        var shortId = GenerateShortId();
        log.Report($"UUID: {uuid}");

        if (configExists)
        {
            var stamp = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
            await ssh.RunAsync($"cp {ConfigPath} {ConfigPath}.bak.{stamp}");
            log.Report($"Старый конфиг сохранён: {ConfigPath}.bak.{stamp}");
        }

        await WriteConfigAsync(ssh, req, uuid, privateKey, shortId, log);
        ct.ThrowIfCancellationRequested();

        await InstallServiceAsync(ssh, log);
        await ConfigureFirewallAsync(ssh, log);
        await TryEnableBbrAsync(ssh, log);

        await VerifyRunningAsync(ssh, log);

        var link = BuildLink(req, uuid, publicKey, shortId);
        return new ProvisionResult(link, uuid, publicKey, shortId, req.Sni, Port);
    }

    private static async Task InstallXrayBinaryAsync(SshSession ssh, ServerInfo info, IProgress<string> log)
    {
        if (info.HasXray)
        {
            var existingVer = await ssh.RunAsync($"{XrayBin} --version");
            log.Report($"Xray уже установлен ({existingVer.Output.Trim()}) — пропускаю загрузку.");
            return;
        }

        var asset = info.XrayAsset;
        const string tmpDir = "/tmp/vpnprov-xray";
        await ssh.RunAsync($"rm -rf {tmpDir}");
        await ssh.RunAsync($"mkdir -p {tmpDir}");

        bool githubOk = await CheckGithubAccessAsync(ssh, log);

        if (githubOk)
        {
            log.Report($"GitHub доступен. Загрузка Xray ({asset}) с GitHub...");
            var url = $"https://github.com/XTLS/Xray-core/releases/latest/download/{asset}";
            bool hasCurl = await ssh.CommandExistsAsync("curl");
            string downloadCmd = hasCurl
                ? $"curl -fL --retry 3 -o {tmpDir}/xray.zip '{url}'"
                : $"wget -q -O {tmpDir}/xray.zip '{url}'";

            var dl = await ssh.RunAsync(downloadCmd);
            if (!dl.Ok)
            {
                log.Report($"Не удалось скачать с GitHub ({dl.Output} {dl.Error}). Пробую локальную версию...".Trim());
                await UploadLocalXrayAsync(ssh, asset, tmpDir, log);
            }
        }
        else
        {
            await UploadLocalXrayAsync(ssh, asset, tmpDir, log);
        }

        if (!await ssh.CommandExistsAsync("unzip"))
        {
            log.Report("Установка unzip...");
            await InstallUnzipAsync(ssh, info);
        }

        var unzip = await ssh.RunAsync($"unzip -o -q {tmpDir}/xray.zip -d {tmpDir}");
        if (!unzip.Ok)
        {
            throw new ProvisionException($"Не удалось распаковать архив: {unzip.Output} {unzip.Error}".Trim());
        }

        await ssh.RunAsync("mkdir -p /usr/local/bin /usr/local/share/xray");
        var install = await ssh.RunAsync($"install -m 755 {tmpDir}/xray {XrayBin}");
        if (!install.Ok)
        {
            throw new ProvisionException($"Не удалось установить бинарник Xray: {install.Output} {install.Error}".Trim());
        }

        await ssh.RunAsync($"cp -f {tmpDir}/geoip.dat /usr/local/share/xray/geoip.dat");
        await ssh.RunAsync($"cp -f {tmpDir}/geosite.dat /usr/local/share/xray/geosite.dat");
        await ssh.RunAsync($"rm -rf {tmpDir}");

        var ver = await ssh.RunAsync($"{XrayBin} --version");
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

        log.Report("GitHub недоступен с сервера — буду использовать локальную версию Xray.");
        return false;
    }

    private static async Task UploadLocalXrayAsync(SshSession ssh, string asset, string tmpDir, IProgress<string> log)
    {
        var localPath = FindLocalXrayArchive(asset);
        if (localPath == null)
        {
            throw new ProvisionException(
                $"GitHub недоступен и локальный архив Xray ({asset}) не найден в дистрибутиве. " +
                "Убедитесь, что папка binaries/ присутствует рядом с программой.");
        }

        log.Report($"Загрузка локальной версии Xray ({Path.GetFileName(localPath)}) на сервер...");
        await ssh.UploadBinaryFileAsync(localPath, $"{tmpDir}/xray.zip");
    }

    private static string? FindLocalXrayArchive(string asset)
    {
        var baseDir = AppContext.BaseDirectory;
        var candidates = new[]
        {
            Path.Combine(baseDir, "binaries", asset),
            Path.Combine(baseDir, "..", "binaries", asset),
        };

        foreach (var path in candidates)
        {
            if (File.Exists(path))
            {
                return path;
            }
        }

        return null;
    }

    private static async Task InstallUnzipAsync(SshSession ssh, ServerInfo info)
    {
        switch (info.PackageManager)
        {
            case "apt":
                var first = await ssh.RunAsync("apt-get install -y unzip");
                if (!first.Ok)
                {
                    await ssh.RunAsync("apt-get update -y");
                    var second = await ssh.RunAsync("apt-get install -y unzip");
                    if (!second.Ok)
                    {
                        throw new ProvisionException($"Не удалось установить unzip: {second.Output} {second.Error}".Trim());
                    }
                }
                break;
            case "dnf":
                var dnf = await ssh.RunAsync("dnf install -y unzip");
                if (!dnf.Ok)
                {
                    throw new ProvisionException($"Не удалось установить unzip: {dnf.Output} {dnf.Error}".Trim());
                }
                break;
            case "apk":
                var apk = await ssh.RunAsync("apk add --no-cache unzip");
                if (!apk.Ok)
                {
                    throw new ProvisionException($"Не удалось установить unzip: {apk.Output} {apk.Error}".Trim());
                }
                break;
            default:
                throw new ProvisionException(
                    $"Неизвестный пакетный менеджер (дистрибутив: {info.DistroId}). Установите unzip вручную и повторите.");
        }
    }

    private static async Task EnsureXrayUserAsync(SshSession ssh, IProgress<string> log)
    {
        if ((await ssh.RunAsync("getent passwd xray")).Ok)
        {
            return;
        }
        var res = await ssh.RunAsync("useradd --system --no-create-home --shell /usr/sbin/nologin xray");
        if (!res.Ok)
        {
            log.Report($"Не удалось создать пользователя xray: {res.Output} {res.Error}".Trim());
        }
    }

    private static async Task<string> GenerateUuidAsync(SshSession ssh)
    {
        var res = await ssh.RunAsync($"{XrayBin} uuid");
        var uuid = res.Output.Trim();
        if (!res.Ok || !Regex.IsMatch(uuid, "^[0-9a-fA-F-]{36}$"))
        {
            throw new ProvisionException($"Не удалось сгенерировать UUID: {res.Output} {res.Error}".Trim());
        }
        return uuid;
    }

    private static async Task<(string PrivateKey, string PublicKey)> GenerateX25519Async(SshSession ssh)
    {
        var res = await ssh.RunAsync($"{XrayBin} x25519");
        if (!res.Ok)
        {
            throw new ProvisionException($"Не удалось сгенерировать ключи X25519: {res.Output} {res.Error}".Trim());
        }

        var priv = MatchKey(res.Output, "PrivateKey") ?? MatchKey(res.Output, "Private Key");
        var pub = MatchKey(res.Output, "Password \\(PublicKey\\)")
            ?? MatchKey(res.Output, "PublicKey")
            ?? MatchKey(res.Output, "Public Key");

        priv ??= HexToBase64Url(MatchKey(res.Output, "PrivateKey \\(hex\\)"));
        pub ??= HexToBase64Url(MatchKey(res.Output, "PublicKey \\(hex\\)"));

        if (priv == null || pub == null)
        {
            throw new ProvisionException($"Не удалось разобрать вывод xray x25519:\n{res.Output}");
        }

        return (priv, pub);
    }

    private static string? MatchKey(string text, string name)
    {
        var m = Regex.Match(text, $@"(?m)^{name}:\s*(\S+)");
        return m.Success ? m.Groups[1].Value : null;
    }

    private static string? HexToBase64Url(string? hex)
    {
        if (hex == null || !Regex.IsMatch(hex, "^[0-9a-fA-F]+$"))
        {
            return null;
        }
        try
        {
            return Convert.ToBase64String(Convert.FromHexString(hex))
                .Replace('+', '-').Replace('/', '_').TrimEnd('=');
        }
        catch
        {
            return null;
        }
    }

    private static string GenerateShortId()
    {
        Span<byte> buf = stackalloc byte[4];
        RandomNumberGenerator.Fill(buf);
        return Convert.ToHexString(buf).ToLowerInvariant();
    }

    private static async Task WriteConfigAsync(SshSession ssh, ProvisionRequest req, string uuid, string privateKey, string shortId, IProgress<string> log)
    {
        log.Report("Запись конфигурации Xray...");

        var config = new Dictionary<string, object?>
        {
            ["log"] = new Dictionary<string, object?> { ["loglevel"] = "warning" },
            ["inbounds"] = new object?[]
            {
                new Dictionary<string, object?>
                {
                    ["protocol"] = "vless",
                    ["port"] = Port,
                    ["settings"] = new Dictionary<string, object?>
                    {
                        ["clients"] = new object?[]
                        {
                            new Dictionary<string, object?>
                            {
                                ["id"] = uuid,
                                ["flow"] = "xtls-rprx-vision"
                            }
                        },
                        ["decryption"] = "none"
                    },
                    ["streamSettings"] = new Dictionary<string, object?>
                    {
                        ["network"] = "tcp",
                        ["security"] = "reality",
                        ["realitySettings"] = new Dictionary<string, object?>
                        {
                            ["show"] = false,
                            ["dest"] = $"{req.Sni}:443",
                            ["xver"] = 0,
                            ["serverNames"] = new object?[] { req.Sni, "" },
                            ["privateKey"] = privateKey,
                            ["minVersion"] = "1.3",
                            ["maxVersion"] = "1.3",
                            ["shortIds"] = new object?[] { shortId }
                        }
                    },
                    ["sniffing"] = new Dictionary<string, object?>
                    {
                        ["enabled"] = true,
                        ["destOverride"] = new object?[] { "http", "tls", "quic" }
                    }
                }
            },
            ["outbounds"] = new object?[]
            {
                new Dictionary<string, object?>
                {
                    ["protocol"] = "freedom",
                    ["tag"] = "direct"
                }
            }
        };

        var json = JsonSerializer.Serialize(config, new JsonSerializerOptions { WriteIndented = true });

        await ssh.UploadTextAsync("/tmp/vpnprov-config.json", json);
        await ssh.RunAsync("mkdir -p /usr/local/etc/xray");
        var mv = await ssh.RunAsync($"mv /tmp/vpnprov-config.json {ConfigPath}");
        if (!mv.Ok)
        {
            throw new ProvisionException($"Не удалось записать конфиг: {mv.Output} {mv.Error}".Trim());
        }
        await ssh.RunAsync($"chown root:xray {ConfigPath}");
        await ssh.RunAsync($"chmod 640 {ConfigPath}");

        var test = await ssh.RunAsync($"{XrayBin} -test -config {ConfigPath}");
        if (!test.Ok)
        {
            throw new ProvisionException($"Конфигурация не прошла проверку xray -test:\n{test.Output}\n{test.Error}".Trim());
        }
        log.Report("Конфигурация проверена (xray -test).");
    }

    private static async Task InstallServiceAsync(SshSession ssh, IProgress<string> log)
    {
        log.Report("Настройка systemd-сервиса xray...");

        var unit = """
            [Unit]
            Description=Xray Service
            After=network.target nss-lookup.target

            [Service]
            User=xray
            CapabilityBoundingSet=CAP_NET_ADMIN CAP_NET_BIND_SERVICE
            AmbientCapabilities=CAP_NET_ADMIN CAP_NET_BIND_SERVICE
            NoNewPrivileges=true
            ExecStart=/usr/local/bin/xray run -config /usr/local/etc/xray/config.json
            Restart=on-failure
            RestartPreventExitStatus=23
            LimitNPROC=10000
            LimitNOFILE=1000000

            [Install]
            WantedBy=multi-user.target
            """;

        await ssh.UploadTextAsync("/tmp/vpnprov-xray.service", unit);
        var mv = await ssh.RunAsync($"mv /tmp/vpnprov-xray.service {ServicePath}");
        if (!mv.Ok)
        {
            throw new ProvisionException($"Не удалось записать systemd-юнит: {mv.Output} {mv.Error}".Trim());
        }

        await ssh.RunAsync("systemctl daemon-reload");
        var enable = await ssh.RunAsync("systemctl enable xray");
        if (!enable.Ok)
        {
            throw new ProvisionException($"Не удалось включить сервис xray: {enable.Output} {enable.Error}".Trim());
        }
        var restart = await ssh.RunAsync("systemctl restart xray");
        if (!restart.Ok)
        {
            throw new ProvisionException($"Не удалось запустить сервис xray: {restart.Output} {restart.Error}".Trim());
        }
    }

    private static async Task ConfigureFirewallAsync(SshSession ssh, IProgress<string> log)
    {
        if (await ssh.CommandExistsAsync("ufw"))
        {
            var allow = await ssh.RunAsync($"ufw allow {Port}/tcp");
            if (allow.Ok)
            {
                await ssh.RunAsync("ufw reload");
                log.Report($"Порт {Port} открыт в ufw.");
            }
            else
            {
                log.Report($"Не удалось открыть порт в ufw: {allow.Output} {allow.Error}".Trim());
            }
        }
        else if (await ssh.CommandExistsAsync("firewall-cmd"))
        {
            var add = await ssh.RunAsync($"firewall-cmd --permanent --add-port={Port}/tcp");
            if (add.Ok)
            {
                await ssh.RunAsync("firewall-cmd --reload");
                log.Report($"Порт {Port} открыт в firewalld.");
            }
            else
            {
                log.Report($"Не удалось открыть порт в firewalld: {add.Output} {add.Error}".Trim());
            }
        }
        else
        {
            log.Report("Менеджер фаервола (ufw/firewalld) не найден — проверьте, что порт открыт на уровне системы.");
        }

        log.Report("Напоминание: у облачных провайдеров (GCP, AWS, Oracle, Cloudflare и др.) " +
            $"порт {Port} нужно дополнительно открыть в панели (security group / firewall).");
    }

    private static async Task TryEnableBbrAsync(SshSession ssh, IProgress<string> log)
    {
        var avail = await ssh.RunAsync("cat /proc/sys/net/ipv4/tcp_available_congestion_control", requireRoot: false);
        if (!avail.Ok || !avail.Output.Contains("bbr"))
        {
            log.Report("BBR недоступен на этом ядре — пропускаю.");
            return;
        }

        const string content = "net.core.default_qdisc=fq\nnet.ipv4.tcp_congestion_control=bbr\n";
        await ssh.UploadTextAsync("/tmp/vpnprov-bbr.conf", content);
        var mv = await ssh.RunAsync("mv /tmp/vpnprov-bbr.conf /etc/sysctl.d/99-vpnprov-bbr.conf");
        if (mv.Ok)
        {
            await ssh.RunAsync("sysctl --system");
            log.Report("BBR включён.");
        }
        else
        {
            log.Report("Не удалось применить BBR-настройки — пропускаю.");
        }
    }

    private static async Task VerifyRunningAsync(SshSession ssh, IProgress<string> log)
    {
        var active = await ssh.RunAsync("systemctl is-active xray");
        if (active.Output.Trim() != "active")
        {
            var status = await ssh.RunAsync("systemctl status xray --no-pager -l");
            throw new ProvisionException($"Сервис xray не запущен (состояние: {active.Output.Trim()}):\n{status.Output}\n{status.Error}".Trim());
        }

        var listening = await ssh.RunAsync("ss -tlnp", requireRoot: false);
        if (!listening.Output.Contains($":{Port} "))
        {
            log.Report($"Предупреждение: сервис активен, но порт {Port} не обнаружен в списке слушающих.");
        }
        else
        {
            log.Report($"Сервис xray активен, порт {Port} слушается.");
        }
    }

    private static string BuildLink(ProvisionRequest req, string uuid, string publicKey, string shortId)
    {
        var pbk = publicKey.Replace('+', '-').Replace('/', '_').TrimEnd('=');
        return $"vless://{uuid}@{req.Host}:{Port}" +
               $"?encryption=none&security=reality&type=tcp&fp=chrome" +
               $"&sni={Uri.EscapeDataString(req.Sni)}&pbk={pbk}&sid={shortId}" +
               $"#{Uri.EscapeDataString(req.Host)}";
    }
}
