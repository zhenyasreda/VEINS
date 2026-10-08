using System.IO;
using System.Security.Cryptography;
using System.Text;
using VpnProvisioner.Core;
using VpnProvisioner.Ssh;

namespace VpnProvisioner.Installers;

public sealed class AmneziaWgInstaller : IProtocolInstaller
{
    private const int Port = 443;
    private const string InterfaceName = "awg0";
    private const string ConfigPath = "/etc/amnezia/amneziawg/awg0.conf";
    private const string ServiceName = "awg-quick@awg0";
    private const string ClientSubnet = "10.66.66";

    public string Id => ProtocolRegistry.AmneziaWg;
    public string DisplayName => "AmneziaWG";

    public async Task<ProvisionResult> ProvisionAsync(SshSession ssh, ServerInfo info, ProvisionRequest req, IProgress<string> log, CancellationToken ct)
    {
        if (info.Port443Busy)
        {
            log.Report("Порт 443 занят. Останавливаю конфликтующие сервисы...");
            await ProtocolRegistry.StopConflictingServicesAsync(ssh, "awg-quick@awg0", log);
        }

        await InstallPackagesAsync(ssh, info, log);
        ct.ThrowIfCancellationRequested();

        log.Report("Генерация ключей WireGuard...");
        var (serverPriv, serverPub) = await GenerateKeyPairAsync(ssh);
        var (clientPriv, clientPub) = await GenerateKeyPairAsync(ssh);
        var presharedKey = await GeneratePresharedKeyAsync(ssh);
        log.Report("Ключи сгенерированы.");
        ct.ThrowIfCancellationRequested();

        var jc = GenerateJc();
        var jmin = 10;
        var jmax = 30;

        await WriteServerConfigAsync(ssh, serverPriv, jc, jmin, jmax, log);
        ct.ThrowIfCancellationRequested();

        await EnableForwardingAsync(ssh, log);
        await SetupIptablesAsync(ssh, log);
        await ConfigureFirewallAsync(ssh, log);
        await TryEnableBbrAsync(ssh, log);

        await StartServiceAsync(ssh, log);
        ct.ThrowIfCancellationRequested();

        await VerifyRunningAsync(ssh, log);

        var clientConfig = BuildClientConfig(req.Host, clientPriv, clientPub, serverPub, presharedKey, jc, jmin, jmax);
        return new ProvisionResult(clientConfig, "", "", "", req.Sni, Port);
    }

    private static async Task InstallPackagesAsync(SshSession ssh, ServerInfo info, IProgress<string> log)
    {
        var awgTools = await ssh.CommandExistsAsync("awg");
        var awgModule = await ssh.RunAsync("lsmod | grep amneziawg", requireRoot: false);

        if (awgTools && awgModule.Ok && awgModule.Output.Contains("amneziawg"))
        {
            log.Report("AmneziaWG уже установлен — пропускаю установку пакетов.");
            return;
        }

        if (info.PackageManager != "apt")
        {
            throw new ProvisionException(
                $"AmneziaWG поддерживается только на дистрибутивах с apt (Ubuntu/Debian). " +
                $"Ваш дистрибутив: {info.DistroId}.");
        }

        log.Report("Установка зависимостей (kernel-headers, dkms)...");
        await ssh.RunAsync("apt-get install -y \"linux-headers-$(uname -r)\" dkms 2>&1");

        bool githubOk = await CheckGithubAccessAsync(ssh, log);

        if (githubOk)
        {
            await InstallFromPpaAsync(ssh, info, log);
        }
        else
        {
            await InstallFromLocalAsync(ssh, info, log);
        }

        var loadMod = await ssh.RunAsync("modprobe amneziawg");
        if (!loadMod.Ok)
        {
            log.Report($"Предупреждение: модуль amneziawg не загрузился ({loadMod.Error}). " +
                "Возможна перезагрузка для завершения DKMS-сборки.");
        }
    }

    private static async Task<bool> CheckGithubAccessAsync(SshSession ssh, IProgress<string> log)
    {
        log.Report("Проверка доступности GitHub/PPA с сервера...");
        var test = await ssh.RunAsync(
            "curl -sI --max-time 10 -o /dev/null -w '%{http_code}' https://ppa.launchpadcontent.net/amnezia/ppa/ubuntu/dists/noble/Release",
            requireRoot: false);
        if (test.Ok && test.Output.Trim() == "200")
        {
            log.Report("PPA Amnezia доступен.");
            return true;
        }
        log.Report("PPA Amnezia недоступен — использую локальные пакеты.");
        return false;
    }

