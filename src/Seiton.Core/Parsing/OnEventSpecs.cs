namespace Seiton.Core.Parsing;

internal static class OnEventSpecs
{
    internal enum ActivityTypesMode
    {
        NotSupported,
        Any,
        Restricted,
    }

    private static readonly EventSpec BranchProtectionRule = new(
        AllowedOptions: ["types"],
        TypesMode: ActivityTypesMode.Restricted,
        AllowedTypes: ["created", "edited", "deleted"]);

    private static readonly EventSpec CheckRun = new(
        AllowedOptions: ["types"],
        TypesMode: ActivityTypesMode.Restricted,
        AllowedTypes: ["created", "rerequested", "completed", "requested_action"]);

    private static readonly EventSpec CheckSuite = new(
        AllowedOptions: ["types"],
        TypesMode: ActivityTypesMode.Restricted,
        AllowedTypes: ["completed"]);

    private static readonly EventSpec Create = new([], ActivityTypesMode.NotSupported, null);
    private static readonly EventSpec Delete = new([], ActivityTypesMode.NotSupported, null);
    private static readonly EventSpec Deployment = new([], ActivityTypesMode.NotSupported, null);
    private static readonly EventSpec DeploymentStatus = new([], ActivityTypesMode.NotSupported, null);

    private static readonly EventSpec Discussion = new(
        AllowedOptions: ["types"],
        TypesMode: ActivityTypesMode.Restricted,
        AllowedTypes:
        [
            "created", "edited", "deleted", "transferred", "pinned", "unpinned", "labeled",
            "unlabeled", "locked", "unlocked", "category_changed", "answered", "unanswered"
        ]);

    private static readonly EventSpec DiscussionComment = new(
        AllowedOptions: ["types"],
        TypesMode: ActivityTypesMode.Restricted,
        AllowedTypes: ["created", "edited", "deleted"]);

    private static readonly EventSpec Fork = new([], ActivityTypesMode.NotSupported, null);
    private static readonly EventSpec Gollum = new([], ActivityTypesMode.NotSupported, null);
    private static readonly EventSpec ImageVersion = new([], ActivityTypesMode.NotSupported, null);

    private static readonly EventSpec Push = new(
        AllowedOptions: ["branches", "branches-ignore", "tags", "tags-ignore", "paths", "paths-ignore"],
        TypesMode: ActivityTypesMode.NotSupported,
        AllowedTypes: null);

    private static readonly EventSpec Label = new(
        AllowedOptions: ["types"],
        TypesMode: ActivityTypesMode.Restricted,
        AllowedTypes: ["created", "edited", "deleted"]);

    private static readonly EventSpec MergeGroup = new(
        AllowedOptions: ["types", "branches", "branches-ignore"],
        TypesMode: ActivityTypesMode.Restricted,
        AllowedTypes: ["checks_requested"]);

    private static readonly EventSpec Milestone = new(
        AllowedOptions: ["types"],
        TypesMode: ActivityTypesMode.Restricted,
        AllowedTypes: ["created", "closed", "opened", "edited", "deleted"]);

    private static readonly EventSpec PageBuild = new([], ActivityTypesMode.NotSupported, null);
    private static readonly EventSpec Public = new([], ActivityTypesMode.NotSupported, null);

    private static readonly EventSpec PullRequest = new(
        AllowedOptions: ["types", "branches", "branches-ignore", "paths", "paths-ignore"],
        TypesMode: ActivityTypesMode.Restricted,
        AllowedTypes:
        [
            "assigned", "unassigned", "labeled", "unlabeled", "opened", "edited", "closed",
            "reopened", "synchronize", "converted_to_draft", "locked", "unlocked", "enqueued", "dequeued",
            "milestoned", "demilestoned", "ready_for_review", "review_requested", "review_request_removed",
            "auto_merge_enabled", "auto_merge_disabled"
        ]);

    private static readonly EventSpec PullRequestReview = new(
        AllowedOptions: ["types"],
        TypesMode: ActivityTypesMode.Restricted,
        AllowedTypes: ["submitted", "edited", "dismissed"]);

