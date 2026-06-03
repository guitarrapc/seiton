using System.Diagnostics;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.Extensions.DependencyInjection;

namespace Seiton.Playground.Tests;

/// <summary>
/// Publishes the Blazor WASM project once and serves <c>wwwroot</c> over loopback HTTP for UI tests.
/// </summary>
/// <summary>WASM publish profile for locally hosted Playground UI tests.</summary>
internal enum PlaygroundWasmPublishMode
{
    /// <summary>Debug, no trim, no AOT — fast iteration for layout tests.</summary>
    DebugFast,

    /// <summary>Release + trim + AOT — matches GitHub Pages production bundle.</summary>
    ReleaseAot,
}

internal static class PlaygroundUiTestHost
{
    /// <summary>
    /// Shared with all playground tests — see <see cref="PlaygroundTestParallelism.AssemblyLockKey"/>.
    /// </summary>
    internal const string ParallelLockKey = PlaygroundTestParallelism.AssemblyLockKey;

    private static readonly SemaphoreSlim Gate = new(1, 1);
    private static HostState? s_debugState;
    private static HostState? s_releaseAotState;

    static PlaygroundUiTestHost()
    {
        AppDomain.CurrentDomain.ProcessExit += (_, _) =>
        {
            try
            {
                ShutdownForProcessExitAsync().GetAwaiter().GetResult();
            }
            catch
            {
                // best effort — runner is exiting
            }
        };
    }

    public sealed record HostState(
        string BaseUrl,
        string WwwRootPath,
        string PublishRoot,
        WebApplication App,
        bool DeletePublishRootOnShutdown = true);

    public static Task<HostState> GetOrCreateAsync(CancellationToken cancellationToken = default)
        => GetOrCreateAsync(PlaygroundWasmPublishMode.DebugFast, cancellationToken);

    public static async Task<HostState> GetOrCreateAsync(
        PlaygroundWasmPublishMode mode,
        CancellationToken cancellationToken = default)
    {
        var existing = GetCachedState(mode);
        if (existing is not null)
        {
            return existing;
        }

        await Gate.WaitAsync(cancellationToken);
        try
        {
            existing = GetCachedState(mode);
            if (existing is not null)
            {
                return existing;
            }

            var created = await CreateAsync(mode, cancellationToken);
            SetCachedState(mode, created);
            return created;
        }
        finally
        {
            Gate.Release();
        }
    }

    private static HostState? GetCachedState(PlaygroundWasmPublishMode mode)
        => mode == PlaygroundWasmPublishMode.ReleaseAot ? s_releaseAotState : s_debugState;

    private static void SetCachedState(PlaygroundWasmPublishMode mode, HostState state)
    {
        if (mode == PlaygroundWasmPublishMode.ReleaseAot)
        {
            s_releaseAotState = state;
        }
        else
        {
            s_debugState = state;
        }
    }

    /// <summary>
    /// Best-effort teardown when the process is exiting. Does not wait indefinitely for
    /// <see cref="Gate"/> (another thread may be in <see cref="CreateAsync"/> during publish).
    /// </summary>
    private static async Task ShutdownForProcessExitAsync()
    {
        if (!await Gate.WaitAsync(TimeSpan.FromSeconds(1)).ConfigureAwait(false))
        {
            return;
        }

        try
        {
            using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(8));
            await ShutdownCoreAsync(deadline.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // Deadline — process exit should not hang on slow IO.
        }
        catch
        {
            // best effort
        }
        finally
        {
            Gate.Release();
        }
    }

    /// <summary>
    /// Stops the Kestrel host, disposes it, and deletes the temporary publish directory (best effort).
    /// Safe to call multiple times and from <see cref="AppDomain.ProcessExit"/> via <see cref="ShutdownForProcessExitAsync"/>.
    /// </summary>
    public static async Task ShutdownAsync(CancellationToken cancellationToken = default)
    {
        await Gate.WaitAsync(cancellationToken);
        try
        {
            await ShutdownCoreAsync(cancellationToken);
        }
        finally
        {
            Gate.Release();
        }
    }

    private static async Task ShutdownCoreAsync(CancellationToken cancellationToken)
    {
        s_debugState = await ShutdownSlotAsync(s_debugState, cancellationToken);
        s_releaseAotState = await ShutdownSlotAsync(s_releaseAotState, cancellationToken);
    }

