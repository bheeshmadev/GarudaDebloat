using System.Diagnostics;
using System.Text.Json;
using Microsoft.Win32;

namespace GarudaDebloat;

internal sealed class PackageScanner
{
    internal sealed class ScanResult
    {
        public List<PackageEntry> Win32Apps { get; } = new();

        public List<PackageEntry> UwpApps { get; } = new();

        public List<PackageEntry> WindowsFeatures { get; } = new();

        public List<PackageEntry> StartupItems { get; } = new();
    }

    public async Task<ScanResult> ScanAllAsync(CancellationToken cancellationToken)
    {
        return await Task.Run(() =>
        {
            cancellationToken.ThrowIfCancellationRequested();

            ScanResult result = new();
            result.Win32Apps.AddRange(ScanWin32Apps());

            cancellationToken.ThrowIfCancellationRequested();
            result.UwpApps.AddRange(ScanUwpApps());

            return result;
        }, cancellationToken);
    }

    private static IEnumerable<PackageEntry> ScanWin32Apps()
    {
        string uninstallPath = @"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall";
        string uninstallPathWow6432 = @"SOFTWARE\Wow6432Node\Microsoft\Windows\CurrentVersion\Uninstall";

        List<(RegistryHive hive, RegistryView view, string path)> targets =
        [
            (RegistryHive.LocalMachine, RegistryView.Registry64, uninstallPath),
            (RegistryHive.LocalMachine, RegistryView.Registry64, uninstallPathWow6432),
            (RegistryHive.LocalMachine, RegistryView.Registry32, uninstallPath),
            (RegistryHive.CurrentUser, RegistryView.Registry64, uninstallPath),
            (RegistryHive.CurrentUser, RegistryView.Registry32, uninstallPath)
        ];

        Dictionary<string, PackageEntry> deduped = new(StringComparer.OrdinalIgnoreCase);

        foreach ((RegistryHive hive, RegistryView view, string path) in targets)
        {
            using RegistryKey baseKey = RegistryKey.OpenBaseKey(hive, view);
            using RegistryKey? uninstallKey = baseKey.OpenSubKey(path);
            if (uninstallKey is null)
            {
                continue;
            }

            foreach (string subKeyName in uninstallKey.GetSubKeyNames())
            {
                using RegistryKey? appKey = uninstallKey.OpenSubKey(subKeyName);
                if (appKey is null)
                {
                    continue;
                }

                string? displayName = appKey.GetValue("DisplayName") as string;
                if (string.IsNullOrWhiteSpace(displayName))
                {
                    continue;
                }

                string? uninstallString = appKey.GetValue("UninstallString") as string;
                if (string.IsNullOrWhiteSpace(uninstallString))
                {
                    continue;
                }

                string publisher = (appKey.GetValue("Publisher") as string) ?? "Unknown";
                string version = (appKey.GetValue("DisplayVersion") as string) ?? "-";
                string quietUninstall = (appKey.GetValue("QuietUninstallString") as string) ?? string.Empty;
                string installLocation = (appKey.GetValue("InstallLocation") as string) ?? string.Empty;
                string displayIcon = (appKey.GetValue("DisplayIcon") as string) ?? string.Empty;

                string key = $"{displayName}|{publisher}|{version}";
                if (deduped.ContainsKey(key))
                {
                    continue;
                }

                deduped[key] = new PackageEntry
                {
                    Name = displayName.Trim(),
                    Publisher = publisher.Trim(),
                    Version = version.Trim(),
                    TypeBadge = "Win32",
                    Category = PackageCategory.Win32,
                    Identifier = key,
                    UninstallString = uninstallString.Trim(),
                    QuietUninstallString = string.IsNullOrWhiteSpace(quietUninstall) ? null : quietUninstall.Trim(),
                    InstallLocation = string.IsNullOrWhiteSpace(installLocation) ? null : installLocation.Trim(),
                    DisplayIconPath = string.IsNullOrWhiteSpace(displayIcon) ? null : displayIcon.Trim()
                };
            }
        }

        return deduped.Values.OrderBy(x => x.Name, StringComparer.OrdinalIgnoreCase);
    }

