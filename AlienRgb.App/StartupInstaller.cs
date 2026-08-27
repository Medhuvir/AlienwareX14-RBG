using System;
using System.Diagnostics;
using System.IO;
using AlienRgb.Core;
using Microsoft.Win32;

namespace AlienRgb.App;

/// <summary>
/// Registers this copy of AlienRgb for the CURRENT Windows account: an HKCU Run entry
/// (start minimized at login) plus a per-account Scheduled Task that restores the lights
/// when the machine exits Modern Standby. Everything is per-user and needs no elevation,
/// so the portable copy works identically from any account's profile folder.
///
/// The task name includes the username because task names share one global namespace —
/// two accounts can't both own a task called "AlienRgbWakeRestore", and a task registered
/// under one account doesn't run while a different account is the one logged in (this is
/// exactly why sleep-restore silently didn't work when the app was copied to a second
/// account without its own task).
/// </summary>
internal static class StartupInstaller
{
    private const string RunKeyPath = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string RunValueName = "AlienRgb";

    private static string TaskName => $"AlienRgbWakeRestore-{Environment.UserName}";
    private static string AppExePath => Path.Combine(AppContext.BaseDirectory, "AlienRgb.App.exe");
    private static string CliExePath => Path.Combine(AppContext.BaseDirectory, "AlienRgb.Cli.exe");

    public static bool IsInstalled()
    {
        using var key = Registry.CurrentUser.OpenSubKey(RunKeyPath);
        return key?.GetValue(RunValueName) is not null;
    }

    /// <summary>Idempotent: safe to call on every launch to self-heal paths if the folder moved.</summary>
    public static void Install()
    {
        using (var key = Registry.CurrentUser.CreateSubKey(RunKeyPath))
            key.SetValue(RunValueName, $"\"{AppExePath}\" --minimized");

        const string query = "*[System[Provider[@Name='Microsoft-Windows-Kernel-Power'] and EventID=507]]";
        RunSchtasks($"/Create /TN \"{TaskName}\" /TR \"\\\"{CliExePath}\\\" apply\" /SC ONEVENT /EC System /MO \"{query}\" /RL LIMITED /F");
        DiagLog.Write($"Startup registration refreshed for {Environment.UserName} (Run entry + task {TaskName}).");
    }

    public static void Uninstall()
    {
        using (var key = Registry.CurrentUser.CreateSubKey(RunKeyPath))
            key.DeleteValue(RunValueName, throwOnMissingValue: false);
        try { RunSchtasks($"/Delete /TN \"{TaskName}\" /F"); }
        catch { /* task may not exist; nothing to remove */ }
        DiagLog.Write($"Startup registration removed for {Environment.UserName}.");
    }

    private static void RunSchtasks(string arguments)
    {
        var psi = new ProcessStartInfo("schtasks.exe", arguments)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        using var proc = Process.Start(psi)
            ?? throw new InvalidOperationException("Failed to start schtasks.exe");
        string err = proc.StandardError.ReadToEnd();
        proc.StandardOutput.ReadToEnd();
        proc.WaitForExit(15000);
        if (proc.ExitCode != 0)
            throw new InvalidOperationException($"schtasks failed (exit {proc.ExitCode}): {err.Trim()}");
    }
}
