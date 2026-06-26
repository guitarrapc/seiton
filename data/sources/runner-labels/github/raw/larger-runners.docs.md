# Larger runners reference

Find information about larger runners, including their specifications and customization options.

## Machine sizes for larger runners

You can choose from several specifications for larger runners.

### Specifications for general larger runners

| CPU | Memory (RAM) | Storage (SSD) | Architecture | Operating system (OS) |
| --- | ------------ | ------------- | ------------ | --------------------- |
| 5   | 14 GB        | 14 GB         | arm64 (M2)   | macOS                 |
| 12  | 30 GB        | 14 GB         | x64 (Intel)  | macOS                 |
| 2   | 8 GB         | 75 GB         | x64, arm64   | Ubuntu                |
| 4   | 16 GB        | 150 GB        | x64, arm64   | Ubuntu, Windows       |
| 8   | 32 GB        | 300 GB        | x64, arm64   | Ubuntu, Windows       |
| 16  | 64 GB        | 600 GB        | x64, arm64   | Ubuntu, Windows       |
| 32  | 128 GB       | 1200 GB       | x64, arm64   | Ubuntu, Windows       |
| 64  | 208 GB       | 2040 GB       | arm64        | Ubuntu, Windows       |
| 64  | 256 GB       | 2040 GB       | x64          | Ubuntu, Windows       |
| 96  | 384 GB       | 2040 GB       | x64          | Ubuntu, Windows       |

> \[!NOTE] The 4-vCPU Windows runner only works with the Windows Server 2025 or the Base Windows 11 Desktop image.

### Specifications for GPU larger runners

| CPU | GPU | GPU card | Memory (RAM) | GPU memory (VRAM) | Storage (SSD) | Operating system (OS) |
| --- | --- | -------- | ------------ | ----------------- | ------------- | --------------------- |
| 4   | 1   | Tesla T4 | 28 GB        | 16 GB             | 176 GB        | Ubuntu, Windows       |

## Runner images

Larger runners run on virtual machines (VMs), and GitHub installs a virtual hard disk (VHD) on this machine during the VM creation process. You can choose from different VM images to install on your runners.

