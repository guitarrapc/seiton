using System.Text;
using Seiton.Core.Linting;
using Seiton.Core.Linting.Rules;

namespace Seiton.Core.Tests;

public sealed partial class RuleInterfaceTests
{

    [Test]
    public async Task RuleRegression_ExprUndefinedVarRule_TableDriven()
    {
        var cases = new[]
        {
            new RuleCase(
            "ok-step-if-uses-steps-context",
            """
            on: pull_request
            jobs:
                build:
                    runs-on: ubuntu-latest
                    steps:
                        - id: prep
                          run: echo ok
                        - if: ${{ steps.prep.outcome == 'success' }}
                          run: echo next
            """,
            []),
            new RuleCase(
            "ok-step-with-safe-context",
            """
            on: workflow_dispatch
            jobs:
                build:
                    runs-on: ubuntu-latest
                    steps:
                        - uses: actions/checkout@v4
                          with:
                            repository: ${{ github.repository }}
            """,
            []),
            new RuleCase(
            "ng-job-if-uses-steps-context",
            """
            on: push
            jobs:
                build:
                    if: ${{ steps.prep.outcome == 'success' }}
                    runs-on: ubuntu-latest
                    steps:
                        - run: echo ng
            """,
            ["context \"steps\" is not allowed here"]),
            new RuleCase(
            "ng-job-if-uses-strategy-context",
            """
            on: push
            jobs:
                build:
                    if: ${{ strategy.fail-fast }}
                    runs-on: ubuntu-latest
                    steps:
                        - run: echo ng
            """,
            ["context \"strategy\" is not allowed here"]),
            new RuleCase(
            "ng-job-if-uses-matrix-context",
            """
            on: push
            jobs:
                build:
                    if: ${{ matrix.os == 'ubuntu-latest' }}
                    runs-on: ubuntu-latest
                    steps:
                        - run: echo ng
            """,
            ["context \"matrix\" is not allowed here"]),
            new RuleCase(
            "ng-job-if-uses-secrets-context",
            """
            on: push
            jobs:
                build:
                    if: ${{ secrets.TOKEN != '' }}
                    runs-on: ubuntu-latest
                    steps:
                        - run: echo ng
            """,
            ["context \"secrets\" is not allowed here"]),
            new RuleCase(
            "ng-step-if-uses-secrets-context",
            """
            on: push
            jobs:
                build:
                    runs-on: ubuntu-latest
                    steps:
                        - if: ${{ secrets.TOKEN != '' }}
                          run: echo ng
            """,
            ["context \"secrets\" is not allowed here"]),
            new RuleCase(
            "ok-step-run-uses-secrets-context",
            """
            on: push
            jobs:
                build:
                    runs-on: ubuntu-latest
                    steps:
                        - run: echo ${{ secrets.TOKEN }}
            """,
            []),
            new RuleCase(
            "ok-step-env-uses-secrets-context",
            """
            on: push
            jobs:
                build:
                    runs-on: ubuntu-latest
                    steps:
                        - env:
                            TOKEN: ${{ secrets.TOKEN }}
                          run: echo ok
            """,
            []),
            new RuleCase(
            "ng-step-if-uses-unknown-context",
            """
            on: push
            jobs:
                build:
                    runs-on: ubuntu-latest
                    steps:
                        - if: ${{ foobar.value == 'x' }}
                          run: echo ng
            """,
            ["undefined context \"foobar\""]),
            new RuleCase(
            "ng-step-env-uses-unknown-context",
            """
            on: push
            jobs:
                build:
                    runs-on: ubuntu-latest
                    steps:
                        - env:
                            DATA: ${{ unknown.payload }}
                          run: echo "$DATA"
            """,
            ["undefined context \"unknown\""]),
            new RuleCase(
            "ng-step-with-uses-unknown-context",
            """
            on: pull_request
            jobs:
                build:
                    runs-on: ubuntu-latest
                    steps:
                        - uses: actions/checkout@v4
                          with:
                            repository: ${{ unknown.repository }}
            """,
            ["undefined context \"unknown\""]),
        };

        await AssertRuleCases(new ExprUndefinedVarRule(), "expr-undefined-var", cases);
    }


    [Test]
    public async Task RuleRegression_ExprUndefinedVarRule_EnvKeyExpression_TableDriven()
    {
        var cases = new[]
        {
            // env key with valid runner property — should only get portability warning (from EnvVarRule, not here)
            new RuleCase(
            "ok-env-key-valid-runner-property",
            """
            on: push
            jobs:
                build:
                    runs-on: ubuntu-latest
                    steps:
                        - run: echo hi
                          env:
                            ${{ runner.name }}: ''
            """,
            []),
            // env key with invalid runner property — should report property not defined
            new RuleCase(
            "ng-container-env-key-invalid-property",
            """
            on: push
            jobs:
                build:
                    runs-on: ubuntu-latest
                    container:
                        image: node:14.16
                        env:
                            ${{ runner.foooooo }}: ''
                    steps:
                        - run: echo hi
            """,
            ["property \"foooooo\" is not defined in \"runner\" context"]),
            // job env key with invalid runner property
            new RuleCase(
            "ng-job-env-key-invalid-property",
            """
            on: push
            jobs:
                build:
                    runs-on: ubuntu-latest
                    env:
                        ${{ runner.fooooooo }}: ''
                    steps:
                        - run: echo hi
            """,
            ["property \"fooooooo\" is not defined in \"runner\" context"]),
        };

        await AssertRuleCases(new ExprUndefinedVarRule(), "expr-undefined-var", cases);
    }


    [Test]
    public async Task RuleRegression_ExprUndefinedVarRule_InputDefaultTypeCheck_TableDriven()
    {
        var cases = new[]
        {
            // ok: boolean default with boolean expression
            new RuleCase(
            "ok-bool-default-bool-expr",
            """
            on:
              workflow_call:
                inputs:
                  input1:
                    type: boolean
                  input2:
                    type: boolean
                    default: ${{ inputs.input1 }}
            jobs:
              test:
                runs-on: ubuntu-latest
                steps:
                  - run: echo ok
            """,
            []),
            // ok: number default with number expression
            new RuleCase(
            "ok-number-default-number-expr",
            """
            on:
              workflow_call:
                inputs:
                  input1:
                    type: number
                  input2:
                    type: number
                    default: ${{ inputs.input1 }}
            jobs:
              test:
                runs-on: ubuntu-latest
                steps:
                  - run: echo ok
            """,
            []),
            // ng: boolean input with string expression
            new RuleCase(
            "ng-bool-default-string-expr",
            """
            on:
              workflow_call:
                inputs:
                  input1:
                    type: string
                  input2:
                    type: boolean
                    default: ${{ inputs.input1 }}
            jobs:
              test:
                runs-on: ubuntu-latest
                steps:
                  - run: echo ng
            """,
            ["type of input \"input2\" must be bool but found type string"]),
            // ng: number input with string expression
            new RuleCase(
            "ng-number-default-string-expr",
            """
            on:
              workflow_call:
                inputs:
                  input1:
                    type: string
                  input2:
                    type: number
                    default: ${{ inputs.input1 }}
            jobs:
              test:
                runs-on: ubuntu-latest
                steps:
                  - run: echo ng
            """,
            ["type of input \"input2\" must be number but found type string"]),
        };

        await AssertRuleCases(new ExprUndefinedVarRule(), "expr-undefined-var", cases);
    }


    [Test]
    public async Task RuleRegression_ExprUndefinedVarRule_ContextAvailability4C_TableDriven()
    {
        var cases = new[]
        {
            // 4.C-A: workflow_call output value should check root context availability
            new RuleCase(
            "ng-workflow-call-output-value-env-not-allowed",
            """
            on:
              workflow_call:
                outputs:
                  result:
                    value: ${{ env.FOO }}
            jobs:
              build:
                runs-on: ubuntu-latest
                steps:
                  - run: echo ok
            """,
            ["context \"env\" is not allowed here"]),

            new RuleCase(
            "ok-workflow-call-output-value-jobs-context",
            """
            on:
              workflow_call:
                outputs:
                  result:
                    value: ${{ jobs.build.outputs.foo }}
            jobs:
              build:
                runs-on: ubuntu-latest
                outputs:
                  foo: bar
                steps:
                  - run: echo ok
            """,
            []),

            // 4.C-B: snapshot.if should be checked for context availability
            new RuleCase(
            "ng-snapshot-if-env-not-allowed",
            """
            on: push
            jobs:
              build:
                runs-on: ubuntu-latest
                snapshot:
                  image-name: my-image
                  if: ${{ env.FOO == 'foo' }}
                steps:
                  - run: echo ok
            """,
            ["context \"env\" is not allowed here"]),

            new RuleCase(
            "ng-snapshot-if-runner-not-allowed",
            """
            on: push
            jobs:
              build:
                runs-on: ubuntu-latest
                snapshot:
                  image-name: my-image
                  if: ${{ runner.name == 'foo' }}
                steps:
                  - run: echo ok
            """,
            ["context \"runner\" is not allowed here"]),

            new RuleCase(
            "ng-snapshot-if-secrets-not-allowed",
            """
            on: push
            jobs:
              build:
                runs-on: ubuntu-latest
                snapshot:
                  image-name: my-image
                  if: ${{ secrets.FOO == 'foo' }}
                steps:
                  - run: echo ok
            """,
            ["context \"secrets\" is not allowed here"]),

            new RuleCase(
            "ok-snapshot-if-strategy-matrix-allowed",
            """
            on: push
            jobs:
              build:
                runs-on: ubuntu-latest
                strategy:
                  matrix:
                    foo: [a, b]
                snapshot:
                  image-name: my-image
                  if: ${{ matrix.foo == 'a' && strategy.fail-fast }}
                steps:
                  - run: echo ok
            """,
            []),

            // 4.C-C: service entrypoint/command should be checked for context availability
            new RuleCase(
            "ng-service-entrypoint-env-not-allowed",
            """
            on: push
            jobs:
              build:
                runs-on: ubuntu-latest
                services:
                  nginx:
                    image: nginx
                    entrypoint: ${{ env.FOO }}
                steps:
                  - run: echo ok
            """,
            ["context \"env\" is not allowed here"]),

            new RuleCase(
            "ng-service-command-env-not-allowed",
            """
            on: push
            jobs:
              build:
                runs-on: ubuntu-latest
                services:
                  nginx:
                    image: nginx
                    command: ${{ env.FOO }}
                steps:
                  - run: echo ok
            """,
            ["context \"env\" is not allowed here"]),

            new RuleCase(
            "ok-service-entrypoint-github-context",
            """
            on: push
            jobs:
              build:
                runs-on: ubuntu-latest
                services:
                  nginx:
                    image: nginx
                    entrypoint: ${{ github.actor }}
                steps:
                  - run: echo ok
            """,
            []),

            // Services expression form: env context should not be allowed
            new RuleCase(
            "ng-services-expression-env-not-allowed",
            """
            on:
              workflow_call:
                inputs:
                  bool:
                    type: boolean
            jobs:
              build:
                runs-on: ubuntu-latest
                services: ${{ inputs.bool || env.FOO }}
                steps:
                  - run: echo ok
            """,
            ["context \"env\" is not allowed here"]),
        };

        await AssertRuleCases(new ExprUndefinedVarRule(), "expr-undefined-var", cases);
    }