    private static async Task InstallFromPpaAsync(SshSession ssh, ServerInfo info, IProgress<string> log)
    {
        var codename = await ssh.RunAsync(". /etc/os-release && echo $VERSION_CODENAME", requireRoot: false);
        var suite = codename.Ok ? codename.Output.Trim() : "noble";

        log.Report("Получение списка пакетов из PPA Amnezia...");
        var pkgList = await ssh.RunAsync(
            $"curl -fsSL 'https://ppa.launchpadcontent.net/amnezia/ppa/ubuntu/dists/{suite}/main/binary-amd64/Packages.gz' | zcat 2>/dev/null | " +
            "grep -E '^(Package|Filename):' | grep -A1 'amneziawg' | grep Filename | awk '{print $2}'");

        if (!pkgList.Ok || string.IsNullOrWhiteSpace(pkgList.Output))
        {
            pkgList = await ssh.RunAsync(
                $"curl -fsSL 'https://ppa.launchpadcontent.net/amnezia/ppa/ubuntu/dists/{suite}/main/binary-all/Packages.gz' | zcat 2>/dev/null | " +
                "grep -E '^(Package|Filename):' | grep -A1 'amneziawg' | grep Filename | awk '{print $2}'");
        }

        if (!pkgList.Ok || string.IsNullOrWhiteSpace(pkgList.Output))
        {
            throw new ProvisionException("Не удалось получить список пакетов из PPA Amnezia.");
        }

        var ppaBase = "https://ppa.launchpadcontent.net/amnezia/ppa/ubuntu";
        var files = pkgList.Output.Split('\n', StringSplitOptions.RemoveEmptyEntries);

        await ssh.RunAsync("mkdir -p /tmp/vpnprov-awg-debs");
        foreach (var file in files)
        {
            var url = $"{ppaBase}/{file.Trim()}";
            var name = Path.GetFileName(file.Trim());
            log.Report($"Загрузка {name}...");
            var dl = await ssh.RunAsync($"curl -fL -o /tmp/vpnprov-awg-debs/{name} '{url}'");
            if (!dl.Ok)
            {
                throw new ProvisionException($"Не удалось скачать {name}: {dl.Output} {dl.Error}".Trim());
            }
        }

        var dpkg = await ssh.RunAsync("dpkg -i /tmp/vpnprov-awg-debs/*.deb 2>&1 || apt-get install -f -y 2>&1");
        if (!dpkg.Ok)
        {
            throw new ProvisionException($"Не удалось установить пакеты AmneziaWG: {dpkg.Output} {dpkg.Error}".Trim());
        }
        await ssh.RunAsync("rm -rf /tmp/vpnprov-awg-debs");
        log.Report("Пакеты AmneziaWG установлены.");
    }

    private static async Task InstallFromLocalAsync(SshSession ssh, ServerInfo info, IProgress<string> log)
    {
        var arch = info.Arch == "x86_64" ? "amd64" : info.Arch is "aarch64" or "arm64" ? "arm64" : "armhf";
        var localDir = FindLocalDebDir();

        if (localDir == null)
        {
            throw new ProvisionException(
                "GitHub недоступен и локальные .deb пакеты AmneziaWG не найдены. " +
                "Убедитесь, что папка binaries/amneziawg/ присутствует рядом с программой.");
        }

        var debs = Directory.GetFiles(localDir, "*.deb");
        if (debs.Length == 0)
        {
            throw new ProvisionException($"В папке {localDir} не найдено .deb файлов для AmneziaWG.");
        }

        await ssh.RunAsync("mkdir -p /tmp/vpnprov-awg-debs");
        foreach (var deb in debs)
        {
            var name = Path.GetFileName(deb);
            if (!name.Contains(arch) && !name.Contains("all")) continue;
            log.Report($"Загрузка {name}...");
            await ssh.UploadBinaryFileAsync(deb, $"/tmp/vpnprov-awg-debs/{name}");
        }

        await ssh.RunAsync("apt-get install -y linux-headers-$(uname -r) 2>&1 || true");
        var dpkg = await ssh.RunAsync("dpkg -i /tmp/vpnprov-awg-debs/*.deb 2>&1 || apt-get install -f -y 2>&1");
        if (!dpkg.Ok)
        {
            throw new ProvisionException($"Не удалось установить .deb пакеты: {dpkg.Output} {dpkg.Error}".Trim());
        }
        await ssh.RunAsync("rm -rf /tmp/vpnprov-awg-debs");
        log.Report("Локальные пакеты AmneziaWG установлены.");
    }

