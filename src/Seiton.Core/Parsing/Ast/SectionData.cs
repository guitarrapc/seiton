namespace Seiton.Core.Parsing.Ast;

// Data-oriented AST rows. Each *Data struct is a row in an AstArena table,
// addressed by the matching typed ID in NodeIds.cs. Rows hold only scalar handles,
// other node IDs, and ranges — never object references or strings.
// See `.github/docs/architecture_spec_ast.md`.

/// <summary>Row data for the <c>concurrency:</c> block.</summary>
public readonly struct ConcurrencyData
{
    public StringNodeId Group { get; init; }

    public BoolNodeId CancelInProgress { get; init; }

    public StringNodeId Queue { get; init; }

    public TextRange Range { get; init; }
}

/// <summary>Row data for the <c>environment:</c> block.</summary>
public readonly struct EnvironmentData
{
    public StringNodeId Name { get; init; }

    public StringNodeId Url { get; init; }

    public BoolNodeId Deployment { get; init; }

    public TextRange Range { get; init; }
}

/// <summary>Row data for container registry credentials.</summary>
public readonly struct CredentialsData
{
    public StringNodeId Username { get; init; }

    public StringNodeId Password { get; init; }

    public StringNodeId Expression { get; init; }

    public TextRange Range { get; init; }
}

/// <summary>Row data for a job <c>snapshot:</c> configuration.</summary>
public readonly struct SnapshotData
{
    public StringNodeId Version { get; init; }

    public StringNodeId ImageName { get; init; }

    public StringNodeId If { get; init; }

    public TextRange? IfKeyRange { get; init; }
}

/// <summary>Row data for the <c>strategy:</c> block.</summary>
public readonly struct StrategyData
{
    public MatrixId Matrix { get; init; }

    public BoolNodeId FailFast { get; init; }

    public IntNodeId MaxParallel { get; init; }

    public TextRange Range { get; init; }
}

/// <summary>Row data for the <c>matrix:</c> block.</summary>
public readonly struct MatrixData
{
    /// <summary>The whole-matrix <c>${{ }}</c> expression, if used.</summary>
    public StringNodeId Expression { get; init; }

    /// <summary>Range over the matrix-combinations row table (<c>include:</c>).</summary>
    public NodeRange Include { get; init; }

    /// <summary>Range over the matrix-combinations row table (<c>exclude:</c>).</summary>
    public NodeRange Exclude { get; init; }

    /// <summary>Range over the matrix-row table. Key lookup is case-insensitive.</summary>
    public NodeRange Rows { get; init; }

    public TextRange Range { get; init; }
}

/// <summary>Row data for a single matrix row (dimension). Key lookup is case-insensitive.</summary>
public readonly struct MatrixRowData
{
    /// <summary>The raw map key slice used for lookup.</summary>
    public Utf8Slice Key { get; init; }

    public StringNodeId Name { get; init; }

    /// <summary>The whole-row <c>${{ }}</c> expression, if used.</summary>
    public StringNodeId Expression { get; init; }

    /// <summary>Range over the raw-yaml id-list store (the row's values).</summary>
    public NodeRange Values { get; init; }
}

/// <summary>Row data for matrix include/exclude combination entries.</summary>
public readonly struct MatrixCombinationsData
{
    /// <summary>The whole-block <c>${{ }}</c> expression, if used.</summary>
    public StringNodeId Expression { get; init; }

    /// <summary>Range over the combination-entry list store (each element is a raw-yaml prop range).</summary>
    public NodeRange Entries { get; init; }
}

/// <summary>Tagged-union row data for an unstructured YAML value (matrix entries).</summary>
public readonly struct RawYamlData
{
    public RawYamlKind Kind { get; init; }

    /// <summary>The scalar value when <see cref="Kind"/> is <see cref="RawYamlKind.String"/>.</summary>
    public StringNodeId Scalar { get; init; }

    /// <summary>Range over the raw-yaml id-list store when <see cref="Kind"/> is <see cref="RawYamlKind.Array"/>.</summary>
    public NodeRange Items { get; init; }

    /// <summary>Range over the raw-yaml prop table when <see cref="Kind"/> is <see cref="RawYamlKind.Object"/>.</summary>
    public NodeRange Properties { get; init; }

    public TextRange Range { get; init; }
}

/// <summary>Row data for one property of a raw-yaml mapping. Key lookup is case-insensitive.</summary>
public readonly struct RawYamlPropData
{
    /// <summary>The raw map key slice used for lookup.</summary>
    public Utf8Slice Key { get; init; }

    public RawYamlId Value { get; init; }
}

/// <summary>Row data for the <c>permissions:</c> block.</summary>
public readonly struct PermissionsData
{
    /// <summary>The scalar form (<c>read-all</c> / <c>write-all</c>), if used.</summary>
    public StringNodeId All { get; init; }

    /// <summary>Range over the permission-scope row table.</summary>
    public NodeRange Scopes { get; init; }

    public TextRange Range { get; init; }
}

