using System.Text;
using Seiton.Core.Flow;
using Seiton.Core.Parsing;

namespace Seiton.Core.Tests;

public sealed class WorkflowFlowMermaidTests
{
    private static WorkflowFlow CollectFlow(string yaml, string filePath = "wf.yml")
    {
        using var result = WorkflowParser.Parse(Encoding.UTF8.GetBytes(yaml.Replace("\r\n", "\n")), filePath);
        return WorkflowFlowCollector.Collect(result, filePath)!;
    }

    [Test]
    public async Task Serialize_JobDagWithSteps_EmitsFlowchartWithChainedStepsAndNeedsEdges()
    {
        var flow = CollectFlow("""
            name: CI
            on: push
            jobs:
              lint:
                runs-on: ubuntu-latest
                steps:
                  - uses: actions/checkout@v4
                  - run: npm run lint
              test:
                runs-on: ubuntu-latest
                needs: lint
                steps:
                  - run: npm test
            """);

        var mermaid = WorkflowFlowMermaid.Serialize([flow]);

        await Assert.That(mermaid).Contains("flowchart LR");
        await Assert.That(mermaid).Contains("subgraph j0[\"lint\"]");
        await Assert.That(mermaid).Contains("j0n0[\"uses: actions/checkout@v4\"]");
        await Assert.That(mermaid).Contains("j0n1[\"run: npm run lint\"]");
        await Assert.That(mermaid).Contains("j0n0 --> j0n1");
        await Assert.That(mermaid).Contains("subgraph j1[\"test\"]");
        await Assert.That(mermaid).Contains("j0 --> j1");
    }

    [Test]
    public async Task Serialize_ParallelStep_EmitsNestedSubgraphWithUnchainedChildren()
    {
        var flow = CollectFlow("""
            on: push
            jobs:
              build:
                runs-on: ubuntu-latest
                steps:
                  - run: echo before
                  - parallel:
                    - run: npm run a
                    - run: npm run b
                  - run: echo after
            """);

        var mermaid = WorkflowFlowMermaid.Serialize([flow]);

        await Assert.That(mermaid).Contains("subgraph j0g1[\"parallel\"]");
        await Assert.That(mermaid).Contains("j0n2[\"run: npm run a\"]");
        await Assert.That(mermaid).Contains("j0n3[\"run: npm run b\"]");
        // Chain goes around the parallel group; children are not chained to each other.
        await Assert.That(mermaid).Contains("j0n0 --> j0g1");
        await Assert.That(mermaid).Contains("j0g1 --> j0n4");
        await Assert.That(mermaid.Contains("j0n2 --> j0n3", StringComparison.Ordinal)).IsFalse();
    }

    [Test]
    public async Task Serialize_ReusableJob_EmitsSubroutineNode()
    {
        var flow = CollectFlow("""
            on: push
            jobs:
              build:
                runs-on: ubuntu-latest
                steps:
                  - run: echo build
              deploy:
                needs: build
                uses: octo/repo/.github/workflows/deploy.yml@v1
            """);

        var mermaid = WorkflowFlowMermaid.Serialize([flow]);

        await Assert.That(mermaid).Contains("j1[[\"deploy — uses: octo/repo/.github/workflows/deploy.yml@v1\"]]");
        await Assert.That(mermaid).Contains("j0 --> j1");
    }

    [Test]
    public async Task Serialize_LabelsAreSingleLineAndQuoteFree()
    {
        var flow = CollectFlow("""
            on: push
            jobs:
              build:
                runs-on: ubuntu-latest
                steps:
                  - run: |
                      echo "line one"
                      echo "line two"
            """);

        var mermaid = WorkflowFlowMermaid.Serialize([flow]);

        await Assert.That(mermaid).Contains("j0n0[\"run: echo 'line one'\"]");
        await Assert.That(mermaid.Contains("line two", StringComparison.Ordinal)).IsFalse();
    }

    [Test]
    public async Task Serialize_MatrixJob_AnnotatesJobLabel()
    {
        var flow = CollectFlow("""
            on: push
            jobs:
              test:
                runs-on: ubuntu-latest
                strategy:
                  matrix:
                    os: [ubuntu, windows]
                    node: [18, 20]
                steps:
                  - run: npm test
            """);

        var mermaid = WorkflowFlowMermaid.Serialize([flow]);

        await Assert.That(mermaid).Contains("subgraph j0[\"test (matrix: os × node)\"]");
    }
}
