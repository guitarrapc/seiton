using Seiton.Cli.Parsing;

if (args.Length < 2 || !string.Equals(args[0], "parse", StringComparison.OrdinalIgnoreCase))
{
	Console.WriteLine("Usage: Seiton.Cli parse <workflow.yml>");
	return 1;
}

var filePath = args[1];
if (!File.Exists(filePath))
{
	Console.Error.WriteLine($"File not found: {filePath}");
	return 2;
}

var bytes = File.ReadAllBytes(filePath);
var result = WorkflowParser.Parse(bytes, filePath);

foreach (var diagnostic in result.Diagnostics)
{
	Console.WriteLine($"{diagnostic.Severity}: {diagnostic.Message} ({diagnostic.Location.StartLine}:{diagnostic.Location.StartColumn})");
}

Console.WriteLine($"Parsed: HasOn={result.Workflow.HasOn}, HasJobs={result.Workflow.HasJobs}, Diagnostics={result.Diagnostics.Length}");
return result.HasFatalError ? 2 : 0;
