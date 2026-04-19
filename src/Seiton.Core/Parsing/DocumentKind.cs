namespace Seiton.Core.Parsing;

public enum DocumentKind
{
    Unknown,
    Workflow,
    ActionMetadata,
}

public readonly record struct DocumentKindClassification(
    DocumentKind PathHintKind,
    DocumentKind FinalKind,
    bool HasHintMismatch,
    bool IsAmbiguous);

public readonly record struct ClassifiedParseResult(
    ParseResult ParseResult,
    DocumentKindClassification Classification);

public static class DocumentKindClassifier
{
    public static DocumentKind GetPathHintKind(string filePath)
    {
        if (string.IsNullOrEmpty(filePath))
        {
            return DocumentKind.Unknown;
        }

        var normalized = filePath.Replace('\\', '/');
        var fileName = Path.GetFileName(normalized);
        if (fileName.Equals("action.yml", StringComparison.OrdinalIgnoreCase) ||
            fileName.Equals("action.yaml", StringComparison.OrdinalIgnoreCase))
        {
            return DocumentKind.ActionMetadata;
        }

        if (IsGithubActionsActionMetadataPath(normalized))
        {
            return DocumentKind.ActionMetadata;
        }

        return DocumentKind.Unknown;
    }

    public static DocumentKind FinalizeKind(DocumentKind pathHintKind, bool hasJobs, bool hasRuns, out bool isAmbiguous, out bool hasHintMismatch)
    {
        isAmbiguous = false;
        hasHintMismatch = false;

        DocumentKind finalKind;
        if (hasJobs && hasRuns)
        {
            isAmbiguous = true;
            finalKind = DocumentKind.Unknown;
        }
        else if (hasJobs)
        {
            finalKind = DocumentKind.Workflow;
        }
        else if (hasRuns)
        {
            finalKind = DocumentKind.ActionMetadata;
        }
        else
        {
            finalKind = DocumentKind.Unknown;
        }

        hasHintMismatch =
            pathHintKind != DocumentKind.Unknown &&
            finalKind != DocumentKind.Unknown &&
            pathHintKind != finalKind;

        return finalKind;
    }

    static bool IsGithubActionsActionMetadataPath(string normalizedPath)
    {
        var marker = "/.github/actions/";
        var markerIndex = normalizedPath.IndexOf(marker, StringComparison.OrdinalIgnoreCase);
        if (markerIndex < 0)
        {
            return false;
        }

        var suffix = normalizedPath[(markerIndex + marker.Length)..];
        var slash = suffix.IndexOf('/');
        if (slash <= 0)
        {
            return false;
        }

        var tail = suffix[(slash + 1)..];
        return tail.Equals("action.yml", StringComparison.OrdinalIgnoreCase)
            || tail.Equals("action.yaml", StringComparison.OrdinalIgnoreCase);
    }
}
