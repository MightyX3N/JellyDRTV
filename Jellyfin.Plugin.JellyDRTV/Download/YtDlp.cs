using System.Diagnostics;
using System.Runtime.InteropServices;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.JellyDRTV.Download;

/// <summary>
/// Finds yt-dlp, or downloads and keeps its own standalone copy up to date.
/// </summary>
public class YtDlp
{
    private const string ReleaseBase = "https://github.com/yt-dlp/yt-dlp/releases/latest/download/";
    private static readonly SemaphoreSlim InstallLock = new(1, 1);
    private readonly string _toolsDir;
    private readonly ILogger _logger;
    private DateTime _lastUpdateCheck = DateTime.MinValue;

    public YtDlp(string toolsDir, ILogger logger)
    {
        _toolsDir = toolsDir;
        _logger = logger;
    }

    private static string AssetName
    {
        get
        {
            if (OperatingSystem.IsWindows())
            {
                return "yt-dlp.exe";
            }

            if (OperatingSystem.IsMacOS())
            {
                return "yt-dlp_macos";
            }

            return RuntimeInformation.OSArchitecture switch
            {
                Architecture.Arm64 => "yt-dlp_linux_aarch64",
                Architecture.Arm => "yt-dlp_linux_armv7l",
                _ => "yt-dlp_linux"
            };
        }
    }

    /// <summary>Returns the yt-dlp executable to use, installing or updating the managed copy if needed.</summary>
    public async Task<string> GetExecutableAsync(string? configuredPath, CancellationToken ct)
    {
        if (!string.IsNullOrWhiteSpace(configuredPath))
        {
            return configuredPath;
        }

        var exe = Path.Combine(_toolsDir, OperatingSystem.IsWindows() ? "yt-dlp.exe" : "yt-dlp");
        await InstallLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (!File.Exists(exe))
            {
                Directory.CreateDirectory(_toolsDir);
                _logger.LogInformation("JellyDRTV: downloading yt-dlp ({Asset})", AssetName);
                var tmp = exe + ".download";
                using (var resp = await Dr.DrApiClient.HttpClient.GetAsync(ReleaseBase + AssetName, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false))
                {
                    resp.EnsureSuccessStatusCode();
                    await using var fs = File.Create(tmp);
                    await resp.Content.CopyToAsync(fs, ct).ConfigureAwait(false);
                }

                File.Move(tmp, exe, true);
                if (!OperatingSystem.IsWindows())
                {
                    File.SetUnixFileMode(exe, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute | UnixFileMode.GroupRead | UnixFileMode.GroupExecute | UnixFileMode.OtherRead | UnixFileMode.OtherExecute);
                }

                _lastUpdateCheck = DateTime.UtcNow;
            }
            else if (DateTime.UtcNow - _lastUpdateCheck > TimeSpan.FromDays(1))
            {
                // DR changes its site now and then; yt-dlp ships fixes quickly.
                _lastUpdateCheck = DateTime.UtcNow;
                var (code, output) = await RunAsync(exe, ["--update"], null, ct).ConfigureAwait(false);
                _logger.LogDebug("JellyDRTV: yt-dlp update check exited {Code}: {Output}", code, output.Trim());
            }
        }
        finally
        {
            InstallLock.Release();
        }

        return exe;
    }

    /// <summary>Returns the yt-dlp version string, or null if it can't run.</summary>
    public async Task<string?> GetVersionAsync(string? configuredPath, CancellationToken ct)
    {
        try
        {
            var exe = !string.IsNullOrWhiteSpace(configuredPath)
                ? configuredPath
                : Path.Combine(_toolsDir, OperatingSystem.IsWindows() ? "yt-dlp.exe" : "yt-dlp");
            if (string.IsNullOrWhiteSpace(configuredPath) && !File.Exists(exe))
            {
                return null;
            }

            var (code, output) = await RunAsync(exe, ["--version"], null, ct).ConfigureAwait(false);
            return code == 0 ? output.Trim() : null;
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or IOException)
        {
            return null;
        }
    }

    /// <summary>Runs a process, streaming each output line to <paramref name="onLine"/>.</summary>
    public static async Task<(int ExitCode, string Output)> RunAsync(string exe, IEnumerable<string> args, Action<string>? onLine, CancellationToken ct)
    {
        var psi = new ProcessStartInfo(exe)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        foreach (var a in args)
        {
            psi.ArgumentList.Add(a);
        }

        using var p = new Process { StartInfo = psi };
        var tail = new System.Collections.Concurrent.ConcurrentQueue<string>();
        void Handle(string? line)
        {
            if (line is null)
            {
                return;
            }

            tail.Enqueue(line);
            while (tail.Count > 40 && tail.TryDequeue(out _))
            {
            }

            onLine?.Invoke(line);
        }

        p.OutputDataReceived += (_, e) => Handle(e.Data);
        p.ErrorDataReceived += (_, e) => Handle(e.Data);
        p.Start();
        p.BeginOutputReadLine();
        p.BeginErrorReadLine();
        try
        {
            await p.WaitForExitAsync(ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            try
            {
                p.Kill(true);
            }
            catch (InvalidOperationException)
            {
            }

            throw;
        }

        return (p.ExitCode, string.Join('\n', tail));
    }
}