    [Test]
    public async Task RuleRegression_ExprUndefinedVarRule_DynamicContext_TableDriven()
    {
        var cases = new[]
        {
            new RuleCase(
            "ok-step-accesses-known-step-id",
            """
            on: push
            jobs:
                build:
                    runs-on: ubuntu-latest
                    steps:
                        - id: prep
                          run: echo ok
                        - if: ${{ steps.prep.outcome == 'success' }}
                          run: echo next
            """,
            []),
            new RuleCase(
            "ok-step-accesses-known-matrix-key",
            """
            on: push
            jobs:
                build:
                    runs-on: ubuntu-latest
                    strategy:
                        matrix:
                            os: [ubuntu-latest, windows-latest]
                    steps:
                        - run: echo ${{ matrix.os }}
            """,
            []),
            new RuleCase(
            "ok-step-accesses-known-needs-job",
            """
            on: push
            jobs:
                build:
                    runs-on: ubuntu-latest
                    steps:
                        - run: echo build
                test:
                    runs-on: ubuntu-latest
                    needs: [build]
                    steps:
                        - run: echo ${{ needs.build.result }}
            """,
            []),
            new RuleCase(
            "ok-step-accesses-known-workflow-call-input",
            """
            on:
                workflow_call:
                    inputs:
                        environment:
                            type: string
                            required: true
            jobs:
                deploy:
                    runs-on: ubuntu-latest
                    steps:
                        - run: echo ${{ inputs.environment }}
            """,
            []),
            new RuleCase(
            "ok-matrix-no-rows-loose-object-no-error",
            """
            on: push
            jobs:
                build:
                    runs-on: ubuntu-latest
                    strategy:
                        matrix:
                            include:
                                - os: ubuntu-latest
                    steps:
                        - run: echo ${{ matrix.os }}
            """,
            []),
            new RuleCase(
            "ng-step-accesses-unknown-step-id",
            """
            on: push
            jobs:
                build:
                    runs-on: ubuntu-latest
                    steps:
                        - id: prep
                          run: echo ok
                        - if: ${{ steps.nonexistent.outcome == 'success' }}
                          run: echo next
            """,
            ["\"nonexistent\" is not defined in \"steps\" context"]),
            new RuleCase(
            "ng-step-accesses-unknown-matrix-key",
            """
            on: push
            jobs:
                build:
                    runs-on: ubuntu-latest
                    strategy:
                        matrix:
                            os: [ubuntu-latest, windows-latest]
                    steps:
                        - env:
                            VALUE: ${{ matrix.unknown_key }}
                          run: echo "$VALUE"
            """,
            ["\"unknown_key\" is not defined in \"matrix\" context"]),
            new RuleCase(
            "ng-step-accesses-unknown-needs-job",
            """
            on: push
            jobs:
                build:
                    runs-on: ubuntu-latest
                    steps:
                        - run: echo build
                test:
                    runs-on: ubuntu-latest
                    needs: [build]
                    steps:
                        - env:
                            RESULT: ${{ needs.nonexistent.outputs.foo }}
                          run: echo "$RESULT"
            """,
            ["\"nonexistent\" is not defined in \"needs\" context"]),
            new RuleCase(
            "ng-step-accesses-unknown-workflow-call-input",
            """
            on:
                workflow_call:
                    inputs:
                        environment:
                            type: string
                            required: true
            jobs:
                deploy:
                    runs-on: ubuntu-latest
                    steps:
                        - env:
                            VAL: ${{ inputs.unknown_param }}
                          run: echo "$VAL"
            """,
            ["\"unknown_param\" is not defined in \"inputs\" context"]),
            // index access: inputs['unknown'] should be flagged the same as inputs.unknown
            new RuleCase(
            "ng-index-access-unknown-input",
            """
            on:
                workflow_call:
                    inputs:
                        environment:
                            type: string
                            required: true
            jobs:
                deploy:
                    runs-on: ubuntu-latest
                    steps:
                        - env:
                            VAL: ${{ inputs['unknown_param'] }}
                          run: echo "$VAL"
            """,
            ["\"unknown_param\" is not defined in \"inputs\" context"]),
            // index access: inputs['environment'] should pass
            new RuleCase(
            "ok-index-access-known-input",
            """
            on:
                workflow_call:
                    inputs:
                        environment:
                            type: string
                            required: true
            jobs:
                deploy:
                    runs-on: ubuntu-latest
                    steps:
                        - env:
                            VAL: ${{ inputs['environment'] }}
                          run: echo "$VAL"
            """,
            []),
            // regression: matrix include-only axis keys should be accessible
            new RuleCase(
            "ok-matrix-include-only-axis-accessible",
            """
            on: push
            jobs:
                test:
                    strategy:
                        matrix:
                            os: [ubuntu-latest, windows-latest]
                            node: [14, 15]
                            include:
                                - node: 15
                                  npm: 7.5.4
                    runs-on: ${{ matrix.os }}
                    steps:
                        - run: echo ${{ matrix.os }}
                        - run: echo ${{ matrix.node }}
                        - run: echo ${{ matrix.npm }}
            """,
            []),
            // regression: include-only matrix (no row axes) should resolve keys
            new RuleCase(
            "ok-matrix-include-only-no-rows",
            """
            on: push
            jobs:
                test:
                    strategy:
                        matrix:
                            include:
                                - os: ubuntu-latest
                                  version: 1
                                - os: windows-latest
                                  version: 2
                    runs-on: ${{ matrix.os }}
                    steps:
                        - run: echo ${{ matrix.version }}
            """,
            []),
            // regression: step env with expression scalar should not error
            new RuleCase(
            "ok-step-env-expression-scalar",
            """
            on: push
            jobs:
                test:
                    strategy:
                        matrix:
                            env_object:
                                - FOO: BAR
                                - FOO: PIYO
                    runs-on: ubuntu-latest
                    steps:
                        - run: echo "$FOO"
                          env: ${{ matrix.env_object }}
            """,
            []),
            // A-3: matrix nested object property access — known property should be fine
            new RuleCase(
            "ok-matrix-nested-object-property",
            """
            on: push
            jobs:
                build:
                    strategy:
                        matrix:
                            package:
                                - name: 'foo'
                                  optional: true
                                - name: 'bar'
                                  optional: false
                    runs-on: ubuntu-latest
                    steps:
                        - run: echo ${{ matrix.package.name }}
            """,
            []),
            // A-3: matrix nested object — unknown property should error
            new RuleCase(
            "ng-matrix-nested-object-unknown-property",
            """
            on: push
            jobs:
                build:
                    strategy:
                        matrix:
                            package:
                                - name: 'foo'
                                  optional: true
                                - name: 'bar'
                                  optional: false
                    runs-on: ubuntu-latest
                    steps:
                        - run: echo ${{ matrix.package.dev }}
            """,
            ["\"dev\" is not defined"]),
            // A-3: matrix undefined axis (no such key at all)
            new RuleCase(
            "ng-matrix-undefined-axis",
            """
            on: push
            jobs:
                build:
                    strategy:
                        matrix:
                            os: [ubuntu-latest, windows-latest]
                    runs-on: ${{ matrix.os }}
                    steps:
                        - run: echo ${{ matrix.platform }}
            """,
            ["\"platform\" is not defined in \"matrix\" context"]),
            // A-3: empty matrix in other job — matrix should be strict empty
            new RuleCase(
            "ng-matrix-empty-in-other-job",
            """
            on: push
            jobs:
                test:
                    strategy:
                        matrix:
                            os: [ubuntu-latest]
                    runs-on: ${{ matrix.os }}
                    steps:
                        - run: echo test
                other:
                    runs-on: ubuntu-latest
                    steps:
                        - run: echo ${{ matrix.os }}
            """,
            ["\"os\" is not defined in \"matrix\" context"]),
            // A-19: popular action output — known output should be fine
            new RuleCase(
            "ok-popular-action-known-output",
            """
            on: push
            jobs:
                build:
                    runs-on: ubuntu-latest
                    steps:
                        - uses: actions/cache@v4
                          id: cache
                          with:
                            key: ${{ hashFiles('**/*.lock') }}
                            path: ./packages
                        - run: echo ${{ steps.cache.outputs.cache-hit }}
            """,
            []),
            // A-19: popular action output — typo should be flagged
            new RuleCase(
            "ng-popular-action-unknown-output",
            """
            on: push
            jobs:
                build:
                    runs-on: ubuntu-latest
                    steps:
                        - uses: actions/cache@v4
                          id: cache
                          with:
                            key: ${{ hashFiles('**/*.lock') }}
                            path: ./packages
                        - run: echo ${{ steps.cache.outputs.cache_hit }}
            """,
            ["\"cache_hit\" is not defined"]),
            // regression: github.event.inputs.unknown should be flagged for workflow_dispatch
            new RuleCase(
            "ng-github-event-inputs-unknown-property",
            """
            on:
              workflow_dispatch:
                inputs:
                  myinput:
                    type: string
            jobs:
              build:
                runs-on: ubuntu-latest
                steps:
                  - run: echo "${{ github.event.inputs.select }}"
            """,
            ["\"select\" is not defined"]),
        };

        await AssertRuleCases(new ExprUndefinedVarRule(), "expr-undefined-var", cases);
    }


