using System.IO;
using System.Text.Json;

namespace VpnProvisioner.Ssh;

public sealed class KnownHostsStore
{
    private static readonly string StorePath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "VpnProvisioner", "known_hosts.json");

    private Dictionary<string, string> _map;

    public KnownHostsStore()
    {
        _map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        try
        {
            if (File.Exists(StorePath))
            {
                var json = File.ReadAllText(StorePath);
                _map = JsonSerializer.Deserialize<Dictionary<string, string>>(json) ?? new();
            }
        }
        catch
        {
            _map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        }
    }

    private static string Key(string host, int port) => $"{host}:{port}";

    public string? GetFingerprint(string host, int port) =>
        _map.TryGetValue(Key(host, port), out var fp) ? fp : null;

    public void SaveFingerprint(string host, int port, string fingerprint)
    {
        _map[Key(host, port)] = fingerprint;
        var dir = Path.GetDirectoryName(StorePath)!;
        Directory.CreateDirectory(dir);
        File.WriteAllText(StorePath, JsonSerializer.Serialize(_map, new JsonSerializerOptions { WriteIndented = true }));
    }
}
