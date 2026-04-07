namespace Seiton.Core.Parsing;

internal static class OnEventSpecs
{
    private static readonly Dictionary<string, EventSpec> Specs = new(StringComparer.Ordinal)
    {
        ["push"] = new EventSpec(
            AllowedOptions: ["branches", "branches-ignore", "tags", "tags-ignore", "paths", "paths-ignore"],
            AllowedTypes: null),
        ["pull_request"] = new EventSpec(
            AllowedOptions: ["types", "branches", "branches-ignore", "paths", "paths-ignore"],
            AllowedTypes:
            [
                "assigned", "unassigned", "labeled", "unlabeled", "opened", "edited", "closed",
                "reopened", "synchronize", "ready_for_review", "locked", "unlocked", "review_requested",
                "review_request_removed", "auto_merge_enabled", "auto_merge_disabled", "enqueued", "dequeued"
            ]),
        ["pull_request_target"] = new EventSpec(
            AllowedOptions: ["types", "branches", "branches-ignore", "paths", "paths-ignore"],
            AllowedTypes:
            [
                "assigned", "unassigned", "labeled", "unlabeled", "opened", "edited", "closed",
                "reopened", "synchronize", "ready_for_review", "locked", "unlocked", "review_requested",
                "review_request_removed", "auto_merge_enabled", "auto_merge_disabled", "enqueued", "dequeued"
            ]),
        ["workflow_dispatch"] = new EventSpec(
            AllowedOptions: ["inputs"],
            AllowedTypes: null),
        ["workflow_call"] = new EventSpec(
            AllowedOptions: ["inputs", "secrets", "outputs"],
            AllowedTypes: null),
        ["workflow_run"] = new EventSpec(
            AllowedOptions: ["workflows", "types", "branches", "branches-ignore"],
            AllowedTypes: ["requested", "completed", "in_progress"]),
        ["release"] = new EventSpec(
            AllowedOptions: ["types"],
            AllowedTypes: ["published", "unpublished", "created", "edited", "deleted", "prereleased", "released"]),
        ["registry_package"] = new EventSpec(
            AllowedOptions: ["types"],
            AllowedTypes: ["published", "updated"]),
        ["check_run"] = new EventSpec(
            AllowedOptions: ["types"],
            AllowedTypes: ["created", "rerequested", "completed", "requested_action"]),
        ["check_suite"] = new EventSpec(
            AllowedOptions: ["types"],
            AllowedTypes: ["completed", "requested", "rerequested"]),
        ["issues"] = new EventSpec(
            AllowedOptions: ["types"],
            AllowedTypes:
            [
                "opened", "edited", "deleted", "transferred", "pinned", "unpinned", "closed", "reopened",
                "assigned", "unassigned", "labeled", "unlabeled", "locked", "unlocked", "milestoned", "demilestoned"
            ]),
        ["issue_comment"] = new EventSpec(
            AllowedOptions: ["types"],
            AllowedTypes: ["created", "edited", "deleted"]),
        ["schedule"] = new EventSpec(
            AllowedOptions: [],
            AllowedTypes: null),
        ["repository_dispatch"] = new EventSpec(
            AllowedOptions: ["types"],
            AllowedTypes: null),
    };

    public static bool TryGet(string eventName, out EventSpec spec) => Specs.TryGetValue(eventName, out spec);

    internal readonly record struct EventSpec(
        string[] AllowedOptions,
        string[]? AllowedTypes)
    {
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
            if (AllowedTypes is null)
            {
                return true;
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
