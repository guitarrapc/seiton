namespace Seiton.Core.Tests;

internal static class TestHelper
{
    public static string NormalizeEol(string value)
    {
        return value
            .Replace("\r\n", "\n", StringComparison.Ordinal);
    }
}
