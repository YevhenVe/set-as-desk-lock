using System;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Threading;
using System.Windows.Forms;

static class Setup
{
    const string Title = "Set as Desktop & Lock - Setup";
    const string PackageName = "SetDeskLock";

    [STAThread]
    static int Main()
    {
        string temp = Path.Combine(Path.GetTempPath(), "SetDeskLockSetup-" + Guid.NewGuid().ToString("N"));
        try
        {
            Directory.CreateDirectory(temp);
            string msix = Extract("SetDeskLock.msix", temp);
            string cer = Extract("SetDeskLock.cer", temp);

            if (IsInstalled())
            {
                DialogResult choice = MessageBox.Show(
                    "Set as Desktop & Lock is already installed.\n\nYes - update it\nNo - remove it\nCancel - do nothing",
                    Title, MessageBoxButtons.YesNoCancel, MessageBoxIcon.Question);
                if (choice == DialogResult.No)
                    return Remove(cer);
                if (choice != DialogResult.Yes)
                    return 0;
            }
            else
            {
                DialogResult choice = MessageBox.Show(
                    "This adds \"Set as Desktop & Lock\" to the image context menu.\n\n" +
                    "The app is self-signed, so Windows will ask for administrator approval to trust its certificate " +
                    "on this PC. Continue only if you trust the person who sent you this file.",
                    Title, MessageBoxButtons.OKCancel, MessageBoxIcon.Information);
                if (choice != DialogResult.OK)
                    return 0;
            }
            return Install(msix, cer);
        }
        catch (Exception e)
        {
            Show(e.Message, MessageBoxIcon.Error);
            return 1;
        }
        finally
        {
            try { Directory.Delete(temp, true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
        }
    }

    static int Install(string msix, string cer)
    {
        string thumbprint = new X509Certificate2(cer).Thumbprint;
        if (!IsTrusted(thumbprint) && !RunCertutil("-addstore TrustedPeople \"" + cer + "\""))
        {
            Show("The certificate was not trusted, so nothing was installed.", MessageBoxIcon.Warning);
            return 1;
        }

        string error;
        if (!PowerShell("Add-AppxPackage -LiteralPath '" + msix.Replace("'", "''") + "' -ForceUpdateFromAnyVersion -ForceApplicationShutdown", out error))
        {
            Show("Installation failed:\n\n" + error, MessageBoxIcon.Error);
            return 1;
        }

        DialogResult restart = MessageBox.Show(
            "Installed.\n\nRestart Explorer now so the menu item appears? Open Explorer windows will close. " +
            "Choose No if files are being copied, and sign out or restart later.",
            Title, MessageBoxButtons.YesNo, MessageBoxIcon.Question);
        if (restart == DialogResult.Yes)
            RestartExplorer();

        Show("Right-click an image and open App extensions to find \"Set as Desktop & Lock\".\n\n" +
             "To move it to the main menu, use Customize menu.", MessageBoxIcon.Information);
        return 0;
    }

    static int Remove(string cer)
    {
        string error;
        if (!PowerShell("Get-AppxPackage -Name " + PackageName + " | Remove-AppxPackage", out error))
        {
            Show("Removal failed:\n\n" + error, MessageBoxIcon.Error);
            return 1;
        }

        string thumbprint = new X509Certificate2(cer).Thumbprint;
        if (IsTrusted(thumbprint) && !RunCertutil("-delstore TrustedPeople " + thumbprint))
        {
            Show("The app was removed, but its certificate is still trusted because the administrator request was declined. " +
                 "Run setup again to remove it.", MessageBoxIcon.Warning);
            return 1;
        }

        Show("Removed.", MessageBoxIcon.Information);
        return 0;
    }

    static string Extract(string name, string directory)
    {
        string path = Path.Combine(directory, name);
        using (Stream source = Assembly.GetExecutingAssembly().GetManifestResourceStream(name))
        using (FileStream target = File.Create(path))
            source.CopyTo(target);
        return path;
    }

    static bool IsInstalled()
    {
        string output;
        return PowerShell("if (-not (Get-AppxPackage -Name " + PackageName + ")) { throw 'Not installed' }", out output);
    }

    static bool IsTrusted(string thumbprint)
    {
        X509Store store = new X509Store(StoreName.TrustedPeople, StoreLocation.LocalMachine);
        store.Open(OpenFlags.ReadOnly);
        try
        {
            return store.Certificates.Find(X509FindType.FindByThumbprint, thumbprint, false).Count > 0;
        }
        finally
        {
            store.Close();
        }
    }

    static bool RunCertutil(string arguments)
    {
        ProcessStartInfo info = new ProcessStartInfo(Path.Combine(Environment.SystemDirectory, "certutil.exe"), arguments);
        info.UseShellExecute = true;
        info.Verb = "runas";
        info.WindowStyle = ProcessWindowStyle.Hidden;
        try
        {
            using (Process process = Process.Start(info))
            {
                process.WaitForExit();
                return process.ExitCode == 0;
            }
        }
        catch (Win32Exception)
        {
            return false;
        }
    }

    static bool PowerShell(string command, out string output)
    {
        string script = "$ProgressPreference='SilentlyContinue';$ErrorActionPreference='Stop';" +
            "[Console]::OutputEncoding=[Text.Encoding]::UTF8;" +
            "try { " + command + "; exit 0 } catch { Write-Output $_.Exception.Message; exit 1 }";
        ProcessStartInfo info = new ProcessStartInfo(
            Path.Combine(Environment.SystemDirectory, @"WindowsPowerShell\v1.0\powershell.exe"),
            "-NoProfile -NonInteractive -EncodedCommand " + Convert.ToBase64String(Encoding.Unicode.GetBytes(script)));
        info.UseShellExecute = false;
        info.CreateNoWindow = true;
        info.RedirectStandardOutput = true;
        info.RedirectStandardError = true;
        info.StandardOutputEncoding = Encoding.UTF8;
        using (Process process = Process.Start(info))
        {
            process.ErrorDataReceived += delegate { };
            process.BeginErrorReadLine();
            output = process.StandardOutput.ReadToEnd().Trim();
            process.WaitForExit();
            return process.ExitCode == 0;
        }
    }

    static void RestartExplorer()
    {
        foreach (Process process in Process.GetProcessesByName("explorer"))
        {
            try { process.Kill(); } catch (Win32Exception) { } catch (InvalidOperationException) { }
        }
        Thread.Sleep(2000);
        if (Process.GetProcessesByName("explorer").Length == 0)
            Process.Start("explorer.exe");
    }

    static void Show(string message, MessageBoxIcon icon)
    {
        MessageBox.Show(message, Title, MessageBoxButtons.OK, icon);
    }
}
