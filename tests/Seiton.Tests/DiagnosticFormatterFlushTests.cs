using Seiton.Core.Parsing;
using Seiton.Output;

namespace Seiton.Tests;

public sealed class DiagnosticFormatterFlushTests
{
    [Test]
    [NotInParallel("Console")]
    public async Task FlushToStandardOutput_WithStringWriterRedirect_WritesDecodedText()
    {
        var original = Console.Out;
        var sb = new StringBuilder();
        using var redirected = new StringWriter(sb);

        try
        {
#pragma warning disable TUnit0055
            Console.SetOut(redirected);
#pragma warning restore TUnit0055

            DiagnosticFormatter.FlushToStandardOutput("line one\nline two\n"u8);

            await Assert.That(sb.ToString()).IsEqualTo("line one\nline two\n");
        }
        finally
        {
#pragma warning disable TUnit0055
            Console.SetOut(original);
#pragma warning restore TUnit0055
        }
    }

    [Test]
    [NotInParallel("Console")]
    public async Task FlushToStandardOutput_WithCustomTextWriterRedirect_WritesDecodedText()
    {
        var original = Console.Out;
        var capture = new CaptureTextWriter();

        try
        {
#pragma warning disable TUnit0055
            Console.SetOut(capture);
#pragma warning restore TUnit0055

            DiagnosticFormatter.FlushToStandardOutput("custom writer\n"u8);

            await Assert.That(capture.Buffer.ToString()).IsEqualTo("custom writer\n");
        }
        finally
        {
#pragma warning disable TUnit0055
            Console.SetOut(original);
#pragma warning restore TUnit0055
        }
    }

    [Test]
    [NotInParallel("Console")]
    public async Task FlushToStandardOutput_EmptySpan_IsNoOp()
    {
        var original = Console.Out;
        var sb = new StringBuilder();
        using var redirected = new StringWriter(sb);

        try
        {
#pragma warning disable TUnit0055
            Console.SetOut(redirected);
#pragma warning restore TUnit0055

            DiagnosticFormatter.FlushToStandardOutput(ReadOnlySpan<byte>.Empty);

            await Assert.That(sb.ToString()).IsEqualTo(string.Empty);
        }
        finally
        {
#pragma warning disable TUnit0055
            Console.SetOut(original);
#pragma warning restore TUnit0055
        }
    }

    [Test]
    [NotInParallel("Console")]
    public async Task WriteToStandardOutput_WithStringWriterRedirect_MatchesBufferPath()
    {
        var original = Console.Out;
        var sb = new StringBuilder();
        using var redirected = new StringWriter(sb);
        var diag = new Diagnostic(
            Severity: DiagnosticSeverity.Error,
            Message: "redirect path",
            Location: new TextRange(0, 0, 2, 1, 2, 5),
            RuleId: "test-rule",
            FilePath: "test.yml");

        try
        {
#pragma warning disable TUnit0055
            Console.SetOut(redirected);
#pragma warning restore TUnit0055

            DiagnosticFormatter.WriteToStandardOutput([diag], OutputFormat.Text, oneline: true, color: false);

            await Assert.That(sb.ToString().TrimEnd()).IsEqualTo("test.yml:2:1: error [test-rule] redirect path");
        }
        finally
        {
#pragma warning disable TUnit0055
            Console.SetOut(original);
#pragma warning restore TUnit0055
        }
    }

    private sealed class CaptureTextWriter : TextWriter
    {
        public StringBuilder Buffer { get; } = new();

        public override Encoding Encoding => Encoding.UTF8;

        public override void Write(char value) => Buffer.Append(value);

        public override void Write(char[] buffer, int index, int count) => Buffer.Append(buffer, index, count);

        public override void Write(ReadOnlySpan<char> buffer) => Buffer.Append(buffer);

        public override void Write(string? value)
        {
            if (value is not null)
            {
                Buffer.Append(value);
            }
        }
    }
}
