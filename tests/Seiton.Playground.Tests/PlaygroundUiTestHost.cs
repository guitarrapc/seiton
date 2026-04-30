using System.Diagnostics;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.FileProviders;

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
                ShutdownAsync().GetAwaiter().GetResult();
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
    /// Stops the Kestrel host, disposes it, and deletes the temporary publish directory (best effort).
    /// Safe to call multiple times and from <see cref="AppDomain.ProcessExit"/>.
    /// </summary>
    public static async Task ShutdownAsync(CancellationToken cancellationToken = default)
    {
        await Gate.WaitAsync(cancellationToken);
        try
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

            TryDeletePublishRoot(state.PublishRoot);
        }
        finally
        {
            Gate.Release();
        }
    }

    private static void TryDeletePublishRoot(string publishRoot)
    {
        try
        {
            if (Directory.Exists(publishRoot))
            {
                Directory.Delete(publishRoot, recursive: true);
            }
        }
        catch
        {
            // best effort — CI / AV / timing
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
            Arguments = $"publish \"{csproj}\" -c Debug -o \"{publishDir}\" -v q -p:RunAOTCompilation=false -p:PublishTrimmed=false -p:PlaygroundSoftCssFingerprint=true",
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

        var builder = WebApplication.CreateBuilder(new WebApplicationOptions
        {
            ContentRootPath = wwwroot,
            WebRootPath = wwwroot,
            ApplicationName = typeof(PlaygroundUiTestHost).Assembly.FullName,
            Args = Array.Empty<string>(),
        });
        builder.WebHost.UseUrls("http://127.0.0.1:0");

        var app = builder.Build();
        app.UseDefaultFiles(new DefaultFilesOptions
        {
            FileProvider = new PhysicalFileProvider(wwwroot),
        });
        app.UseStaticFiles(new StaticFileOptions
        {
            FileProvider = new PhysicalFileProvider(wwwroot),
        });

        await app.StartAsync(cancellationToken);
        var addresses = app.Services.GetRequiredService<IServer>().Features
            .Get<IServerAddressesFeature>()!.Addresses;
        var urlBase = addresses.First(u => u.StartsWith("http://", StringComparison.Ordinal)).TrimEnd('/');
        var baseUrl = urlBase + "/";

        return new HostState(baseUrl, wwwroot, publishDir, app);
    }
}