    private static readonly EventSpec PullRequestReviewComment = new(
        AllowedOptions: ["types"],
        TypesMode: ActivityTypesMode.Restricted,
        AllowedTypes: ["created", "edited", "deleted"]);

    private static readonly EventSpec PullRequestTarget = new(
        AllowedOptions: ["types", "branches", "branches-ignore", "paths", "paths-ignore"],
        TypesMode: ActivityTypesMode.Restricted,
        AllowedTypes:
        [
            "assigned", "unassigned", "labeled", "unlabeled", "opened", "edited", "closed",
            "reopened", "synchronize", "converted_to_draft", "locked", "unlocked", "enqueued", "dequeued",
            "milestoned", "demilestoned", "ready_for_review", "review_requested", "review_request_removed",
            "auto_merge_enabled", "auto_merge_disabled"
        ]);

    private static readonly EventSpec WorkflowDispatch = new(
        AllowedOptions: ["inputs"],
        TypesMode: ActivityTypesMode.NotSupported,
        AllowedTypes: null);

    private static readonly EventSpec WorkflowCall = new(
        AllowedOptions: ["inputs", "secrets", "outputs"],
        TypesMode: ActivityTypesMode.NotSupported,
        AllowedTypes: null);

    private static readonly EventSpec WorkflowRun = new(
        AllowedOptions: ["workflows", "types", "branches", "branches-ignore"],
        TypesMode: ActivityTypesMode.Restricted,
        AllowedTypes: ["requested", "completed", "in_progress"]);

    private static readonly EventSpec Release = new(
        AllowedOptions: ["types"],
        TypesMode: ActivityTypesMode.Restricted,
        AllowedTypes: ["published", "unpublished", "created", "edited", "deleted", "prereleased", "released"]);

    private static readonly EventSpec RegistryPackage = new(
        AllowedOptions: ["types"],
        TypesMode: ActivityTypesMode.Restricted,
        AllowedTypes: ["published", "updated"]);

    private static readonly EventSpec Issues = new(
        AllowedOptions: ["types"],
        TypesMode: ActivityTypesMode.Restricted,
        AllowedTypes:
        [
            "opened", "edited", "deleted", "transferred", "pinned", "unpinned", "closed", "reopened",
            "assigned", "unassigned", "labeled", "unlabeled", "locked", "unlocked", "milestoned", "demilestoned",
            "typed", "untyped"
        ]);

    private static readonly EventSpec IssueComment = new(
        AllowedOptions: ["types"],
        TypesMode: ActivityTypesMode.Restricted,
        AllowedTypes: ["created", "edited", "deleted"]);

    private static readonly EventSpec Schedule = new([], ActivityTypesMode.NotSupported, null);

    private static readonly EventSpec RepositoryDispatch = new(
        AllowedOptions: ["types"],
        TypesMode: ActivityTypesMode.Any,
        AllowedTypes: null);

    private static readonly EventSpec Status = new([], ActivityTypesMode.NotSupported, null);

    private static readonly EventSpec Watch = new(
        AllowedOptions: ["types"],
        TypesMode: ActivityTypesMode.Restricted,
        AllowedTypes: ["started"]);

