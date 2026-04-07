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
        ["branch_protection_rule"] = new EventSpec(
            AllowedOptions: ["types"],
            TypesMode: ActivityTypesMode.Restricted,
            AllowedTypes: ["created", "edited", "deleted"]),
        ["check_run"] = new EventSpec(
            AllowedOptions: ["types"],
            TypesMode: ActivityTypesMode.Restricted,
            AllowedTypes: ["created", "rerequested", "completed", "requested_action"]),
        ["check_suite"] = new EventSpec(
            AllowedOptions: ["types"],
            TypesMode: ActivityTypesMode.Restricted,
            AllowedTypes: ["completed"]),
        ["create"] = new EventSpec(
            AllowedOptions: [],
            TypesMode: ActivityTypesMode.NotSupported,
            AllowedTypes: null),
        ["delete"] = new EventSpec(
            AllowedOptions: [],
            TypesMode: ActivityTypesMode.NotSupported,
            AllowedTypes: null),
        ["deployment"] = new EventSpec(
            AllowedOptions: [],
            TypesMode: ActivityTypesMode.NotSupported,
            AllowedTypes: null),
        ["deployment_status"] = new EventSpec(
            AllowedOptions: [],
            TypesMode: ActivityTypesMode.NotSupported,
            AllowedTypes: null),
        ["discussion"] = new EventSpec(
            AllowedOptions: ["types"],
            TypesMode: ActivityTypesMode.Restricted,
            AllowedTypes:
            [
                "created", "edited", "deleted", "transferred", "pinned", "unpinned", "labeled",
                "unlabeled", "locked", "unlocked", "category_changed", "answered", "unanswered"
            ]),
        ["discussion_comment"] = new EventSpec(
            AllowedOptions: ["types"],
            TypesMode: ActivityTypesMode.Restricted,
            AllowedTypes: ["created", "edited", "deleted"]),
        ["fork"] = new EventSpec(
            AllowedOptions: [],
            TypesMode: ActivityTypesMode.NotSupported,
            AllowedTypes: null),
        ["gollum"] = new EventSpec(
            AllowedOptions: [],
            TypesMode: ActivityTypesMode.NotSupported,
            AllowedTypes: null),
        ["image_version"] = new EventSpec(
            AllowedOptions: [],
            TypesMode: ActivityTypesMode.NotSupported,
            AllowedTypes: null),
        ["push"] = new EventSpec(
            AllowedOptions: ["branches", "branches-ignore", "tags", "tags-ignore", "paths", "paths-ignore"],
            TypesMode: ActivityTypesMode.NotSupported,
            AllowedTypes: null),
        ["label"] = new EventSpec(
            AllowedOptions: ["types"],
            TypesMode: ActivityTypesMode.Restricted,
            AllowedTypes: ["created", "edited", "deleted"]),
        ["merge_group"] = new EventSpec(
            AllowedOptions: ["types", "branches", "branches-ignore"],
            TypesMode: ActivityTypesMode.Restricted,
            AllowedTypes: ["checks_requested"]),
        ["milestone"] = new EventSpec(
            AllowedOptions: ["types"],
            TypesMode: ActivityTypesMode.Restricted,
            AllowedTypes: ["created", "closed", "opened", "edited", "deleted"]),
        ["page_build"] = new EventSpec(
            AllowedOptions: [],
            TypesMode: ActivityTypesMode.NotSupported,
            AllowedTypes: null),
        ["public"] = new EventSpec(
            AllowedOptions: [],
            TypesMode: ActivityTypesMode.NotSupported,
            AllowedTypes: null),
        ["pull_request"] = new EventSpec(
            AllowedOptions: ["types", "branches", "branches-ignore", "paths", "paths-ignore"],
            TypesMode: ActivityTypesMode.Restricted,
            AllowedTypes:
            [
                "assigned", "unassigned", "labeled", "unlabeled", "opened", "edited", "closed",
                "reopened", "synchronize", "converted_to_draft", "locked", "unlocked", "enqueued", "dequeued",
                "milestoned", "demilestoned", "ready_for_review", "review_requested", "review_request_removed",
                "auto_merge_enabled", "auto_merge_disabled"
            ]),
        ["pull_request_review"] = new EventSpec(
            AllowedOptions: ["types"],
            TypesMode: ActivityTypesMode.Restricted,
            AllowedTypes: ["submitted", "edited", "dismissed"]),
        ["pull_request_review_comment"] = new EventSpec(
            AllowedOptions: ["types"],
            TypesMode: ActivityTypesMode.Restricted,
            AllowedTypes: ["created", "edited", "deleted"]),
        ["pull_request_target"] = new EventSpec(
            AllowedOptions: ["types", "branches", "branches-ignore", "paths", "paths-ignore"],
            TypesMode: ActivityTypesMode.Restricted,
            AllowedTypes:
            [
                "assigned", "unassigned", "labeled", "unlabeled", "opened", "edited", "closed",
                "reopened", "synchronize", "converted_to_draft", "locked", "unlocked", "enqueued", "dequeued",
                "milestoned", "demilestoned", "ready_for_review", "review_requested", "review_request_removed",
                "auto_merge_enabled", "auto_merge_disabled"
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
        ["issues"] = new EventSpec(
            AllowedOptions: ["types"],
            TypesMode: ActivityTypesMode.Restricted,
            AllowedTypes:
            [
                "opened", "edited", "deleted", "transferred", "pinned", "unpinned", "closed", "reopened",
                "assigned", "unassigned", "labeled", "unlabeled", "locked", "unlocked", "milestoned", "demilestoned",
                "typed", "untyped"
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
        ["status"] = new EventSpec(
            AllowedOptions: [],
            TypesMode: ActivityTypesMode.NotSupported,
            AllowedTypes: null),
        ["watch"] = new EventSpec(
            AllowedOptions: ["types"],
            TypesMode: ActivityTypesMode.Restricted,
            AllowedTypes: ["started"]),
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
