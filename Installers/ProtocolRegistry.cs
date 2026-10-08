namespace VpnProvisioner.Installers;

public static class ProtocolRegistry
{
    public const string XrayReality = "xray-reality";
    public const string Hysteria2 = "hysteria2";
    public const string Trojan = "trojan";
    public const string AmneziaWg = "amneziawg";

    private static readonly List<IProtocolInstaller> Installers = new()
    {
        new XrayRealityInstaller(),
        new Hysteria2Installer(),
        new TrojanInstaller(),
        new AmneziaWgInstaller(),
    };

    public static IReadOnlyList<IProtocolInstaller> All => Installers;

    public static IProtocolInstaller Get(string id)
    {
        var installer = Installers.FirstOrDefault(i => i.Id == id);
        if (installer == null)
        {
            throw new Core.ProvisionException($"Неизвестный протокол: {id}");
        }
        return installer;
    }
}
