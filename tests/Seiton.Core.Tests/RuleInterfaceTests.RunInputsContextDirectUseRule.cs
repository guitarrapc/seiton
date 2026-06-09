using Seiton.Core.Linting.Rules;

namespace Seiton.Core.Tests;

public sealed partial class RuleInterfaceTests
{

    [Test]
    public async Task RuleRegression_RunInputsContextDirectUseRule_TableDriven()
    {
        var cases = new[]
        {
            new RuleCase(
            "ok-run-uses-shell-variable-only",
            """
            on: workflow_dispatch
            jobs:
                build:
                    runs-on: ubuntu-latest
                    env:
                        TARGET: ${{ inputs.target }}
                    steps:
                        - run: echo "$TARGET"
            """,
            []),
            new RuleCase(
            "ok-block-run-does-not-bleed-into-env-or-next-step-if",
            """
            name: ci
            on: workflow_dispatch
            jobs:
                build:
                    runs-on: ubuntu-latest
                    steps:
                    - name: benchmark
                        run: |
                            dotnet run --filter "${FILTER}"
                            echo "result=success" >> "$GITHUB_OUTPUT"
                        env:
                            FILTER: ${{ inputs.target }}
                    - name: report
                        run: |
                            echo first

                            echo second
                    - name: update
                        if: ${{ inputs.target == '*' }}
                        run: |
                            echo done
            """.Replace("\r\n", "\n").Replace("\n", "\r\n"),
            []),
            new RuleCase(
            "ok-run-uses-non-inputs-expression",
            """
            on: push
            jobs:
                build:
                    runs-on: ubuntu-latest
                    steps:
                        - run: echo "${{ github.ref_name }}"
            """,
            []),
            new RuleCase(
            "ok-run-inputs-inside-single-quotes-default",
            """
            on: workflow_dispatch
            jobs:
                build:
                    runs-on: ubuntu-latest
                    steps:
                        - run: echo '${{ inputs.target }}'
            """,
            []),
            new RuleCase(
            "ok-run-inputs-inside-single-quoted-heredoc",
            """
            on: workflow_dispatch
            jobs:
                build:
                    runs-on: ubuntu-latest
                    steps:
                        - run: |
                            cat <<'EOF'
                            ${{ inputs.target }}
                            EOF
            """,
            []),
            new RuleCase(
            "ng-run-uses-inputs-dot-access",
            """
            on: workflow_dispatch
            jobs:
                build:
                    runs-on: ubuntu-latest
                    steps:
                        - run: echo "${{ inputs.target }}"
            """,
            ["must not reference", "inputs.*", "shell variables"]),
            new RuleCase(
            "ng-run-uses-inputs-bracket-access",
            """
            on: workflow_dispatch
            jobs:
                build:
                    runs-on: ubuntu-latest
                    steps:
                        - run: echo "${{ inputs['target'] }}"
            """,
            ["must not reference", "inputs.*", "shell variables"]),
            new RuleCase(
            "ng-run-uses-inputs-dot-access-with-whitespace",
            """
            on: workflow_dispatch
            jobs:
                build:
                    runs-on: ubuntu-latest
                    steps:
                        - run: echo "${{ inputs . target }}"
            """,
            ["must not reference", "inputs.*", "shell variables"]),
            new RuleCase(
            "ng-run-uses-github-event-inputs-dot-access",
            """
            on: workflow_dispatch
            jobs:
                build:
                    runs-on: ubuntu-latest
                    steps:
                        - run: echo "${{ github.event.inputs.target }}"
            """,
            ["must not reference", "inputs.*", "shell variables"]),
            new RuleCase(
            "ng-run-uses-github-event-inputs-dot-access-with-whitespace",
            """
            on: workflow_dispatch
            jobs:
                build:
                    runs-on: ubuntu-latest
                    steps:
                        - run: echo "${{ github . event . inputs . target }}"
            """,
            ["must not reference", "inputs.*", "shell variables"]),
            new RuleCase(
            "ng-run-uses-github-event-inputs-bracket-with-whitespace",
            """
            on: workflow_dispatch
            jobs:
                build:
                    runs-on: ubuntu-latest
                    steps:
                        - run: echo "${{ github . event . inputs [ 'target' ] }}"
            """,
            ["must not reference", "inputs.*", "shell variables"]),
            new RuleCase(
            "ng-run-uses-inputs-in-function",
            """
            on: workflow_dispatch
            jobs:
                build:
                    runs-on: ubuntu-latest
                    steps:
                        - run: echo "${{ format('{0}', inputs.target) }}"
            """,
            ["must not reference", "inputs.*", "shell variables"]),
        };

        await AssertRuleCases(new RunInputsContextDirectUseRule(), "run-inputs-context-direct-use", cases);
    }
}
