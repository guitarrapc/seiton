namespace Seiton.Core.Parsing.Ast;

public sealed class Permissions
{
    public StringNodeId All { get; init; }

    public SliceMap<PermissionScope>? Scopes { get; init; }

    public TextRange Range { get; init; }
}

public readonly struct PermissionScope
{
    public StringNodeId Name { get; init; }

    public Utf8Slice NameText { get; init; }

    public StringNodeId Value { get; init; }

    public Utf8Slice ValueText { get; init; }
}

public sealed class Env
{
    public StringNodeId Expression { get; init; }

    public SliceMap<EnvVar>? Vars { get; init; }

    public TextRange Range { get; init; }
}

public readonly struct EnvVar
{
    public StringNodeId Name { get; init; }

    public StringNodeId Value { get; init; }
}

public sealed class Defaults
{
    public DefaultsRun Run { get; init; } = null!;

    public TextRange Range { get; init; }
}

public sealed class DefaultsRun
{
    public StringNodeId Shell { get; init; }

    public StringNodeId WorkingDirectory { get; init; }

    public TextRange Range { get; init; }
}

public sealed class Concurrency
{
    public StringNodeId Group { get; init; }

    public BoolNodeId CancelInProgress { get; init; }

    public TextRange Range { get; init; }
}

public sealed class Environment
{
    public StringNodeId Name { get; init; }

    public StringNodeId Url { get; init; }

    public BoolNodeId Deployment { get; init; }

    public TextRange Range { get; init; }
}

public sealed class Runner
{
    public StringNodeId[]? Labels { get; init; }

    public StringNodeId LabelsExpr { get; init; }

    public StringNodeId Group { get; init; }

    public TextRange Range { get; init; }
}

public sealed class Strategy
{
    public Matrix? Matrix { get; init; }

    public BoolNodeId FailFast { get; init; }

    public IntNodeId MaxParallel { get; init; }

    public TextRange Range { get; init; }
}

public sealed class Matrix
{
    public StringNodeId Expression { get; init; }

    public IReadOnlyList<MatrixCombinations>? Include { get; init; }

    public IReadOnlyList<MatrixCombinations>? Exclude { get; init; }

    public SliceMap<MatrixRow>? Rows { get; init; }

    public TextRange Range { get; init; }
}

public sealed class MatrixRow
{
    public StringNodeId Expression { get; init; }

    public IReadOnlyList<RawYamlValue>? Values { get; init; }

    public StringNodeId Name { get; init; }
}

public sealed class MatrixCombinations
{
    public StringNodeId Expression { get; init; }

    public IReadOnlyList<SliceMap<RawYamlValue>>? Entries { get; init; }
}

public abstract class RawYamlValue
{
}

public sealed class RawYamlString : RawYamlValue
{
    public StringNodeId Value { get; init; }
}

public sealed class RawYamlArray : RawYamlValue
{
    public IReadOnlyList<RawYamlValue> Items { get; init; } = [];
}

public sealed class RawYamlObject : RawYamlValue
{
    public SliceMap<RawYamlValue> Properties { get; init; }
}

public sealed class Container
{
    public StringNodeId Image { get; init; }

    public Credentials? Credentials { get; init; }

    public Env? Env { get; init; }

    public StringNodeId[]? Ports { get; init; }

    public StringNodeId[]? Volumes { get; init; }

    public StringNodeId Options { get; init; }

    public TextRange Range { get; init; }
}

public sealed class Services
{
    public StringNodeId Expression { get; init; }

    public SliceMap<Service>? ServiceMap { get; init; }

    public TextRange Range { get; init; }
}

public sealed class Service
{
    public StringNodeId Name { get; init; }

    public Container Container { get; init; } = null!;

    public TextRange Range { get; init; }
}

public sealed class Credentials
{
    public StringNodeId Username { get; init; }

    public StringNodeId Password { get; init; }

    public StringNodeId Expression { get; init; }

    public TextRange Range { get; init; }
}

public sealed class WorkflowCall
{
    public StringNodeId Uses { get; init; }

    public TextRange? UsesKeyRange { get; init; }

    public SliceMap<WorkflowCallInput>? Inputs { get; init; }

    public SliceMap<WorkflowCallSecret>? Secrets { get; init; }

    public bool InheritSecrets { get; init; }
}

public readonly struct WorkflowCallInput
{
    public StringNodeId Name { get; init; }

    public StringNodeId Value { get; init; }
}

public readonly struct WorkflowCallSecret
{
    public StringNodeId Name { get; init; }

    public StringNodeId Value { get; init; }
}