    public static bool TryGet(string eventName, out EventSpec spec)
    {
        if (eventName == "branch_protection_rule")
        {
            spec = BranchProtectionRule;
            return true;
        }

        if (eventName == "check_run")
        {
            spec = CheckRun;
            return true;
        }

        if (eventName == "check_suite")
        {
            spec = CheckSuite;
            return true;
        }

        if (eventName == "create")
        {
            spec = Create;
            return true;
        }

        if (eventName == "delete")
        {
            spec = Delete;
            return true;
        }

        if (eventName == "deployment")
        {
            spec = Deployment;
            return true;
        }

        if (eventName == "deployment_status")
        {
            spec = DeploymentStatus;
            return true;
        }

        if (eventName == "discussion")
        {
            spec = Discussion;
            return true;
        }

        if (eventName == "discussion_comment")
        {
            spec = DiscussionComment;
            return true;
        }

        if (eventName == "fork")
        {
            spec = Fork;
            return true;
        }

        if (eventName == "gollum")
        {
            spec = Gollum;
            return true;
        }

        if (eventName == "image_version")
        {
            spec = ImageVersion;
            return true;
        }

        if (eventName == "push")
        {
            spec = Push;
            return true;
        }

        if (eventName == "label")
        {
            spec = Label;
            return true;
        }

        if (eventName == "merge_group")
        {
            spec = MergeGroup;
            return true;
        }

        if (eventName == "milestone")
        {
            spec = Milestone;
            return true;
        }

        if (eventName == "page_build")
        {
            spec = PageBuild;
            return true;
        }

        if (eventName == "public")
        {
            spec = Public;
            return true;
        }

        if (eventName == "pull_request")
        {
            spec = PullRequest;
            return true;
        }

        if (eventName == "pull_request_review")
        {
            spec = PullRequestReview;
            return true;
        }

        if (eventName == "pull_request_review_comment")
        {
            spec = PullRequestReviewComment;
            return true;
        }

        if (eventName == "pull_request_target")
        {
            spec = PullRequestTarget;
            return true;
        }

        if (eventName == "workflow_dispatch")
        {
            spec = WorkflowDispatch;
            return true;
        }

        if (eventName == "workflow_call")
        {
            spec = WorkflowCall;
            return true;
        }

        if (eventName == "workflow_run")
        {
            spec = WorkflowRun;
            return true;
        }

        if (eventName == "release")
        {
            spec = Release;
            return true;
        }

        if (eventName == "registry_package")
        {
            spec = RegistryPackage;
            return true;
        }

        if (eventName == "issues")
        {
            spec = Issues;
            return true;
        }

        if (eventName == "issue_comment")
        {
            spec = IssueComment;
            return true;
        }

        if (eventName == "schedule")
        {
            spec = Schedule;
            return true;
        }

        if (eventName == "repository_dispatch")
        {
            spec = RepositoryDispatch;
            return true;
        }

        if (eventName == "status")
        {
            spec = Status;
            return true;
        }

        if (eventName == "watch")
        {
            spec = Watch;
            return true;
        }

        spec = default;
        return false;
    }

