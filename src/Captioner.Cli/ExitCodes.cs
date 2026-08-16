namespace Captioner.Cli;

internal static class ExitCodes
{
    public const int Success = 0;
    public const int GeneralError = 1;
    public const int UsageError = 2;
    public const int DependencyMissing = 3;
    public const int PartialFailure = 4;
    public const int Cancelled = 130;
}
