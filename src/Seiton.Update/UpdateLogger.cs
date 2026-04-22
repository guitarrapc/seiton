namespace Seiton.Update;

internal static class UpdateLogger
{
    public static void Info(string message)
    {
        Console.WriteLine($"[{DateTimeOffset.UtcNow:O}] {message}");
    }

    public static void Error(string message)
    {
        Console.Error.WriteLine($"[{DateTimeOffset.UtcNow:O}] {message}");
    }

    public static void Warn(string message)
    {
        Console.WriteLine($"[{DateTimeOffset.UtcNow:O}] [WARN] {message}");
    }
}
