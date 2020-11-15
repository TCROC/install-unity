using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Security.Principal;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;

namespace sttz.InstallUnity
{

/// <summary>
/// Platform-specific installer code for macOS.
/// </summary>
public class WindowsPlatform : IInstallerPlatform
{
    /// <summary>
    /// Bundle ID of Unity editors (applies for 5+, even 2018).
    /// </summary>
    const string BUNDLE_ID = "com.unity3d.UnityEditor5.x";

    /// <summary>
    /// Volume where the packages will be installed to.
    /// </summary>
    const string INSTALL_VOLUME = "/";

    /// <summary>
    /// Default installation path.
    /// </summary>
    const string INSTALL_PATH = @"C:\Program Files\Unity";

    /// <summary>
    /// Path used to temporarily move existing installation out of the way.
    /// </summary>
    const string INSTALL_PATH_TMP = INSTALL_PATH + " (Moved by " + UnityInstaller.PRODUCT_NAME + ")";

    /// <summary>
    /// Match the mount point from hdiutil's output, e.g.:
    /// /dev/disk4s2        	Apple_HFS                      	/private/tmp/dmg.0bDM7Q
    /// </summary>
    static Regex MOUNT_POINT_REGEX = new Regex(@"^(?:\/dev\/\w+)[\t ]+(?:\w+)[\t ]+(\/.*)$", RegexOptions.Multiline);

    // -------- IInstallerPlatform --------

    string GetUserLibraryDirectory()
    {
        var home = Environment.GetFolderPath(Environment.SpecialFolder.Personal);
        return Path.Combine(home, "Library");
    }

    string GetUserApplicationSupportDirectory()
    {
        return Path.Combine(Path.Combine(GetUserLibraryDirectory(), "Application Support"), UnityInstaller.PRODUCT_NAME);
    }

    public string GetConfigurationDirectory()
    {
        return GetUserApplicationSupportDirectory();
    }

    public string GetCacheDirectory()
    {
        return GetUserApplicationSupportDirectory();
    }

    public string GetDownloadDirectory()
    {
        return Path.Combine(Path.GetTempPath(), UnityInstaller.PRODUCT_NAME);
    }

    public Task<bool> IsAdmin(CancellationToken cancellation = default)
    {
        return CheckIsRoot(false, cancellation);
    }

    public async Task<IEnumerable<Installation>> FindInstallations(CancellationToken cancellation = default)
    {
        // TODO
        return Enumerable.Empty<Installation>();
    }

    public async Task PrepareInstall(UnityInstaller.Queue queue, string installationPaths, CancellationToken cancellation = default)
    {
        if (installing.version.IsValid)
            throw new InvalidOperationException($"Already installing another version: {installing.version}");

        installing = queue.metadata;
        this.installationPaths = installationPaths;
        installedEditor = false;

        // Move existing installation out of the way
        movedExisting = false;
        if (Directory.Exists(INSTALL_PATH)) {
            if (Directory.Exists(INSTALL_PATH_TMP)) {
                throw new InvalidOperationException($"Fallback installation path '{INSTALL_PATH_TMP}' already exists.");
            }
            Logger.LogInformation("Temporarily moving existing installation at default install path: " + INSTALL_PATH);
            await Move(INSTALL_PATH, INSTALL_PATH_TMP, cancellation);
            movedExisting = true;
        }

        // Check for upgrading installation
        upgradeOriginalPath = null;
        if (!queue.items.Any(i => i.package.name == PackageMetadata.EDITOR_PACKAGE_NAME)) {
            var installs = await FindInstallations(cancellation);
            var existingInstall = installs.Where(i => i.version == queue.metadata.version).FirstOrDefault();
            if (existingInstall == null) {
                throw new InvalidOperationException($"Not installing editor but version {queue.metadata.version} not already installed.");
            }

            upgradeOriginalPath = existingInstall.path;

            Logger.LogInformation($"Temporarily moving installation to upgrade from '{existingInstall}' to default install path");
            await Move(existingInstall.path, INSTALL_PATH, cancellation);
        }
    }

    public async Task Install(UnityInstaller.Queue queue, UnityInstaller.QueueItem item, CancellationToken cancellation = default)
    {
        if (item.package.name != PackageMetadata.EDITOR_PACKAGE_NAME && !installedEditor && upgradeOriginalPath == null) {
            throw new InvalidOperationException("Cannot install package without installing editor first.");
        }

        var extentsion = Path.GetExtension(item.filePath).ToLower();
        if (extentsion == ".exe") {
            await InstallExe(item.filePath, cancellation);
        } else {
            throw new Exception("Cannot install package of type: " + extentsion);
        }

        if (item.package.name == PackageMetadata.EDITOR_PACKAGE_NAME) {
            installedEditor = true;
        }
    }

