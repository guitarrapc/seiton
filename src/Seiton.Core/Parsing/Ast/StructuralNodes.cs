namespace Seiton.Core.Parsing.Ast;

/// <summary>The <c>permissions:</c> block (either a single keyword or per-scope map).</summary>
public sealed class Permissions
{
    public StringNodeId All { get; init; }

    public SliceMap<PermissionScope>? Scopes { get; init; }

    public TextRange Range { get; init; }
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
    public StringNodeId Expression { get; init; }

    public SliceMap<EnvVar>? Vars { get; init; }

    public TextRange Range { get; init; }
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
    public DefaultsRun Run { get; init; } = null!;

    public TextRange Range { get; init; }
}

/// <summary>The <c>defaults.run:</c> section (default shell and working directory).</summary>
public sealed class DefaultsRun
{
    public StringNodeId Shell { get; init; }

    public StringNodeId WorkingDirectory { get; init; }

    public TextRange Range { get; init; }
}

/// <summary>The <c>concurrency:</c> block.</summary>
public sealed class Concurrency
{
    public StringNodeId Group { get; init; }

    public BoolNodeId CancelInProgress { get; init; }

    public StringNodeId Queue { get; init; }

    public TextRange Range { get; init; }
}

/// <summary>The <c>environment:</c> block for deployment environments.</summary>
public sealed class Environment
{
    public StringNodeId Name { get; init; }

    public StringNodeId Url { get; init; }

    public BoolNodeId Deployment { get; init; }

    public TextRange Range { get; init; }
}

/// <summary>The <c>runs-on:</c> specification for job runner selection.</summary>
public sealed class Runner
{
    public IReadOnlyList<StringNodeId>? Labels { get; init; }

    public StringNodeId LabelsExpr { get; init; }

    public StringNodeId Group { get; init; }

    public TextRange Range { get; init; }
}

/// <summary>The <c>strategy:</c> block for job execution strategy.</summary>
public sealed class Strategy
{
    public Matrix? Matrix { get; init; }

    public BoolNodeId FailFast { get; init; }

    public IntNodeId MaxParallel { get; init; }

    public TextRange Range { get; init; }
}

/// <summary>The <c>matrix:</c> block defining build matrix dimensions.</summary>
public sealed class Matrix
{
    public StringNodeId Expression { get; init; }

    public IReadOnlyList<MatrixCombinations>? Include { get; init; }

    public IReadOnlyList<MatrixCombinations>? Exclude { get; init; }

    public SliceMap<MatrixRow>? Rows { get; init; }

    public TextRange Range { get; init; }
}

/// <summary>A single row (dimension) in a matrix definition.</summary>
public sealed class MatrixRow
{
    public StringNodeId Expression { get; init; }

    public IReadOnlyList<RawYamlValue>? Values { get; init; }

    public StringNodeId Name { get; init; }
}

/// <summary>Matrix include/exclude combination entries.</summary>
public sealed class MatrixCombinations
{
    public StringNodeId Expression { get; init; }

    public IReadOnlyList<SliceMap<RawYamlValue>>? Entries { get; init; }
}

/// <summary>Base class for unstructured YAML values in matrix entries.</summary>
public abstract class RawYamlValue
{
    /// <summary>Source location of this value node. Used for diagnostic reporting.</summary>
    public TextRange Range { get; init; }
}

/// <summary>A raw YAML scalar text value.</summary>
public sealed class RawYamlString : RawYamlValue
{
    public StringNodeId Value { get; init; }
}

/// <summary>A raw YAML array value.</summary>
public sealed class RawYamlArray : RawYamlValue
{
    public IReadOnlyList<RawYamlValue> Items { get; init; } = [];
}

/// <summary>A raw YAML mapping value.</summary>
public sealed class RawYamlObject : RawYamlValue
{
    public SliceMap<RawYamlValue> Properties { get; init; }
}

/// <summary>The <c>container:</c> block for job containerization.</summary>
public sealed class Container
{
    public StringNodeId Image { get; init; }

    public Credentials? Credentials { get; init; }

    public Env? Env { get; init; }

    public IReadOnlyList<StringNodeId>? Ports { get; init; }

    public IReadOnlyList<StringNodeId>? Volumes { get; init; }

    public StringNodeId Options { get; init; }

    public StringNodeId Entrypoint { get; init; }

    public StringNodeId Command { get; init; }

    public TextRange Range { get; init; }
}

/// <summary>The <c>services:</c> block for job service containers.</summary>
public sealed class Services
{
    public StringNodeId Expression { get; init; }

    public SliceMap<Service>? ServiceMap { get; init; }

    public TextRange Range { get; init; }
}

/// <summary>A single service container definition.</summary>
public sealed class Service
{
    public StringNodeId Name { get; init; }

    public Container Container { get; init; } = null!;

    public TextRange Range { get; init; }
}

/// <summary>Registry credentials for a container image.</summary>
public sealed class Credentials
{
    public StringNodeId Username { get; init; }

    public StringNodeId Password { get; init; }

    public StringNodeId Expression { get; init; }

    public TextRange Range { get; init; }
}

/// <summary>A reusable workflow call (<c>uses:</c> at job level).</summary>
public sealed class WorkflowCall
{
    public StringNodeId Uses { get; init; }

    public TextRange? UsesKeyRange { get; init; }

    public SliceMap<WorkflowCallInput>? Inputs { get; init; }

    public TextRange? WithKeyRange { get; init; }

    public SliceMap<WorkflowCallSecret>? Secrets { get; init; }

    public TextRange? SecretsKeyRange { get; init; }

    public bool InheritSecrets { get; init; }
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
