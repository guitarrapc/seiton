using System.Reflection;

namespace Seiton.Commands;

internal static class CiWorkflowResources
{
    private static readonly Assembly ThisAssembly = typeof(CiWorkflowResources).Assembly;
    private const string ResourceName = "CiTemplates/seiton.yml";

    /// <summary>Get the CI workflow template content.</summary>
    public static string? GetWorkflowTemplate()
    {
        using var stream = ThisAssembly.GetManifestResourceStream(ResourceName);
        if (stream is null)
            return null;

        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }
}