    private static async Task<HostState?> ShutdownSlotAsync(HostState? slot, CancellationToken cancellationToken)
    {
        var state = slot;
        if (state is null)
        {
            return null;
        }

        try
        {
            await state.App.StopAsync(cancellationToken);
        }
        catch
        {
            // ignore
        }

        try
        {
            await state.App.DisposeAsync();
        }
        catch
        {
            // ignore
        }

        if (state.DeletePublishRootOnShutdown)
        {
            await TryDeletePublishRootAsync(state.PublishRoot, cancellationToken);
        }

        return null;
    }

    /// <summary>
    /// Deletes the publish tree with retries — file locks / AV on Windows and CI can need a few attempts.
    /// </summary>
    private static async Task TryDeletePublishRootAsync(string publishRoot, CancellationToken cancellationToken)
    {
        const int maxAttempts = 25;
        var delay = TimeSpan.FromMilliseconds(150);

        for (var attempt = 0; attempt < maxAttempts; attempt++)
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (!Directory.Exists(publishRoot))
            {
                return;
            }

            try
            {
                Directory.Delete(publishRoot, recursive: true);
            }
            catch
            {
                // File locks, indexer, AV — try again after backoff.
            }

            if (!Directory.Exists(publishRoot))
            {
                return;
            }

            try
            {
                await Task.Delay(delay, cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return;
            }
        }
    }

    private static async Task<HostState> CreateAsync(
        PlaygroundWasmPublishMode mode,
        CancellationToken cancellationToken)
    {
        var prePublished = await TryCreateFromPrePublishedAsync(mode, cancellationToken);
        if (prePublished is not null)
        {
            return prePublished;
        }

        // Only one WASM host mode at a time — holding Debug + Release AOT doubles disk and Kestrel RSS.
        await ShutdownOtherModeAsync(mode, cancellationToken);

        var root = RepoPaths.FindRepoRoot();
        // Bump suffix when Playground WASM bits change so cached hosts are not stale.
        var suffix = mode == PlaygroundWasmPublishMode.ReleaseAot ? "aot-v6" : "dbg";
        var publishDir = Path.Combine(Path.GetTempPath(), $"seiton-playground-ui-{suffix}-" + Guid.NewGuid().ToString("n"));
        Directory.CreateDirectory(publishDir);

        var csproj = Path.Combine(root, "src", "Seiton.Playground", "Seiton.Playground.csproj");
        var publishArgs = mode == PlaygroundWasmPublishMode.ReleaseAot
            ? $"publish \"{csproj}\" -c Release -o \"{publishDir}\" -v q -p:RunAOTCompilation=true -p:PublishTrimmed=true -p:PlaygroundSoftFingerprint=true"
            : $"publish \"{csproj}\" -c Debug -o \"{publishDir}\" -v q -p:RunAOTCompilation=false -p:PublishTrimmed=false -p:PlaygroundSoftFingerprint=true";
        var psi = new ProcessStartInfo
        {
            FileName = "dotnet",
            Arguments = publishArgs,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };

        using var publish = Process.Start(psi) ?? throw new InvalidOperationException("Failed to start dotnet publish.");
        var stdoutTask = publish.StandardOutput.ReadToEndAsync(cancellationToken);
        var stderrTask = publish.StandardError.ReadToEndAsync(cancellationToken);
        await publish.WaitForExitAsync(cancellationToken);
        await Task.WhenAll(stdoutTask, stderrTask);

        if (publish.ExitCode != 0)
        {
            var err = await stderrTask;
            var stdoutText = await stdoutTask;
            var detail = string.IsNullOrWhiteSpace(err) ? stdoutText : err;
            throw new InvalidOperationException($"dotnet publish failed ({publish.ExitCode}): {detail}");
        }

        var wwwroot = Path.Combine(publishDir, "wwwroot");
        if (!File.Exists(Path.Combine(wwwroot, "index.html")))
        {
            throw new InvalidOperationException($"Published index.html not found under '{wwwroot}'.");
        }

        var manifestPath = Path.Combine(publishDir, "Seiton.Playground.staticwebassets.endpoints.json");
        if (!File.Exists(manifestPath))
        {
            throw new InvalidOperationException($"Static assets manifest not found: '{manifestPath}'");
        }

        // .NET 10 SDK fingerprints _framework/ JS files and relies on MapStaticAssets() to
        // resolve unfingerprinted paths (e.g. dotnet.js → dotnet.{hash}.js) and to dynamically
        // generate dotnet.boot.js at runtime. Using plain UseStaticFiles() no longer works
        // because dotnet.boot.js is not emitted as a static file.
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions
        {
            ContentRootPath = publishDir,
            WebRootPath = wwwroot,
            ApplicationName = typeof(PlaygroundUiTestHost).Assembly.FullName,
            Args = Array.Empty<string>(),
        });
        builder.WebHost.UseUrls("http://127.0.0.1:0");

