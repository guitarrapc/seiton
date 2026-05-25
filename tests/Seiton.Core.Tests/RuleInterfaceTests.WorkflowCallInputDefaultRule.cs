using Seiton.Core.Linting.Rules;

namespace Seiton.Core.Tests;

public sealed partial class RuleInterfaceTests
{

    [Test]
    public async Task RuleRegression_WorkflowCallInputDefaultRule_TableDriven()
    {
        var cases = new[]
        {
            new RuleCase(
            "ng-boolean-input-non-bool-default",
            """
            on:
                workflow_call:
                    inputs:
                        debug:
                            type: boolean
                            default: "yes"
            jobs:
                build:
                    runs-on: ubuntu-latest
                    steps:
                        - run: echo ok
            """,
            ["boolean", "default"]),
            new RuleCase(
            "ng-number-input-non-number-default",
            """
            on:
                workflow_call:
                    inputs:
                        retries:
                            type: number
                            default: "three"
            jobs:
                build:
                    runs-on: ubuntu-latest
                    steps:
                        - run: echo ok
            """,
            ["number", "default"]),
            new RuleCase(
            "ok-boolean-input-true-default",
            """
            on:
                workflow_call:
                    inputs:
                        debug:
                            type: boolean
                            default: true
            jobs:
                build:
                    runs-on: ubuntu-latest
                    steps:
                        - run: echo ok
            """,
            []),
            new RuleCase(
            "ok-string-input-any-default",
            """
            on:
                workflow_call:
                    inputs:
                        name:
                            type: string
                            default: "hello"
            jobs:
                build:
                    runs-on: ubuntu-latest
                    steps:
                        - run: echo ok
            """,
            []),
            new RuleCase(
            "ng-required-input-with-default",
            """
            on:
                workflow_call:
                    inputs:
                        path:
                            type: string
                            required: true
                            default: ""
            jobs:
                build:
                    runs-on: ubuntu-latest
                    steps:
                        - run: echo ok
            """,
            ["default", "required"]),
            new RuleCase(
            "ok-required-input-without-default",
            """
            on:
                workflow_call:
                    inputs:
                        path:
                            type: string
                            required: true
            jobs:
                build:
                    runs-on: ubuntu-latest
                    steps:
                        - run: echo ok
            """,
            []),
        };

        await AssertRuleCases(new WorkflowCallInputDefaultRule(), "workflow-call-input-default", cases);
    }
}