    public async Task<Installation> CompleteInstall(bool aborted, CancellationToken cancellation = default)
    {
        if (!installing.version.IsValid)
            throw new InvalidOperationException("Not installing any version to complete");

        string destination = null;
        if (upgradeOriginalPath != null) {
            // Move back installation
            destination = upgradeOriginalPath;
            Logger.LogInformation("Moving back upgraded installation to: " + destination);
            await Move(INSTALL_PATH, destination, cancellation);
        } else if (!aborted) {
            // Move new installations to "Unity VERSION"
            destination = GetUniqueInstallationPath(installing.version, installationPaths);
            Logger.LogInformation("Moving newly installed version to: " + destination);
            await Move(INSTALL_PATH, destination, cancellation);
        } else if (aborted) {
            // Clean up partial installation
            Logger.LogInformation("Deleting aborted installation at path: " + INSTALL_PATH);
            await Delete(INSTALL_PATH, cancellation);
        }

        // Move back original Unity folder
        if (movedExisting) {
            Logger.LogInformation("Moving back installation that was at default installation path");
            await Move(INSTALL_PATH_TMP, INSTALL_PATH, cancellation);
        }

        if (!aborted) {
            var executable = ExecutableFromAppPath(Path.Combine(destination, "Unity.exe"));
            if (executable == null) return default;

            var installation = new Installation() {
                version = installing.version,
                executable = executable,
                path = destination
            };

            installing = default;
            movedExisting = false;
            upgradeOriginalPath = null;

            return installation;
        } else {
            return default;
        }
    }

    public async Task MoveInstallation(Installation installation, string newPath, CancellationToken cancellation = default)
    {
        if (Directory.Exists(newPath) || File.Exists(newPath))
            throw new ArgumentException("Destination path already exists: " + newPath);

        await Move(installation.path, newPath, cancellation);
        installation.path = newPath;
    }

    public async Task Uninstall(Installation installation, CancellationToken cancellation = default)
    {
        await Delete(installation.path, cancellation);
    }

    public async Task Run(Installation installation, IEnumerable<string> arguments, bool child)
    {
        if (!child) {
            var cmd = new System.Diagnostics.Process();
            cmd.StartInfo.FileName = "cmd";
            cmd.StartInfo.Arguments = $"-a \"{installation.executable}\" -n --args {string.Join(" ", arguments)}";
            Logger.LogInformation($"$ {cmd.StartInfo.FileName} {cmd.StartInfo.Arguments}");
            
            cmd.Start();
            
            while (!cmd.HasExited) {
                await Task.Delay(100);
            }

        } else {
            if (!arguments.Contains("-logFile")) {
                arguments = arguments.Append("-logFile").Append("-");
            }

            var cmd = new System.Diagnostics.Process();
            cmd.StartInfo.FileName = installation.executable;
            cmd.StartInfo.Arguments = string.Join(" ", arguments);
            cmd.StartInfo.UseShellExecute = false;

            cmd.StartInfo.RedirectStandardOutput = true;
            cmd.StartInfo.RedirectStandardError = true;
            cmd.EnableRaisingEvents = true;

            cmd.OutputDataReceived += (s, a) => {
                if (a.Data == null) return;
                Logger.LogInformation(a.Data);
            };
            cmd.ErrorDataReceived += (s, a) => {
                if (a.Data == null) return;
                Logger.LogError(a.Data);
            };

            cmd.Start();
            cmd.BeginOutputReadLine();
            cmd.BeginErrorReadLine();

            while (!cmd.HasExited) {
                await Task.Delay(100);
            }

            cmd.WaitForExit(); // Let stdout and stderr flush
            Logger.LogInformation($"Unity exited with code {cmd.ExitCode}");
            Environment.Exit(cmd.ExitCode);
        }
    }

    // -------- Helpers --------

    ILogger Logger = UnityInstaller.CreateLogger<MacPlatform>();

    bool? isRoot;
    string pwd;
    VersionMetadata installing;
    string installationPaths;
    string upgradeOriginalPath;
    bool movedExisting;
    bool installedEditor;

    /// <summary>
    /// Get the path to the Unity executable inside the App bundle.
    /// </summary>
    string ExecutableFromAppPath(string appPath)
    {
        var executable = Path.Combine(appPath, "Contents", "MacOS", "Unity");
        if (!File.Exists(executable)) {
            Logger.LogError("Could not find Unity executable at path: " + executable);
            return null;
        }
        return executable;
    }

    /// <summary>
    /// Install an exe package using the `cmd` command.
    /// </summary>
    async Task InstallExe(string filePath, CancellationToken cancellation = default)
    {
        var result = await Command.Run("cmd", $"\"{filePath}\" /S /D=\"{INSTALL_VOLUME}\"", cancellation: cancellation);
        if (result.exitCode != 0) {
            throw new Exception($"ERROR: {result.error}");
        }
    }

