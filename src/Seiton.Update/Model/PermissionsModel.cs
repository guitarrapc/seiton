namespace Seiton.Update.Model;

internal sealed record PermissionsModel(IReadOnlyList<PermissionScopeModel> Scopes);

internal sealed record PermissionScopeModel(string Name, IReadOnlyList<string> Allowed);
