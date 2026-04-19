namespace Seiton.Core.Parsing.Ast;

public sealed class Permissions
{
    public StringNode? All { get; init; }

    public IReadOnlyDictionary<Utf8String, PermissionScope>? Scopes { get; init; }

    public TextRange Range { get; init; }
}

public sealed class PermissionScope
{
    public StringNode Name { get; init; } = null!;

    public Utf8String NameText { get; init; }

    public StringNode Value { get; init; } = null!;

    public Utf8String ValueText { get; init; }
}

public sealed class Env
{
    public StringNode? Expression { get; init; }

    public IReadOnlyDictionary<Utf8String, EnvVar>? Vars { get; init; }

    public TextRange Range { get; init; }
}

public sealed class EnvVar
{
    public StringNode Name { get; init; } = null!;

    public StringNode Value { get; init; } = null!;
}

public sealed class Defaults
{
    public DefaultsRun Run { get; init; } = null!;

    public TextRange Range { get; init; }
}

public sealed class DefaultsRun
{
    public StringNode? Shell { get; init; }

    public StringNode? WorkingDirectory { get; init; }

    public TextRange Range { get; init; }
}

public sealed class Concurrency
{
    public StringNode Group { get; init; } = null!;

    public BoolNode? CancelInProgress { get; init; }

    public TextRange Range { get; init; }
}

public sealed class Environment
{
    public StringNode Name { get; init; } = null!;

    public StringNode? Url { get; init; }

    public BoolNode? Deployment { get; init; }

    public TextRange Range { get; init; }
}

public sealed class Runner
{
    public IReadOnlyList<StringNode>? Labels { get; init; }

    public StringNode? LabelsExpr { get; init; }

    public StringNode? Group { get; init; }

    public TextRange Range { get; init; }
}

public sealed class Strategy
{
    public Matrix? Matrix { get; init; }

    public BoolNode? FailFast { get; init; }

    public IntNode? MaxParallel { get; init; }

    public TextRange Range { get; init; }
}

public sealed class Matrix
{
    public StringNode? Expression { get; init; }

    public IReadOnlyList<MatrixCombinations>? Include { get; init; }

    public IReadOnlyList<MatrixCombinations>? Exclude { get; init; }

    public IReadOnlyDictionary<Utf8String, MatrixRow>? Rows { get; init; }

    public TextRange Range { get; init; }
}

public sealed class MatrixRow
{
    public StringNode? Expression { get; init; }

    public IReadOnlyList<RawYamlValue>? Values { get; init; }

    public StringNode Name { get; init; } = null!;
}

public sealed class MatrixCombinations
{
    public StringNode? Expression { get; init; }

    public IReadOnlyList<IReadOnlyDictionary<Utf8String, RawYamlValue>>? Entries { get; init; }
}

public abstract class RawYamlValue
{
}

public sealed class RawYamlString : RawYamlValue
{
    public StringNode Value { get; init; } = null!;
}

public sealed class RawYamlArray : RawYamlValue
{
    public IReadOnlyList<RawYamlValue> Items { get; init; } = [];
}

public sealed class RawYamlObject : RawYamlValue
{
    public IReadOnlyDictionary<Utf8String, RawYamlValue> Properties { get; init; } = new Dictionary<Utf8String, RawYamlValue>();
}

public sealed class Container
{
    public StringNode Image { get; init; } = null!;

    public Credentials? Credentials { get; init; }

    public Env? Env { get; init; }

    public IReadOnlyList<StringNode>? Ports { get; init; }

    public IReadOnlyList<StringNode>? Volumes { get; init; }

    public StringNode? Options { get; init; }

    public TextRange Range { get; init; }
}

public sealed class Services
{
    public StringNode? Expression { get; init; }

    public IReadOnlyDictionary<Utf8String, Service>? ServiceMap { get; init; }

    public TextRange Range { get; init; }
}

public sealed class Service
{
    public StringNode Name { get; init; } = null!;

    public Container Container { get; init; } = null!;

    public TextRange Range { get; init; }
}

public sealed class Credentials
{
    public StringNode? Username { get; init; }

    public StringNode? Password { get; init; }

    public StringNode? Expression { get; init; }

    public TextRange Range { get; init; }
}

public sealed class WorkflowCall
{
    public StringNode Uses { get; init; } = null!;

    public TextRange? UsesKeyRange { get; init; }

    public IReadOnlyDictionary<Utf8String, WorkflowCallInput>? Inputs { get; init; }

    public IReadOnlyDictionary<Utf8String, WorkflowCallSecret>? Secrets { get; init; }

    public bool InheritSecrets { get; init; }
}

public sealed class WorkflowCallInput
{
    public StringNode Name { get; init; } = null!;

    public StringNode Value { get; init; } = null!;
}

public sealed class WorkflowCallSecret
{
    public StringNode Name { get; init; } = null!;

    public StringNode Value { get; init; } = null!;
}