    private static IEnumerable<PackageEntry> ScanUwpApps()
    {
        const string command =
            "Get-AppxPackage -AllUsers | " +
            "Select-Object Name, Publisher, @{Name='Version';Expression={$_.Version.ToString()}}, PackageFullName | " +
            "ConvertTo-Json -Depth 4";

        string json = RunPowerShellAndGetStdOut(command);
        if (string.IsNullOrWhiteSpace(json))
        {
            return [];
        }

        List<PackageEntry> packages = new();
        foreach (JsonElement item in EnumerateJsonRows(json))
        {
            string name = GetJsonString(item, "Name");
            if (string.IsNullOrWhiteSpace(name))
            {
                continue;
            }

            string publisherRaw = GetJsonString(item, "Publisher");
            string publisher = ParsePublisher(publisherRaw);
            string version = GetJsonString(item, "Version", "-");
            string fullName = GetJsonString(item, "PackageFullName");

            packages.Add(new PackageEntry
            {
                Name = name,
                Publisher = string.IsNullOrWhiteSpace(publisher) ? "Unknown" : publisher,
                Version = string.IsNullOrWhiteSpace(version) ? "-" : version,
                TypeBadge = "UWP",
                Category = PackageCategory.Uwp,
                Identifier = fullName,
                UwpPackageName = name,
                UwpPackageFullName = fullName
            });
        }

        return packages
            .GroupBy(x => x.Identifier, StringComparer.OrdinalIgnoreCase)
            .Select(x => x.First())
            .OrderBy(x => x.Name, StringComparer.OrdinalIgnoreCase);
    }

    private static IEnumerable<PackageEntry> ScanWindowsFeatures()
    {
        const string command =
            "Get-WindowsOptionalFeature -Online | " +
            "Where-Object { $_.State -eq 'Enabled' } | " +
            "Select-Object FeatureName, State | " +
            "ConvertTo-Json -Depth 4";

        string json = RunPowerShellAndGetStdOut(command);
        if (string.IsNullOrWhiteSpace(json))
        {
            return [];
        }

        List<PackageEntry> features = new();
        foreach (JsonElement item in EnumerateJsonRows(json))
        {
            string featureName = GetJsonString(item, "FeatureName");
            if (string.IsNullOrWhiteSpace(featureName))
            {
                continue;
            }

            features.Add(new PackageEntry
            {
                Name = featureName,
                Publisher = "Microsoft",
                Version = "Enabled",
                TypeBadge = "Feature",
                Category = PackageCategory.WindowsFeature,
                Identifier = featureName,
                FeatureName = featureName
            });
        }

        return features.OrderBy(x => x.Name, StringComparer.OrdinalIgnoreCase);
    }

    private static IEnumerable<PackageEntry> ScanStartupItems()
    {
        List<PackageEntry> items = new();

        items.AddRange(ScanStartupRegistry(RegistryHive.LocalMachine, RegistryView.Registry64, @"SOFTWARE\Microsoft\Windows\CurrentVersion\Run"));
        items.AddRange(ScanStartupRegistry(RegistryHive.LocalMachine, RegistryView.Registry32, @"SOFTWARE\Microsoft\Windows\CurrentVersion\Run"));
        items.AddRange(ScanStartupRegistry(RegistryHive.CurrentUser, RegistryView.Registry64, @"SOFTWARE\Microsoft\Windows\CurrentVersion\Run"));
        items.AddRange(ScanStartupRegistry(RegistryHive.CurrentUser, RegistryView.Registry32, @"SOFTWARE\Microsoft\Windows\CurrentVersion\Run"));

        items.AddRange(ScanStartupFolder(Environment.GetFolderPath(Environment.SpecialFolder.Startup), "Startup Folder (Current User)"));
        items.AddRange(ScanStartupFolder(Environment.GetFolderPath(Environment.SpecialFolder.CommonStartup), "Startup Folder (All Users)"));

        return items
            .GroupBy(x => x.Identifier, StringComparer.OrdinalIgnoreCase)
            .Select(x => x.First())
            .OrderBy(x => x.Name, StringComparer.OrdinalIgnoreCase);
    }

