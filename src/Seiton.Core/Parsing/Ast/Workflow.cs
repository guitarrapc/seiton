namespace Seiton.Core.Parsing.Ast;

/// <summary>AST node representing a GitHub Actions workflow file.</summary>
public sealed class Workflow
{
    public StringNodeId Name { get; init; }

    public StringNodeId RunName { get; init; }

    public NodeRange On { get; internal set; }

    public PermissionsId Permissions { get; internal set; }

    public EnvId Env { get; internal set; }

    public DefaultsId Defaults { get; internal set; }

    public ConcurrencyId Concurrency { get; internal set; }

    /// <summary>The <c>jobs:</c> map — range over <see cref="JobEntryData"/> rows. default = key absent.</summary>
    public NodeRange Jobs { get; internal set; }

    public TextRange Range { get; init; }
}
