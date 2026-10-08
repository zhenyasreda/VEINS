namespace VpnProvisioner.Core;

public class ProvisionException : Exception
{
    public ProvisionException(string message) : base(message) { }

    public ProvisionException(string message, Exception inner) : base(message, inner) { }
}
