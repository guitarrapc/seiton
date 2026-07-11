namespace Seiton.Core.Parsing.Ast;

// Data-oriented AST rows (Stage 2). Each *Data struct is a row in an AstArena table,
// addressed by the matching typed ID in NodeIds.cs. Rows hold only scalar handles,
// other node IDs, and ranges — never object references or strings.
// See `.github/docs/plan_data_oriented_ast.md`.

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
