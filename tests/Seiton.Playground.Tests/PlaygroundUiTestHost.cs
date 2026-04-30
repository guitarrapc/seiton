using System.Diagnostics;
using System.Text;
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

        // main.js imports ./_framework/dotnet.js; SDK publish often places the loader at {publishRoot}/dotnet.js
        // (not under wwwroot). Bare static hosts need it under wwwroot/_framework for that relative import.
        EnsureDotNetJsForStaticHosting(publishDir, wwwroot);

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

    /// <summary>
    /// Ensures <c>wwwroot/_framework/dotnet.js</c> exists for <c>main.*.js</c> imports.
    /// Prefer the SDK-published <c>{publishDir}/dotnet.js</c>; fall back to copying a hashed <c>_framework/dotnet.*.js</c>.
    /// </summary>
    private static void EnsureDotNetJsForStaticHosting(string publishDir, string wwwroot)
    {
        var frameworkDir = Path.Combine(wwwroot, "_framework");
        if (!Directory.Exists(frameworkDir))
        {
            return;
        }

        var target = Path.Combine(frameworkDir, "dotnet.js");
        if (File.Exists(target))
        {
            return;
        }

        var publishRootLoader = Path.Combine(publishDir, "dotnet.js");
        if (File.Exists(publishRootLoader))
        {
            File.Copy(publishRootLoader, target, overwrite: false);
            return;
        }

        EnsureFrameworkDotNetJsAlias(wwwroot);
    }

    /// <summary>
    /// Fallback: copy an on-disk hashed dotnet loader chunk to <c>dotnet.js</c> when the SDK leaves no sibling file.
    /// </summary>
    private static void EnsureFrameworkDotNetJsAlias(string wwwroot)
    {
        var frameworkDir = Path.Combine(wwwroot, "_framework");
        if (!Directory.Exists(frameworkDir))
        {
            return;
        }

        var target = Path.Combine(frameworkDir, "dotnet.js");
        if (File.Exists(target))
        {
            return;
        }

        var candidates = Directory.EnumerateFiles(frameworkDir, "*.js", SearchOption.TopDirectoryOnly)
            .Select(Path.GetFullPath).Where(IsDotNetFingerprintedJs).ToArray();

        if (candidates.Length == 0)
        {
            return;
        }

        var bootstrap =
            ResolveDotNetLoaderByContent(candidates)
            ?? candidates.OrderByDescending(p => new FileInfo(p).Length).ThenBy(Path.GetFileName, StringComparer.Ordinal).First();

        File.Copy(bootstrap, target, overwrite: false);
    }

    private static bool IsDotNetFingerprintedJs(string path)
    {
        var name = Path.GetFileName(path);
        return name.StartsWith("dotnet.", StringComparison.Ordinal)
            && !name.Contains(".native.", StringComparison.Ordinal)
            && !name.Contains(".runtime.", StringComparison.Ordinal)
            && !name.Contains(".cli.", StringComparison.Ordinal)
            && !string.Equals(name, "dotnet.js", StringComparison.Ordinal);
    }

    private static string? ResolveDotNetLoaderByContent(IEnumerable<string> candidatePaths)
    {
        foreach (var path in candidatePaths.OrderBy(Path.GetFileName, StringComparer.Ordinal))
        {
            long byteLimit = 96 * 1024;
            var fi = new FileInfo(path);
            if (fi.Length < byteLimit)
            {
                byteLimit = fi.Length;
            }

            if (byteLimit <= 0)
            {
                continue;
            }

            var prefix = ReadUtf8Prefix(path, (int)byteLimit);
            if (prefix.Contains("createDotnetRuntime", StringComparison.Ordinal)
                || prefix.Contains("export { dotnet", StringComparison.Ordinal)
                || prefix.Contains("dotnet as", StringComparison.Ordinal)
                || (prefix.Contains("export", StringComparison.Ordinal) && prefix.Contains("dotnet.run", StringComparison.Ordinal)))
            {
                return path;
            }
        }

        return null;
    }

    private static string ReadUtf8Prefix(string path, int maxBytes)
    {
        var buf = new byte[maxBytes];
        using var fs = File.OpenRead(path);
        var read = fs.ReadAtLeast(buf, maxBytes, throwOnEndOfStream: false);
        return Encoding.UTF8.GetString(buf, 0, read);
    }
}
