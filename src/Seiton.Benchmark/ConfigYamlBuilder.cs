using System.Text;

namespace Seiton.Benchmark;

/// <summary>
/// Builds synthetic seiton lint-config YAML of varying complexity for benchmarks.
/// </summary>
internal static class ConfigYamlBuilder
{
    /// <summary>
    /// Minimal config: 1 rule override only.
    /// </summary>
    internal static string BuildMinimal()
    {
        return """
            rules:
              dangerous-triggers:
                severity: warning
            """;
    }

    /// <summary>
    /// Typical config: multiple rules + exclusions + fix + network.
    /// </summary>
    internal static string BuildTypical()
    {
        return """
            rules:
              job-permissions-required:
                enabled: false
              deny-write-all:
                severity: error
              dangerous-triggers:
                severity: error
                events:
                  - issue_comment
              runner-label:
                known-hosted-labels:
                  - ubuntu-24.04-large
              credentials:
                public-registries:
                  - registry.example.com
              cache-poisoning-trigger:
                untrusted-triggers:
                  - issue_comment
              unredacted-secrets:
                output-commands:
                  - tee
              forbidden-uses:
                deny:
                  - some-untrusted-org/*
              expr-undefined-var:
                assume-events:
                  - workflow_dispatch
                  - repository_dispatch

            exclusions:
              - file: ".github/workflows/legacy-*.yml"
                rules:
                  - runner-no-latest
                  - job-permissions-required
              - file: ".github/workflows/release.yml"
                jobs:
                  - publish
                rules:
                  - credentials

            fix:
              defaults:
                job-timeout-minutes: 15
              pinning:
                enable-network: true
                min-age-days: 14
                exclude-branches:
                  - main
                  - master
                ignore-actions:
                  - uses: "slsa-framework/*"
                    ref: "*"
              images:
                enable-network: true
                exclude-images:
                  - scratch
                exclude-tags:
                  - latest

            network:
              on-error: skip
              timeout-seconds: 30
              max-concurrency: 4
              github:
                ghes-api-url: ""
                ghes-fallback: false
            """;
    }

    /// <summary>
    /// Heavy config: many rules with all property types, many exclusions.
    /// Exercises the full breadth of the parser.
    /// </summary>
    internal static string BuildHeavy(int extraExclusions = 20)
    {
        var sb = new StringBuilder(capacity: 4_096);

        // rules section — every rule-specific key type
        sb.AppendLine("rules:");
        sb.AppendLine("  job-permissions-required:");
        sb.AppendLine("    enabled: false");
        sb.AppendLine("  deny-write-all:");
        sb.AppendLine("    severity: error");
        sb.AppendLine("  deny-read-all:");
        sb.AppendLine("    severity: error");
        sb.AppendLine("  dangerous-triggers:");
        sb.AppendLine("    severity: warning");
        sb.AppendLine("    events:");
        sb.AppendLine("      - issue_comment");
        sb.AppendLine("      - pull_request_target");
        sb.AppendLine("      - workflow_run");
        sb.AppendLine("  runner-label:");
        sb.AppendLine("    known-hosted-labels:");
        sb.AppendLine("      - ubuntu-24.04-large");
        sb.AppendLine("      - windows-2025-large");
        sb.AppendLine("      - macos-15-xlarge");
        sb.AppendLine("  credentials:");
        sb.AppendLine("    public-registries:");
        sb.AppendLine("      - registry.example.com");
        sb.AppendLine("      - ghcr.io");
        sb.AppendLine("      - docker.io");
        sb.AppendLine("  cache-poisoning-trigger:");
        sb.AppendLine("    untrusted-triggers:");
        sb.AppendLine("      - issue_comment");
        sb.AppendLine("      - pull_request_target");
        sb.AppendLine("  self-hosted-runner-trigger:");
        sb.AppendLine("    severity: warning");
        sb.AppendLine("  unredacted-secrets:");
        sb.AppendLine("    output-commands:");
        sb.AppendLine("      - tee");
        sb.AppendLine("      - set-output");
        sb.AppendLine("  forbidden-uses:");
        sb.AppendLine("    allow:");
        sb.AppendLine("      - actions/*");
        sb.AppendLine("      - github/*");
        sb.AppendLine("    deny:");
        sb.AppendLine("      - some-untrusted-org/*");
        sb.AppendLine("      - deprecated-action/*");
        sb.AppendLine("  expr-undefined-var:");
        sb.AppendLine("    assume-events:");
        sb.AppendLine("      - workflow_dispatch");
        sb.AppendLine("      - repository_dispatch");
        sb.AppendLine("      - schedule");
        sb.AppendLine("  overprovisioned-secrets:");
        sb.AppendLine("    max-step-env-secrets: 3");
        sb.AppendLine("    max-job-secrets: 5");
        sb.AppendLine("  known-vulnerable-actions:");
        sb.AppendLine("    enabled: true");
        sb.AppendLine("  impostor-commit:");
        sb.AppendLine("    enabled: true");

        // exclusions section
        sb.AppendLine();
        sb.AppendLine("exclusions:");
        sb.AppendLine("  - file: \".github/workflows/legacy-*.yml\"");
        sb.AppendLine("    rules:");
        sb.AppendLine("      - runner-no-latest");
        sb.AppendLine("      - job-permissions-required");
        sb.AppendLine("  - file: \".github/workflows/release.yml\"");
        sb.AppendLine("    jobs:");
        sb.AppendLine("      - publish");
        sb.AppendLine("    rules:");
        sb.AppendLine("      - credentials");
        for (var i = 0; i < extraExclusions; i++)
        {
            sb.Append("  - file: \".github/workflows/gen-").Append(i).AppendLine(".yml\"");
            sb.AppendLine("    rules:");
            sb.AppendLine("      - runner-no-latest");
        }

        // fix section — all sub-sections populated
        sb.AppendLine();
        sb.AppendLine("fix:");
        sb.AppendLine("  defaults:");
        sb.AppendLine("    job-timeout-minutes: 15");
        sb.AppendLine("  pinning:");
        sb.AppendLine("    enable-network: true");
        sb.AppendLine("    min-age-days: 14");
        sb.AppendLine("    exclude-branches:");
        sb.AppendLine("      - main");
        sb.AppendLine("      - master");
        sb.AppendLine("      - release");
        sb.AppendLine("    ignore-actions:");
        sb.AppendLine("      - uses: \"slsa-framework/*\"");
        sb.AppendLine("        ref: \"*\"");
        sb.AppendLine("      - uses: \"actions/*\"");
        sb.AppendLine("        ref: \"v*\"");
        sb.AppendLine("  images:");
        sb.AppendLine("    enable-network: true");
        sb.AppendLine("    exclude-images:");
        sb.AppendLine("      - scratch");
        sb.AppendLine("    exclude-tags:");
        sb.AppendLine("      - latest");
        sb.AppendLine("    ignore-images:");
        sb.AppendLine("      - mcr.microsoft.com/**");

        // network section
        sb.AppendLine();
        sb.AppendLine("network:");
        sb.AppendLine("  on-error: skip");
        sb.AppendLine("  timeout-seconds: 30");
        sb.AppendLine("  max-concurrency: 4");
        sb.AppendLine("  github:");
        sb.AppendLine("    ghes-api-url: \"\"");
        sb.AppendLine("    ghes-fallback: false");

        return sb.ToString();
    }
}
