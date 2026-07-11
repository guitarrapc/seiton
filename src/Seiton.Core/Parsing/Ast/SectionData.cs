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
