using Seiton.Core.Linting.Rules;

namespace Seiton.Core.Tests;

public sealed partial class RuleInterfaceTests
{

    [Test]
    public async Task RuleRegression_ShellNameRule_TableDriven()
    {
        var cases = new[]
        {
            new RuleCase(
            "ok-bash",
            """
            on: push
            jobs:
                build:
                    runs-on: ubuntu-latest
                    permissions: {}
                    steps:
                        - run: echo ok
                          shell: bash
            """,
            []),
            new RuleCase(
            "ok-pwsh",
            """
            on: push
            jobs:
                build:
                    runs-on: ubuntu-latest
                    permissions: {}
                    steps:
                        - run: echo ok
                          shell: pwsh
            """,
            []),
            new RuleCase(
            "ok-powershell",
            """
            on: push
            jobs:
                build:
                    runs-on: windows-latest
                    permissions: {}
                    steps:
                        - run: echo ok
                          shell: powershell
            """,
            []),
            new RuleCase(
            "ok-sh",
            """
            on: push
            jobs:
                build:
                    runs-on: ubuntu-latest
                    permissions: {}
                    steps:
                        - run: echo ok
                          shell: sh
            """,
            []),
            new RuleCase(
            "ok-cmd",
            """
            on: push
            jobs:
                build:
                    runs-on: windows-latest
                    permissions: {}
                    steps:
                        - run: echo ok
                          shell: cmd
            """,
            []),
            new RuleCase(
            "ok-python",
            """
            on: push
            jobs:
                build:
                    runs-on: ubuntu-latest
                    permissions: {}
                    steps:
                        - run: print('ok')
                          shell: python
            """,
            []),
            new RuleCase(
            "ok-expression-skipped",
            """
            on: push
            jobs:
                build:
                    runs-on: ubuntu-latest
                    permissions: {}
                    steps:
                        - run: echo ok
                          shell: ${{ inputs.shell }}
            """,
            []),
            new RuleCase(
            "ok-no-shell",
            """
            on: push
            jobs:
                build:
                    runs-on: ubuntu-latest
                    permissions: {}
                    steps:
                        - run: echo ok
            """,
            []),
            new RuleCase(
            "ng-invalid-shell",
            """
            on: push
            jobs:
                build:
                    runs-on: ubuntu-latest
                    permissions: {}
                    steps:
                        - run: echo ng
                          shell: zsh
            """,
            ["shell name", "invalid"]),
            new RuleCase(
            "ng-empty-shell",
            """
            on: push
            jobs:
                build:
                    runs-on: ubuntu-latest
                    permissions: {}
                    steps:
                        - run: echo ng
                          shell: ''
            """,
            ["shell name", "invalid"]),
            new RuleCase(
            "ok-workflow-defaults-bash",
            """
            on: push
            defaults:
                run:
                    shell: bash
            jobs:
                build:
                    runs-on: ubuntu-latest
                    permissions: {}
                    steps:
                        - run: echo ok
            """,
            []),
            new RuleCase(
            "ok-job-defaults-pwsh",
            """
            on: push
            jobs:
                build:
                    runs-on: windows-latest
                    permissions: {}
                    defaults:
                        run:
                            shell: pwsh
                    steps:
                        - run: echo ok
            """,
            []),
            new RuleCase(
            "ng-workflow-defaults-invalid-shell",
            """
            on: push
            defaults:
                run:
                    shell: zsh
            jobs:
                build:
                    runs-on: ubuntu-latest
                    permissions: {}
                    steps:
                        - run: echo ok
            """,
            ["shell name", "invalid"]),
            new RuleCase(
            "ng-job-defaults-invalid-shell",
            """
            on: push
            jobs:
                build:
                    runs-on: ubuntu-latest
                    permissions: {}
                    defaults:
                        run:
                            shell: fish
                    steps:
                        - run: echo ok
            """,
            ["shell name", "invalid"]),
            new RuleCase(
            "ok-custom-shell-template-perl",
            """
            on: push
            jobs:
                build:
                    runs-on: ubuntu-latest
                    permissions: {}
                    steps:
                        - run: print "ok"
                          shell: perl {0}
            """,
            []),
            new RuleCase(
            "ok-custom-shell-template-ruby",
            """
            on: push
            jobs:
                build:
                    runs-on: ubuntu-latest
                    permissions: {}
                    steps:
                        - run: puts 'ok'
                          shell: ruby {0}
            """,
            []),
        };

        await AssertRuleCases(new ShellNameRule(), "shell-name", cases);
    }


    [Test]
    public async Task RuleRegression_ShellNameRule_OsSpecific_TableDriven()
    {
        var cases = new[]
        {
            new RuleCase(
            "ng-cmd-on-ubuntu",
            """
            on: push
            jobs:
                build:
                    runs-on: ubuntu-latest
                    steps:
                        - run: echo ok
                          shell: cmd
            """,
            ["cmd", "not available on"]),
            new RuleCase(
            "ng-powershell-on-ubuntu",
            """
            on: push
            jobs:
                build:
                    runs-on: ubuntu-latest
                    steps:
                        - run: echo ok
                          shell: powershell
            """,
            ["powershell", "not available on"]),
            new RuleCase(
            "ok-pwsh-on-ubuntu",
            """
            on: push
            jobs:
                build:
                    runs-on: ubuntu-latest
                    steps:
                        - run: echo ok
                          shell: pwsh
            """,
            []),
            new RuleCase(
            "ok-cmd-on-windows",
            """
            on: push
            jobs:
                build:
                    runs-on: windows-latest
                    steps:
                        - run: echo ok
                          shell: cmd
            """,
            []),
            new RuleCase(
            "ng-sh-on-windows",
            """
            on: push
            jobs:
                build:
                    runs-on: windows-latest
                    steps:
                        - run: echo ng
                          shell: sh
            """,
            ["sh", "not available on"]),
            new RuleCase(
            "ok-sh-on-ubuntu",
            """
            on: push
            jobs:
                build:
                    runs-on: ubuntu-latest
                    steps:
                        - run: echo ok
                          shell: sh
            """,
            []),
        };

        await AssertRuleCases(new ShellNameRule(), "shell-name", cases);
    }
}
