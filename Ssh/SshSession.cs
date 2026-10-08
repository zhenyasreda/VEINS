using System.Diagnostics;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using Renci.SshNet;
using VpnProvisioner.Core;

namespace VpnProvisioner.Ssh;

public sealed class SshSession : IDisposable
{
    private static readonly TimeSpan CommandTimeout = TimeSpan.FromMinutes(15);
    private const int ConnectAttempts = 3;

    private readonly SshClient _ssh;
    private readonly ConnectionInfo _connectionInfo;

    public string Host { get; }
    public int Port { get; }
    public bool IsRoot { get; private set; }

    private SshSession(SshClient ssh, ConnectionInfo connInfo, string host, int port)
    {
        _ssh = ssh;
        _connectionInfo = connInfo;
        Host = host;
        Port = port;
    }

    public static SshSession Connect(ProvisionRequest req, IProgress<string> log)
    {
        if (!string.IsNullOrEmpty(req.KeyPath))
        {
            return ConnectWithKey(req, log);
        }

        if (!string.IsNullOrEmpty(req.Password))
        {
            return ConnectWithPasswordAndUpgrade(req, log);
        }

        throw new ProvisionException("Укажите либо путь к SSH-ключу, либо пароль.");
    }

    private static SshSession ConnectWithKey(ProvisionRequest req, IProgress<string> log)
    {
        log.Report($"Подключение к {req.Host}:{req.SshPort} (пользователь {req.SshUser})...");

        PrivateKeyFile keyFile;
        try
        {
            keyFile = string.IsNullOrEmpty(req.KeyPassphrase)
                ? new PrivateKeyFile(req.KeyPath!)
                : new PrivateKeyFile(req.KeyPath!, req.KeyPassphrase);
        }
        catch (Exception ex)
        {
            throw new ProvisionException($"Не удалось прочитать приватный ключ «{req.KeyPath}»: {ex.Message}. " +
                "Если ключ защищён паролем, укажите его в поле «Пароль ключа».", ex);
        }

        var store = new KnownHostsStore();
        string? expected = store.GetFingerprint(req.Host, req.SshPort);
        string? actual = null;
        Exception? lastError = null;

        for (int attempt = 1; attempt <= ConnectAttempts; attempt++)
        {
            actual = null;
            var connInfo = BuildKeyConnection(req, keyFile);
            var sshClient = new SshClient(connInfo);
            sshClient.KeepAliveInterval = TimeSpan.FromSeconds(15);
            AttachHostKeyHandler(sshClient, log, expected, v => actual = v);

            try
            {
                sshClient.Connect();
                if (expected == null && actual != null)
                {
                    store.SaveFingerprint(req.Host, req.SshPort, actual);
                }

                var session = new SshSession(sshClient, connInfo, req.Host, req.SshPort);
                ProbeRoot(session, req, log);
                log.Report("Подключение установлено.");
                return session;
            }
            catch (Exception ex) when (actual != null && expected != null && expected != actual)
            {
                sshClient.Dispose();
                throw new ProvisionException(
                    "Ключ сервера не совпадает с сохранённым. Если вы переустановили ОС на сервере — " +
                    $"удалите запись для {req.Host}:{req.SshPort} из {KnownHostsStorePath()}.", ex);
            }
            catch (Exception ex)
            {
                sshClient.Dispose();
                lastError = ex;
                if (attempt < ConnectAttempts)
                {
                    log.Report($"Попытка {attempt} не удалась ({ex.Message}), повторяю через 3 сек...");
                    Thread.Sleep(3000);
                }
            }
        }

        throw new ProvisionException($"Ошибка SSH-подключения: {lastError?.Message}", lastError!);
    }

