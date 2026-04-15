# GitHub-hosted runners reference

Find information about GitHub-hosted runners, including their specifications and customization options.

## Supported runners and hardware resources

Ranges of GitHub-hosted runners are available for use in public and private repositories.

For lists of available runners, see:

* [Standard runners for **public** repositories](#standard-github-hosted-runners-for-public-repositories)
* [Standard runners for **private** repositories](#standard-github-hosted-runners-for--private-repositories)

GitHub-hosted Linux runners support hardware acceleration for Android SDK tools, which makes running Android tests much faster and consumes fewer minutes. For more information on Android hardware acceleration, see [Configure hardware acceleration for the Android Emulator](https://developer.android.com/studio/run/emulator-acceleration) in the Android Developers documentation.

> \[!NOTE]
> The `-latest` runner images are the latest stable images that GitHub provides, and might not be the most recent version of the operating system available from the operating system vendor.

> \[!WARNING]
> Beta and Deprecated Images are provided "as-is", "with all faults" and "as available" and are excluded from the service level agreement and warranty. Beta Images may not be covered by customer support.

### Standard GitHub-hosted runners for public repositories

For public repositories, jobs using the workflow labels shown in the table below will run with the associated specifications. With the exception of single-CPU runners, each GitHub-hosted runner is a new virtual machine (VM) hosted by GitHub. Single-CPU runners are hosted in a container on a shared VM—see [GitHub-hosted runners reference](/en/actions/reference/runners/github-hosted-runners#single-cpu-runners). Use of the standard GitHub-hosted runners is free and unlimited on public repositories.

<table style="width:100%">
  <thead>
    <tr>
      <th scope="col"><b>Virtual machine / container</b></th>
      <th scope="col"><b>Processor (CPU)</b></th>
      <th scope="col"><b>Memory (RAM)</b></th>
      <th scope="col"><b>Storage (SSD)</b></th>
      <th scope="col"><b>Architecture</b></th>
      <th scope="col"><b>Workflow label</b></th>
    </tr>
  </thead>
  <tbody>
    <tr>
          <td>Linux</td>
          <td>1</td>
          <td>5 GB</td>
          <td>14 GB</td>
          <td> x64 </td>
          <td>
            <code><a href="https://github.com/actions/runner-images/blob/main/images/ubuntu-slim/ubuntu-slim-Readme.md">ubuntu-slim</a></code>
          </td>
        </tr>
    <tr>
      <td>Linux</td>
      <td>4</td>
      <td>16 GB</td>
      <td>14 GB</td>
      <td> x64 </td>
      <td>
        <code><a href="https://github.com/actions/runner-images/blob/main/images/ubuntu/Ubuntu2404-Readme.md">ubuntu-latest</a></code>,
        <code><a href="https://github.com/actions/runner-images/blob/main/images/ubuntu/Ubuntu2404-Readme.md">ubuntu-24.04</a></code>,
        <code><a href="https://github.com/actions/runner-images/blob/main/images/ubuntu/Ubuntu2204-Readme.md">ubuntu-22.04</a></code>
      </td>
    </tr>
    <tr>
      <td>Windows</td>
      <td>4</td>
      <td>16 GB</td>
      <td>14 GB</td>
      <td> x64 </td>
      <td>
        <code><a href="https://github.com/actions/runner-images/blob/main/images/windows/Windows2025-Readme.md">windows-latest</a></code>,
         <code><a href="https://github.com/actions/runner-images/blob/main/images/windows/Windows2025-Readme.md">windows-2025</a></code>,
        <code><a href="https://github.com/actions/runner-images/blob/main/images/windows/Windows2025-VS2026-Readme.md">windows-2025-vs2026</a></code> (public preview),
        <code><a href="https://github.com/actions/runner-images/blob/main/images/windows/Windows2022-Readme.md">windows-2022</a></code>
      </td>
    </tr>
    <tr>
      <td>Linux</td>
      <td>4</td>
      <td>16 GB</td>
      <td>14 GB</td>
      <td> arm64 </td>
      <td>
        <code><a href="https://github.com/actions/partner-runner-images/blob/main/images/arm-ubuntu-24-image.md">ubuntu-24.04-arm</a></code>,
        <code><a href="https://github.com/actions/partner-runner-images/blob/main/images/arm-ubuntu-22-image.md">ubuntu-22.04-arm</a></code>
      </td>
    </tr>
    <tr>
      <td>Windows</td>
      <td>4</td>
      <td>16 GB</td>
      <td>14 GB</td>
      <td>arm64</td>
      <td>
        <code><a href="https://github.com/actions/partner-runner-images/blob/main/images/arm-windows-11-image.md">windows-11-arm</a></code>
      </td>
    </tr>
    <tr>
      <td>macOS</td>
      <td>4</td>
      <td>14 GB</td>
      <td>14 GB</td>
      <td> Intel </td>
      <td>
        <code><a href="https://github.com/actions/runner-images/blob/main/images/macos/macos-15-Readme.md">macos-15-intel</a></code>,
        <code><a href="https://github.com/actions/runner-images/blob/main/images/macos/macos-26-Readme.md">macos-26-intel</a></code>
      </td>
    </tr>
    <tr>
      <td>macOS</td>
      <td>3 (M1)</td>
      <td>7 GB</td>
      <td>14 GB</td>
      <td> arm64 </td>
      <td>
        <code><a href="https://github.com/actions/runner-images/blob/main/images/macos/macos-15-arm64-Readme.md">macos-latest</a></code>,
        <code><a href="https://github.com/actions/runner-images/blob/main/images/macos/macos-14-arm64-Readme.md">macos-14</a></code>,
        <code><a href="https://github.com/actions/runner-images/blob/main/images/macos/macos-15-arm64-Readme.md">macos-15</a></code>,
        <code><a href="https://github.com/actions/runner-images/blob/main/images/macos/macos-26-arm64-Readme.md">macos-26</a></code>
      </td>
    </tr>
  </tbody>

</table>

### Standard GitHub-hosted runners for  private repositories

For  private repositories, jobs using the workflow labels shown in the table below will run on virtual machines with the associated specifications. These runners use your GitHub account's allotment of free minutes, and are then charged at the per minute rates. See [Actions runner pricing](/en/billing/reference/actions-minute-multipliers).

<table style="width:100%">
  <thead>
    <tr>
      <th scope="col"><b>Virtual Machine</b></th>
      <th scope="col"><b>Processor (CPU)</b></th>
      <th scope="col"><b>Memory (RAM)</b></th>
      <th scope="col"><b>Storage (SSD)</b></th>
      <th scope="col"><b>Architecture</b></th>
      <th scope="col"><b>Workflow label</b></th>
    </tr>
  </thead>
  <tbody>
    <tr>
          <td>Linux</td>
          <td>1</td>
          <td>5 GB</td>
          <td>14 GB</td>
          <td> x64 </td>
          <td>
            <code><a href="https://github.com/actions/runner-images/blob/main/images/ubuntu-slim/ubuntu-slim-Readme.md">ubuntu-slim</a></code>
          </td>
        </tr>
    <tr>
      <td>Linux</td>
      <td>2</td>
      <td>8 GB</td>
      <td>14 GB</td>
      <td> x64 </td>
      <td>
        <code><a href="https://github.com/actions/runner-images/blob/main/images/ubuntu/Ubuntu2404-Readme.md">ubuntu-latest</a></code>,
        <code><a href="https://github.com/actions/runner-images/blob/main/images/ubuntu/Ubuntu2404-Readme.md">ubuntu-24.04</a></code>,
        <code><a href="https://github.com/actions/runner-images/blob/main/images/ubuntu/Ubuntu2204-Readme.md">ubuntu-22.04</a></code>
      </td>
    </tr>
    <tr>
      <td>Windows</td>
      <td>2</td>
      <td>8 GB</td>
      <td>14 GB</td>
      <td> x64 </td>
      <td>
        <code><a href="https://github.com/actions/runner-images/blob/main/images/windows/Windows2025-Readme.md">windows-latest</a></code>,
        <code><a href="https://github.com/actions/runner-images/blob/main/images/windows/Windows2025-Readme.md">windows-2025</a></code>,
        <code><a href="https://github.com/actions/runner-images/blob/main/images/windows/Windows2022-Readme.md">windows-2022</a></code>
      </td>
    </tr>
    <tr>
      <td>Linux</td>
      <td>2</td>
      <td>8 GB</td>
      <td>14 GB</td>
      <td> arm64 </td>
      <td>
        <code><a href="https://github.com/actions/partner-runner-images/blob/main/images/arm-ubuntu-24-image.md">ubuntu-24.04-arm</a></code>,
        <code><a href="https://github.com/actions/partner-runner-images/blob/main/images/arm-ubuntu-22-image.md">ubuntu-22.04-arm</a></code>
      </td>
    </tr>
    <tr>
      <td>Windows</td>
      <td>2</td>
      <td>8 GB</td>
      <td>14 GB</td>
      <td> arm64 </td>
      <td>
        <code><a href="https://github.com/actions/partner-runner-images/blob/main/images/arm-windows-11-image.md">windows-11-arm</a></code>
      </td>
    </tr>
    <tr>
      <td>macOS</td>
      <td>4</td>
      <td>14 GB</td>
      <td>14 GB</td>
      <td> Intel </td>
      <td>
        <code><a href="https://github.com/actions/runner-images/blob/main/images/macos/macos-15-Readme.md">macos-15-intel</a></code>,
        <code><a href="https://github.com/actions/runner-images/blob/main/images/macos/macos-26-Readme.md">macos-26-intel</a></code>
      </td>
    </tr>
    <tr>
      <td>macOS</td>
      <td>3 (M1)</td>
      <td>7 GB</td>
      <td>14 GB</td>
      <td> arm64 </td>
      <td>
        <code><a href="https://github.com/actions/runner-images/blob/main/images/macos/macos-15-arm64-Readme.md">macos-latest</a></code>,
        <code><a href="https://github.com/actions/runner-images/blob/main/images/macos/macos-14-arm64-Readme.md">macos-14</a></code>,
        <code><a href="https://github.com/actions/runner-images/blob/main/images/macos/macos-15-arm64-Readme.md">macos-15</a></code>,
        <code><a href="https://github.com/actions/runner-images/blob/main/images/macos/macos-26-arm64-Readme.md">macos-26</a></code>
      </td>
    </tr>
  </tbody>
</table>

Workflow logs list the runner used to run a job. For more information, see [Viewing workflow run history](/en/actions/monitoring-and-troubleshooting-workflows/viewing-workflow-run-history).

### Limitations for arm64 macOS runners

* All actions provided by GitHub are compatible with arm64 GitHub-hosted runners. However, community actions may not be compatible with arm64 and need to be manually installed at runtime.
* Nested-virtualization is not supported due to the limitation of Apple's Virtualization Framework.
* Networking capabilities such as Azure private networking and assigning static IPs are not currently available for macOS larger runners.
* The arm64 macOS runners do not have a static UUID/UDID assigned to them because Apple does not support this feature. However, Intel MacOS runners are assigned a static UDID, specifically `4203018E-580F-C1B5-9525-B745CECA79EB`. If you are building and signing on the same host you plan to test the build on, you can sign with a [development provisioning profile](https://developer.apple.com/help/account/provisioning-profiles/create-a-development-provisioning-profile/). If you do require a static UDID, you can use Intel runners and add their UDID to your Apple Developer account.

### Single-CPU runners

Single-CPU GitHub-hosted runners are available in both public and private repositories. These runners—specified using the workflow label `ubuntu-slim`—offer a lower-cost option for running lightweight operations. This type of runner is optimized for automation tasks, issue operations and short-running jobs. They are not suitable for typical heavyweight CI/CD builds.

`ubuntu-slim` runners execute Actions workflows in Ubuntu Linux, inside a container rather than a full VM instance. When the job begins, GitHub automatically provisions a new container for that job. All steps in the job execute in the container, allowing the steps in that job to share information using the runner's file system. When the job has finished, the container is automatically decommissioned. Each container provides hypervisor level 2 isolation.

> \[!NOTE]
> The container for `ubuntu-slim` runners runs in unprivileged mode. This means that some operations requiring elevated privileges—such as mounting file systems, using Docker-in-Docker, or accessing low-level kernel features—are not supported.

A minimal set of tools is installed on the `ubuntu-slim` runner image, appropriate for lightweight tasks. For details on what software is installed on the `ubuntu-slim` image, see the [README file](https://github.com/actions/runner-images/blob/main/images/ubuntu-slim/ubuntu-slim-Readme.md) in the `actions/runner-images` repository.

#### Usage limits

Single-CPU runners follow the same concurrency model as other GitHub-hosted standard runners. See [Actions limits](/en/actions/reference/limits#job-concurrency-limits-for-github-hosted-runners). The concurrency for the runners is determined by your plan.

The job timeout for single-CPU runners is 15 minutes. If a job reaches this limit, the job is terminated and fails.

### Larger runners

Customers on GitHub Team and GitHub Enterprise Cloud plans can choose from a range of managed virtual machines that have more resources than the [standard GitHub-hosted runners](/en/actions/how-tos/using-github-hosted-runners/using-github-hosted-runners/about-github-hosted-runners#supported-runners-and-hardware-resources). These machines are referred to as "larger runners." They offer the following advanced features:

* More RAM, CPU, and disk space
* Static IP addresses
* Azure private networking
* The ability to group runners
* Autoscaling to support concurrent workflows
* GPU-powered runners

These larger runners are hosted by GitHub and have the runner application and other tools preinstalled.

For more information, see [Using larger runners](/en/actions/using-github-hosted-runners/about-larger-runners).

## Administrative privileges

The Linux and macOS virtual machines both run using passwordless `sudo`. When you need to execute commands or install tools that require more privileges than the current user, you can use `sudo` without needing to provide a password. For more information, see the [Sudo Manual](https://www.sudo.ws/man/1.8.27/sudo.man.html).

Windows virtual machines are configured to run as administrators with User Account Control (UAC) disabled. For more information, see [How User Account Control works](https://docs.microsoft.com/windows/security/identity-protection/user-account-control/how-user-account-control-works) in the Windows documentation.

## IP addresses

To get a list of IP address ranges that GitHub Actions uses for GitHub-hosted runners, you can use the GitHub REST API. For more information, see the `actions` key in the response of the `GET /meta` endpoint. For more information, see [REST API endpoints for meta data](/en/rest/meta/meta#get-github-meta-information).

Windows and Ubuntu runners are hosted in Azure and subsequently have the same IP address ranges as the Azure datacenters. macOS runners are hosted in GitHub's own macOS cloud.

Since there are so many IP address ranges for GitHub-hosted runners, we do not recommend that you use these as allowlists for your internal resources. Instead, we recommend you use larger runners with a static IP address range, or self-hosted runners. For more information, see [Using larger runners](/en/actions/using-github-hosted-runners/about-larger-runners) or [Self-hosted runners](/en/actions/hosting-your-own-runners/managing-self-hosted-runners/about-self-hosted-runners).

The list of GitHub Actions IP addresses returned by the API is updated once a week.

## Communication requirements for GitHub-hosted runners

A GitHub-hosted runner must establish connections to GitHub-owned endpoints to perform essential communication operations. In addition, your runner may require access to additional networks that you specify or utilize within an action.

To ensure proper communications for GitHub-hosted runners between networks within your configuration, ensure that the following communications are allowed.

> \[!NOTE]
> Some of the domains listed are configured using `CNAME` records. Some firewalls might require you to add rules recursively for all `CNAME` records. Note that the `CNAME` records might change in the future, and that only the domains listed will remain constant.

**Needed for essential operations:**

```shell copy
github.com
api.github.com
*.actions.githubusercontent.com
```

**Needed for downloading actions:**

```shell copy
codeload.github.com
```

**Needed for uploading/downloading job summaries, logs, workflow artifacts, and caches:**

```shell copy
results-receiver.actions.githubusercontent.com
*.blob.core.windows.net
```

**Needed for runner version updates:**

```shell copy
objects.githubusercontent.com
objects-origin.githubusercontent.com
github-releases.githubusercontent.com
github-registry-files.githubusercontent.com
```

**Needed for retrieving OIDC tokens:**

```shell copy
*.actions.githubusercontent.com
```

**Needed for downloading or publishing packages or containers to GitHub Packages:**

```shell copy
*.pkg.github.com
pkg-containers.githubusercontent.com
ghcr.io
```

**Needed for Git Large File Storage**

```shell copy
github-cloud.githubusercontent.com
github-cloud.s3.amazonaws.com
```

**Needed for jobs for Dependabot updates**

```shell copy
dependabot-actions.githubapp.com
```

**Needed for downloading release assets:**

```shell copy
release-assets.githubusercontent.com
```

**Needed for VNet:**

```shell copy
api.snapcraft.io
```

## File systems

GitHub executes actions and shell commands in specific directories on the virtual machine. The file paths on virtual machines are not static. Use the environment variables GitHub provides to construct file paths for the `home`, `workspace`, and `workflow` directories.

| Directory             | Environment variable | Description                                                                                                                                                     |
| --------------------- | -------------------- | --------------------------------------------------------------------------------------------------------------------------------------------------------------- |
| `home`                | `HOME`               | Contains user-related data. For example, this directory could contain credentials from a login attempt.                                                         |
| `workspace`           | `GITHUB_WORKSPACE`   | Actions and shell commands execute in this directory. An action can modify the contents of this directory, which subsequent actions can access.                 |
| `workflow/event.json` | `GITHUB_EVENT_PATH`  | The `POST` payload of the webhook event that triggered the workflow. GitHub rewrites this each time an action executes to isolate file content between actions. |

For a list of the environment variables GitHub creates for each workflow, see [Store information in variables](/en/actions/learn-github-actions/variables#default-environment-variables).

### Docker container filesystem

Actions that run in Docker containers have static directories under the `/github` path. However, we strongly recommend using the default environment variables to construct file paths in Docker containers.

GitHub reserves the `/github` path prefix and creates three directories for actions.

* `/github/home`
* `/github/workspace` - **Note:** GitHub Actions must be run by the default Docker user (root). Ensure your Dockerfile does not set the `USER` instruction, otherwise you will not be able to access `GITHUB_WORKSPACE`.
* `/github/workflow`