    [Test]
    public async Task RuleRegression_ExprUndefinedVarRule_ComparisonTypeCheck_TableDriven()
    {
        var cases = new[]
        {
            new RuleCase(
            "ng-bool-input-greater-than-number",
            """
            on:
                workflow_call:
                    inputs:
                        timeout:
                            type: boolean
            jobs:
                test:
                    runs-on: ubuntu-latest
                    steps:
                        - if: ${{ inputs.timeout > 60 }}
                          run: echo timeout
            """,
            ["bool value cannot be compared to number value with '>' operator"]),
            new RuleCase(
            "ok-number-input-less-than-number",
            """
            on:
                workflow_call:
                    inputs:
                        count:
                            type: number
            jobs:
                test:
                    runs-on: ubuntu-latest
                    steps:
                        - if: ${{ inputs.count < 100 }}
                          run: echo ok
            """,
            []),
            new RuleCase(
            "ok-string-input-equals-string",
            """
            on:
                workflow_call:
                    inputs:
                        env:
                            type: string
            jobs:
                test:
                    runs-on: ubuntu-latest
                    steps:
                        - if: ${{ inputs.env == 'production' }}
                          run: echo deploy
            """,
            []),
            new RuleCase(
            "ng-bool-input-less-or-equal-number",
            """
            on:
                workflow_call:
                    inputs:
                        verbose:
                            type: boolean
            jobs:
                test:
                    runs-on: ubuntu-latest
                    steps:
                        - if: ${{ inputs.verbose <= 5 }}
                          run: echo ok
            """,
            ["bool value cannot be compared to number value with '<=' operator"]),
            new RuleCase(
            "ng-bool-input-greater-or-equal-number",
            """
            on:
                workflow_call:
                    inputs:
                        flag:
                            type: boolean
            jobs:
                test:
                    runs-on: ubuntu-latest
                    steps:
                        - if: ${{ inputs.flag >= 1 }}
                          run: echo ok
            """,
            ["bool value cannot be compared to number value with '>=' operator"]),
            new RuleCase(
            "ng-bool-input-not-equals-number",
            """
            on:
                workflow_call:
                    inputs:
                        flag:
                            type: boolean
            jobs:
                test:
                    runs-on: ubuntu-latest
                    steps:
                        - if: ${{ inputs.flag != 60 }}
                          run: echo ok
            """,
            ["bool value cannot be compared to number value with '!=' operator"]),
            new RuleCase(
            "ok-string-input-not-equals-string",
            """
            on:
                workflow_call:
                    inputs:
                        env:
                            type: string
            jobs:
                test:
                    runs-on: ubuntu-latest
                    steps:
                        - if: ${{ inputs.env != 'staging' }}
                          run: echo deploy
            """,
            []),
            new RuleCase(
            "ok-any-input-greater-than-number",
            """
            on: push
            jobs:
                test:
                    runs-on: ubuntu-latest
                    steps:
                        - if: ${{ github.event.number > 0 }}
                          run: echo ok
            """,
            []),
        };

        await AssertRuleCases(new ExprUndefinedVarRule(), "expr-undefined-var", cases);
    }


    [Test]
    public async Task RuleRegression_ExprUndefinedVarRule_TemplateTypeCheck_TableDriven()
    {
        var cases = new[]
        {
            new RuleCase(
            "ng-step-env-object-in-template",
            """
            on: push
            jobs:
                build:
                    runs-on: ubuntu-latest
                    steps:
                        - env:
                            VAL: ${{ fromJson('{"a":1}') }}
                          run: echo "$VAL"
            """,
            ["{a: number} value in ${{ }}"]),
            new RuleCase(
            "ng-step-env-null-in-template",
            """
            on: push
            jobs:
                build:
                    runs-on: ubuntu-latest
                    steps:
                        - env:
                            VAL: ${{ null }}
                          run: echo "$VAL"
            """,
            ["null value in ${{ }}"]),
            new RuleCase(
            "ok-step-if-object-no-template-warning",
            """
            on: push
            jobs:
                build:
                    runs-on: ubuntu-latest
                    steps:
                        - if: ${{ fromJson('{"a":1}') }}
                          run: echo ok
            """,
            []),
            new RuleCase(
            "ok-step-env-string-in-template",
            """
            on: push
            jobs:
                build:
                    runs-on: ubuntu-latest
                    steps:
                        - env:
                            VAL: ${{ github.ref }}
                          run: echo "$VAL"
            """,
            []),
        };

        await AssertRuleCases(new ExprUndefinedVarRule(), "expr-undefined-var", cases);
    }


    [Test]
    public async Task RuleRegression_ExprUndefinedVarRule_MatrixArrayTemplateTypeCheck_TableDriven()
    {
        var cases = new[]
        {
            new RuleCase(
            "ng-matrix-array-in-template",
            """
            on: push
            jobs:
                test:
                    strategy:
                        matrix:
                            bar:
                                - [42]
                                - [true]
                    runs-on: ubuntu-latest
                    steps:
                        - run: echo ${{ matrix.bar }}
            """,
            ["array value in ${{ }}"]),
            new RuleCase(
            "ok-matrix-array-element-access",
            """
            on: push
            jobs:
                test:
                    strategy:
                        matrix:
                            bar:
                                - [42]
                                - [true]
                    runs-on: ubuntu-latest
                    steps:
                        - run: echo ${{ matrix.bar[0] }}
            """,
            []),
            new RuleCase(
            "ok-matrix-mixed-types-any",
            """
            on: push
            jobs:
                test:
                    strategy:
                        matrix:
                            foo:
                                - 'string value'
                                - 42
                    runs-on: ubuntu-latest
                    steps:
                        - run: echo ${{ matrix.foo }}
            """,
            []),
            new RuleCase(
            "ng-matrix-object-in-template",
            """
            on: push
            jobs:
                test:
                    strategy:
                        matrix:
                            obj:
                                - { a: 1, b: 2 }
                                - { a: 3, b: 4 }
                    runs-on: ubuntu-latest
                    steps:
                        - run: echo ${{ matrix.obj }}
            """,
            ["{a: number; b: number} value in ${{ }}"]),
        };

        await AssertRuleCases(new ExprUndefinedVarRule(), "expr-undefined-var", cases);
    }


    [Test]
    public async Task RuleRegression_ExprUndefinedVarRule_EnvMappingTypeCheck_TableDriven()
    {
        var cases = new[]
        {
            new RuleCase(
            "ng-env-string-as-mapping",
            """
            on: push
            jobs:
                test:
                    strategy:
                        matrix:
                            env_string:
                                - 'FOO=BAR'
                                - 'FOO=PIYO'
                    runs-on: ubuntu-latest
                    steps:
                        - run: echo "$FOO"
                          env: ${{ matrix.env_string }}
            """,
            ["cannot be expanded as mapping"]),
            new RuleCase(
            "ok-env-object-as-mapping",
            """
            on: push
            jobs:
                test:
                    strategy:
                        matrix:
                            env_object:
                                - FOO: BAR
                                - FOO: PIYO
                    runs-on: ubuntu-latest
                    steps:
                        - run: echo "$FOO"
                          env: ${{ matrix.env_object }}
            """,
            []),
            new RuleCase(
            "ok-env-any-as-mapping",
            """
            on: push
            jobs:
                test:
                    runs-on: ubuntu-latest
                    steps:
                        - run: echo "$FOO"
                          env: ${{ fromJson('{"FOO":"bar"}') }}
            """,
            []),
            new RuleCase(
            "ng-env-array-as-mapping",
            """
            on: push
            jobs:
                test:
                    strategy:
                        matrix:
                            arr:
                                - [1, 2]
                                - [3, 4]
                    runs-on: ubuntu-latest
                    steps:
                        - run: echo test
                          env: ${{ matrix.arr }}
            """,
            ["cannot be expanded as mapping"]),
        };

        await AssertRuleCases(new ExprUndefinedVarRule(), "expr-undefined-var", cases);
    }


