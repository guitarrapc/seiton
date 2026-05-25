using Seiton.Core.Linting.Rules;

namespace Seiton.Core.Tests;

public sealed partial class RuleInterfaceTests
{

    [Test]
    public async Task RuleRegression_DispatchInputsRule_TableDriven()
    {
        var cases = new[]
        {
            new RuleCase(
            "ok-choice-with-options-and-default",
            """
            on:
                workflow_dispatch:
                    inputs:
                        target:
                            type: choice
                            options: [dev, prod]
                            default: dev
            jobs:
                build:
                    runs-on: ubuntu-latest
                    steps:
                        - run: echo ok
            """,
            []),
            new RuleCase(
            "ng-choice-without-options",
            """
            on:
                workflow_dispatch:
                    inputs:
                        target:
                            type: choice
            jobs:
                build:
                    runs-on: ubuntu-latest
                    steps:
                        - run: echo ng
            """,
            ["type 'choice' must define non-empty options"]),
            new RuleCase(
            "ng-choice-duplicate-options",
            """
            on:
                workflow_dispatch:
                    inputs:
                        target:
                            type: choice
                            options: [dev, dev]
            jobs:
                build:
                    runs-on: ubuntu-latest
                    steps:
                        - run: echo ng
            """,
            ["has duplicated option"]),
            new RuleCase(
            "ng-choice-default-not-in-options",
            """
            on:
                workflow_dispatch:
                    inputs:
                        target:
                            type: choice
                            options: [dev, prod]
                            default: staging
            jobs:
                build:
                    runs-on: ubuntu-latest
                    steps:
                        - run: echo ng
            """,
            ["default value 'staging'", "not included in options"]),
            new RuleCase(
            "ng-non-choice-has-options",
            """
            on:
                workflow_dispatch:
                    inputs:
                        count:
                            type: number
                            options: [1, 2]
            jobs:
                build:
                    runs-on: ubuntu-latest
                    steps:
                        - run: echo ng
            """,
            ["has options but type is"]),
            new RuleCase(
            "ng-number-default-not-number",
            """
            on:
                workflow_dispatch:
                    inputs:
                        count:
                            type: number
                            default: NaNValue
            jobs:
                build:
                    runs-on: ubuntu-latest
                    steps:
                        - run: echo ng
            """,
            ["is not a valid number"]),
            new RuleCase(
            "ng-boolean-default-invalid",
            """
            on:
                workflow_dispatch:
                    inputs:
                        force:
                            type: boolean
                            default: yes
            jobs:
                build:
                    runs-on: ubuntu-latest
                    steps:
                        - run: echo ng
            """,
            ["must be 'true' or 'false'"]),
            new RuleCase(
            "ng-more-than-25-inputs",
            """
            on:
                workflow_dispatch:
                    inputs:
                        i01: { type: string }
                        i02: { type: string }
                        i03: { type: string }
                        i04: { type: string }
                        i05: { type: string }
                        i06: { type: string }
                        i07: { type: string }
                        i08: { type: string }
                        i09: { type: string }
                        i10: { type: string }
                        i11: { type: string }
                        i12: { type: string }
                        i13: { type: string }
                        i14: { type: string }
                        i15: { type: string }
                        i16: { type: string }
                        i17: { type: string }
                        i18: { type: string }
                        i19: { type: string }
                        i20: { type: string }
                        i21: { type: string }
                        i22: { type: string }
                        i23: { type: string }
                        i24: { type: string }
                        i25: { type: string }
                        i26: { type: string }
            jobs:
                build:
                    runs-on: ubuntu-latest
                    steps:
                        - run: echo ng
            """,
            ["maximum number of inputs", "25 but 26"]),
        };

        await AssertRuleCases(new DispatchInputsRule(), "dispatch-inputs", cases);
    }
}
