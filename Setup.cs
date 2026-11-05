using System;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Threading;
using System.Windows.Forms;
using Microsoft.Win32;

static class Setup
{
    const string Title = "Set as Lock and Home - Setup";
    const string PackageName = "SetDeskLock";
    const string SettingsKey = @"Software\SetDeskLock";

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
                    "Set as Lock and Home is already installed.\n\nYes - update it\nNo - remove it\nCancel - do nothing",
                    Title, MessageBoxButtons.YesNoCancel, MessageBoxIcon.Question);
                if (choice == DialogResult.No)
                    return Remove(cer);
                if (choice != DialogResult.Yes)
                    return 0;
            }
            else
            {
                DialogResult choice = MessageBox.Show(
                    "This adds \"Set as Lock and Home\" to the image context menu.\n\n" +
                    "The app is self-signed, so Windows will ask for administrator approval to trust its certificate " +
                    "on this PC. Continue only if you trust the person who sent you this file.",
                    Title, MessageBoxButtons.OKCancel, MessageBoxIcon.Information);
                if (choice != DialogResult.OK)
                    return 0;
            }
            string language = ChooseLanguage();
            if (language == null)
                return 0;
            return Install(msix, cer, language);
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

    static int Install(string msix, string cer, string language)
    {
        string thumbprint = new X509Certificate2(cer).Thumbprint;
        if (!IsTrusted(thumbprint) && !RunCertutil("-addstore TrustedPeople \"" + cer + "\""))
        {
            Show("The certificate was not trusted, so nothing was installed.", MessageBoxIcon.Warning);
            return 1;
        }

        string error;
        if (!PowerShell("Add-AppxPackage -Path '" + msix.Replace("'", "''") + "' -ForceUpdateFromAnyVersion -ForceApplicationShutdown", out error))
        {
            Show("Installation failed:\n\n" + error, MessageBoxIcon.Error);
            return 1;
        }

        using (RegistryKey settings = Registry.CurrentUser.CreateSubKey(SettingsKey))
            settings.SetValue("Language", language);

        DialogResult restart = MessageBox.Show(
            "Installed.\n\nRestart Explorer now so the menu item appears? Open Explorer windows will close. " +
            "Choose No if files are being copied, and sign out or restart later.",
            Title, MessageBoxButtons.YesNo, MessageBoxIcon.Question);
        if (restart == DialogResult.Yes)
            RestartExplorer();

        Show("Right-click an image and open App extensions to find \"Set as Lock and Home\".\n\n" +
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

        Registry.CurrentUser.DeleteSubKeyTree(SettingsKey, false);

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

    static string ChooseLanguage()
    {
        string[,] options = { { "EN", "English" }, { "UA", "Українська" }, { "RU", "Русский" } };
        string current = SavedLanguage();

        using (Form form = new Form())
        {
            form.Text = Title;
            form.FormBorderStyle = FormBorderStyle.FixedDialog;
            form.StartPosition = FormStartPosition.CenterScreen;
            form.MaximizeBox = false;
            form.MinimizeBox = false;
            form.AutoSize = true;
            form.AutoSizeMode = AutoSizeMode.GrowAndShrink;

            FlowLayoutPanel panel = new FlowLayoutPanel();
            panel.FlowDirection = FlowDirection.TopDown;
            panel.AutoSize = true;
            panel.Padding = new Padding(12);
            form.Controls.Add(panel);

            Label label = new Label();
            label.Text = "Menu language / Мова меню / Язык меню";
            label.AutoSize = true;
            panel.Controls.Add(label);

            RadioButton[] buttons = new RadioButton[options.GetLength(0)];
            for (int i = 0; i < buttons.Length; i++)
            {
                buttons[i] = new RadioButton();
                buttons[i].Text = options[i, 1];
                buttons[i].AutoSize = true;
                buttons[i].Checked = options[i, 0] == current;
                panel.Controls.Add(buttons[i]);
            }

            Button ok = new Button();
            ok.Text = "OK";
            ok.DialogResult = DialogResult.OK;
            panel.Controls.Add(ok);
            form.AcceptButton = ok;

            if (form.ShowDialog() != DialogResult.OK)
                return null;
            for (int i = 0; i < buttons.Length; i++)
            {
                if (buttons[i].Checked)
                    return options[i, 0];
            }
            return "EN";
        }
    }

    static string SavedLanguage()
    {
        using (RegistryKey settings = Registry.CurrentUser.OpenSubKey(SettingsKey))
        {
            string saved = settings == null ? null : settings.GetValue("Language") as string;
            return saved ?? "EN";
        }
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
