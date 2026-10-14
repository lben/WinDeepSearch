using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Security.Cryptography.X509Certificates;
using Microsoft.Win32;
using Windows.Management.Deployment;
using WinDeepSearch.Core;

namespace WinDeepSearch;

/// <summary>
/// Adds and removes the right-click entries. The installer runs "--register" after installing and
/// "--unregister" before uninstalling. Exit code 0: both menus; 1: only the classic "Show more options" menu.
/// </summary>
static class Registration
{
    const string PackageName = "WinDeepSearch";
    const string Publisher = "CN=WinDeepSearch";
    const string Verb = "WinDeepSearch";

    // Files, folders, the empty space inside an open folder, and drives.
    static readonly string[] ItemTypes = ["*", "Directory", @"Directory\Background", "Drive"];

    static string InstallDir => AppContext.BaseDirectory;
    static string Exe => Environment.ProcessPath!;
    static string LogFile => Path.Combine(AppPaths.Root, "registration.log");

    public static int Register()
    {
        RegisterClassicMenu();
        try
        {
            RegisterWindows11Menu();
            Log("Registered both menus.");
            return 0;
        }
        catch (Exception e)
        {
            Log("Only the classic menu was registered: " + e.Message);
            return 1;
        }
    }

    public static int Unregister()
    {
        foreach (var type in ItemTypes)
            Registry.CurrentUser.DeleteSubKeyTree($@"Software\Classes\{type}\shell\{Verb}", throwOnMissingSubKey: false);
        try
        {
            RemovePackage(new PackageManager());
            return 0;
        }
        catch (Exception e)
        {
            Log("Couldn't remove the Windows 11 menu entry: " + e.Message);
            return 1;
        }
    }

    static void RegisterClassicMenu()
    {
        foreach (var type in ItemTypes)
        {
            using var verb = Registry.CurrentUser.CreateSubKey($@"Software\Classes\{type}\shell\{Verb}");
            verb.SetValue("", "WinDeepSearch");
            verb.SetValue("Icon", $"\"{Exe}\",0");
            using var command = verb.CreateSubKey("command");
            // The background of an open folder is passed as %V; everything else as %1.
            command.SetValue("", $"\"{Exe}\" --path \"{(type == @"Directory\Background" ? "%V" : "%1")}\"");
        }
    }

    /// <summary>
    /// The Windows 11 menu only lists entries from packaged apps, so a small "sparse" package that points at
    /// the install folder declares the menu command (WinDeepSearch.ShellExt.dll). Windows accepts the package
    /// only if its signing certificate is trusted, which needs one administrator prompt.
    /// </summary>
    static void RegisterWindows11Menu()
    {
        string package = Path.Combine(InstallDir, "WinDeepSearch.msix");
        string certificate = Path.Combine(InstallDir, "WinDeepSearch.cer");
        if (!File.Exists(package) || !File.Exists(certificate)) throw new FileNotFoundException("The menu package is missing from the install folder.");

        if (!IsTrusted(certificate)) Trust(certificate);

        var manager = new PackageManager();
        RemovePackage(manager);
        var options = new AddPackageOptions { ExternalLocationUri = new Uri(InstallDir) };
        var result = manager.AddPackageByUriAsync(new Uri(package), options).AsTask().GetAwaiter().GetResult();
        if (!result.IsRegistered) throw new InvalidOperationException(result.ErrorText);
    }

    static void RemovePackage(PackageManager manager)
    {
        foreach (var package in manager.FindPackagesForUser("", PackageName, Publisher))
            manager.RemovePackageAsync(package.Id.FullName).AsTask().GetAwaiter().GetResult();
    }

    static bool IsTrusted(string certificate)
    {
        using var cert = X509CertificateLoader.LoadCertificateFromFile(certificate);
        using var store = new X509Store(StoreName.TrustedPeople, StoreLocation.LocalMachine);
        store.Open(OpenFlags.ReadOnly);
        return store.Certificates.Find(X509FindType.FindByThumbprint, cert.Thumbprint, validOnly: false).Count > 0;
    }

    static void Trust(string certificate)
    {
        try
        {
            using var process = Process.Start(new ProcessStartInfo("certutil.exe", $"-f -addstore TrustedPeople \"{certificate}\"")
            {
                UseShellExecute = true,
                Verb = "runas",
                WindowStyle = ProcessWindowStyle.Hidden,
            })!;
            process.WaitForExit();
            if (process.ExitCode != 0) throw new InvalidOperationException($"certutil failed with exit code {process.ExitCode}.");
        }
        catch (Win32Exception e)
        {
            throw new InvalidOperationException("The administrator prompt to trust the certificate was declined.", e);
        }
    }

    static void Log(string message)
    {
        try
        {
            Directory.CreateDirectory(AppPaths.Root);
            File.WriteAllText(LogFile, $"{DateTime.Now:u} {message}{Environment.NewLine}");
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            // Logging is only for troubleshooting.
        }
    }
}