    private static string? FindLocalDebDir()
    {
        var baseDir = AppContext.BaseDirectory;
        var candidates = new[]
        {
            Path.Combine(baseDir, "binaries", "amneziawg"),
            Path.Combine(baseDir, "..", "binaries", "amneziawg"),
        };
        foreach (var dir in candidates)
        {
            if (Directory.Exists(dir)) return dir;
        }
        return null;
    }

    private static async Task<(string Private, string Public)> GenerateKeyPairAsync(SshSession ssh)
    {
        var genkey = await ssh.RunAsync("awg genkey", requireRoot: false);
        if (!genkey.Ok)
        {
            genkey = await ssh.RunAsync("wg genkey", requireRoot: false);
        }
        if (!genkey.Ok)
        {
            throw new ProvisionException($"Не удалось сгенерировать WireGuard-ключ: {genkey.Output} {genkey.Error}".Trim());
        }
        var priv = genkey.Output.Trim();

        var pubkey = await ssh.RunAsync($"echo '{priv}' | awg pubkey", requireRoot: false);
        if (!pubkey.Ok)
        {
            pubkey = await ssh.RunAsync($"echo '{priv}' | wg pubkey", requireRoot: false);
        }
        if (!pubkey.Ok)
        {
            throw new ProvisionException($"Не удалось получить публичный ключ: {pubkey.Output} {pubkey.Error}".Trim());
        }

        return (priv, pubkey.Output.Trim());
    }

    private static async Task<string> GeneratePresharedKeyAsync(SshSession ssh)
    {
        var res = await ssh.RunAsync("awg genpsk", requireRoot: false);
        if (!res.Ok)
        {
            res = await ssh.RunAsync("wg genpsk", requireRoot: false);
        }
        if (!res.Ok)
        {
            throw new ProvisionException($"Не удалось сгенерировать preshared key: {res.Output} {res.Error}".Trim());
        }
        return res.Output.Trim();
    }

    private static int GenerateJc()
    {
        Span<byte> buf = stackalloc byte[1];
        RandomNumberGenerator.Fill(buf);
        return 128 + (buf[0] % 128);
    }

    private static async Task WriteServerConfigAsync(SshSession ssh, string serverPriv, int jc, int jmin, int jmax, IProgress<string> log)
    {
        log.Report("Запись конфигурации сервера AmneziaWG...");

        var postUp = $"iptables -t nat -A POSTROUTING -s {ClientSubnet}.0/24 -o $(ip route | grep default | awk '{{print $5}}') -j MASQUERADE";
        var postDown = $"iptables -t nat -D POSTROUTING -s {ClientSubnet}.0/24 -o $(ip route | grep default | awk '{{print $5}}') -j MASQUERADE";

        var config = $"""
            [Interface]
            PrivateKey = {serverPriv}
            Address = {ClientSubnet}.1/24
            ListenPort = {Port}
            Jc = {jc}
            Jmin = {jmin}
            Jmax = {jmax}
            PostUp = {postUp}
            PostDown = {postDown}
            """;

        await ssh.UploadTextAsync("/tmp/vpnprov-awg.conf", config);
        await ssh.RunAsync("mkdir -p /etc/amnezia/amneziawg");
        var mv = await ssh.RunAsync($"mv /tmp/vpnprov-awg.conf {ConfigPath}");
        if (!mv.Ok)
        {
            throw new ProvisionException($"Не удалось записать конфиг: {mv.Output} {mv.Error}".Trim());
        }
        await ssh.RunAsync($"chmod 600 {ConfigPath}");
        log.Report("Конфигурация сервера записана.");
    }

    private static async Task EnableForwardingAsync(SshSession ssh, IProgress<string> log)
    {
        const string content = "net.ipv4.ip_forward=1\n";
        await ssh.UploadTextAsync("/tmp/vpnprov-awg-forward.conf", content);
        await ssh.RunAsync("mv /tmp/vpnprov-awg-forward.conf /etc/sysctl.d/99-awg-forwarding.conf");
        await ssh.RunAsync("sysctl --system");
        log.Report("IP forwarding включён.");
    }