/// <summary>Row data for a single permission scope entry (e.g. <c>contents: read</c>). Key lookup is case-sensitive.</summary>
public readonly struct PermissionScopeData
{
    /// <summary>The raw map key slice used for lookup.</summary>
    public Utf8Slice Key { get; init; }

    public StringNodeId Name { get; init; }

    public Utf8Slice NameText { get; init; }

    public StringNodeId Value { get; init; }

    public Utf8Slice ValueText { get; init; }
}

/// <summary>Row data for the <c>env:</c> block (mapping form or whole-map expression form).</summary>
public readonly struct EnvData
{
    /// <summary>The whole-map <c>${{ }}</c> expression, if used instead of a mapping.</summary>
    public StringNodeId Expression { get; init; }

    /// <summary>Range over the env-var row table.</summary>
    public NodeRange Vars { get; init; }

    public TextRange Range { get; init; }
}

/// <summary>Row data for a single environment variable. Key lookup is case-sensitive.</summary>
public readonly struct EnvVarData
{
    /// <summary>The raw map key slice used for lookup.</summary>
    public Utf8Slice Key { get; init; }

    public StringNodeId Name { get; init; }

    public StringNodeId Value { get; init; }
}

/// <summary>Row data for the <c>container:</c> block.</summary>
public readonly struct ContainerData
{
    public StringNodeId Image { get; init; }

    public CredentialsId Credentials { get; init; }

    public EnvId Env { get; init; }

    public StringIdRange Ports { get; init; }

    public StringIdRange Volumes { get; init; }

    public StringNodeId Options { get; init; }

    public StringNodeId Entrypoint { get; init; }

    public StringNodeId Command { get; init; }

    public TextRange Range { get; init; }
}

/// <summary>Row data for the <c>services:</c> block (mapping form or whole-map expression form).</summary>
public readonly struct ServicesData
{
    /// <summary>The whole-map <c>${{ }}</c> expression, if used instead of a mapping.</summary>
    public StringNodeId Expression { get; init; }

    /// <summary>Range over the service row table. Key lookup is case-insensitive.</summary>
    public NodeRange ServiceMap { get; init; }

    public TextRange Range { get; init; }
}

/// <summary>Row data for a single service container definition. Key lookup is case-insensitive.</summary>
public readonly struct ServiceData
{
    /// <summary>The raw map key slice used for lookup.</summary>
    public Utf8Slice Key { get; init; }

    public StringNodeId Name { get; init; }

    public ContainerId Container { get; init; }

    public TextRange Range { get; init; }
}

/// <summary>Row data for a reusable workflow call (<c>uses:</c> at job level).</summary>
public readonly struct WorkflowCallData
{
    public StringNodeId Uses { get; init; }

    public TextRange? UsesKeyRange { get; init; }

    /// <summary>Range over the workflow-call input row table (<c>with:</c>).</summary>
    public NodeRange Inputs { get; init; }

    public TextRange? WithKeyRange { get; init; }

    /// <summary>Range over the workflow-call secret row table.</summary>
    public NodeRange Secrets { get; init; }

    public TextRange? SecretsKeyRange { get; init; }

    public bool InheritSecrets { get; init; }
}

/// <summary>Row data for an input passed to a reusable workflow call. Key lookup is case-insensitive.</summary>
public readonly struct WorkflowCallInputData
{
    /// <summary>The raw map key slice used for lookup.</summary>
    public Utf8Slice Key { get; init; }

    public StringNodeId Name { get; init; }

    public StringNodeId Value { get; init; }
}

/// <summary>Row data for a secret passed to a reusable workflow call. Key lookup is case-insensitive.</summary>
public readonly struct WorkflowCallSecretData
{
    /// <summary>The raw map key slice used for lookup.</summary>
    public Utf8Slice Key { get; init; }

    public StringNodeId Name { get; init; }

    public StringNodeId Value { get; init; }
}

/// <summary>Row data for the <c>runs-on:</c> runner selection.</summary>
public readonly struct RunnerData
{
    public StringIdRange Labels { get; init; }

    public StringNodeId LabelsExpr { get; init; }

    public StringNodeId Group { get; init; }

    public TextRange Range { get; init; }
}

/// <summary>Row data for the <c>defaults.run:</c> section.</summary>
public readonly struct DefaultsRunData
{
    public StringNodeId Shell { get; init; }

    public StringNodeId WorkingDirectory { get; init; }

    public TextRange Range { get; init; }
}

/// <summary>Row data for the <c>defaults:</c> section.</summary>
public readonly struct DefaultsData
{
    public DefaultsRunId Run { get; init; }

    public TextRange Range { get; init; }
}