    private static IEnumerable<PackageEntry> ScanStartupRegistry(RegistryHive hive, RegistryView view, string subPath)
    {
        List<PackageEntry> items = new();

        using RegistryKey baseKey = RegistryKey.OpenBaseKey(hive, view);
        using RegistryKey? runKey = baseKey.OpenSubKey(subPath);
        if (runKey is null)
        {
            return items;
        }

        string hiveName = hive == RegistryHive.LocalMachine ? "HKLM" : "HKCU";

        foreach (string valueName in runKey.GetValueNames())
        {
            object? value = runKey.GetValue(valueName);
            if (value is null)
            {
                continue;
            }

            string command = value.ToString() ?? string.Empty;
            if (string.IsNullOrWhiteSpace(command))
            {
                continue;
            }

            items.Add(new PackageEntry
            {
                Name = valueName,
                Publisher = hiveName,
                Version = "Registry",
                TypeBadge = "Startup",
                Category = PackageCategory.Startup,
                Identifier = $"{hiveName}|{subPath}|{valueName}",
                StartupRegistryHive = hiveName,
                StartupRegistryPath = subPath,
                StartupRegistryValueName = valueName
            });
        }

        return items;
    }

    private static IEnumerable<PackageEntry> ScanStartupFolder(string folderPath, string publisherLabel)
    {
        List<PackageEntry> items = new();
        if (string.IsNullOrWhiteSpace(folderPath) || !Directory.Exists(folderPath))
        {
            return items;
        }

        foreach (string shortcutFile in Directory.EnumerateFiles(folderPath, "*.lnk", SearchOption.TopDirectoryOnly))
        {
            string name = Path.GetFileNameWithoutExtension(shortcutFile);
            items.Add(new PackageEntry
            {
                Name = name,
                Publisher = publisherLabel,
                Version = "Shortcut",
                TypeBadge = "Startup",
                Category = PackageCategory.Startup,
                Identifier = $"FILE|{shortcutFile}",
                StartupFilePath = shortcutFile
            });
        }

        return items;
    }

    private static string ParsePublisher(string publisher)
    {
        if (string.IsNullOrWhiteSpace(publisher))
        {
            return "Unknown";
        }

        const string prefix = "CN=";
        int idx = publisher.IndexOf(prefix, StringComparison.OrdinalIgnoreCase);
        if (idx < 0)
        {
            return publisher;
        }

        int start = idx + prefix.Length;
        int end = publisher.IndexOf(',', start);
        return end > start ? publisher[start..end] : publisher[start..];
    }

    private static string RunPowerShellAndGetStdOut(string command)
    {
        ProcessStartInfo psi = new()
        {
            FileName = "powershell.exe",
            Arguments = $"-NoProfile -ExecutionPolicy Bypass -Command \"{command}\"",
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };

        using Process process = new() { StartInfo = psi };
        process.Start();

        string output = process.StandardOutput.ReadToEnd();
        _ = process.StandardError.ReadToEnd();

        process.WaitForExit();
        if (process.ExitCode != 0)
        {
            return string.Empty;
        }

        return output;
    }

    private static IEnumerable<JsonElement> EnumerateJsonRows(string json)
    {
        using JsonDocument doc = JsonDocument.Parse(json);

        if (doc.RootElement.ValueKind == JsonValueKind.Array)
        {
            foreach (JsonElement item in doc.RootElement.EnumerateArray())
            {
                yield return item;
            }

            yield break;
        }

        if (doc.RootElement.ValueKind == JsonValueKind.Object)
        {
            yield return doc.RootElement;
        }
    }

    private static string GetJsonString(JsonElement element, string name, string fallback = "")
    {
        if (!element.TryGetProperty(name, out JsonElement value))
        {
            return fallback;
        }

        return value.ValueKind switch
        {
            JsonValueKind.Null => fallback,
            JsonValueKind.String => value.GetString() ?? fallback,
            _ => value.ToString() ?? fallback
        };
    }
}