    private static async Task SetupIptablesAsync(SshSession ssh, IProgress<string> log)
    {
        var iface = await ssh.RunAsync("ip route | grep default | awk '{print $5}'", requireRoot: false);
        var extIface = iface.Output.Trim();
        if (string.IsNullOrEmpty(extIface)) extIface = "eth0";

        await ssh.RunAsync($"iptables -t nat -A POSTROUTING -s {ClientSubnet}.0/24 -o {extIface} -j MASQUERADE");
        await ssh.RunAsync($"iptables -A FORWARD -i {InterfaceName} -o {extIface} -j ACCEPT");
        await ssh.RunAsync($"iptables -A FORWARD -o {InterfaceName} -i {extIface} -m state --state RELATED,ESTABLISHED -j ACCEPT");
        log.Report("iptables/NAT настроены.");
    }

    private static async Task ConfigureFirewallAsync(SshSession ssh, IProgress<string> log)
    {
        if (await ssh.CommandExistsAsync("ufw"))
        {
            await ssh.RunAsync($"ufw allow {Port}/udp");
            await ssh.RunAsync($"ufw allow {Port}/tcp");
            await ssh.RunAsync($"ufw route allow {Port}/udp");
            await ssh.RunAsync("ufw reload");
            log.Report($"Порт {Port} (UDP) открыт в ufw.");
        }
        else if (await ssh.CommandExistsAsync("firewall-cmd"))
        {
            await ssh.RunAsync($"firewall-cmd --permanent --add-port={Port}/udp");
            await ssh.RunAsync("firewall-cmd --reload");
            log.Report($"Порт {Port}/udp открыт в firewalld.");
        }
        else
        {
            log.Report($"Менеджер фаервола не найден — убедитесь, что порт {Port}/udp открыт.");
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

    private static async Task StartServiceAsync(SshSession ssh, IProgress<string> log)
    {
        log.Report("Запуск сервиса AmneziaWG...");
        await ssh.RunAsync("systemctl daemon-reload");
        var enable = await ssh.RunAsync($"systemctl enable {ServiceName}");
        if (!enable.Ok)
        {
            throw new ProvisionException($"Не удалось включить сервис {ServiceName}: {enable.Output} {enable.Error}".Trim());
        }
        var start = await ssh.RunAsync($"systemctl restart {ServiceName}");
        if (!start.Ok)
        {
            throw new ProvisionException($"Не удалось запустить сервис {ServiceName}: {start.Output} {start.Error}".Trim());
        }
    }

    private static async Task VerifyRunningAsync(SshSession ssh, IProgress<string> log)
    {
        var active = await ssh.RunAsync($"systemctl is-active {ServiceName}");
        if (active.Output.Trim() != "active")
        {
            var status = await ssh.RunAsync($"systemctl status {ServiceName} --no-pager -l");
            throw new ProvisionException($"Сервис {ServiceName} не запущен ({active.Output.Trim()}):\n{status.Output}\n{status.Error}".Trim());
        }

        var ifaceCheck = await ssh.RunAsync($"awg show {InterfaceName}", requireRoot: false);
        if (!ifaceCheck.Ok)
        {
            ifaceCheck = await ssh.RunAsync($"wg show {InterfaceName}", requireRoot: false);
        }
        if (ifaceCheck.Ok)
        {
            log.Report($"Интерфейс {InterfaceName} активен, порт {Port}/UDP.");
        }
        else
        {
            log.Report($"Сервис активен, но интерфейс {InterfaceName} не найден в awg/wg show.");
        }
    }

    private static string BuildClientConfig(
        string host, string clientPriv, string clientPub,
        string serverPub, string presharedKey, int jc, int jmin, int jmax)
    {
        return $"""
            [Interface]
            PrivateKey = {clientPriv}
            Address = {ClientSubnet}.2/24
            DNS = 1.1.1.1

            [Peer]
            PublicKey = {serverPub}
            PresharedKey = {presharedKey}
            AllowedIPs = 0.0.0.0/0
            Endpoint = {host}:{Port}
            PersistentKeepalive = 25
            Jc = {jc}
            Jmin = {jmin}
            Jmax = {jmax}
            """;
    }
}