**GitHub-owned images:** These images are maintained by GitHub and are available for Linux (x64 and arm64), Windows (x64 and arm64), and macOS (x64 and arm64) runners. For more information on these images and a full list of included tools for each runner operating system, see the [GitHub Actions Runner Images](https://github.com/actions/runner-images) repository.

**Partner Images:** Partner images are not managed by GitHub and are pulled from the Azure Marketplace. See below for resources on where to find more information and to report issues for partner images.

* [Base Windows 11 desktop image](https://azuremarketplace.microsoft.com/en-us/marketplace/apps/microsoftwindowsdesktop.windows-11?tab=Overview).
* [NVIDIA GPU-Optimized VMI](https://azuremarketplace.microsoft.com/en-us/marketplace/apps/nvidia.ngc_azure_17_11)
* [Data Science Virtual Machine - Windows 2019](https://azuremarketplace.microsoft.com/en-us/marketplace/apps/microsoft-dsvm.dsvm-win-2019?tab=overview).

## Available macOS larger runners and labels

The following machines are available for macOS larger runners. When you create a macOS larger runner, the runner name is also available as a workflow label that you can use with `runs-on`.

| Runner Size | Architecture | Processor (CPU)                   | Memory (RAM) | Storage (SSD) | Workflow label                                                                                                                      |
| ----------- | ------------ | --------------------------------- | ------------ | ------------- | ----------------------------------------------------------------------------------------------------------------------------------- |
| Large       | Intel        | 12                                | 30 GB        | 14 GB         | <code>macos-latest-large</code>, <code>macos-14-large</code>, <code>macos-15-large</code> (latest), <code>macos-26-large</code>     |
| XLarge      | arm64 (M2)   | 5 (+ 8 GPU hardware acceleration) | 14 GB        | 14 GB         | <code>macos-latest-xlarge</code>, <code>macos-14-xlarge</code>, <code>macos-15-xlarge</code> (latest), <code>macos-26-xlarge</code> |

## Limitations for macOS larger runners

* All actions provided by GitHub are compatible with arm64 GitHub-hosted runners. However, community actions may not be compatible with arm64 and need to be manually installed at runtime.
* Nested-virtualization is not supported due to the limitation of Apple's Virtualization Framework.
* Networking capabilities such as Azure private networking and assigning static IPs are not currently available for macOS larger runners.
* The arm64 macOS runners do not have a static UUID/UDID assigned to them because Apple does not support this feature. However, Intel MacOS runners are assigned a static UDID, specifically `4203018E-580F-C1B5-9525-B745CECA79EB`. If you are building and signing on the same host you plan to test the build on, you can sign with a [development provisioning profile](https://developer.apple.com/help/account/provisioning-profiles/create-a-development-provisioning-profile/). If you do require a static UDID, you can use Intel runners and add their UDID to your Apple Developer account.

## Troubleshooting larger runners

If you notice the jobs that target your larger runners are delayed or not running, there are several factors that may be causing this.

* **Concurrency settings:** You may have reached your maximum concurrency limit. If you would like to enable more jobs to run in parallel, you can update your autoscaling settings to a larger number. See [Managing larger runners](/en/actions/using-github-hosted-runners/managing-larger-runners#configuring-autoscaling-for-larger-runners).
* **Repository permissions:** Ensure you have the appropriate repository permissions enabled for your larger runners. By default, enterprise runners are not available at the repository level and must be manually enabled by an organization administrator. See [Managing larger runners](/en/actions/using-github-hosted-runners/managing-larger-runners#allowing-repositories-to-access-larger-runners).
* **Billing information:** You must have a valid credit card on file in order to use larger runners. After adding a credit card to your account, it can take up to 10 minutes to enable the use of your larger runners. See [Managing your payment and billing information](/en/billing/managing-your-billing/managing-your-payment-and-billing-information).
* **Spending limit:** Your GitHub Actions spending limit must be set to a value greater than zero. See [Setting up budgets to control spending on metered products](/en/billing/managing-billing-for-github-actions/managing-your-spending-limit-for-github-actions).
* **Fair use policy:** GitHub has a fair use policy that begins to throttle jobs based on several factors, such as how many jobs you are running or how many jobs are running across the entirety of GitHub Actions.
* **Job queue to assign time:** Job queue to assign time refers to the time between a job request and GitHub assigning a VM to execute the job. Standard GitHub-hosted runners utilizing prescribed YAML workflow labels (such as `ubuntu-latest`) are always in a "warm" state. With larger runners, a warm VM may not be ready to pick up a job on first request as the pools for these machines are smaller. As a result, GitHub may need to create a new VM, which increases the queue to assign time. Once a runner is in use, VMs are ready for subsequent workflow runs within 5 minutes. If not used again within that time, a subset of those machines remains warm, reducing the queue to assign time for future workflow runs over the next 24 hours. The higher the volume of jobs you run, the more VMs will remain in the warm pool.

## Networking for larger runners

By default, larger runners receive a dynamic IP address that changes for each job run. Optionally, GitHub Enterprise Cloud customers can configure their larger runners to receive static IP addresses from GitHub's IP address pool. For more information, see [About GitHub's IP addresses](/en/authentication/keeping-your-account-and-data-secure/about-githubs-ip-addresses).

When enabled, instances of the larger runner will receive IP addresses from specific ranges that are unique to the runner, allowing you to use the ranges to configure a firewall allowlist. You can use up to 10 larger runners with static IP address ranges in total across all your larger runners. For more information, see [Managing larger runners](/en/actions/using-github-hosted-runners/managing-larger-runners#networking-for-larger-runners).

If you would like to use more than 10 larger runners with static IP address ranges, please contact us through the [GitHub Support portal](https://support.github.com).

> \[!NOTE]
> If runners are unused for more than 90 days, their IP address ranges are automatically removed and cannot be recovered.