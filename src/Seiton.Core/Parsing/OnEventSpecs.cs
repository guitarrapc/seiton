namespace Seiton.Core.Parsing;

internal static class OnEventSpecs
{
    internal enum ActivityTypesMode
    {
        NotSupported,
        Any,
        Restricted,
    }

    internal enum EventId
    {
        BranchProtectionRule,
        CheckRun,
        CheckSuite,
        Create,
        Delete,
        Deployment,
        DeploymentStatus,
        Discussion,
        DiscussionComment,
        Fork,
        Gollum,
        ImageVersion,
        Push,
        Label,
        MergeGroup,
        Milestone,
        PageBuild,
        Public,
        PullRequest,
        PullRequestReview,
        PullRequestReviewComment,
        PullRequestTarget,
        WorkflowDispatch,
        WorkflowCall,
        WorkflowRun,
        Release,
        RegistryPackage,
        Issues,
        IssueComment,
        Schedule,
        RepositoryDispatch,
        Status,
        Watch,
    }

    public static bool TryGet(ReadOnlySpan<byte> eventNameUtf8, out string eventName, out EventSpec spec)
    {
        if (eventNameUtf8.SequenceEqual("branch_protection_rule"u8)) { eventName = "branch_protection_rule"; spec = new(EventId.BranchProtectionRule); return true; }
        if (eventNameUtf8.SequenceEqual("check_run"u8)) { eventName = "check_run"; spec = new(EventId.CheckRun); return true; }
        if (eventNameUtf8.SequenceEqual("check_suite"u8)) { eventName = "check_suite"; spec = new(EventId.CheckSuite); return true; }
        if (eventNameUtf8.SequenceEqual("create"u8)) { eventName = "create"; spec = new(EventId.Create); return true; }
        if (eventNameUtf8.SequenceEqual("delete"u8)) { eventName = "delete"; spec = new(EventId.Delete); return true; }
        if (eventNameUtf8.SequenceEqual("deployment"u8)) { eventName = "deployment"; spec = new(EventId.Deployment); return true; }
        if (eventNameUtf8.SequenceEqual("deployment_status"u8)) { eventName = "deployment_status"; spec = new(EventId.DeploymentStatus); return true; }
        if (eventNameUtf8.SequenceEqual("discussion"u8)) { eventName = "discussion"; spec = new(EventId.Discussion); return true; }
        if (eventNameUtf8.SequenceEqual("discussion_comment"u8)) { eventName = "discussion_comment"; spec = new(EventId.DiscussionComment); return true; }
        if (eventNameUtf8.SequenceEqual("fork"u8)) { eventName = "fork"; spec = new(EventId.Fork); return true; }
        if (eventNameUtf8.SequenceEqual("gollum"u8)) { eventName = "gollum"; spec = new(EventId.Gollum); return true; }
        if (eventNameUtf8.SequenceEqual("image_version"u8)) { eventName = "image_version"; spec = new(EventId.ImageVersion); return true; }
        if (eventNameUtf8.SequenceEqual("push"u8)) { eventName = "push"; spec = new(EventId.Push); return true; }
        if (eventNameUtf8.SequenceEqual("label"u8)) { eventName = "label"; spec = new(EventId.Label); return true; }
        if (eventNameUtf8.SequenceEqual("merge_group"u8)) { eventName = "merge_group"; spec = new(EventId.MergeGroup); return true; }
        if (eventNameUtf8.SequenceEqual("milestone"u8)) { eventName = "milestone"; spec = new(EventId.Milestone); return true; }
        if (eventNameUtf8.SequenceEqual("page_build"u8)) { eventName = "page_build"; spec = new(EventId.PageBuild); return true; }
        if (eventNameUtf8.SequenceEqual("public"u8)) { eventName = "public"; spec = new(EventId.Public); return true; }
        if (eventNameUtf8.SequenceEqual("pull_request"u8)) { eventName = "pull_request"; spec = new(EventId.PullRequest); return true; }
        if (eventNameUtf8.SequenceEqual("pull_request_review"u8)) { eventName = "pull_request_review"; spec = new(EventId.PullRequestReview); return true; }
        if (eventNameUtf8.SequenceEqual("pull_request_review_comment"u8)) { eventName = "pull_request_review_comment"; spec = new(EventId.PullRequestReviewComment); return true; }
        if (eventNameUtf8.SequenceEqual("pull_request_target"u8)) { eventName = "pull_request_target"; spec = new(EventId.PullRequestTarget); return true; }
        if (eventNameUtf8.SequenceEqual("workflow_dispatch"u8)) { eventName = "workflow_dispatch"; spec = new(EventId.WorkflowDispatch); return true; }
        if (eventNameUtf8.SequenceEqual("workflow_call"u8)) { eventName = "workflow_call"; spec = new(EventId.WorkflowCall); return true; }
        if (eventNameUtf8.SequenceEqual("workflow_run"u8)) { eventName = "workflow_run"; spec = new(EventId.WorkflowRun); return true; }
        if (eventNameUtf8.SequenceEqual("release"u8)) { eventName = "release"; spec = new(EventId.Release); return true; }
        if (eventNameUtf8.SequenceEqual("registry_package"u8)) { eventName = "registry_package"; spec = new(EventId.RegistryPackage); return true; }
        if (eventNameUtf8.SequenceEqual("issues"u8)) { eventName = "issues"; spec = new(EventId.Issues); return true; }
        if (eventNameUtf8.SequenceEqual("issue_comment"u8)) { eventName = "issue_comment"; spec = new(EventId.IssueComment); return true; }
        if (eventNameUtf8.SequenceEqual("schedule"u8)) { eventName = "schedule"; spec = new(EventId.Schedule); return true; }
        if (eventNameUtf8.SequenceEqual("repository_dispatch"u8)) { eventName = "repository_dispatch"; spec = new(EventId.RepositoryDispatch); return true; }
        if (eventNameUtf8.SequenceEqual("status"u8)) { eventName = "status"; spec = new(EventId.Status); return true; }
        if (eventNameUtf8.SequenceEqual("watch"u8)) { eventName = "watch"; spec = new(EventId.Watch); return true; }

        eventName = string.Empty;
        spec = default;
        return false;
    }

