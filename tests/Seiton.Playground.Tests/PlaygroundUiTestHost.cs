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
    private static readonly SemaphoreSlim Gate = new(1, 1);
    private static HostState? s_state;

    public sealed record HostState(string BaseUrl, string WwwRootPath, WebApplication App);

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
        await publish.WaitForExitAsync(cancellationToken);
        if (publish.ExitCode != 0)
        {
            var err = await publish.StandardError.ReadToEndAsync(cancellationToken);
            throw new InvalidOperationException($"dotnet publish failed ({publish.ExitCode}): {err}");
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

        return new HostState(baseUrl, wwwroot, app);
    }
}
