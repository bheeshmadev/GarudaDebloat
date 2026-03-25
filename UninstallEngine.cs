using System.Diagnostics;
using System.Text;
using System.Text.RegularExpressions;
using Microsoft.Win32;

namespace GarudaDebloat;

internal sealed class UninstallEngine
{
    internal readonly record struct LogMessage(string Text, bool IsError);
    private readonly record struct ProcessResult(int ExitCode, string StdOut, string StdErr);

    internal sealed class UninstallResult
    {
        public int Total { get; init; }
        public int Succeeded { get; set; }
        public int Failed { get; set; }
        public bool FeatureDisabled { get; set; }
    }

    public async Task<UninstallResult> UninstallSelectedAsync(
        IReadOnlyCollection<PackageEntry> selectedItems,
        IProgress<LogMessage> progress,
        CancellationToken cancellationToken)
    {
        UninstallResult result = new() { Total = selectedItems.Count };

        foreach (PackageEntry item in selectedItems)
        {
            cancellationToken.ThrowIfCancellationRequested();
            progress.Report(new LogMessage($"[INFO] Processing {item.TypeBadge}: {item.Name}", false));

            bool success;
            try
            {
                success = item.Category switch
                {
                    PackageCategory.Win32 => await UninstallWin32Async(item, progress, cancellationToken),
                    PackageCategory.Uwp => await UninstallUwpAsync(item, progress, cancellationToken),
                    PackageCategory.WindowsFeature => await DisableFeatureAsync(item, progress, cancellationToken),
                    PackageCategory.Startup => RemoveStartupEntry(item, progress),
                    _ => false
                };
            }
            catch (Exception ex)
            {
                progress.Report(new LogMessage($"[ERROR] {item.Name} failed with exception: {ex.Message}", true));
                success = false;
            }

            if (success)
            {
                result.Succeeded++;
                if (item.Category == PackageCategory.WindowsFeature)
                {
                    result.FeatureDisabled = true;
                }
            }
            else
            {
                result.Failed++;
            }
        }

        return result;
    }

    private static async Task<bool> UninstallWin32Async(PackageEntry item, IProgress<LogMessage> progress, CancellationToken ct)
    {
        string? uninstallString = item.UninstallString;
        if (string.IsNullOrWhiteSpace(uninstallString))
        {
            progress.Report(new LogMessage($"[ERROR] {item.Name}: missing uninstall string.", true));
            return false;
        }

        List<(string Exe, string Args, string Label)> attempts = new();

        if (TryBuildMsiCommand(uninstallString, out string msiExe, out string msiArgs))
        {
            attempts.Add((msiExe, msiArgs, "MSI normalized"));
        }

        if (!string.IsNullOrWhiteSpace(item.QuietUninstallString) && TryParseCommand(item.QuietUninstallString, out string quietExe, out string quietArgs))
        {
            attempts.Add((quietExe, quietArgs, "QuietUninstallString"));
        }

        if (TryParseCommand(uninstallString, out string exe, out string args))
        {
            attempts.Add((exe, args, "UninstallString"));

            string[] silentFlags = ["/S", "/quiet", "/qn", "/silent", "/VERYSILENT", "/s", "/passive"];
            foreach (string flag in silentFlags)
            {
                attempts.Add((exe, EnsureFlag(args, flag), $"UninstallString + {flag}"));
            }

            if (exe.EndsWith("OneDriveSetup.exe", StringComparison.OrdinalIgnoreCase))
            {
                attempts.Add((exe, "/uninstall", "OneDrive targeted"));
                attempts.Add((exe, "/uninstall /allusers", "OneDrive targeted all users"));
            }
        }
        else
        {
            progress.Report(new LogMessage($"[ERROR] {item.Name}: invalid uninstall command.", true));
            return false;
        }

        foreach ((string attemptExe, string attemptArgs, string label) in attempts
                     .GroupBy(x => $"{x.Exe}|{x.Args}", StringComparer.OrdinalIgnoreCase)
                     .Select(x => x.First()))
        {
            progress.Report(new LogMessage($"[INFO] {item.Name}: trying {label}.", false));
            ProcessResult result = await RunProcessDetailedAsync(attemptExe, attemptArgs, ct);
            if (IsSuccessfulUninstallExitCode(result.ExitCode))
            {
                CleanupResidualFiles(item, progress);
                progress.Report(new LogMessage($"[OK] {item.Name} removed using {label}.", false));
                return true;
            }

            string details = BuildProcessErrorDetails(result);
            progress.Report(new LogMessage($"[WARN] {item.Name}: {label} failed with code {result.ExitCode}.{details}", true));
        }

        progress.Report(new LogMessage($"[ERROR] {item.Name}: all uninstall attempts failed.", true));
        return false;
    }

