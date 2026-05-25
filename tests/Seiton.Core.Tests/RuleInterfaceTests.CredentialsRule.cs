using Seiton.Core.Linting.Rules;

namespace Seiton.Core.Tests;

public sealed partial class RuleInterfaceTests
{

    [Test]
    public async Task RuleRegression_CredentialsRule_TableDriven()
    {
        var cases = new[]
        {
            new RuleCase(
            "ok-no-host-image",
            """
            on: push
            jobs:
                build:
                    runs-on: ubuntu-latest
                    container:
                        image: node:20
                    steps:
                        - run: echo ok
            """,
            []),
            new RuleCase(
            "ok-public-registry-without-credentials",
            """
            on: push
            jobs:
                build:
                    runs-on: ubuntu-latest
                    container:
                        image: ghcr.io/owner/app:latest
                    services:
                        cache:
                            image: docker.io/library/redis:7
                    steps:
                        - run: echo ok
            """,
            []),
            new RuleCase(
            "ok-additional-public-registries-without-credentials",
            """
            on: push
            jobs:
                build:
                    runs-on: ubuntu-latest
                    container:
                        image: registry.k8s.io/pause:3.10
                    services:
                        a:
                            image: quay.io/org/app:1
                        b:
                            image: mcr.microsoft.com/dotnet/runtime:8.0
                        c:
                            image: cgr.dev/chainguard/wolfi-base:latest
                        d:
                            image: nvcr.io/nvidia/pytorch:24.01-py3
                        e:
                            image: registry.access.redhat.com/ubi9/ubi:latest
                    steps:
                        - run: echo ok
            """,
            []),
            new RuleCase(
            "ok-private-registry-with-credentials",
            """
            on: push
            jobs:
                build:
                    runs-on: ubuntu-latest
                    container:
                        image: registry.example.com/team/app:1.0.0
                        credentials:
                            username: ${{ secrets.REG_USER }}
                            password: ${{ secrets.REG_PASS }}
                    steps:
                        - run: echo ok
            """,
            []),
            new RuleCase(
            "ng-job-container-private-without-credentials",
            """
            on: push
            jobs:
                build:
                    runs-on: ubuntu-latest
                    container:
                        image: registry.example.com/team/app:1.0.0
                    steps:
                        - run: echo ng
            """,
            ["credentials are not configured", "registry.example.com"]),
            new RuleCase(
            "ng-service-private-without-credentials",
            """
            on: push
            jobs:
                build:
                    runs-on: ubuntu-latest
                    services:
                        db:
                            image: private.example.org/team/db:15
                    steps:
                        - run: echo ng
            """,
            ["credentials are not configured", "private.example.org"]),
            new RuleCase(
            "ng-hardcoded-password-in-container",
            """
            on: push
            jobs:
                build:
                    runs-on: ubuntu-latest
                    container:
                        image: 'example.com/owner/image'
                        credentials:
                            username: user
                            password: pass
                    steps:
                        - run: echo ng
            """,
            ["\"password\" section in \"container\" section should be specified via secrets"]),
            new RuleCase(
            "ng-hardcoded-password-in-service",
            """
            on: push
            jobs:
                build:
                    runs-on: ubuntu-latest
                    services:
                        redis:
                            image: redis
                            credentials:
                                username: user
                                password: pass
                    steps:
                        - run: echo ng
            """,
            ["\"password\" section in \"redis\" service should be specified via secrets"]),
            new RuleCase(
            "ok-password-via-secrets-expression",
            """
            on: push
            jobs:
                build:
                    runs-on: ubuntu-latest
                    container:
                        image: 'example.com/owner/image'
                        credentials:
                            username: ${{ secrets.REG_USER }}
                            password: ${{ secrets.REG_PASS }}
                    steps:
                        - run: echo ok
            """,
            []),
        };

        await AssertRuleCases(new CredentialsRule(), "credentials", cases);
    }
}