    [Test]
    public async Task RuleRegression_ExprUndefinedVarRule_CredentialsObjectTypeCheck_TableDriven()
    {
        var cases = new[]
        {
            new RuleCase(
            "ok-credentials-fromjson-object",
            """
            on: push
            jobs:
                test:
                    runs-on: ubuntu-latest
                    container:
                        image: ubuntu:latest
                        credentials: ${{ fromJSON('{}') }}
                    steps:
                        - run: echo ok
            """,
            []),
            new RuleCase(
            "ng-credentials-string-expression",
            """
            on: push
            jobs:
                test:
                    runs-on: ubuntu-latest
                    container:
                        image: ubuntu:latest
                        credentials: ${{ 'username:password' }}
                    steps:
                        - run: echo
            """,
            ["type of expression at \"credentials\" must be object but found type string"]),
            new RuleCase(
            "ng-services-string-expression",
            """
            on: push
            jobs:
                test:
                    services: ${{ 'redis' }}
                    runs-on: ubuntu-latest
                    steps:
                        - run: echo
            """,
            ["type of expression at \"services\" must be object but found type string"]),
            new RuleCase(
            "ok-services-fromjson-object",
            """
            on: push
            jobs:
                test:
                    services: ${{ fromJSON('{}') }}
                    runs-on: ubuntu-latest
                    steps:
                        - run: echo ok
            """,
            []),
            new RuleCase(
            "ng-service-credentials-string-expression",
            """
            on: push
            jobs:
                test:
                    runs-on: ubuntu-latest
                    services:
                        redis:
                            image: redis:latest
                            credentials: ${{ 'user:pass' }}
                    steps:
                        - run: echo
            """,
            ["type of expression at \"credentials\" must be object but found type string"]),
        };

        await AssertRuleCases(new ExprUndefinedVarRule(), "expr-undefined-var", cases);
    }


    [Test]
    public async Task RuleRegression_ExprUndefinedVarRule_IndexTypeCheckWithOverrides_TableDriven()
    {
        var cases = new[]
        {
            new RuleCase(
            "ng-bool-index-on-object",
            """
            on:
                workflow_dispatch:
                    inputs:
                        verbose:
                            type: boolean
            jobs:
                test:
                    runs-on: ubuntu-latest
                    steps:
                        - run: echo "${{ env[inputs.verbose] }}"
            """,
            ["index of object must be string, but got bool"]),
            new RuleCase(
            "ng-number-index-on-object",
            """
            on:
                workflow_dispatch:
                    inputs:
                        age:
                            type: number
            jobs:
                test:
                    runs-on: ubuntu-latest
                    steps:
                        - run: echo "${{ env[inputs.age] }}"
            """,
            ["index of object must be string, but got number"]),
            new RuleCase(
            "ok-string-index-on-object",
            """
            on:
                workflow_dispatch:
                    inputs:
                        name:
                            type: string
            jobs:
                test:
                    runs-on: ubuntu-latest
                    steps:
                        - run: echo "${{ env[inputs.name] }}"
            """,
            []),
        };

        await AssertRuleCases(new ExprUndefinedVarRule(), "expr-undefined-var", cases);
    }


    [Test]
    public async Task RuleRegression_ExprUndefinedVarRule_SecretsResolution_TableDriven()
    {
        var cases = new[]
        {
            new RuleCase(
            "ok-workflow-call-secret-known",
            """
            on:
                workflow_call:
                    secrets:
                        DEPLOY_KEY:
                            required: true
            jobs:
                deploy:
                    runs-on: ubuntu-latest
                    steps:
                        - env:
                            KEY: ${{ secrets.DEPLOY_KEY }}
                          run: echo "$KEY"
            """,
            []),
            new RuleCase(
            "ng-workflow-call-secret-unknown",
            """
            on:
                workflow_call:
                    secrets:
                        DEPLOY_KEY:
                            required: true
            jobs:
                deploy:
                    runs-on: ubuntu-latest
                    steps:
                        - env:
                            KEY: ${{ secrets.UNKNOWN_SECRET }}
                          run: echo "$KEY"
            """,
            ["\"UNKNOWN_SECRET\" is not defined in \"secrets\" context"]),
        };

        await AssertRuleCases(new ExprUndefinedVarRule(), "expr-undefined-var", cases);
    }


    [Test]
    public async Task RuleRegression_ExprUndefinedVarRule_NeedsOutputValidation_TableDriven()
    {
        var cases = new[]
        {
            // #8: needs.build.outputs.built should be detected as undefined when build has no such output
            new RuleCase(
            "ng-needs-unknown-output",
            """
            on: push
            jobs:
                build:
                    runs-on: ubuntu-latest
                    outputs:
                        image_tag: ${{ steps.build.outputs.tag }}
                    steps:
                        - id: build
                          run: echo "tag=v1" >> $GITHUB_OUTPUT
                test:
                    runs-on: ubuntu-latest
                    needs: [build]
                    steps:
                        - env:
                            TAG: ${{ needs.build.outputs.typo_output }}
                          run: echo "$TAG"
            """,
            ["\"typo_output\" is not defined in \"needs\" context"]),
            // #8: needs.build.outputs.image_tag should be valid
            new RuleCase(
            "ok-needs-known-output",
            """
            on: push
            jobs:
                build:
                    runs-on: ubuntu-latest
                    outputs:
                        image_tag: ${{ steps.build.outputs.tag }}
                    steps:
                        - id: build
                          run: echo "tag=v1" >> $GITHUB_OUTPUT
                test:
                    runs-on: ubuntu-latest
                    needs: [build]
                    steps:
                        - env:
                            TAG: ${{ needs.build.outputs.image_tag }}
                          run: echo "$TAG"
            """,
            []),
        };

        await AssertRuleCases(new ExprUndefinedVarRule(), "expr-undefined-var", cases);
    }


    [Test]
    public async Task RuleRegression_ExprUndefinedVarRule_ReusableWorkflowCallNeedsOutputs_TableDriven()
    {
        var cases = new[]
        {
            // Reusable workflow call jobs don't declare outputs locally — their outputs come from
            // the called workflow. The linter cannot determine the available outputs without
            // fetching the remote workflow, so needs.<reusable-job>.outputs.* must be treated as
            // loose (no false positive).
            new RuleCase(
            "ok-reusable-workflow-call-needs-outputs",
            """
            on: push
            jobs:
                new-version:
                    uses: owner/repo/.github/workflows/reusable.yml@main
                    with:
                        ref: main
                deploy:
                    runs-on: ubuntu-latest
                    needs: [new-version]
                    steps:
                        - env:
                            TAG: ${{ needs.new-version.outputs.version }}
                          run: echo "$TAG"
            """,
            []),
            // Local reusable workflow call — needs.<reusable-job>.outputs.* is only treated as
            // loose when the referenced workflow cannot be resolved locally. If it can be
            // resolved and defines on.workflow_call.outputs, validation is strict.
            new RuleCase(
            "ok-local-reusable-workflow-call-needs-outputs",
            """
            on: push
            jobs:
                new-version:
                    uses: ./.github/workflows/reusable.yml
                deploy:
                    runs-on: ubuntu-latest
                    needs: [new-version]
                    steps:
                        - env:
                            TAG: ${{ needs.new-version.outputs.version }}
                          run: echo "$TAG"
            """,
            []),
        };

        await AssertRuleCases(new ExprUndefinedVarRule(), "expr-undefined-var", cases);
    }


    [Test]
    public async Task RuleRegression_ExprUndefinedVarRule_LocalReusableWorkflowOutputResolution()
    {
        var rootDir = Path.Combine(Path.GetTempPath(), "seiton-local-reusable-" + Guid.NewGuid().ToString("N", System.Globalization.CultureInfo.InvariantCulture));
        var workflowsDir = Path.Combine(rootDir, ".github", "workflows");
        Directory.CreateDirectory(workflowsDir);

        var reusablePath = Path.Combine(workflowsDir, "reusable.yml");
        var callerPath = Path.Combine(workflowsDir, "caller.yml");

        try
        {
            // Reusable workflow declares one output: "version"
            var reusableYaml = """
            on:
              workflow_call:
                outputs:
                  version:
                    description: The computed version
                    value: ${{ jobs.compute.outputs.ver }}
            jobs:
              compute:
                runs-on: ubuntu-latest
                outputs:
                  ver: ${{ steps.v.outputs.ver }}
                steps:
                  - id: v
                    run: echo "ver=1.0.0" >> "$GITHUB_OUTPUT"
            """;

            // Case 1: ng — references undefined output "typo_output"
            var callerYamlNg = """
            on: push
            jobs:
              new-version:
                uses: ./.github/workflows/reusable.yml
              deploy:
                runs-on: ubuntu-latest
                needs: [new-version]
                steps:
                  - env:
                      TAG: ${{ needs.new-version.outputs.typo_output }}
                    run: echo "$TAG"
            """;

            // Case 2: ok — references valid output "version"
            var callerYamlOk = """
            on: push
            jobs:
              new-version:
                uses: ./.github/workflows/reusable.yml
              deploy:
                runs-on: ubuntu-latest
                needs: [new-version]
                steps:
                  - env:
                      TAG: ${{ needs.new-version.outputs.version }}
                    run: echo "$TAG"
            """;

            File.WriteAllText(reusablePath, NormalizeYaml(reusableYaml), Encoding.UTF8);

            // Test ng case
            File.WriteAllText(callerPath, NormalizeYaml(callerYamlNg), Encoding.UTF8);
            using var resultNg = new LintEngine([new ExprUndefinedVarRule()])
                .Check(File.ReadAllBytes(callerPath), callerPath);
            var msgsNg = resultNg.Diagnostics.Where(x => x.RuleId == "expr-undefined-var").Select(x => x.Message).ToArray();
            await Assert.That(msgsNg.Any(m => m.Contains("\"typo_output\" is not defined", StringComparison.Ordinal))).IsTrue();

            // Test ok case
            File.WriteAllText(callerPath, NormalizeYaml(callerYamlOk), Encoding.UTF8);
            using var resultOk = new LintEngine([new ExprUndefinedVarRule()])
                .Check(File.ReadAllBytes(callerPath), callerPath);
            var msgsOk = resultOk.Diagnostics.Where(x => x.RuleId == "expr-undefined-var").Select(x => x.Message).ToArray();
            await Assert.That(msgsOk.Any(m => m.Contains("is not defined", StringComparison.Ordinal))).IsFalse();
        }
        finally
        {
            if (Directory.Exists(rootDir))
            {
                Directory.Delete(rootDir, recursive: true);
            }
        }
    }


