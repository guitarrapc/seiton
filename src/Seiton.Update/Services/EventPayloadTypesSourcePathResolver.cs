namespace Seiton.Update.Services;

internal static class EventPayloadTypesSourcePathResolver
{
    public static string ResolveRaw(string repoRoot)
    {
        var rawPath = Path.Combine(repoRoot, "data", "sources", "webhooks", "github", "raw", "webhook-events-and-payloads.html");
        if (File.Exists(rawPath))
        {
            return rawPath;
        }

        throw new FileNotFoundException(
            "Event payload types raw source not found. Run fetch-event-payload-types-sources first.",
            rawPath);
    }

    public static string ResolveRawDir(string repoRoot)
    {
        return Path.Combine(repoRoot, "data", "sources", "webhooks", "github", "raw");
    }

    public static string ResolveParsed(string repoRoot)
    {
        var parsedPath = Path.Combine(repoRoot, "data", "sources", "webhooks", "github", "parsed", "parsed-event-payload-types.json");
        if (File.Exists(parsedPath))
        {
            return parsedPath;
        }

        throw new FileNotFoundException(
            "Event payload types parsed source not found. Run parse-event-payload-types-sources first.",
            parsedPath);
    }

    public static string ResolveParsedDir(string repoRoot)
    {
        return Path.Combine(repoRoot, "data", "sources", "webhooks", "github", "parsed");
    }

    public static string ResolvePrimary(string repoRoot)
    {
        var snapshotPath = Path.Combine(repoRoot, "data", "sources", "webhooks", "github", "event_payload_types.json");
        if (File.Exists(snapshotPath))
        {
            return snapshotPath;
        }

        throw new FileNotFoundException(
            "Primary event_payload_types.json not found. Run parse-event-payload-types-sources first.",
            snapshotPath);
    }

    public static string ResolvePrimaryDir(string repoRoot)
    {
        return Path.Combine(repoRoot, "data", "sources", "webhooks", "github");
    }

    /// <summary>
    /// Legacy path where the hand-written event_payload_types.json used to live.
    /// </summary>
    public static string ResolveLegacy(string repoRoot)
    {
        return Path.Combine(repoRoot, "data", "sources", "webhooks", "event_payload_types.json");
    }
}