    public static bool TryGet(ReadOnlySpan<byte> eventNameUtf8, out string eventName, out EventSpec spec)
    {
        if (eventNameUtf8.SequenceEqual("branch_protection_rule"u8))
        {
            eventName = "branch_protection_rule";
            spec = BranchProtectionRule;
            return true;
        }

        if (eventNameUtf8.SequenceEqual("check_run"u8))
        {
            eventName = "check_run";
            spec = CheckRun;
            return true;
        }

        if (eventNameUtf8.SequenceEqual("check_suite"u8))
        {
            eventName = "check_suite";
            spec = CheckSuite;
            return true;
        }

        if (eventNameUtf8.SequenceEqual("create"u8))
        {
            eventName = "create";
            spec = Create;
            return true;
        }

        if (eventNameUtf8.SequenceEqual("delete"u8))
        {
            eventName = "delete";
            spec = Delete;
            return true;
        }

        if (eventNameUtf8.SequenceEqual("deployment"u8))
        {
            eventName = "deployment";
            spec = Deployment;
            return true;
        }

        if (eventNameUtf8.SequenceEqual("deployment_status"u8))
        {
            eventName = "deployment_status";
            spec = DeploymentStatus;
            return true;
        }

        if (eventNameUtf8.SequenceEqual("discussion"u8))
        {
            eventName = "discussion";
            spec = Discussion;
            return true;
        }

        if (eventNameUtf8.SequenceEqual("discussion_comment"u8))
        {
            eventName = "discussion_comment";
            spec = DiscussionComment;
            return true;
        }

        if (eventNameUtf8.SequenceEqual("fork"u8))
        {
            eventName = "fork";
            spec = Fork;
            return true;
        }

        if (eventNameUtf8.SequenceEqual("gollum"u8))
        {
            eventName = "gollum";
            spec = Gollum;
            return true;
        }

        if (eventNameUtf8.SequenceEqual("image_version"u8))
        {
            eventName = "image_version";
            spec = ImageVersion;
            return true;
        }

        if (eventNameUtf8.SequenceEqual("push"u8))
        {
            eventName = "push";
            spec = Push;
            return true;
        }

        if (eventNameUtf8.SequenceEqual("label"u8))
        {
            eventName = "label";
            spec = Label;
            return true;
        }

        if (eventNameUtf8.SequenceEqual("merge_group"u8))
        {
            eventName = "merge_group";
            spec = MergeGroup;
            return true;
        }

        if (eventNameUtf8.SequenceEqual("milestone"u8))
        {
            eventName = "milestone";
            spec = Milestone;
            return true;
        }

        if (eventNameUtf8.SequenceEqual("page_build"u8))
        {
            eventName = "page_build";
            spec = PageBuild;
            return true;
        }

        if (eventNameUtf8.SequenceEqual("public"u8))
        {
            eventName = "public";
            spec = Public;
            return true;
        }

        if (eventNameUtf8.SequenceEqual("pull_request"u8))
        {
            eventName = "pull_request";
            spec = PullRequest;
            return true;
        }

        if (eventNameUtf8.SequenceEqual("pull_request_review"u8))
        {
            eventName = "pull_request_review";
            spec = PullRequestReview;
            return true;
        }

        if (eventNameUtf8.SequenceEqual("pull_request_review_comment"u8))
        {
            eventName = "pull_request_review_comment";
            spec = PullRequestReviewComment;
            return true;
        }

        if (eventNameUtf8.SequenceEqual("pull_request_target"u8))
        {
            eventName = "pull_request_target";
            spec = PullRequestTarget;
            return true;
        }

        if (eventNameUtf8.SequenceEqual("workflow_dispatch"u8))
        {
            eventName = "workflow_dispatch";
            spec = WorkflowDispatch;
            return true;
        }

        if (eventNameUtf8.SequenceEqual("workflow_call"u8))
        {
            eventName = "workflow_call";
            spec = WorkflowCall;
            return true;
        }

        if (eventNameUtf8.SequenceEqual("workflow_run"u8))
        {
            eventName = "workflow_run";
            spec = WorkflowRun;
            return true;
        }

        if (eventNameUtf8.SequenceEqual("release"u8))
        {
            eventName = "release";
            spec = Release;
            return true;
        }

        if (eventNameUtf8.SequenceEqual("registry_package"u8))
        {
            eventName = "registry_package";
            spec = RegistryPackage;
            return true;
        }

        if (eventNameUtf8.SequenceEqual("issues"u8))
        {
            eventName = "issues";
            spec = Issues;
            return true;
        }

        if (eventNameUtf8.SequenceEqual("issue_comment"u8))
        {
            eventName = "issue_comment";
            spec = IssueComment;
            return true;
        }

        if (eventNameUtf8.SequenceEqual("schedule"u8))
        {
            eventName = "schedule";
            spec = Schedule;
            return true;
        }

        if (eventNameUtf8.SequenceEqual("repository_dispatch"u8))
        {
            eventName = "repository_dispatch";
            spec = RepositoryDispatch;
            return true;
        }

        if (eventNameUtf8.SequenceEqual("status"u8))
        {
            eventName = "status";
            spec = Status;
            return true;
        }

        if (eventNameUtf8.SequenceEqual("watch"u8))
        {
            eventName = "watch";
            spec = Watch;
            return true;
        }

        eventName = string.Empty;
        spec = default;
        return false;
    }

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

        public bool IsOptionAllowed(ReadOnlySpan<byte> optionUtf8)
        {
            for (var i = 0; i < AllowedOptions.Length; i++)
            {
                if (Utf8EqualsAscii(optionUtf8, AllowedOptions[i]))
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

        public bool IsTypeAllowed(ReadOnlySpan<byte> valueUtf8)
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
                if (Utf8EqualsAscii(valueUtf8, AllowedTypes[i]))
                {
                    return true;
                }
            }

            return false;
        }

        private static bool Utf8EqualsAscii(ReadOnlySpan<byte> utf8, string ascii)
        {
            if (utf8.Length != ascii.Length)
            {
                return false;
            }

            for (var i = 0; i < ascii.Length; i++)
            {
                if (utf8[i] != (byte)ascii[i])
                {
                    return false;
                }
            }

            return true;
        }
    }
}