    [Test]
    public async Task RuleRegression_ExprUndefinedVarRule_LocalReusableWorkflowNoOutputs()
    {
        var rootDir = Path.Combine(Path.GetTempPath(), "seiton-local-reusable-noout-" + Guid.NewGuid().ToString("N", System.Globalization.CultureInfo.InvariantCulture));
        var workflowsDir = Path.Combine(rootDir, ".github", "workflows");
        Directory.CreateDirectory(workflowsDir);

        var reusablePath = Path.Combine(workflowsDir, "reusable-no-outputs.yml");
        var callerPath = Path.Combine(workflowsDir, "caller.yml");

        try
        {
            // Reusable workflow with workflow_call but NO outputs declared
            var reusableYaml = """
            on:
              workflow_call:
                inputs:
                  ref:
                    type: string
            jobs:
              work:
                runs-on: ubuntu-latest
                steps:
                  - run: echo "working"
            """;

            // Caller references an output that doesn't exist — should be flagged
            var callerYaml = """
            on: push
            jobs:
              compute:
                uses: ./.github/workflows/reusable-no-outputs.yml
              deploy:
                runs-on: ubuntu-latest
                needs: [compute]
                steps:
                  - env:
                      X: ${{ needs.compute.outputs.something }}
                    run: echo "$X"
            """;

            File.WriteAllText(reusablePath, NormalizeYaml(reusableYaml), Encoding.UTF8);
            File.WriteAllText(callerPath, NormalizeYaml(callerYaml), Encoding.UTF8);

            using var result = new LintEngine([new ExprUndefinedVarRule()])
                .Check(File.ReadAllBytes(callerPath), callerPath);
            var msgs = result.Diagnostics.Where(x => x.RuleId == "expr-undefined-var").Select(x => x.Message).ToArray();
            // The called workflow declares no outputs, so needs.compute.outputs.something should be flagged
            await Assert.That(msgs.Any(m => m.Contains("is not defined", StringComparison.Ordinal) || m.Contains("no properties are defined", StringComparison.Ordinal))).IsTrue();
        }
        finally
        {
            if (Directory.Exists(rootDir))
            {
                Directory.Delete(rootDir, recursive: true);
            }
        }
    }


    [Test]
    public async Task RuleRegression_ExprUndefinedVarRule_NeedsUndefinedJob_TableDriven()
    {
        var cases = new[]
        {
            // A-4: needs.prepare undefined when not in needs list
            new RuleCase(
            "ng-needs-job-not-in-needs-list",
            """
            on: push
            jobs:
                prepare:
                    runs-on: ubuntu-latest
                    outputs:
                        prepared: ${{ steps.a.outputs.val }}
                    steps:
                        - id: a
                          run: echo "val=1" >> $GITHUB_OUTPUT
                        - run: echo '${{ needs.prepare.outputs.prepared }}'
            """,
            ["\"prepare\" is not defined in \"needs\" context"]),
            // A-4: needs.some_job undefined (job doesn't exist)
            new RuleCase(
            "ng-needs-nonexistent-job",
            """
            on: push
            jobs:
                install:
                    runs-on: ubuntu-latest
                    outputs:
                        installed: ok
                    steps:
                        - run: echo install
                build:
                    needs: [install]
                    runs-on: ubuntu-latest
                    steps:
                        - run: echo '${{ needs.some_job }}'
            """,
            ["\"some_job\" is not defined in \"needs\" context"]),
            // A-4: needs.build undefined in other job (build not in other's needs)
            new RuleCase(
            "ng-needs-job-not-declared-in-needs",
            """
            on: push
            jobs:
                build:
                    runs-on: ubuntu-latest
                    outputs:
                        built: ok
                    steps:
                        - run: echo build
                other:
                    runs-on: ubuntu-latest
                    steps:
                        - run: echo '${{ needs.build.outputs.built }}'
            """,
            ["\"build\" is not defined in \"needs\" context"]),
        };

        await AssertRuleCases(new ExprUndefinedVarRule(), "expr-undefined-var", cases);
    }


    [Test]
    public async Task RuleRegression_ExprUndefinedVarRule_StepsCrossJob_TableDriven()
    {
        var cases = new[]
        {
            // A-5: steps.get_value undefined in other job (step IDs are job-local)
            new RuleCase(
            "ng-steps-cross-job-reference",
            """
            on: push
            jobs:
                test:
                    runs-on: ubuntu-latest
                    steps:
                        - id: get_value
                          run: echo "name=foo" >> $GITHUB_OUTPUT
                        - run: echo '${{ steps.get_value.outputs.name }}'
                other:
                    runs-on: ubuntu-latest
                    steps:
                        - run: echo '${{ steps.get_value.outputs.name }}'
            """,
            ["\"get_value\" is not defined in \"steps\" context"]),
        };

        await AssertRuleCases(new ExprUndefinedVarRule(), "expr-undefined-var", cases);
    }


    [Test]
    public async Task RuleRegression_ExprUndefinedVarRule_StepsOrderValidation_TableDriven()
    {
        var cases = new[]
        {
            // #9: referencing a step ID that hasn't been defined yet should be an error
            new RuleCase(
            "ng-step-reference-before-definition",
            """
            on: push
            jobs:
                build:
                    runs-on: ubuntu-latest
                    steps:
                        - if: ${{ steps.later.outcome == 'success' }}
                          run: echo "first"
                        - id: later
                          run: echo "later"
            """,
            ["\"later\" is not defined in \"steps\" context"]),
            // #9: referencing a step ID that was defined earlier is fine
            new RuleCase(
            "ok-step-reference-after-definition",
            """
            on: push
            jobs:
                build:
                    runs-on: ubuntu-latest
                    steps:
                        - id: earlier
                          run: echo "earlier"
                        - if: ${{ steps.earlier.outcome == 'success' }}
                          run: echo "second"
            """,
            []),
        };

        await AssertRuleCases(new ExprUndefinedVarRule(), "expr-undefined-var", cases);
    }


    [Test]
    public async Task RuleRegression_ExprUndefinedVarRule_ParallelStepIdsVisibleAfterParallel_TableDriven()
    {
        var cases = new[]
        {
            new RuleCase(
            "ok-reference-step-id-defined-inside-parallel-after-parallel",
            """
            on: push
            jobs:
                dotnet:
                    runs-on: ubuntu-latest
                    steps:
                        - parallel:
                            - uses: owner/action@v1
                              id: sha
                            - run: echo setup
                        - run: echo '${{ steps.sha.outputs.short }}'
            """,
            []),
            new RuleCase(
            "ng-reference-parallel-sibling-step-id-inside-parallel",
            """
            on: push
            jobs:
                dotnet:
                    runs-on: ubuntu-latest
                    steps:
                        - parallel:
                            - uses: owner/action@v1
                              id: sha
                            - run: echo '${{ steps.sha.outputs.short }}'
            """,
            ["\"sha\" is not defined in \"steps\" context"]),
        };

        await AssertRuleCases(new ExprUndefinedVarRule(), "expr-undefined-var", cases);
    }


    [Test]
    public async Task RuleRegression_ExprUndefinedVarRule_RunnerContextInMatrix_TableDriven()
    {
        var cases = new[]
        {
            // #23: runner context should NOT be available in strategy.matrix expressions
            // (currently Job scope doesn't include runner, so this may already pass)
            new RuleCase(
            "ng-matrix-uses-runner-context",
            """
            on: push
            jobs:
                build:
                    strategy:
                        matrix:
                            os: [ubuntu-latest]
                    runs-on: ${{ matrix.os }}
                    steps:
                        - if: ${{ runner.os == 'Linux' }}
                          run: echo ok
            """,
            []),
            // runner context IS valid in step scope — should not error
            new RuleCase(
            "ok-step-uses-runner-context",
            """
            on: push
            jobs:
                build:
                    runs-on: ubuntu-latest
                    steps:
                        - if: ${{ runner.os == 'Linux' }}
                          run: echo ok
            """,
            []),
        };

        await AssertRuleCases(new ExprUndefinedVarRule(), "expr-undefined-var", cases);
    }


    [Test]
    public async Task RuleRegression_ExprUndefinedVarRule_ReusableWorkflowOutputs_TableDriven()
    {
        var cases = new[]
        {
            // #25: jobs.<id>.outputs.<name> in workflow_call output value should validate
            new RuleCase(
            "ng-workflow-output-references-unknown-job-output",
            """
            on:
                workflow_call:
                    outputs:
                        image:
                            value: ${{ jobs.build.outputs.imagetag }}
            jobs:
                build:
                    runs-on: ubuntu-latest
                    outputs:
                        image_tag: ${{ steps.b.outputs.tag }}
                    steps:
                        - id: b
                          run: echo "tag=v1" >> $GITHUB_OUTPUT
            """,
            ["\"imagetag\" is not defined"]),
            // #25: correct output name should not error
            new RuleCase(
            "ok-workflow-output-references-known-job-output",
            """
            on:
                workflow_call:
                    outputs:
                        image:
                            value: ${{ jobs.build.outputs.image_tag }}
            jobs:
                build:
                    runs-on: ubuntu-latest
                    outputs:
                        image_tag: ${{ steps.b.outputs.tag }}
                    steps:
                        - id: b
                          run: echo "tag=v1" >> $GITHUB_OUTPUT
            """,
            []),
        };

        await AssertRuleCases(new ExprUndefinedVarRule(), "expr-undefined-var", cases);
    }


