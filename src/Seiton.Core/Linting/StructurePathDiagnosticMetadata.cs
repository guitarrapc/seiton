using Seiton.Core.Linting.PinRemediation;

namespace Seiton.Core.Linting;

/// <summary>Builds <see cref="DiagnosticStructurePathMetadata"/> payloads for diagnostics.</summary>
internal static class StructurePathDiagnosticMetadata
{
    public static IReadOnlyDictionary<string, string> For(string structurePath) =>
        new PinSingleEntryReadOnlyDictionary(DiagnosticStructurePathMetadata.Key, structurePath);
}