    private static async Task<bool> UninstallUwpAsync(PackageEntry item, IProgress<LogMessage> progress, CancellationToken ct)
    {
        string packageName = item.UwpPackageName ?? item.Name;

        if (string.IsNullOrWhiteSpace(packageName))
        {
            progress.Report(new LogMessage($"[ERROR] {item.Name}: missing package name.", true));
            return false;
        }

        string escapedName = EscapePowerShellLiteral(packageName);

        string command =
            "$ErrorActionPreference='SilentlyContinue'; " +
            $"$name='{escapedName}'; " +
            "$removedAny=$false; " +
            "Get-AppxPackage -AllUsers -Name $name | ForEach-Object { Remove-AppxPackage -Package $_.PackageFullName -AllUsers -ErrorAction SilentlyContinue; $removedAny=$true }; " +
            "Get-AppxPackage -Name $name | ForEach-Object { Remove-AppxPackage -Package $_.PackageFullName -ErrorAction SilentlyContinue; $removedAny=$true }; " +
            "$prov = Get-AppxProvisionedPackage -Online | Where-Object { $_.DisplayName -eq $name -or $_.PackageName -like ($name + '*') }; " +
            "foreach($p in $prov){ Remove-AppxProvisionedPackage -Online -PackageName $p.PackageName -AllUsers | Out-Null; $removedAny=$true }; " +
            "$remaining = (Get-AppxPackage -AllUsers -Name $name | Measure-Object).Count + ((Get-AppxProvisionedPackage -Online | Where-Object { $_.DisplayName -eq $name -or $_.PackageName -like ($name + '*') } | Measure-Object).Count); " +
            "if($remaining -eq 0){ exit 0 } elseif(-not $removedAny){ exit 3 } else { exit 2 }";

        ProcessResult result = await RunPowerShellDetailedAsync(command, ct);
        if (result.ExitCode == 0)
        {
            CleanupResidualFiles(item, progress);
            progress.Report(new LogMessage($"[OK] {item.Name} UWP package removed and deprovisioned.", false));
            return true;
        }

        string errorHint = result.ExitCode switch
        {
            2 => " Package is still present after removal attempt (possibly protected/non-removable).",
            3 => " No matching package found for the current system context.",
            _ => string.Empty
        };

        string details = BuildProcessErrorDetails(result);
        progress.Report(new LogMessage($"[ERROR] {item.Name}: UWP removal failed with code {result.ExitCode}.{errorHint}{details}", true));
        return false;
    }

    private static async Task<bool> DisableFeatureAsync(PackageEntry item, IProgress<LogMessage> progress, CancellationToken ct)
    {
        string featureName = item.FeatureName ?? item.Name;
        string escapedFeature = EscapePowerShellLiteral(featureName);

        string command = "$ErrorActionPreference='Stop'; " +
                         $"Disable-WindowsOptionalFeature -Online -FeatureName '{escapedFeature}' -NoRestart";

        ProcessResult result = await RunPowerShellDetailedAsync(command, ct);
        if (IsSuccessfulUninstallExitCode(result.ExitCode))
        {
            progress.Report(new LogMessage($"[OK] Feature disabled: {item.Name}", false));
            return true;
        }

        string details = BuildProcessErrorDetails(result);
        progress.Report(new LogMessage($"[ERROR] Feature disable failed: {item.Name} (code {result.ExitCode}).{details}", true));
        return false;
    }

