namespace VpnProvisioner.Ssh;

public readonly record struct ExecResult(int Exit, string Output, string Error)
{
    public bool Ok => Exit == 0;
}
