using Seiton.Update.Generators;
using Seiton.Update.Model;

namespace Seiton.Update.Tests;

public sealed class WebhookTypesCSharpGeneratorTests
{
    [Test]
    public async Task Generate_IsTypeAllowedConditions_AreAlphabeticallyOrdered()
    {
        var events = new[]
        {
            new WebhookEventModel("project", ["created", "updated", "closed", "reopened", "edited", "deleted"]),
            new WebhookEventModel("project_card", ["created", "moved", "converted", "edited", "deleted"]),
        };

        var generator = new WebhookTypesCSharpGenerator();
        var output = generator.Generate(events, new Dictionary<string, string[]>(StringComparer.Ordinal));

        await Assert.That(output).Contains(
            "EventId.Project => valueUtf8.SequenceEqual(\"closed\"u8) || valueUtf8.SequenceEqual(\"created\"u8) || valueUtf8.SequenceEqual(\"deleted\"u8) || valueUtf8.SequenceEqual(\"edited\"u8) || valueUtf8.SequenceEqual(\"reopened\"u8) || valueUtf8.SequenceEqual(\"updated\"u8),");
        await Assert.That(output).Contains(
            "EventId.ProjectCard => valueUtf8.SequenceEqual(\"converted\"u8) || valueUtf8.SequenceEqual(\"created\"u8) || valueUtf8.SequenceEqual(\"deleted\"u8) || valueUtf8.SequenceEqual(\"edited\"u8) || valueUtf8.SequenceEqual(\"moved\"u8),");
    }
}
