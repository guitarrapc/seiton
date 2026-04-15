namespace Seiton.Update;

internal static class TextNormalization
{
    public static string NormalizeToLf(string text)
    {
        return text.IndexOf('\r') < 0
            ? text
            : text.ReplaceLineEndings("\n");
    }
}
