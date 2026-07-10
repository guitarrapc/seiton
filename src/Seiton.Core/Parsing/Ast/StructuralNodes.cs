namespace Seiton.Core.Parsing.Ast;

// Section nodes are arena-pooled (see AstArena.Alloc* methods): properties are settable
// and each class has an internal Reset() releasing references, mirroring Job/Step.

/// <summary>The <c>permissions:</c> block (either a single keyword or per-scope map).</summary>
public sealed class Permissions
{
    public StringNodeId All { get; set; }

    public SliceMap<PermissionScope>? Scopes { get; set; }

    public TextRange Range { get; set; }

    internal void Reset()
    {
        All = default;
        Scopes = null;
        Range = default;
    }
}

/// <summary>A single permission scope entry (e.g. <c>contents: read</c>).</summary>
public readonly struct PermissionScope
{
    public StringNodeId Name { get; init; }

    public Utf8Slice NameText { get; init; }

    public StringNodeId Value { get; init; }

    public Utf8Slice ValueText { get; init; }
}

/// <summary>The <c>env:</c> block containing environment variable definitions.</summary>
public sealed class Env
{
    public StringNodeId Expression { get; set; }

    public SliceMap<EnvVar>? Vars { get; set; }

    public TextRange Range { get; set; }

    internal void Reset()
    {
        Expression = default;
        Vars = null;
        Range = default;
    }
}

/// <summary>A single environment variable (key-value pair).</summary>
public readonly struct EnvVar
{
    public StringNodeId Name { get; init; }

    public StringNodeId Value { get; init; }
}

/// <summary>The <c>defaults:</c> section.</summary>
public sealed class Defaults
{
    public DefaultsRun Run { get; set; } = null!;

    public TextRange Range { get; set; }

    internal void Reset()
    {
        Run = null!;
        Range = default;
    }
}

/// <summary>The <c>defaults.run:</c> section (default shell and working directory).</summary>
public sealed class DefaultsRun
{
    public StringNodeId Shell { get; set; }

    public StringNodeId WorkingDirectory { get; set; }

    public TextRange Range { get; set; }

    internal void Reset()
    {
        Shell = default;
        WorkingDirectory = default;
        Range = default;
    }
}

/// <summary>The <c>concurrency:</c> block.</summary>
public sealed class Concurrency
{
    public StringNodeId Group { get; set; }

    public BoolNodeId CancelInProgress { get; set; }

    public StringNodeId Queue { get; set; }

    public TextRange Range { get; set; }

    internal void Reset()
    {
        Group = default;
        CancelInProgress = default;
        Queue = default;
        Range = default;
    }
}

/// <summary>The <c>environment:</c> block for deployment environments.</summary>
public sealed class Environment
{
    public StringNodeId Name { get; set; }

    public StringNodeId Url { get; set; }

    public BoolNodeId Deployment { get; set; }

    public TextRange Range { get; set; }

    internal void Reset()
    {
        Name = default;
        Url = default;
        Deployment = default;
        Range = default;
    }
}

/// <summary>The <c>runs-on:</c> specification for job runner selection.</summary>
public sealed class Runner
{
    public IReadOnlyList<StringNodeId>? Labels { get; set; }

    public StringNodeId LabelsExpr { get; set; }

    public StringNodeId Group { get; set; }

    public TextRange Range { get; set; }

    internal void Reset()
    {
        Labels = null;
        LabelsExpr = default;
        Group = default;
        Range = default;
    }
}

/// <summary>The <c>strategy:</c> block for job execution strategy.</summary>
public sealed class Strategy
{
    public Matrix? Matrix { get; set; }

    public BoolNodeId FailFast { get; set; }

    public IntNodeId MaxParallel { get; set; }

    public TextRange Range { get; set; }

    internal void Reset()
    {
        Matrix = null;
        FailFast = default;
        MaxParallel = default;
        Range = default;
    }
}

/// <summary>The <c>matrix:</c> block defining build matrix dimensions.</summary>
public sealed class Matrix
{
    public StringNodeId Expression { get; set; }

    public IReadOnlyList<MatrixCombinations>? Include { get; set; }

    public IReadOnlyList<MatrixCombinations>? Exclude { get; set; }

    public SliceMap<MatrixRow>? Rows { get; set; }

    public TextRange Range { get; set; }

    internal void Reset()
    {
        Expression = default;
        Include = null;
        Exclude = null;
        Rows = null;
        Range = default;
    }
}

/// <summary>A single row (dimension) in a matrix definition.</summary>
public sealed class MatrixRow
{
    public StringNodeId Expression { get; set; }