    [Test]
    public async Task RuleRegression_ExprUndefinedVarRule_RunAndWithExpressions_TableDriven()
    {
        var cases = new[]
        {
            // A-4: run field expression uses unknown context
            new RuleCase(
            "ng-run-field-unknown-context",
            """
            on: push
            jobs:
                build:
                    runs-on: ubuntu-latest
                    steps:
                        - run: echo ${{ bogus.value }}
            """,
            ["undefined context \"bogus\""]),
            // A-4: run field expression uses matrix key from wrong job
            new RuleCase(
            "ng-run-field-matrix-key-from-wrong-job",
            """
            on: push
            jobs:
                build:
                    strategy:
                        matrix:
                            os: [ubuntu-latest]
                    runs-on: ${{ matrix.os }}
                    steps:
                        - run: echo build
                test:
                    runs-on: ubuntu-latest
                    steps:
                        - run: echo ${{ matrix.os }}
            """,
            ["\"os\" is not defined in \"matrix\" context"]),
            // A-5: action with input expression using unknown context
            new RuleCase(
            "ng-action-with-input-unknown-context",
            """
            on: push
            jobs:
                build:
                    runs-on: ubuntu-latest
                    steps:
                        - uses: actions/checkout@v4
                          with:
                            ref: ${{ nosuch.branch }}
            """,
            ["undefined context \"nosuch\""]),
            // A-4/A-5: run and with expressions using valid context should not error
            new RuleCase(
            "ok-run-and-with-valid-contexts",
            """
            on: push
            jobs:
                build:
                    runs-on: ubuntu-latest
                    steps:
                        - uses: actions/checkout@v4
                          with:
                            ref: ${{ github.ref }}
                        - run: echo ${{ github.sha }}
            """,
            []),
        };

        await AssertRuleCases(new ExprUndefinedVarRule(), "expr-undefined-var", cases);
    }


    [Test]
    public async Task RuleRegression_ExprUndefinedVarRule_ContextAvailability_WorkflowLevel_TableDriven()
    {
        var cases = new[]
        {
            // run-name: env context not allowed
            new RuleCase(
            "ng-run-name-env",
            """
            run-name: ${{ env.FOO }}
            on: push
            jobs:
                build:
                    runs-on: ubuntu-latest
                    steps:
                        - run: echo
            """,
            ["context \"env\" is not allowed here"]),
            // workflow env: env context not allowed (self-reference)
            new RuleCase(
            "ng-workflow-env-self-ref",
            """
            on: push
            env:
                BAR: ${{ env.BAR }}
            jobs:
                build:
                    runs-on: ubuntu-latest
                    steps:
                        - run: echo
            """,
            ["context \"env\" is not allowed here"]),
            // workflow concurrency: env context not allowed
            new RuleCase(
            "ng-workflow-concurrency-env",
            """
            on: push
            concurrency:
                group: ${{ env.FOO }}
            jobs:
                build:
                    runs-on: ubuntu-latest
                    steps:
                        - run: echo
            """,
            ["context \"env\" is not allowed here"]),
            // workflow_call input default: env context not allowed
            new RuleCase(
            "ng-workflow-call-input-default-env",
            """
            on:
                workflow_call:
                    inputs:
                        foo:
                            type: string
                            default: ${{ env.FOO }}
            jobs:
                build:
                    runs-on: ubuntu-latest
                    steps:
                        - run: echo
            """,
            ["context \"env\" is not allowed here"]),
            // OK: workflow env using github and secrets
            new RuleCase(
            "ok-workflow-env-github-secrets",
            """
            on: push
            env:
                FOO: ${{ github.sha }}
                BAR: ${{ secrets.TOKEN }}
            jobs:
                build:
                    runs-on: ubuntu-latest
                    steps:
                        - run: echo
            """,
            []),
        };

        await AssertRuleCases(new ExprUndefinedVarRule(), "expr-undefined-var", cases);
    }


    [Test]
    public async Task RuleRegression_ExprUndefinedVarRule_ContextAvailability_JobLevel_TableDriven()
    {
        var cases = new[]
        {
            // job.name: runner not allowed
            new RuleCase(
            "ng-job-name-runner",
            """
            on: push
            jobs:
                build:
                    name: ${{ runner.name }}
                    runs-on: ubuntu-latest
                    steps:
                        - run: echo
            """,
            ["context \"runner\" is not allowed here"]),
            // job.runs-on: env and runner not allowed
            new RuleCase(
            "ng-job-runs-on-env",
            """
            on: push
            jobs:
                build:
                    runs-on: ${{ env.SUFFIX }}
                    steps:
                        - run: echo
            """,
            ["context \"env\" is not allowed here"]),
            new RuleCase(
            "ng-job-runs-on-runner",
            """
            on: push
            jobs:
                build:
                    runs-on: ${{ runner.OS }}
                    steps:
                        - run: echo
            """,
            ["context \"runner\" is not allowed here"]),
            // job.concurrency: env not allowed
            new RuleCase(
            "ng-job-concurrency-env",
            """
            on: push
            jobs:
                build:
                    concurrency:
                        group: ${{ env.FOO }}
                    runs-on: ubuntu-latest
                    steps:
                        - run: echo
            """,
            ["context \"env\" is not allowed here"]),
            // job.container.credentials: runner not allowed
            new RuleCase(
            "ng-job-container-credentials-runner",
            """
            on: push
            jobs:
                build:
                    runs-on: ubuntu-latest
                    container:
                        image: node:14
                        credentials:
                            username: ${{ runner.os }}
                            password: ${{ env.FOO }}
                    steps:
                        - run: echo
            """,
            ["context \"runner\" is not allowed here"]),
            // job.continue-on-error: env not allowed
            new RuleCase(
            "ng-job-continue-on-error-env",
            """
            on: push
            jobs:
                build:
                    runs-on: ubuntu-latest
                    continue-on-error: ${{ env.FOO == '' }}
                    steps:
                        - run: echo
            """,
            ["context \"env\" is not allowed here"]),
            // job.environment: runner not allowed
            new RuleCase(
            "ng-job-environment-runner",
            """
            on: push
            jobs:
                build:
                    runs-on: ubuntu-latest
                    environment:
                        name: ${{ runner.name }}
                    steps:
                        - run: echo
            """,
            ["context \"runner\" is not allowed here"]),
            // job.strategy: env not allowed
            new RuleCase(
            "ng-job-strategy-env",
            """
            on: push
            jobs:
                build:
                    strategy:
                        matrix:
                            os:
                                - ${{ env.OS }}
                    runs-on: ubuntu-latest
                    steps:
                        - run: echo
            """,
            ["context \"env\" is not allowed here"]),
            // job.timeout-minutes: env not allowed
            new RuleCase(
            "ng-job-timeout-env",
            """
            on: push
            jobs:
                build:
                    runs-on: ubuntu-latest
                    timeout-minutes: ${{ env.TIMEOUT }}
                    steps:
                        - run: echo
            """,
            ["context \"env\" is not allowed here"]),
            // job.outputs: OK (env, runner, steps all allowed)
            new RuleCase(
            "ok-job-outputs-env-runner-steps",
            """
            on: push
            jobs:
                build:
                    runs-on: ubuntu-latest
                    outputs:
                        foo: ${{ runner.name }}-${{ env.FOO }}-${{ steps.s1.outputs.x }}
                    steps:
                        - id: s1
                          run: echo
            """,
            []),
            // job.defaults.run: env allowed, runner not allowed
            new RuleCase(
            "ng-job-defaults-run-runner",
            """
            on: push
            jobs:
                build:
                    runs-on: ubuntu-latest
                    defaults:
                        run:
                            working-directory: ${{ runner.temp }}
                    steps:
                        - run: echo
            """,
            ["context \"runner\" is not allowed here"]),
            // job.services.image: env not allowed
            new RuleCase(
            "ng-job-services-image-env",
            """
            on: push
            jobs:
                build:
                    runs-on: ubuntu-latest
                    services:
                        nginx:
                            image: ${{ env.IMAGE }}
                    steps:
                        - run: echo
            """,
            ["context \"env\" is not allowed here"]),
            // job.services.credentials: runner not allowed
            new RuleCase(
            "ng-job-services-credentials-runner",
            """
            on: push
            jobs:
                build:
                    runs-on: ubuntu-latest
                    services:
                        nginx:
                            image: nginx
                            credentials:
                                username: ${{ runner.name }}
                                password: ${{ env.PASSWORD }}
                    steps:
                        - run: echo
            """,
            ["context \"runner\" is not allowed here"]),
            // job.secrets: OK (secrets allowed for reusable workflow calls)
            new RuleCase(
            "ok-job-secrets-secrets",
            """
            on: push
            jobs:
                caller:
                    uses: owner/repo/workflow.yml@main
                    secrets:
                        password: ${{ secrets.PASSWORD }}
            """,
            []),
            // job.with (reusable): env not allowed
            new RuleCase(
            "ng-job-with-env",
            """
            on: push
            jobs:
                caller:
                    uses: owner/repo/workflow.yml@main
                    with:
                        some-input: ${{ env.HELLO }}
            """,
            ["context \"env\" is not allowed here"]),
        };

        await AssertRuleCases(new ExprUndefinedVarRule(), "expr-undefined-var", cases);
    }


