namespace VpnProvisioner.Core;

public sealed record ProvisionResult(
    string Link,
    string Uuid,
    string PublicKey,
    string ShortId,
    string Sni,
    int Port);