    public IReadOnlyList<RawYamlValue>? Values { get; set; }

    public StringNodeId Name { get; set; }

    internal void Reset()
    {
        Expression = default;
        Values = null;
        Name = default;
    }
}

/// <summary>Matrix include/exclude combination entries.</summary>
public sealed class MatrixCombinations
{
    public StringNodeId Expression { get; set; }

    public IReadOnlyList<SliceMap<RawYamlValue>>? Entries { get; set; }

    internal void Reset()
    {
        Expression = default;
        Entries = null;
    }
}

/// <summary>Base class for unstructured YAML values in matrix entries.</summary>
public abstract class RawYamlValue
{
    /// <summary>Source location of this value node. Used for diagnostic reporting.</summary>
    public TextRange Range { get; set; }
}

/// <summary>A raw YAML scalar text value.</summary>
public sealed class RawYamlString : RawYamlValue
{
    public StringNodeId Value { get; set; }

    internal void Reset()
    {
        Value = default;
        Range = default;
    }
}

/// <summary>A raw YAML array value.</summary>
public sealed class RawYamlArray : RawYamlValue
{
    public IReadOnlyList<RawYamlValue> Items { get; set; } = [];

    internal void Reset()
    {
        Items = [];
        Range = default;
    }
}

/// <summary>A raw YAML mapping value.</summary>
public sealed class RawYamlObject : RawYamlValue
{
    public SliceMap<RawYamlValue> Properties { get; set; }

    internal void Reset()
    {
        Properties = default;
        Range = default;
    }
}

/// <summary>The <c>container:</c> block for job containerization.</summary>
public sealed class Container
{
    public StringNodeId Image { get; set; }

    public Credentials? Credentials { get; set; }

    public Env? Env { get; set; }

    public IReadOnlyList<StringNodeId>? Ports { get; set; }

    public IReadOnlyList<StringNodeId>? Volumes { get; set; }

    public StringNodeId Options { get; set; }

    public StringNodeId Entrypoint { get; set; }

    public StringNodeId Command { get; set; }

    public TextRange Range { get; set; }

    internal void Reset()
    {
        Image = default;
        Credentials = null;
        Env = null;
        Ports = null;
        Volumes = null;
        Options = default;
        Entrypoint = default;
        Command = default;
        Range = default;
    }
}

/// <summary>The <c>services:</c> block for job service containers.</summary>
public sealed class Services
{
    public StringNodeId Expression { get; set; }

    public SliceMap<Service>? ServiceMap { get; set; }

    public TextRange Range { get; set; }

    internal void Reset()
    {
        Expression = default;
        ServiceMap = null;
        Range = default;
    }
}

/// <summary>A single service container definition.</summary>
public sealed class Service
{
    public StringNodeId Name { get; set; }

    public Container Container { get; set; } = null!;

    public TextRange Range { get; set; }

    internal void Reset()
    {
        Name = default;
        Container = null!;
        Range = default;
    }
}

/// <summary>Registry credentials for a container image.</summary>
public sealed class Credentials
{
    public StringNodeId Username { get; set; }

    public StringNodeId Password { get; set; }

    public StringNodeId Expression { get; set; }

    public TextRange Range { get; set; }

    internal void Reset()
    {
        Username = default;
        Password = default;
        Expression = default;
        Range = default;
    }
}

/// <summary>A reusable workflow call (<c>uses:</c> at job level).</summary>
public sealed class WorkflowCall
{
    public StringNodeId Uses { get; set; }

    public TextRange? UsesKeyRange { get; set; }

    public SliceMap<WorkflowCallInput>? Inputs { get; set; }

    public TextRange? WithKeyRange { get; set; }

    public SliceMap<WorkflowCallSecret>? Secrets { get; set; }

    public TextRange? SecretsKeyRange { get; set; }

    public bool InheritSecrets { get; set; }

    internal void Reset()
    {
        Uses = default;
        UsesKeyRange = null;
        Inputs = null;
        WithKeyRange = null;
        Secrets = null;
        SecretsKeyRange = null;
        InheritSecrets = false;
    }
}

/// <summary>An input passed to a reusable workflow call.</summary>
public readonly struct WorkflowCallInput
{
    public StringNodeId Name { get; init; }

    public StringNodeId Value { get; init; }
}

/// <summary>A secret passed to a reusable workflow call.</summary>
public readonly struct WorkflowCallSecret
{
    public StringNodeId Name { get; init; }

    public StringNodeId Value { get; init; }
}