    /// <summary>
    /// Find a unique path for a new installation.
    /// Tries paths in installationPaths until one is unused, falls back to adding
    /// increasing numbers to the the last path in installationPaths or using the
    /// default installation path.
    /// </summary>
    /// <param name="version">Unity version being installed</param>
    /// <param name="installationPaths">Paths string (see <see cref="Configuration.installPathMac"/></param>
    string GetUniqueInstallationPath(UnityVersion version, string installationPaths)
    {
        string expanded = null;
        if (!string.IsNullOrEmpty(installationPaths)) {
            var comparison = StringComparison.OrdinalIgnoreCase;
            var paths = installationPaths.Split(new char[] { ';' }, StringSplitOptions.RemoveEmptyEntries);
            foreach (var path in paths) {
                expanded = path.Trim();
                expanded = Helpers.Replace(expanded, "{major}", version.major.ToString(), comparison);
                expanded = Helpers.Replace(expanded, "{minor}", version.minor.ToString(), comparison);
                expanded = Helpers.Replace(expanded, "{patch}", version.patch.ToString(), comparison);
                expanded = Helpers.Replace(expanded, "{type}",  ((char)version.type).ToString(), comparison);
                expanded = Helpers.Replace(expanded, "{build}", version.build.ToString(), comparison);
                expanded = Helpers.Replace(expanded, "{hash}",  version.hash, comparison);
                
                if (!Directory.Exists(expanded)) {
                    return expanded;
                }
            }
        }

        if (expanded != null) {
            return Helpers.GenerateUniqueFileName(expanded);
        } else {
            return Helpers.GenerateUniqueFileName(INSTALL_PATH);
        }
    }

    /// <summary>
    /// Move a directory, first trying directly and falling back to `sudo mv` if that fails.
    /// </summary>
    async Task Move(string sourcePath, string newPath, CancellationToken cancellation)
    {
        var baseDst = Path.GetDirectoryName(newPath);

        try {
            if (!Directory.Exists(baseDst)) {
                Directory.CreateDirectory(baseDst);
            }
            Directory.Move(sourcePath, newPath);
            return;
        } catch (Exception e) {
            Logger.LogInformation($"Move as user failed, trying as root... ({e.Message})");
        }

        // Try again with admin privileges
        var result = await Command.Run("bash", $"mkdir -p \"{baseDst}\"", cancellation: cancellation);
        if (result.exitCode != 0) {
            throw new Exception($"ERROR: {result.error}");
        }

        result = await Command.Run("bash", $"mv \"{sourcePath}\" \"{newPath}\"", cancellation: cancellation);
        if (result.exitCode != 0) {
            throw new Exception($"ERROR: {result.error}");
        }
    }

    /// <summary>
    /// Copy a directory, first trying as the current user and using sudo if that fails.
    /// </summary>
    async Task Copy(string sourcePath, string newPath, CancellationToken cancellation)
    {
        var baseDst = Path.GetDirectoryName(newPath);

        (int exitCode, string output, string error) result;
        try {
            result = await Command.Run("bash", $"mkdir -p \"{baseDst}\"", cancellation: cancellation);
            if (result.exitCode != 0) {
                throw new Exception($"ERROR: {result.error}");
            }

            result = await Command.Run("bash", $"cp -R \"{sourcePath}\" \"{newPath}\"", cancellation: cancellation);
            if (result.exitCode != 0) {
                throw new Exception($"ERROR: {result.error}");
            }

            return;
        } catch (Exception e) {
            Logger.LogInformation($"Copy as user failed, trying as root... ({e.Message})");
        }

        // Try again with admin privileges
        result = await Command.Run("bash", $"mkdir -p \"{baseDst}\"", cancellation: cancellation);
        if (result.exitCode != 0) {
            throw new Exception($"ERROR: {result.error}");
        }

        result = await Command.Run("bash", $"mv \"{sourcePath}\" \"{newPath}\"", cancellation: cancellation);
        if (result.exitCode != 0) {
            throw new Exception($"ERROR: {result.error}");
        }
    }

    /// <summary>
    /// Delete a directory, first trying directly and falling back to `sudo rm` if that fails.
    /// </summary>
    async Task Delete(string deletePath, CancellationToken cancellation = default)
    {
        // First try deleting the installation directly
        try {
            Directory.Delete(deletePath, true);
            return;
        } catch (Exception e) {
            Logger.LogInformation($"Deleting as user failed, trying as root... ({e.Message})");
        }

        // Try again with admin privileges
        var result = await Command.Run("bash", $"rm -rf \"{deletePath}\"", cancellation: cancellation);
        if (result.exitCode != 0) {
            throw new Exception($"ERROR: {result.error}");
        }
    }

    /// <summary>
    /// Check if the program is running as root.
    /// </summary>
    Task<bool> CheckIsRoot(bool withSudo, CancellationToken cancellation)
    {
        using (WindowsIdentity identity = WindowsIdentity.GetCurrent())
        {
            WindowsPrincipal principal = new WindowsPrincipal(identity);
            if (!principal.IsInRole(WindowsBuiltInRole.Administrator))
            {
                throw new InvalidOperationException($"Must be run as administrator. Right click the terminal or console and select 'run as administrator'.");
            }
        }

        return Task.FromResult<bool>(true);
    }

    public Task<bool> PromptForPasswordIfNecessary(CancellationToken cancellation = default) => IsAdmin(cancellation);
    }

}