    [Test]
    public async Task RuleRegression_ExprUndefinedVarRule_ContextAvailability_StepLevel_TableDriven()
    {
        var cases = new[]
        {
            // step.name: OK (all step contexts available)
            new RuleCase(
            "ok-step-name-env-runner",
            """
            on: push
            jobs:
                build:
                    runs-on: ubuntu-latest
                    steps:
                        - name: ${{ env.VERSION }} on ${{ runner.name }}
                          run: echo
            """,
            []),
            // step.continue-on-error: OK (inputs allowed)
            new RuleCase(
            "ok-step-continue-on-error-inputs",
            """
            on:
                workflow_call:
                    inputs:
                        bool:
                            type: boolean
            jobs:
                build:
                    runs-on: ubuntu-latest
                    steps:
                        - continue-on-error: ${{ inputs.bool }}
                          run: echo
            """,
            []),
            // step.timeout-minutes: OK
            new RuleCase(
            "ok-step-timeout-minutes-inputs",
            """
            on:
                workflow_call:
                    inputs:
                        timeout:
                            type: number
            jobs:
                build:
                    runs-on: ubuntu-latest
                    steps:
                        - timeout-minutes: ${{ inputs.timeout }}
                          run: echo
            """,
            []),
            // step.working-directory: OK (runner allowed at step level)
            new RuleCase(
            "ok-step-working-directory-runner",
            """
            on: push
            jobs:
                build:
                    runs-on: ubuntu-latest
                    steps:
                        - working-directory: ${{ runner.temp }}
                          run: echo
            """,
            []),
            // step.if: secrets not allowed
            new RuleCase(
            "ng-step-if-secrets",
            """
            on: push
            jobs:
                build:
                    runs-on: ubuntu-latest
                    steps:
                        - if: ${{ secrets.PASSWORD != '' }}
                          run: echo
            """,
            ["context \"secrets\" is not allowed here"]),
        };

        await AssertRuleCases(new ExprUndefinedVarRule(), "expr-undefined-var", cases);
    }


    [Test]
    public async Task RuleRegression_ExprUndefinedVarRule_EnvContextBanned_TableDriven()
    {
        var cases = new[]
        {
            // workflow env cannot reference env context
            new RuleCase(
            "ng-workflow-env-env-context",
            """
            on: push
            env:
                ERROR1: ${{ env.PATH }}
            jobs:
                build:
                    runs-on: ubuntu-latest
                    steps:
                        - run: echo
            """,
            ["context \"env\" is not allowed here"]),
            // job env cannot reference env context
            new RuleCase(
            "ng-job-env-env-context",
            """
            on: push
            jobs:
                build:
                    runs-on: ubuntu-latest
                    env:
                        ERROR2: ${{ env.PATH }}
                    steps:
                        - run: echo
            """,
            ["context \"env\" is not allowed here"]),
            // step env CAN reference env context (OK)
            new RuleCase(
            "ok-step-env-env-context",
            """
            on: push
            jobs:
                build:
                    runs-on: ubuntu-latest
                    steps:
                        - run: echo
                          env:
                            BAR: ${{ env.FOO }}
            """,
            []),
            // container env CAN reference env context (OK)
            new RuleCase(
            "ok-container-env-env-context",
            """
            on: push
            jobs:
                build:
                    runs-on: ubuntu-latest
                    container:
                        image: node:14
                        env:
                            MYPATH: ${{ env.PATH }}
                    steps:
                        - run: echo
            """,
            []),
        };

        await AssertRuleCases(new ExprUndefinedVarRule(), "expr-undefined-var", cases);
    }


    [Test]
    public async Task RuleRegression_ExprUndefinedVarRule_JobIfEnvBanned_TableDriven()
    {
        var cases = new[]
        {
            // job.if with env context: not allowed
            new RuleCase(
            "ng-job-if-env-dollar-brace",
            """
            on: push
            jobs:
                test1:
                    runs-on: ubuntu-latest
                    if: ${{ env.FOO == 'aaa' }}
                    steps:
                        - run: echo
            """,
            ["context \"env\" is not allowed here"]),
            // job.if without ${{ }}: env not allowed
            new RuleCase(
            "ng-job-if-env-bare",
            """
            on: push
            jobs:
                test2:
                    runs-on: ubuntu-latest
                    if: env.FOO == 'aaa'
                    steps:
                        - run: echo
            """,
            ["context \"env\" is not allowed here"]),
            // reusable workflow call job if: env not allowed
            new RuleCase(
            "ng-reusable-job-if-env",
            """
            on: push
            jobs:
                test3:
                    uses: org/repo/workflow.yml@v1
                    if: ${{ env.FOO == 'aaa' }}
            """,
            ["context \"env\" is not allowed here"]),
        };

        await AssertRuleCases(new ExprUndefinedVarRule(), "expr-undefined-var", cases);
    }


    [Test]
    public async Task RuleRegression_ExprUndefinedVarRule_ShellKeyContextAvailability_TableDriven()
    {
        var cases = new[]
        {
            // workflow-level defaults.run.shell: no context available
            new RuleCase(
            "ng-workflow-defaults-shell-env",
            """
            on: push
            defaults:
                run:
                    shell: ${{ env.SHELL }}
            jobs:
                build:
                    runs-on: ubuntu-latest
                    steps:
                        - run: echo
            """,
            ["context \"env\" is not allowed here"]),
            // job-level defaults.run.shell: env IS available (OK)
            new RuleCase(
            "ok-job-defaults-shell-env",
            """
            on: push
            jobs:
                build:
                    runs-on: ubuntu-latest
                    defaults:
                        run:
                            shell: ${{ env.SHELL }}
                    steps:
                        - run: echo
            """,
            []),
            // step-level shell: no context available
            new RuleCase(
            "ng-step-shell-env",
            """
            on: push
            jobs:
                build:
                    runs-on: ubuntu-latest
                    steps:
                        - run: echo
                          shell: ${{ env.SHELL }}
            """,
            ["context \"env\" is not allowed here"]),
        };

        await AssertRuleCases(new ExprUndefinedVarRule(), "expr-undefined-var", cases);
    }


    [Test]
    public async Task RuleRegression_ExprUndefinedVarRule_SpecialFunctionAvailability_TableDriven()
    {
        var cases = new[]
        {
            // status functions OK in job.if
            new RuleCase(
            "ok-always-in-job-if",
            """
            on: push
            jobs:
                build:
                    runs-on: ubuntu-latest
                    if: always()
                    steps:
                        - run: echo
            """,
            []),
            new RuleCase(
            "ok-failure-in-step-if",
            """
            on: push
            jobs:
                build:
                    runs-on: ubuntu-latest
                    steps:
                        - if: failure()
                          run: echo
            """,
            []),
            // status functions NOT OK in strategy.matrix
            new RuleCase(
            "ng-always-in-strategy-matrix",
            """
            on: push
            jobs:
                build:
                    strategy:
                        matrix:
                            errors:
                                - ${{ always() }}
                    runs-on: ubuntu-latest
                    steps:
                        - run: echo
            """,
            ["function \"always\" is not allowed here"]),
            // hashFiles OK in step level
            new RuleCase(
            "ok-hashfiles-in-step-run",
            """
            on: push
            jobs:
                build:
                    runs-on: ubuntu-latest
                    steps:
                        - run: echo "${{ hashFiles('...') }}"
            """,
            []),
            // hashFiles NOT OK in job.if
            new RuleCase(
            "ng-hashfiles-in-job-if",
            """
            on: push
            jobs:
                build:
                    runs-on: ubuntu-latest
                    if: ${{ hashFiles('...') }}
                    steps:
                        - run: echo
            """,
            ["function \"hashFiles\" is not allowed here"]),
            // success() NOT OK in step.run
            new RuleCase(
            "ng-success-in-step-run",
            """
            on: push
            jobs:
                build:
                    runs-on: ubuntu-latest
                    steps:
                        - run: echo 'success? ${{ success() }}'
            """,
            ["function \"success\" is not allowed here"]),
            // hashFiles NOT OK in strategy.matrix
            new RuleCase(
            "ng-hashfiles-in-strategy-matrix",
            """
            on: push
            jobs:
                build:
                    strategy:
                        matrix:
                            errors:
                                - ${{ hashFiles('...') }}
                    runs-on: ubuntu-latest
                    steps:
                        - run: echo
            """,
            ["function \"hashFiles\" is not allowed here"]),
        };

        await AssertRuleCases(new ExprUndefinedVarRule(), "expr-undefined-var", cases);
    }


    [Test]
    public async Task RuleRegression_ExprUndefinedVarRule_StepIdNoContext_TableDriven()
    {
        var cases = new[]
        {
            // step.id: no context allowed
            new RuleCase(
            "ng-step-id-expression",
            """
            on: push
            jobs:
                build:
                    runs-on: ubuntu-latest
                    steps:
                        - id: ${{ inputs.foo }}
                          run: echo
            """,
            ["context \"inputs\" is not allowed here"]),
        };

        await AssertRuleCases(new ExprUndefinedVarRule(), "expr-undefined-var", cases);
    }


    [Test]
    public async Task RuleRegression_ExprUndefinedVarRule_MessageIncludesAvailableContexts_TableDriven()
    {
        var cases = new[]
        {
            // Error message should list available contexts
            new RuleCase(
            "ng-job-if-env-lists-available-contexts",
            """
            on: push
            jobs:
                test:
                    runs-on: ubuntu-latest
                    if: ${{ env.FOO == 'aaa' }}
                    steps:
                        - run: echo
            """,
            ["context \"env\" is not allowed here", "available contexts are"]),
            // "no context is available here" for shell
            new RuleCase(
            "ng-workflow-shell-no-context",
            """
            on: push
            defaults:
                run:
                    shell: ${{ env.SHELL }}
            jobs:
                build:
                    runs-on: ubuntu-latest
                    steps:
                        - run: echo
            """,
            ["context \"env\" is not allowed here", "no context is available here"]),
        };

        await AssertRuleCases(new ExprUndefinedVarRule(), "expr-undefined-var", cases);
    }


