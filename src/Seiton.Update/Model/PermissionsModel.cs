namespace Seiton.Update.Model;

internal sealed record PermissionsModel(IReadOnlyList<PermissionScopeModel> Scopes);

/// <summary>
/// A permission scope. <paramref name="DeprecationNote"/> is set only for scopes GitHub still accepts
/// but has retired; it is rendered into the diagnostic message of the <c>deprecated-permissions</c> rule.
/// </summary>
internal sealed record PermissionScopeModel(string Name, IReadOnlyList<string> Allowed, string? DeprecationNote = null);