    private static bool RemoveStartupEntry(PackageEntry item, IProgress<LogMessage> progress)
    {
        if (!string.IsNullOrWhiteSpace(item.StartupFilePath))
        {
            if (!File.Exists(item.StartupFilePath))
            {
                progress.Report(new LogMessage($"[WARN] Startup shortcut not found: {item.StartupFilePath}", true));
                return false;
            }

            File.Delete(item.StartupFilePath);
            progress.Report(new LogMessage($"[OK] Startup shortcut removed: {item.Name}", false));
            return true;
        }

        if (!string.IsNullOrWhiteSpace(item.StartupRegistryHive) &&
            !string.IsNullOrWhiteSpace(item.StartupRegistryPath) &&
            !string.IsNullOrWhiteSpace(item.StartupRegistryValueName))
        {
            RegistryHive hive = item.StartupRegistryHive.Equals("HKLM", StringComparison.OrdinalIgnoreCase)
                ? RegistryHive.LocalMachine
                : RegistryHive.CurrentUser;

            using RegistryKey baseKey64 = RegistryKey.OpenBaseKey(hive, RegistryView.Registry64);
            using RegistryKey baseKey32 = RegistryKey.OpenBaseKey(hive, RegistryView.Registry32);

            bool deleted = TryDeleteRegistryValue(baseKey64, item.StartupRegistryPath, item.StartupRegistryValueName) ||
                           TryDeleteRegistryValue(baseKey32, item.StartupRegistryPath, item.StartupRegistryValueName);

            if (deleted)
            {
                progress.Report(new LogMessage($"[OK] Startup registry entry removed: {item.Name}", false));
                return true;
            }

            progress.Report(new LogMessage($"[ERROR] Startup registry value not found: {item.Name}", true));
            return false;
        }

        progress.Report(new LogMessage($"[ERROR] Startup item metadata missing: {item.Name}", true));
        return false;
    }

    private static bool TryDeleteRegistryValue(RegistryKey baseKey, string subPath, string valueName)
    {
        using RegistryKey? key = baseKey.OpenSubKey(subPath, writable: true);
        if (key is null)
        {
            return false;
        }

        if (!key.GetValueNames().Contains(valueName, StringComparer.OrdinalIgnoreCase))
        {
            return false;
        }

        key.DeleteValue(valueName, throwOnMissingValue: false);
        return true;
    }

    private static async Task<ProcessResult> RunPowerShellDetailedAsync(string command, CancellationToken ct)
    {
        return await RunProcessDetailedAsync(
            "powershell.exe",
            $"-NoProfile -ExecutionPolicy Bypass -Command \"{command}\"",
            ct);
    }

    private static async Task<ProcessResult> RunProcessDetailedAsync(string fileName, string arguments, CancellationToken ct)
    {
        ProcessStartInfo psi = new()
        {
            FileName = fileName,
            Arguments = arguments,
            UseShellExecute = false,
            CreateNoWindow = true,
            WindowStyle = ProcessWindowStyle.Hidden,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };

        using Process process = new() { StartInfo = psi };
        process.Start();

        Task<string> stdOutTask = process.StandardOutput.ReadToEndAsync(ct);
        Task<string> stdErrTask = process.StandardError.ReadToEndAsync(ct);

        await process.WaitForExitAsync(ct);
        string stdOut = await stdOutTask;
        string stdErr = await stdErrTask;

        return new ProcessResult(process.ExitCode, stdOut, stdErr);
    }