    [Test]
    public async Task RuleRegression_ExprUndefinedVarRule_InputsWithoutWorkflowCall_TableDriven()
    {
        var cases = new[]
        {
            // When no workflow_call event, inputs has no properties → inputs.some_input is undefined
            new RuleCase(
            "ng-inputs-without-workflow-call",
            """
            on: push
            jobs:
                test:
                    runs-on: ubuntu-latest
                    steps:
                        - run: echo ${{ inputs.some_input }}
            """,
            ["property \"some_input\" is not defined in \"inputs\" context"]),
            // With workflow_call + defined input → OK
            new RuleCase(
            "ok-inputs-with-workflow-call",
            """
            on:
                workflow_call:
                    inputs:
                        my_input:
                            type: string
            jobs:
                test:
                    runs-on: ubuntu-latest
                    steps:
                        - run: echo ${{ inputs.my_input }}
            """,
            []),
        };

        await AssertRuleCases(new ExprUndefinedVarRule(), "expr-undefined-var", cases);
    }


    [Test]
    public async Task RuleRegression_ExprUndefinedVarRule_WorkflowCallOutputsSema_TableDriven()
    {
        var cases = new[]
        {
            // job0 has no outputs → jobs.job0.outputs.some_output is undefined
            new RuleCase(
            "ng-workflow-call-output-no-job-outputs",
            """
            on:
                workflow_call:
                    outputs:
                        output1:
                            value: ${{ jobs.job0.outputs.some_output }}
            jobs:
                job0:
                    runs-on: ubuntu-latest
                    steps:
                        - run: echo hi
            """,
            ["property \"some_output\" is not defined"]),
            // job1 has outputs but unknown_output is not among them
            new RuleCase(
            "ng-workflow-call-output-unknown-property",
            """
            on:
                workflow_call:
                    outputs:
                        output2:
                            value: ${{ jobs.job1.outputs.unknown_output }}
            jobs:
                job1:
                    runs-on: ubuntu-latest
                    outputs:
                        foo: bar
                    steps:
                        - run: echo hello
            """,
            ["property \"unknown_output\" is not defined"]),
        };

        await AssertRuleCases(new ExprUndefinedVarRule(), "expr-undefined-var", cases);
    }


    [Test]
    public async Task RuleRegression_ExprUndefinedVarRule_InputDefaultForwardReference_TableDriven()
    {
        var cases = new[]
        {
            // input2 not yet defined when input1.default references it
            new RuleCase(
            "ng-input-default-forward-ref",
            """
            on:
                workflow_call:
                    inputs:
                        input1:
                            type: string
                            default: ${{ inputs.input2 }}
                        input2:
                            type: string
            jobs:
                test:
                    runs-on: ubuntu-latest
                    steps:
                        - run: echo ok
            """,
            ["property \"input2\" is not defined in \"inputs\" context"]),
            // input3 references itself — not yet defined
            new RuleCase(
            "ng-input-default-self-ref",
            """
            on:
                workflow_call:
                    inputs:
                        input1:
                            type: string
                        input2:
                            type: string
                        input3:
                            type: boolean
                            default: ${{ inputs.input3 }}
            jobs:
                test:
                    runs-on: ubuntu-latest
                    steps:
                        - run: echo ok
            """,
            ["property \"input3\" is not defined in \"inputs\" context"]),
            // input2 references input1 (already defined) → OK
            new RuleCase(
            "ok-input-default-back-ref",
            """
            on:
                workflow_call:
                    inputs:
                        input1:
                            type: string
                        input2:
                            type: string
                            default: ${{ inputs.input1 }}
            jobs:
                test:
                    runs-on: ubuntu-latest
                    steps:
                        - run: echo ok
            """,
            []),
            // Chained back-references across several defaults: each default sees exactly
            // the inputs declared before it (exercises the incremental prefix building).
            new RuleCase(
            "ok-input-default-chained-refs",
            """
            on:
                workflow_call:
                    inputs:
                        input1:
                            type: string
                        input2:
                            type: string
                            default: ${{ inputs.input1 }}
                        input3:
                            type: string
                            default: ${{ inputs.input2 }}
                        input4:
                            type: string
                            default: ${{ inputs.input1 }}
            jobs:
                test:
                    runs-on: ubuntu-latest
                    steps:
                        - run: echo ok
            """,
            []),
            // Mixed: a forward reference mid-chain errors, and validation of the LATER
            // default still sees the correct (larger) prefix and passes.
            new RuleCase(
            "ng-input-default-chain-forward-mid",
            """
            on:
                workflow_call:
                    inputs:
                        input1:
                            type: string
                        input2:
                            type: string
                            default: ${{ inputs.input3 }}
                        input3:
                            type: string
                        input4:
                            type: string
                            default: ${{ inputs.input2 }}
            jobs:
                test:
                    runs-on: ubuntu-latest
                    steps:
                        - run: echo ok
            """,
            ["property \"input3\" is not defined in \"inputs\" context"]),
        };

        await AssertRuleCases(new ExprUndefinedVarRule(), "expr-undefined-var", cases);
    }

    [Test]
    public async Task RuleRegression_ExprUndefinedVarRule_ConcurrencyQueue_TableDriven()
    {
        var cases = new[]
        {
            // Workflow-level concurrency.queue with valid context → OK
            new RuleCase(
            "ok-workflow-concurrency-queue-uses-github-context",
            """
            on: push
            concurrency:
                group: deploy-${{ github.ref }}
                queue: ${{ github.event_name == 'push' && 'max' || 'single' }}
            jobs:
                build:
                    runs-on: ubuntu-latest
                    steps:
                        - run: echo ok
            """,
            []),
            // Job-level concurrency.queue with valid context → OK
            new RuleCase(
            "ok-job-concurrency-queue-uses-github-context",
            """
            on: push
            jobs:
                deploy:
                    runs-on: ubuntu-latest
                    concurrency:
                        group: deploy-${{ github.ref }}
                        queue: ${{ github.event_name == 'push' && 'max' || 'single' }}
                    steps:
                        - run: echo deploy
            """,
            []),
            // Job-level concurrency.queue with inputs context under workflow_call → OK
            new RuleCase(
            "ok-job-concurrency-queue-uses-inputs",
            """
            on:
                workflow_call:
                    inputs:
                        queue_mode:
                            type: string
            jobs:
                deploy:
                    runs-on: ubuntu-latest
                    concurrency:
                        group: deploy
                        queue: ${{ inputs.queue_mode }}
                    steps:
                        - run: echo deploy
            """,
            []),
            // Workflow-level concurrency.queue with unavailable context → ERROR
            new RuleCase(
            "ng-workflow-concurrency-queue-uses-steps-context",
            """
            on: push
            concurrency:
                group: deploy-${{ github.ref }}
                queue: ${{ steps.prep.outputs.mode }}
            jobs:
                build:
                    runs-on: ubuntu-latest
                    steps:
                        - run: echo ok
            """,
            ["context \"steps\" is not allowed here"]),
            // Job-level concurrency.queue with unavailable context → ERROR
            new RuleCase(
            "ng-job-concurrency-queue-uses-steps-context",
            """
            on: push
            jobs:
                deploy:
                    runs-on: ubuntu-latest
                    concurrency:
                        group: deploy
                        queue: ${{ steps.prep.outputs.mode }}
                    steps:
                        - run: echo deploy
            """,
            ["context \"steps\" is not allowed here"]),
            // Job-level concurrency.queue with undefined inputs property → ERROR
            new RuleCase(
            "ng-job-concurrency-queue-uses-undefined-input",
            """
            on:
                workflow_call:
                    inputs:
                        environment:
                            type: string
            jobs:
                deploy:
                    runs-on: ubuntu-latest
                    concurrency:
                        group: deploy
                        queue: ${{ inputs.missing_input }}
                    steps:
                        - run: echo deploy
            """,
            ["property \"missing_input\" is not defined in \"inputs\" context"]),
        };

        await AssertRuleCases(new ExprUndefinedVarRule(), "expr-undefined-var", cases);
    }


    [Test]
    public async Task RuleRegression_ExprUndefinedVarRule_AssumeEvents_InputsContext()
    {
        var yaml = NormalizeYaml("""
        on: [push, workflow_dispatch]
        jobs:
          build:
            runs-on: ubuntu-latest
            steps:
              - run: echo "${{ inputs.target }}"
        """);

        await AssertUndefinedInputsDiagnostic(yaml, assumeEvents: null, expectedUndefined: true, "assume-events-none.yml");
        await AssertUndefinedInputsDiagnostic(yaml, assumeEvents: ["issue_comment"], expectedUndefined: true, "assume-events-issue.yml");
        await AssertUndefinedInputsDiagnostic(yaml, assumeEvents: ["workflow_dispatch"], expectedUndefined: false, "assume-events-dispatch.yml");
        await AssertUndefinedInputsDiagnostic(yaml, assumeEvents: ["workflow_call"], expectedUndefined: false, "assume-events-call.yml");
    }

    private static async Task AssertUndefinedInputsDiagnostic(
        string yaml,
        IReadOnlyList<string>? assumeEvents,
        bool expectedUndefined,
        string filePath)
    {
        var config = assumeEvents is null
            ? null
            : new LintConfig
            {
                Rules = new Dictionary<string, RuleConfig>(StringComparer.Ordinal)
                {
                    ["expr-undefined-var"] = new RuleConfig
                    {
                        AssumeEvents = assumeEvents,
                    },
                },
            };

        using var result = new LintEngine([new ExprUndefinedVarRule()])
            .Check(Encoding.UTF8.GetBytes(yaml), filePath, config);
        var hasUndefined = result.Diagnostics
            .Where(x => x.RuleId == "expr-undefined-var")
            .Any(x => x.Message.Contains("is not defined", StringComparison.Ordinal));
        await Assert.That(hasUndefined).IsEqualTo(expectedUndefined);
    }
}
