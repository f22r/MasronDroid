using System.Windows;
using Velopack;

namespace MasronDroid;

public partial class App : Application { }

internal static class Program
{
    [STAThread]
    public static void Main()
    {
        VelopackApp.Build().OnFirstRun(_ => InstallAndroidDrivers()).Run();
        var app = new App();
        app.InitializeComponent();
        app.Run();
    }
    private static void InstallAndroidDrivers()
    {
        const string script = """
            $ErrorActionPreference = 'Stop'
            & "$env:SystemRoot/System32/pnputil.exe" /scan-devices
            $session = New-Object -ComObject Microsoft.Update.Session
            $found = $session.CreateUpdateSearcher().Search("IsInstalled=0 and Type='Driver' and IsHidden=0")
            $updates = New-Object -ComObject Microsoft.Update.UpdateColl
            foreach ($driver in $found.Updates) {
                if ($driver.DriverModel -match '(?i)\b(ADB|Fastboot|Android Bootloader)\b' -and !$driver.InstallationBehavior.CanRequestUserInput) {
                    if (!$driver.EulaAccepted) { continue }
                    [void]$updates.Add($driver)
                }
            }
            if ($updates.Count) {
                $download = $session.CreateUpdateDownloader()
                $download.Updates = $updates
                [void]$download.Download()
                $install = $session.CreateUpdateInstaller()
                $install.Updates = $updates
                [void]$install.Install()
            }
            """;
        var encoded = Convert.ToBase64String(System.Text.Encoding.Unicode.GetBytes(script));
        try
        {
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
            {
                FileName = System.IO.Path.Combine(Environment.SystemDirectory, "WindowsPowerShell", "v1.0", "powershell.exe"),
                Arguments = "-NoProfile -NonInteractive -EncodedCommand " + encoded,
                UseShellExecute = true, Verb = "runas", WindowStyle = System.Diagnostics.ProcessWindowStyle.Hidden
            })?.Dispose();
        }
        catch (System.ComponentModel.Win32Exception ex)
        {
            System.Diagnostics.Trace.WriteLine(ex.Message);
        }
    }
}
