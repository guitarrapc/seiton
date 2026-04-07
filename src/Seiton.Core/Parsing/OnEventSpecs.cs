namespace Seiton.Core.Parsing;

internal static class OnEventSpecs
{
    internal enum ActivityTypesMode
    {
        NotSupported,
        Any,
        Restricted,
    }

    private static readonly Dictionary<string, EventSpec> Specs = new(StringComparer.Ordinal)
    {
        ["push"] = new EventSpec(
            AllowedOptions: ["branches", "branches-ignore", "tags", "tags-ignore", "paths", "paths-ignore"],
            TypesMode: ActivityTypesMode.NotSupported,
            AllowedTypes: null),
        ["pull_request"] = new EventSpec(
            AllowedOptions: ["types", "branches", "branches-ignore", "paths", "paths-ignore"],
            TypesMode: ActivityTypesMode.Restricted,
            AllowedTypes:
            [
                "assigned", "unassigned", "labeled", "unlabeled", "opened", "edited", "closed",
                "reopened", "synchronize", "ready_for_review", "locked", "unlocked", "review_requested",
                "review_request_removed", "auto_merge_enabled", "auto_merge_disabled", "enqueued", "dequeued"
            ]),
        ["pull_request_target"] = new EventSpec(
            AllowedOptions: ["types", "branches", "branches-ignore", "paths", "paths-ignore"],
            TypesMode: ActivityTypesMode.Restricted,
            AllowedTypes:
            [
                "assigned", "unassigned", "labeled", "unlabeled", "opened", "edited", "closed",
                "reopened", "synchronize", "ready_for_review", "locked", "unlocked", "review_requested",
                "review_request_removed", "auto_merge_enabled", "auto_merge_disabled", "enqueued", "dequeued"
            ]),
        ["workflow_dispatch"] = new EventSpec(
            AllowedOptions: ["inputs"],
            TypesMode: ActivityTypesMode.NotSupported,
            AllowedTypes: null),
        ["workflow_call"] = new EventSpec(
            AllowedOptions: ["inputs", "secrets", "outputs"],
            TypesMode: ActivityTypesMode.NotSupported,
            AllowedTypes: null),
        ["workflow_run"] = new EventSpec(
            AllowedOptions: ["workflows", "types", "branches", "branches-ignore"],
            TypesMode: ActivityTypesMode.Restricted,
            AllowedTypes: ["requested", "completed", "in_progress"]),
        ["release"] = new EventSpec(
            AllowedOptions: ["types"],
            TypesMode: ActivityTypesMode.Restricted,
            AllowedTypes: ["published", "unpublished", "created", "edited", "deleted", "prereleased", "released"]),
        ["registry_package"] = new EventSpec(
            AllowedOptions: ["types"],
            TypesMode: ActivityTypesMode.Restricted,
            AllowedTypes: ["published", "updated"]),
        ["check_run"] = new EventSpec(
            AllowedOptions: ["types"],
            TypesMode: ActivityTypesMode.Restricted,
            AllowedTypes: ["created", "rerequested", "completed", "requested_action"]),
        ["check_suite"] = new EventSpec(
            AllowedOptions: ["types"],
            TypesMode: ActivityTypesMode.Restricted,
            AllowedTypes: ["completed", "requested", "rerequested"]),
        ["issues"] = new EventSpec(
            AllowedOptions: ["types"],
            TypesMode: ActivityTypesMode.Restricted,
            AllowedTypes:
            [
                "opened", "edited", "deleted", "transferred", "pinned", "unpinned", "closed", "reopened",
                "assigned", "unassigned", "labeled", "unlabeled", "locked", "unlocked", "milestoned", "demilestoned"
            ]),
        ["issue_comment"] = new EventSpec(
            AllowedOptions: ["types"],
            TypesMode: ActivityTypesMode.Restricted,
            AllowedTypes: ["created", "edited", "deleted"]),
        ["schedule"] = new EventSpec(
            AllowedOptions: [],
            TypesMode: ActivityTypesMode.NotSupported,
            AllowedTypes: null),
        ["repository_dispatch"] = new EventSpec(
            AllowedOptions: ["types"],
            TypesMode: ActivityTypesMode.Any,
            AllowedTypes: null),
    };

    public static bool TryGet(string eventName, out EventSpec spec) => Specs.TryGetValue(eventName, out spec);

    internal readonly record struct EventSpec(
        string[] AllowedOptions,
        ActivityTypesMode TypesMode,
        string[]? AllowedTypes)
    {
        public bool IsTypeOptionSupported() => TypesMode is not ActivityTypesMode.NotSupported;

        public bool IsOptionAllowed(string option)
        {
            for (var i = 0; i < AllowedOptions.Length; i++)
            {
                if (string.Equals(AllowedOptions[i], option, StringComparison.Ordinal))
                {
                    return true;
                }
            }

            return false;
        }

        public bool IsTypeAllowed(string value)
        {
            if (TypesMode is ActivityTypesMode.Any)
            {
                return true;
            }

            if (TypesMode is ActivityTypesMode.NotSupported || AllowedTypes is null)
            {
                return false;
            }

            for (var i = 0; i < AllowedTypes.Length; i++)
            {
                if (string.Equals(AllowedTypes[i], value, StringComparison.Ordinal))
                {
                    return true;
                }
            }

            return false;
        }
    }
}