    internal readonly struct EventSpec
    {
        private EventId Id { get; }

        public EventSpec(EventId id)
        {
            Id = id;
        }

        public bool IsTypeOptionSupported() => GetTypesMode() is not ActivityTypesMode.NotSupported;

        public bool IsOptionAllowed(ReadOnlySpan<byte> optionUtf8)
        {
            return Id switch
            {
                EventId.BranchProtectionRule => optionUtf8.SequenceEqual("types"u8),
                EventId.CheckRun => optionUtf8.SequenceEqual("types"u8),
                EventId.CheckSuite => optionUtf8.SequenceEqual("types"u8),
                EventId.Discussion => optionUtf8.SequenceEqual("types"u8),
                EventId.DiscussionComment => optionUtf8.SequenceEqual("types"u8),
                EventId.Push => optionUtf8.SequenceEqual("branches"u8) || optionUtf8.SequenceEqual("branches-ignore"u8) || optionUtf8.SequenceEqual("tags"u8) || optionUtf8.SequenceEqual("tags-ignore"u8) || optionUtf8.SequenceEqual("paths"u8) || optionUtf8.SequenceEqual("paths-ignore"u8),
                EventId.Label => optionUtf8.SequenceEqual("types"u8),
                EventId.MergeGroup => optionUtf8.SequenceEqual("types"u8) || optionUtf8.SequenceEqual("branches"u8) || optionUtf8.SequenceEqual("branches-ignore"u8),
                EventId.Milestone => optionUtf8.SequenceEqual("types"u8),
                EventId.PullRequest => optionUtf8.SequenceEqual("types"u8) || optionUtf8.SequenceEqual("branches"u8) || optionUtf8.SequenceEqual("branches-ignore"u8) || optionUtf8.SequenceEqual("paths"u8) || optionUtf8.SequenceEqual("paths-ignore"u8),
                EventId.PullRequestReview => optionUtf8.SequenceEqual("types"u8),
                EventId.PullRequestReviewComment => optionUtf8.SequenceEqual("types"u8),
                EventId.PullRequestTarget => optionUtf8.SequenceEqual("types"u8) || optionUtf8.SequenceEqual("branches"u8) || optionUtf8.SequenceEqual("branches-ignore"u8) || optionUtf8.SequenceEqual("paths"u8) || optionUtf8.SequenceEqual("paths-ignore"u8),
                EventId.WorkflowDispatch => optionUtf8.SequenceEqual("inputs"u8),
                EventId.WorkflowCall => optionUtf8.SequenceEqual("inputs"u8) || optionUtf8.SequenceEqual("secrets"u8) || optionUtf8.SequenceEqual("outputs"u8),
                EventId.WorkflowRun => optionUtf8.SequenceEqual("workflows"u8) || optionUtf8.SequenceEqual("types"u8) || optionUtf8.SequenceEqual("branches"u8) || optionUtf8.SequenceEqual("branches-ignore"u8),
                EventId.Release => optionUtf8.SequenceEqual("types"u8),
                EventId.RegistryPackage => optionUtf8.SequenceEqual("types"u8),
                EventId.Issues => optionUtf8.SequenceEqual("types"u8),
                EventId.IssueComment => optionUtf8.SequenceEqual("types"u8),
                EventId.RepositoryDispatch => optionUtf8.SequenceEqual("types"u8),
                EventId.Watch => optionUtf8.SequenceEqual("types"u8),
                _ => false,
            };
        }

        public bool IsTypeAllowed(ReadOnlySpan<byte> valueUtf8)
        {
            if (GetTypesMode() is ActivityTypesMode.Any)
            {
                return true;
            }

            if (GetTypesMode() is ActivityTypesMode.NotSupported)
            {
                return false;
            }

            return Id switch
            {
                EventId.BranchProtectionRule => valueUtf8.SequenceEqual("created"u8) || valueUtf8.SequenceEqual("edited"u8) || valueUtf8.SequenceEqual("deleted"u8),
                EventId.CheckRun => valueUtf8.SequenceEqual("created"u8) || valueUtf8.SequenceEqual("rerequested"u8) || valueUtf8.SequenceEqual("completed"u8) || valueUtf8.SequenceEqual("requested_action"u8),
                EventId.CheckSuite => valueUtf8.SequenceEqual("completed"u8),
                EventId.Discussion => valueUtf8.SequenceEqual("created"u8) || valueUtf8.SequenceEqual("edited"u8) || valueUtf8.SequenceEqual("deleted"u8) || valueUtf8.SequenceEqual("transferred"u8) || valueUtf8.SequenceEqual("pinned"u8) || valueUtf8.SequenceEqual("unpinned"u8) || valueUtf8.SequenceEqual("labeled"u8) || valueUtf8.SequenceEqual("unlabeled"u8) || valueUtf8.SequenceEqual("locked"u8) || valueUtf8.SequenceEqual("unlocked"u8) || valueUtf8.SequenceEqual("category_changed"u8) || valueUtf8.SequenceEqual("answered"u8) || valueUtf8.SequenceEqual("unanswered"u8),
                EventId.DiscussionComment => valueUtf8.SequenceEqual("created"u8) || valueUtf8.SequenceEqual("edited"u8) || valueUtf8.SequenceEqual("deleted"u8),
                EventId.Label => valueUtf8.SequenceEqual("created"u8) || valueUtf8.SequenceEqual("edited"u8) || valueUtf8.SequenceEqual("deleted"u8),
                EventId.MergeGroup => valueUtf8.SequenceEqual("checks_requested"u8),
                EventId.Milestone => valueUtf8.SequenceEqual("created"u8) || valueUtf8.SequenceEqual("closed"u8) || valueUtf8.SequenceEqual("opened"u8) || valueUtf8.SequenceEqual("edited"u8) || valueUtf8.SequenceEqual("deleted"u8),
                EventId.PullRequest => IsPullRequestType(valueUtf8),
                EventId.PullRequestReview => valueUtf8.SequenceEqual("submitted"u8) || valueUtf8.SequenceEqual("edited"u8) || valueUtf8.SequenceEqual("dismissed"u8),
                EventId.PullRequestReviewComment => valueUtf8.SequenceEqual("created"u8) || valueUtf8.SequenceEqual("edited"u8) || valueUtf8.SequenceEqual("deleted"u8),
                EventId.PullRequestTarget => IsPullRequestType(valueUtf8),
                EventId.WorkflowRun => valueUtf8.SequenceEqual("requested"u8) || valueUtf8.SequenceEqual("completed"u8) || valueUtf8.SequenceEqual("in_progress"u8),
                EventId.Release => valueUtf8.SequenceEqual("published"u8) || valueUtf8.SequenceEqual("unpublished"u8) || valueUtf8.SequenceEqual("created"u8) || valueUtf8.SequenceEqual("edited"u8) || valueUtf8.SequenceEqual("deleted"u8) || valueUtf8.SequenceEqual("prereleased"u8) || valueUtf8.SequenceEqual("released"u8),
                EventId.RegistryPackage => valueUtf8.SequenceEqual("published"u8) || valueUtf8.SequenceEqual("updated"u8),
                EventId.Issues => valueUtf8.SequenceEqual("opened"u8) || valueUtf8.SequenceEqual("edited"u8) || valueUtf8.SequenceEqual("deleted"u8) || valueUtf8.SequenceEqual("transferred"u8) || valueUtf8.SequenceEqual("pinned"u8) || valueUtf8.SequenceEqual("unpinned"u8) || valueUtf8.SequenceEqual("closed"u8) || valueUtf8.SequenceEqual("reopened"u8) || valueUtf8.SequenceEqual("assigned"u8) || valueUtf8.SequenceEqual("unassigned"u8) || valueUtf8.SequenceEqual("labeled"u8) || valueUtf8.SequenceEqual("unlabeled"u8) || valueUtf8.SequenceEqual("locked"u8) || valueUtf8.SequenceEqual("unlocked"u8) || valueUtf8.SequenceEqual("milestoned"u8) || valueUtf8.SequenceEqual("demilestoned"u8) || valueUtf8.SequenceEqual("typed"u8) || valueUtf8.SequenceEqual("untyped"u8),
                EventId.IssueComment => valueUtf8.SequenceEqual("created"u8) || valueUtf8.SequenceEqual("edited"u8) || valueUtf8.SequenceEqual("deleted"u8),
                EventId.Watch => valueUtf8.SequenceEqual("started"u8),
                _ => false,
            };
        }

        private ActivityTypesMode GetTypesMode()
        {
            return Id switch
            {
                EventId.RepositoryDispatch => ActivityTypesMode.Any,
                EventId.BranchProtectionRule or EventId.CheckRun or EventId.CheckSuite or EventId.Discussion or EventId.DiscussionComment or EventId.Label or EventId.MergeGroup or EventId.Milestone or EventId.PullRequest or EventId.PullRequestReview or EventId.PullRequestReviewComment or EventId.PullRequestTarget or EventId.WorkflowRun or EventId.Release or EventId.RegistryPackage or EventId.Issues or EventId.IssueComment or EventId.Watch => ActivityTypesMode.Restricted,
                _ => ActivityTypesMode.NotSupported,
            };
        }

        private static bool IsPullRequestType(ReadOnlySpan<byte> value)
        {
            return value.SequenceEqual("assigned"u8) || value.SequenceEqual("unassigned"u8) || value.SequenceEqual("labeled"u8) || value.SequenceEqual("unlabeled"u8) || value.SequenceEqual("opened"u8) || value.SequenceEqual("edited"u8) || value.SequenceEqual("closed"u8) || value.SequenceEqual("reopened"u8) || value.SequenceEqual("synchronize"u8) || value.SequenceEqual("converted_to_draft"u8) || value.SequenceEqual("locked"u8) || value.SequenceEqual("unlocked"u8) || value.SequenceEqual("enqueued"u8) || value.SequenceEqual("dequeued"u8) || value.SequenceEqual("milestoned"u8) || value.SequenceEqual("demilestoned"u8) || value.SequenceEqual("ready_for_review"u8) || value.SequenceEqual("review_requested"u8) || value.SequenceEqual("review_request_removed"u8) || value.SequenceEqual("auto_merge_enabled"u8) || value.SequenceEqual("auto_merge_disabled"u8);
        }
    }
}
