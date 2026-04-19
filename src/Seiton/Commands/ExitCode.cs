namespace Seiton.Commands;

internal static class ExitCode
{
    public const int Success = 0;
    public const int LintIssuesFound = 1;
    public const int InvalidOptions = 2;
    public const int FatalError = 3;
}
