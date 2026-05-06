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
internal static class PlaygroundUiTestHost
{
    /// <summary>
    /// Shared with browser UI tests so nothing mutates the cached host in parallel with Playwright runs.
    /// </summary>
    internal const string ParallelLockKey = "seiton-playground-ui-static-host";

    private static readonly SemaphoreSlim Gate = new(1, 1);
    private static HostState? s_state;

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

    public sealed record HostState(string BaseUrl, string WwwRootPath, string PublishRoot, WebApplication App);

    public static async Task<HostState> GetOrCreateAsync(CancellationToken cancellationToken = default)
    {
        if (s_state is not null)
        {
            return s_state;
        }

        await Gate.WaitAsync(cancellationToken);
        try
        {
            return s_state ??= await CreateAsync(cancellationToken);
        }
        finally
        {
            Gate.Release();
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
        var state = s_state;
        if (state is null)
        {
            return;
        }

        s_state = null;

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

        await TryDeletePublishRootAsync(state.PublishRoot, cancellationToken);
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

    private static async Task<HostState> CreateAsync(CancellationToken cancellationToken)
    {
        var root = RepoPaths.FindRepoRoot();
        var publishDir = Path.Combine(Path.GetTempPath(), "seiton-playground-ui-" + Guid.NewGuid().ToString("n"));
        Directory.CreateDirectory(publishDir);

        var csproj = Path.Combine(root, "src", "Seiton.Playground", "Seiton.Playground.csproj");
        var psi = new ProcessStartInfo
        {
            FileName = "dotnet",
            Arguments = $"publish \"{csproj}\" -c Debug -o \"{publishDir}\" -v q -p:RunAOTCompilation=false -p:PublishTrimmed=false -p:PlaygroundSoftFingerprint=true",
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
}