    private static bool TryBuildMsiCommand(string uninstallString, out string exe, out string args)
    {
        exe = string.Empty;
        args = string.Empty;

        if (!uninstallString.Contains("msiexec", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        Match match = Regex.Match(uninstallString, "\\{[A-Za-z0-9-]+\\}");
        if (match.Success)
        {
            exe = "msiexec.exe";
            args = $"/x {match.Value} /qn /norestart";
            return true;
        }

        if (!TryParseCommand(uninstallString, out exe, out args))
        {
            return false;
        }

        args = Regex.Replace(args, "(?i)/i", "/x");

        if (!args.Contains("/x", StringComparison.OrdinalIgnoreCase))
        {
            args = "/x " + args;
        }

        if (!args.Contains("/qn", StringComparison.OrdinalIgnoreCase))
        {
            args += " /qn";
        }

        if (!args.Contains("/norestart", StringComparison.OrdinalIgnoreCase))
        {
            args += " /norestart";
        }

        return true;
    }

    private static bool TryParseCommand(string command, out string fileName, out string args)
    {
        fileName = string.Empty;
        args = string.Empty;

        if (string.IsNullOrWhiteSpace(command))
        {
            return false;
        }

        string trimmed = command.Trim();

        if (trimmed.StartsWith('"'))
        {
            int endQuote = trimmed.IndexOf('"', 1);
            if (endQuote <= 1)
            {
                return false;
            }

            fileName = trimmed[1..endQuote];
            args = endQuote + 1 < trimmed.Length ? trimmed[(endQuote + 1)..].Trim() : string.Empty;
            return true;
        }

        int exeIndex = trimmed.IndexOf(".exe", StringComparison.OrdinalIgnoreCase);
        if (exeIndex > 0)
        {
            int pathEnd = exeIndex + 4;
            fileName = trimmed[..pathEnd].Trim().Trim('"');
            args = pathEnd < trimmed.Length ? trimmed[pathEnd..].Trim() : string.Empty;
            return true;
        }

        int msiIndex = trimmed.IndexOf(".msi", StringComparison.OrdinalIgnoreCase);
        if (msiIndex > 0)
        {
            int pathEnd = msiIndex + 4;
            fileName = trimmed[..pathEnd].Trim().Trim('"');
            args = pathEnd < trimmed.Length ? trimmed[pathEnd..].Trim() : string.Empty;
            return true;
        }

        int firstSpace = trimmed.IndexOf(' ');
        if (firstSpace < 0)
        {
            fileName = trimmed;
            return true;
        }

        fileName = trimmed[..firstSpace].Trim();
        args = trimmed[(firstSpace + 1)..].Trim();
        return true;
    }

    private static void CleanupResidualFiles(PackageEntry item, IProgress<LogMessage> progress)
    {
        IEnumerable<string> candidates = item.Category switch
        {
            PackageCategory.Win32 => BuildWin32CleanupCandidates(item),
            PackageCategory.Uwp => BuildUwpCleanupCandidates(item),
            _ => []
        };

        foreach (string path in candidates.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            if (!IsSafeRemovalPath(path))
            {
                continue;
            }

            try
            {
                if (Directory.Exists(path))
                {
                    TryDeleteDirectory(path);
                    progress.Report(new LogMessage($"[INFO] Residual directory removed: {path}", false));
                }
                else if (File.Exists(path))
                {
                    File.Delete(path);
                    progress.Report(new LogMessage($"[INFO] Residual file removed: {path}", false));
                }
            }
            catch (Exception ex)
            {
                progress.Report(new LogMessage($"[WARN] Residual cleanup skipped for {path}: {ex.Message}", true));
            }
        }
    }

    private static IEnumerable<string> BuildWin32CleanupCandidates(PackageEntry item)
    {
        List<string> paths = new();

        if (!string.IsNullOrWhiteSpace(item.InstallLocation))
        {
            paths.Add(item.InstallLocation);
        }

        if (TryGetDirectoryFromCommandPath(item.UninstallString, out string? uninstallDir) && uninstallDir is not null)
        {
            paths.Add(uninstallDir);
        }

        if (TryGetDirectoryFromCommandPath(item.DisplayIconPath, out string? iconDir) && iconDir is not null)
        {
            paths.Add(iconDir);
        }

        string localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        string roamingAppData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        string programData = Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData);

        if (!string.IsNullOrWhiteSpace(item.Name))
        {
            paths.Add(Path.Combine(localAppData, item.Name));
            paths.Add(Path.Combine(roamingAppData, item.Name));
            paths.Add(Path.Combine(programData, item.Name));
        }

        return paths;
    }

    private static IEnumerable<string> BuildUwpCleanupCandidates(PackageEntry item)
    {
        List<string> paths = new();

        string packageFamily = ExtractPackageFamilyName(item.UwpPackageFullName, item.UwpPackageName);
        if (string.IsNullOrWhiteSpace(packageFamily))
        {
            return paths;
        }

        string localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        paths.Add(Path.Combine(localAppData, "Packages", packageFamily));

        return paths;
    }

    private static string ExtractPackageFamilyName(string? packageFullName, string? packageName)
    {
        if (string.IsNullOrWhiteSpace(packageFullName))
        {
            return string.Empty;
        }

        string[] parts = packageFullName.Split('_', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length >= 4)
        {
            return parts[0] + "_" + parts[3];
        }

        return packageName ?? string.Empty;
    }

    private static bool TryGetDirectoryFromCommandPath(string? command, out string? directory)
    {
        directory = null;
        if (string.IsNullOrWhiteSpace(command))
        {
            return false;
        }

        string raw = command.Trim().Trim('"');
        int commaIndex = raw.IndexOf(',');
        if (commaIndex > 0)
        {
            raw = raw[..commaIndex];
        }

        if (TryParseCommand(raw, out string exePath, out _))
        {
            raw = exePath;
        }

        string expanded = Environment.ExpandEnvironmentVariables(raw).Trim('"');
        if (File.Exists(expanded))
        {
            directory = Path.GetDirectoryName(expanded);
            return !string.IsNullOrWhiteSpace(directory);
        }

        if (Directory.Exists(expanded))
        {
            directory = expanded;
            return true;
        }

        return false;
    }

    private static bool IsSafeRemovalPath(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return false;
        }

        string full;
        try
        {
            full = Path.GetFullPath(Environment.ExpandEnvironmentVariables(path));
        }
        catch
        {
            return false;
        }

        if (full.Length < 6)
        {
            return false;
        }

        string windows = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
        string programFiles = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
        string programFilesX86 = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86);
        string localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        string roamingAppData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        string programData = Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData);

        string[] blockedExact =
        [
            Path.GetPathRoot(full) ?? string.Empty,
            windows,
            programFiles,
            programFilesX86,
            localAppData,
            roamingAppData,
            programData
        ];

        if (blockedExact.Any(x => full.Equals(x, StringComparison.OrdinalIgnoreCase)))
        {
            return false;
        }

        bool allowedParent = full.StartsWith(programFiles, StringComparison.OrdinalIgnoreCase)
            || full.StartsWith(programFilesX86, StringComparison.OrdinalIgnoreCase)
            || full.StartsWith(localAppData, StringComparison.OrdinalIgnoreCase)
            || full.StartsWith(roamingAppData, StringComparison.OrdinalIgnoreCase)
            || full.StartsWith(programData, StringComparison.OrdinalIgnoreCase);

        return allowedParent;
    }

    private static void TryDeleteDirectory(string directoryPath)
    {
        foreach (string file in Directory.EnumerateFiles(directoryPath, "*", SearchOption.AllDirectories))
        {
            File.SetAttributes(file, FileAttributes.Normal);
        }

        Directory.Delete(directoryPath, recursive: true);
    }

    private static string EnsureFlag(string args, string flag)
    {
        if (args.Contains(flag, StringComparison.OrdinalIgnoreCase))
        {
            return args;
        }

        StringBuilder sb = new(args);
        if (sb.Length > 0)
        {
            sb.Append(' ');
        }

        sb.Append(flag);
        return sb.ToString();
    }

    private static string EscapePowerShellLiteral(string value)
    {
        return value.Replace("'", "''");
    }

    private static bool IsSuccessfulUninstallExitCode(int exitCode)
    {
        return exitCode is 0 or 1641 or 3010;
    }

    private static string BuildProcessErrorDetails(ProcessResult result)
    {
        string source = string.IsNullOrWhiteSpace(result.StdErr) ? result.StdOut : result.StdErr;
        if (string.IsNullOrWhiteSpace(source))
        {
            return string.Empty;
        }

        string compact = source.Replace(Environment.NewLine, " ").Trim();
        if (compact.Length > 220)
        {
            compact = compact[..220] + "...";
        }

        return $" Details: {compact}";
    }
}