        var app = builder.Build();
        app.MapStaticAssets(manifestPath);
        app.MapFallbackToFile("index.html");

        await app.StartAsync(cancellationToken);
        var addresses = app.Services.GetRequiredService<IServer>().Features
            .Get<IServerAddressesFeature>()!.Addresses;
        var urlBase = addresses.First(u => u.StartsWith("http://", StringComparison.Ordinal)).TrimEnd('/');
        var baseUrl = urlBase + "/";

        return new HostState(baseUrl, wwwroot, publishDir, app);
    }

    /// <summary>
    /// When CI (or a local script) pre-publishes the playground, skip in-test <c>dotnet publish</c>
    /// (saves tens of GB peak RAM during WASM AOT inside the test process).
    /// </summary>
    private static async Task<HostState?> TryCreateFromPrePublishedAsync(
        PlaygroundWasmPublishMode mode,
        CancellationToken cancellationToken)
    {
        var envName = mode == PlaygroundWasmPublishMode.ReleaseAot
            ? "SEITON_PLAYGROUND_PUBLISH_DIR_RELEASE"
            : "SEITON_PLAYGROUND_PUBLISH_DIR_DEBUG";
        var publishDir = Environment.GetEnvironmentVariable(envName);
        if (string.IsNullOrWhiteSpace(publishDir) || !Directory.Exists(publishDir))
        {
            return null;
        }

        publishDir = Path.GetFullPath(publishDir);
        var wwwroot = Path.Combine(publishDir, "wwwroot");
        if (!File.Exists(Path.Combine(wwwroot, "index.html")))
        {
            throw new InvalidOperationException(
                $"Pre-published playground missing index.html under '{wwwroot}' (env {envName}).");
        }

        var manifestPath = Path.Combine(publishDir, "Seiton.Playground.staticwebassets.endpoints.json");
        if (!File.Exists(manifestPath))
        {
            throw new InvalidOperationException(
                $"Pre-published playground missing static assets manifest: '{manifestPath}'.");
        }

        var builder = WebApplication.CreateBuilder(new WebApplicationOptions
        {
            ContentRootPath = publishDir,
            WebRootPath = wwwroot,
            ApplicationName = typeof(PlaygroundUiTestHost).Assembly.FullName,
            Args = Array.Empty<string>(),
        });
        builder.WebHost.UseUrls("http://127.0.0.1:0");

        var app = builder.Build();
        app.MapStaticAssets(manifestPath);
        app.MapFallbackToFile("index.html");

        await app.StartAsync(cancellationToken);
        var addresses = app.Services.GetRequiredService<IServer>().Features
            .Get<IServerAddressesFeature>()!.Addresses;
        var urlBase = addresses.First(u => u.StartsWith("http://", StringComparison.Ordinal)).TrimEnd('/');
        var baseUrl = urlBase + "/";

        // PublishRoot equals publishDir; ShutdownAsync will not delete pre-published trees.
        return new HostState(baseUrl, wwwroot, publishDir, app, DeletePublishRootOnShutdown: false);
    }

    private static async Task ShutdownOtherModeAsync(PlaygroundWasmPublishMode mode, CancellationToken cancellationToken)
    {
        if (mode == PlaygroundWasmPublishMode.DebugFast)
        {
            s_releaseAotState = await ShutdownSlotAsync(s_releaseAotState, cancellationToken);
        }
        else
        {
            s_debugState = await ShutdownSlotAsync(s_debugState, cancellationToken);
        }
    }
}