    private static SshSession ConnectWithPasswordAndUpgrade(ProvisionRequest req, IProgress<string> log)
    {
        var keysDir = KeysDirectory();
        Directory.CreateDirectory(keysDir);
        var keyPath = Path.Combine(keysDir, $"{req.Host}_{req.SshPort}_id_ed25519");

        if (!File.Exists(keyPath))
        {
            log.Report("Генерация SSH-ключа...");
            GenerateEd25519Key(keyPath);
            log.Report($"Ключ сохранён: {keyPath}");
        }

        log.Report($"Подключение к {req.Host}:{req.SshPort} (пользователь {req.SshUser}, пароль)...");

        var store = new KnownHostsStore();
        string? expected = store.GetFingerprint(req.Host, req.SshPort);
        string? actual = null;

        var pwConnInfo = BuildPasswordConnection(req);
        var pwClient = new SshClient(pwConnInfo);
        pwClient.KeepAliveInterval = TimeSpan.FromSeconds(15);
        AttachHostKeyHandler(pwClient, log, expected, v => actual = v);

        try
        {
            pwClient.Connect();
        }
        catch (Exception ex) when (actual != null && expected != null && expected != actual)
        {
            pwClient.Dispose();
            throw new ProvisionException(
                "Ключ сервера не совпадает с сохранённым. Если вы переустановили ОС на сервере — " +
                $"удалите запись для {req.Host}:{req.SshPort} из {KnownHostsStorePath()}.", ex);
        }
        catch (Exception ex)
        {
            pwClient.Dispose();
            throw new ProvisionException($"Ошибка SSH-подключения по паролю: {ex.Message}", ex);
        }

        if (expected == null && actual != null)
        {
            store.SaveFingerprint(req.Host, req.SshPort, actual);
        }

        var pubKeyPath = keyPath + ".pub";
        var pubKey = File.ReadAllText(pubKeyPath).Trim();

        log.Report("Установка публичного ключа на сервер...");
        var cmd = pwClient.CreateCommand(
            "mkdir -p ~/.ssh && chmod 700 ~/.ssh && " +
            "touch ~/.ssh/authorized_keys && chmod 600 ~/.ssh/authorized_keys && " +
            $"grep -qF '{pubKey}' ~/.ssh/authorized_keys || printf '%s\\n' '{pubKey}' >> ~/.ssh/authorized_keys");
        cmd.CommandTimeout = TimeSpan.FromMinutes(2);
        cmd.Execute();
        if (cmd.ExitStatus != 0)
        {
            pwClient.Dispose();
            throw new ProvisionException($"Не удалось установить публичный ключ: {cmd.Error}".Trim());
        }

        pwClient.Dispose();
        log.Report("Публичный ключ установлен. Переподключение по ключу...");

        var upgradedReq = new ProvisionRequest
        {
            Host = req.Host,
            SshPort = req.SshPort,
            SshUser = req.SshUser,
            KeyPath = keyPath,
            KeyPassphrase = null,
            Password = null,
            Sni = req.Sni,
            ForceReconfigure = req.ForceReconfigure,
            Protocol = req.Protocol,
        };

        return ConnectWithKey(upgradedReq, log);
    }

    private static void GenerateEd25519Key(string keyPath)
    {
        var psi = new ProcessStartInfo
        {
            FileName = "ssh-keygen",
            Arguments = $"-t ed25519 -f \"{keyPath}\" -N \"\" -q",
            UseShellExecute = false,
            CreateNoWindow = true,
        };

        try
        {
            using var proc = Process.Start(psi)
                ?? throw new ProvisionException("Не удалось запустить ssh-keygen.");
            proc.WaitForExit(TimeSpan.FromSeconds(15));
            if (proc.ExitCode != 0)
            {
                throw new ProvisionException($"ssh-keygen завершился с кодом {proc.ExitCode}.");
            }
        }
        catch (Exception ex)
        {
            throw new ProvisionException(
                "Не удалось сгенерировать SSH-ключ (ssh-keygen не найден или ошибка). " +
                "Укажите путь к существующему ключу вручную.", ex);
        }
    }

