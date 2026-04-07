using VYaml.Parser;

namespace Seiton.Core.Parsing;

public static class WorkflowParser
{
    public static ParseResult Parse(byte[] utf8Yaml, string filePath)
    {
        var diagnostics = new List<Diagnostic>(16);
        var reader = new VYamlStreamReader(utf8Yaml.AsMemory());

        reader.SkipHeader();

        if (reader.CurrentEventType != ParseEventType.MappingStart)
        {
            AddError(diagnostics, "workflow root must be mapping", reader.CurrentMark);
            return new ParseResult(default, diagnostics.ToArray(), HasFatalError: true);
        }

        reader.Read(); // skip MappingStart

        var hasName = false;
        var hasRunName = false;
        var hasOn = false;
        var hasJobs = false;
        Utf8Slice name = default;
        Utf8Slice runName = default;

        while (!reader.End && reader.CurrentEventType != ParseEventType.MappingEnd)
        {
            if (reader.CurrentEventType != ParseEventType.Scalar)
            {
                AddError(diagnostics, "workflow key must be scalar", reader.CurrentMark);
                reader.SkipCurrentNode();
                if (!reader.End && reader.CurrentEventType != ParseEventType.MappingEnd)
                {
                    reader.SkipCurrentNode();
                }
                continue;
            }

            var keyMark = reader.CurrentMark;
            var keyText = reader.GetScalarString() ?? string.Empty;
            reader.Read(); // consume key

            if (string.Equals(keyText, "name", StringComparison.Ordinal))
            {
                hasName = true;
                name = ReadScalarOrSkip(ref reader, diagnostics, "name must be scalar");
                continue;
            }

            if (string.Equals(keyText, "run-name", StringComparison.Ordinal))
            {
                hasRunName = true;
                runName = ReadScalarOrSkip(ref reader, diagnostics, "run-name must be scalar");
                continue;
            }

            if (string.Equals(keyText, "on", StringComparison.Ordinal))
            {
                hasOn = true;
                if (!reader.End)
                {
                    if (reader.CurrentEventType is not ParseEventType.Scalar and not ParseEventType.MappingStart and not ParseEventType.SequenceStart)
                    {
                        AddError(diagnostics, "on must be scalar, mapping, or sequence", reader.CurrentMark);
                    }
                    reader.SkipCurrentNode();
                }
                continue;
            }

            if (string.Equals(keyText, "jobs", StringComparison.Ordinal))
            {
                hasJobs = true;
                if (!reader.End)
                {
                    if (reader.CurrentEventType != ParseEventType.MappingStart)
                    {
                        AddError(diagnostics, "jobs must be mapping", reader.CurrentMark);
                    }
                    reader.SkipCurrentNode();
                }
                continue;
            }

            if (string.Equals(keyText, "permissions", StringComparison.Ordinal) ||
                string.Equals(keyText, "env", StringComparison.Ordinal) ||
                string.Equals(keyText, "defaults", StringComparison.Ordinal) ||
                string.Equals(keyText, "concurrency", StringComparison.Ordinal))
            {
                if (!reader.End)
                {
                    reader.SkipCurrentNode();
                }
                continue;
            }

            AddError(diagnostics, $"unexpected workflow key: {keyText}", keyMark);
            if (!reader.End)
            {
                reader.SkipCurrentNode();
            }
        }

        if (reader.CurrentEventType == ParseEventType.MappingEnd)
        {
            reader.Read();
        }

        if (!hasOn)
        {
            AddError(diagnostics, "required key 'on' is missing", new Marker(0, 1, 1));
        }

        if (!hasJobs)
        {
            AddError(diagnostics, "required key 'jobs' is missing", new Marker(0, 1, 1));
        }

        var document = new WorkflowDocument(
            HasName: hasName,
            Name: name,
            HasRunName: hasRunName,
            RunName: runName,
            HasOn: hasOn,
            HasJobs: hasJobs);

        return new ParseResult(document, diagnostics.ToArray(), HasFatalError: false);
    }

    private static Utf8Slice ReadScalarOrSkip(ref VYamlStreamReader reader, List<Diagnostic> diagnostics, string errorMessage)
    {
        if (reader.End)
        {
            return default;
        }

        if (reader.CurrentEventType != ParseEventType.Scalar)
        {
            AddError(diagnostics, errorMessage, reader.CurrentMark);
            reader.SkipCurrentNode();
            return default;
        }

        var slice = reader.GetScalarSlice();
        reader.Read();
        return slice;
    }

    private static void AddError(List<Diagnostic> diagnostics, string message, Marker mark)
    {
        var location = new TextRange(
            Start: mark.Position,
            Length: 0,
            StartLine: mark.Line,
            StartColumn: mark.Col,
            EndLine: mark.Line,
            EndColumn: mark.Col);

        diagnostics.Add(new Diagnostic(DiagnosticSeverity.Error, message, location));
    }
}