    public static string KeysDirectory() => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "VpnProvisioner", "keys");

    private static void AttachHostKeyHandler(SshClient ssh, IProgress<string> log, string? expected, Action<string?> onReceived)
    {
        ssh.HostKeyReceived += (_, e) =>
        {
            var actual = e.FingerPrintSHA256 ?? ComputeFingerprint(e.HostKey);
            if (expected == null)
            {
                e.CanTrust = true;
                log.Report($"Новый сервер. Отпечаток ключа: {actual}");
            }
            else if (expected == actual)
            {
                e.CanTrust = true;
            }
            else
            {
                e.CanTrust = false;
                log.Report($"ОПАСНОЕНИЕ: отпечаток ключа сервера изменился! Ожидался {expected}, получен {actual}. " +
                    "Подключение прервано (возможная атака MITM).");
            }
            onReceived(actual);
        };
    }

    private static void ProbeRoot(SshSession session, ProvisionRequest req, IProgress<string> log)
    {
        var probe = session.RunSync("id -u", requireRoot: false);
        session.IsRoot = probe.Ok && probe.Output.Trim() == "0";
        if (!session.IsRoot)
        {
            log.Report($"Пользователь {req.SshUser} не root — команды будут выполняться через sudo " +
                "(требуется sudo без пароля).");
        }
    }

    private static ConnectionInfo BuildKeyConnection(ProvisionRequest req, PrivateKeyFile keyFile)
    {
        var conn = new ConnectionInfo(req.Host, req.SshPort, req.SshUser,
            new PrivateKeyAuthenticationMethod(req.SshUser, keyFile));
        conn.Timeout = TimeSpan.FromSeconds(30);
        return conn;
    }

    private static ConnectionInfo BuildPasswordConnection(ProvisionRequest req)
    {
        var conn = new ConnectionInfo(req.Host, req.SshPort, req.SshUser,
            new PasswordAuthenticationMethod(req.SshUser, req.Password!));
        conn.Timeout = TimeSpan.FromSeconds(30);
        return conn;
    }

    public static string KnownHostsStorePath() => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "VpnProvisioner", "known_hosts.json");

    private static string ComputeFingerprint(byte[] hostKey)
    {
        var hash = SHA256.HashData(hostKey);
        return "SHA256:" + Convert.ToBase64String(hash).TrimEnd('=');
    }

    public async Task<ExecResult> RunAsync(string command, bool requireRoot = true)
    {
        var cmd = requireRoot && !IsRoot ? "sudo " + command : command;
        try
        {
            var sshCommand = _ssh.CreateCommand(cmd);
            sshCommand.CommandTimeout = CommandTimeout;
            await sshCommand.ExecuteAsync(CancellationToken.None);
            return new ExecResult(
                sshCommand.ExitStatus ?? -1,
                (sshCommand.Result ?? "").TrimEnd('\r', '\n'),
                (sshCommand.Error ?? "").Trim());
        }
        catch (Exception ex)
        {
            throw new ProvisionException($"Ошибка выполнения команды «{command}»: {ex.Message}", ex);
        }
    }

    public ExecResult RunSync(string command, bool requireRoot = true)
    {
        var cmd = requireRoot && !IsRoot ? "sudo " + command : command;
        try
        {
            var sshCommand = _ssh.CreateCommand(cmd);
            sshCommand.CommandTimeout = CommandTimeout;
            var stdout = sshCommand.Execute();
            return new ExecResult(sshCommand.ExitStatus ?? -1, (stdout ?? "").TrimEnd('\r', '\n'), (sshCommand.Error ?? "").Trim());
        }
        catch (Exception ex)
        {
            throw new ProvisionException($"Ошибка выполнения команды «{command}»: {ex.Message}", ex);
        }
    }

    public async Task<bool> CommandExistsAsync(string binary) =>
        (await RunAsync($"command -v {binary}", requireRoot: false)).Ok;

    public async Task UploadTextAsync(string remotePath, string content)
    {
        var b64 = Convert.ToBase64String(Encoding.UTF8.GetBytes(content));
        var res = await RunAsync($"printf '%s' '{b64}' | base64 -d > {remotePath}", requireRoot: false);
        if (!res.Ok)
        {
            throw new ProvisionException($"Не удалось записать файл {remotePath}: {res.Output} {res.Error}".Trim());
        }
    }

    public async Task UploadBinaryFileAsync(string localPath, string remotePath)
    {
        await Task.Run(() =>
        {
            var sftp = new SftpClient(_connectionInfo);
            try
            {
                sftp.Connect();
                using var stream = File.OpenRead(localPath);
                sftp.UploadFile(stream, remotePath);
            }
            catch (Exception ex)
            {
                throw new ProvisionException($"Не удалось загрузить файл {localPath} → {remotePath}: {ex.Message}", ex);
            }
            finally
            {
                try { sftp.Dispose(); } catch { }
            }
        });
    }

    public void Dispose()
    {
        try { _ssh.Dispose(); } catch { }
    }
}
