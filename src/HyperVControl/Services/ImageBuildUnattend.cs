using System.Text;
using System.Xml;

namespace HyperVControl.Services;

/// <summary>Values written into a Windows image's <c>Windows\Panther\unattend.xml</c>.</summary>
internal sealed record UnattendSettings(string ComputerName, string AdminUser, string AdminPassword, bool EnableRdp, bool AutoLogon,
    bool PreventDeviceEncryption, string InputLocale, string SystemLocale, string UiLanguage, string UserLocale)
{
    public override string ToString() => $"UnattendSettings {{ ComputerName = {ComputerName}, AdminUser = {AdminUser}, AdminPassword = <redacted> }}";
}

/// <summary>
/// Builds the answer file for an offline-applied image. Only the specialize and oobeSystem passes run (Setup's
/// windowsPE pass never does): computer name, RDP and its firewall group, device-encryption prevention, BypassNRO,
/// locales, hidden OOBE pages, one local administrator, autologon, and first-logon commands that turn off the
/// Windows Hello-only sign-in, stop password expiry and delete the answer file (it holds the password).
/// An <see cref="XmlWriter"/> escapes every value, so passwords and names are always data.
/// </summary>
internal static class ImageBuildUnattend
{
    internal const string Namespace = "urn:schemas-microsoft-com:unattend";
    internal const string WcmNamespace = "http://schemas.microsoft.com/WMIConfig/2002/State";
    internal const string RemoteDesktopFirewallGroup = "@FirewallAPI.dll,-28752";

    private sealed class Utf8StringWriter : StringWriter
    {
        public override Encoding Encoding => new UTF8Encoding(false);
    }

    public static string Build(UnattendSettings s)
    {
        using var text = new Utf8StringWriter();
        using (var w = XmlWriter.Create(text, new XmlWriterSettings { Indent = true, IndentChars = "  ", NewLineChars = "\r\n", Encoding = new UTF8Encoding(false) }))
        {
            w.WriteStartDocument();
            w.WriteStartElement("unattend", Namespace);
            w.WriteAttributeString("xmlns", "wcm", null, WcmNamespace);

            w.WriteStartElement("settings", Namespace);
            w.WriteAttributeString("pass", "specialize");
            Component(w, "Microsoft-Windows-Shell-Setup", () => w.WriteElementString("ComputerName", Namespace, s.ComputerName));
            if (s.EnableRdp)
            {
                Component(w, "Microsoft-Windows-TerminalServices-LocalSessionManager", () => w.WriteElementString("fDenyTSConnections", Namespace, "false"));
                Component(w, "Networking-MPSSVC-Svc", () =>
                {
                    w.WriteStartElement("FirewallGroups", Namespace);
                    w.WriteStartElement("FirewallGroup", Namespace);
                    w.WriteAttributeString("action", WcmNamespace, "add");
                    w.WriteAttributeString("keyValue", WcmNamespace, "RemoteDesktop");
                    w.WriteElementString("Active", Namespace, "true");
                    w.WriteElementString("Group", Namespace, RemoteDesktopFirewallGroup);
                    w.WriteElementString("Profile", Namespace, "all");
                    w.WriteEndElement();
                    w.WriteEndElement();
                });
            }
            var specializeCommands = new List<string>();
            if (s.PreventDeviceEncryption)
                specializeCommands.Add("reg.exe add \"HKLM\\SYSTEM\\CurrentControlSet\\Control\\BitLocker\" /v PreventDeviceEncryption /t REG_DWORD /d 1 /f");
            // Best effort for the network page; the local account below is the supported way past OOBE.
            specializeCommands.Add("reg.exe add \"HKLM\\SOFTWARE\\Microsoft\\Windows\\CurrentVersion\\OOBE\" /v BypassNRO /t REG_DWORD /d 1 /f");
            Component(w, "Microsoft-Windows-Deployment", () =>
            {
                w.WriteStartElement("RunSynchronous", Namespace);
                for (var i = 0; i < specializeCommands.Count; i++)
                {
                    w.WriteStartElement("RunSynchronousCommand", Namespace);
                    w.WriteAttributeString("action", WcmNamespace, "add");
                    w.WriteElementString("Order", Namespace, (i + 1).ToString(System.Globalization.CultureInfo.InvariantCulture));
                    w.WriteElementString("Path", Namespace, specializeCommands[i]);
                    w.WriteEndElement();
                }
                w.WriteEndElement();
            });
            w.WriteEndElement();

            w.WriteStartElement("settings", Namespace);
            w.WriteAttributeString("pass", "oobeSystem");
            Component(w, "Microsoft-Windows-International-Core", () =>
            {
                w.WriteElementString("InputLocale", Namespace, s.InputLocale);
                w.WriteElementString("SystemLocale", Namespace, s.SystemLocale);
                w.WriteElementString("UILanguage", Namespace, s.UiLanguage);
                w.WriteElementString("UserLocale", Namespace, s.UserLocale);
            });
            Component(w, "Microsoft-Windows-Shell-Setup", () =>
            {
                w.WriteStartElement("OOBE", Namespace);
                w.WriteElementString("HideEULAPage", Namespace, "true");
                w.WriteElementString("HideOnlineAccountScreens", Namespace, "true");
                w.WriteElementString("HideWirelessSetupInOOBE", Namespace, "true");
                w.WriteElementString("ProtectYourPC", Namespace, "3");
                w.WriteEndElement();

                w.WriteStartElement("UserAccounts", Namespace);
                w.WriteStartElement("LocalAccounts", Namespace);
                w.WriteStartElement("LocalAccount", Namespace);
                w.WriteAttributeString("action", WcmNamespace, "add");
                w.WriteElementString("Name", Namespace, s.AdminUser);
                w.WriteElementString("Group", Namespace, "Administrators");
                Password(w, s.AdminPassword);
                w.WriteEndElement();
                w.WriteEndElement();
                w.WriteEndElement();

                if (s.AutoLogon)
                {
                    w.WriteStartElement("AutoLogon", Namespace);
                    w.WriteElementString("Enabled", Namespace, "true");
                    w.WriteElementString("Username", Namespace, s.AdminUser);
                    w.WriteElementString("LogonCount", Namespace, "9999999");
                    Password(w, s.AdminPassword);
                    w.WriteEndElement();
                }

                string[] firstLogon =
                [
                    // Turn off "only allow Windows Hello sign-in" so Enhanced Session (RDP) shows a password prompt.
                    "reg.exe add \"HKLM\\SOFTWARE\\Microsoft\\Windows NT\\CurrentVersion\\PasswordLess\\Device\" /v DevicePasswordLessBuildVersion /t REG_DWORD /d 0 /f",
                    "net.exe accounts /maxpwage:unlimited",
                    // The answer file holds the password: delete it once the first sign-in has consumed it.
                    "cmd.exe /c del /q %WINDIR%\\Panther\\unattend.xml"
                ];
                w.WriteStartElement("FirstLogonCommands", Namespace);
                for (var i = 0; i < firstLogon.Length; i++)
                {
                    w.WriteStartElement("SynchronousCommand", Namespace);
                    w.WriteAttributeString("action", WcmNamespace, "add");
                    w.WriteElementString("Order", Namespace, (i + 1).ToString(System.Globalization.CultureInfo.InvariantCulture));
                    w.WriteElementString("CommandLine", Namespace, firstLogon[i]);
                    w.WriteEndElement();
                }
                w.WriteEndElement();
            });
            w.WriteEndElement();

            w.WriteEndElement();
            w.WriteEndDocument();
        }
        return text.ToString() + "\r\n";
    }

    /// <summary>Writes the answer file as UTF-8 with a byte order mark, matching Windows System Image Manager output.</summary>
    public static void Write(string path, UnattendSettings settings)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, Build(settings), new UTF8Encoding(true));
    }

    private static void Component(XmlWriter w, string name, Action body)
    {
        w.WriteStartElement("component", Namespace);
        w.WriteAttributeString("name", name);
        w.WriteAttributeString("processorArchitecture", "amd64");
        w.WriteAttributeString("publicKeyToken", "31bf3856ad364e35");
        w.WriteAttributeString("language", "neutral");
        w.WriteAttributeString("versionScope", "nonSxS");
        body();
        w.WriteEndElement();
    }

    private static void Password(XmlWriter w, string password)
    {
        w.WriteStartElement("Password", Namespace);
        w.WriteElementString("Value", Namespace, password);
        w.WriteElementString("PlainText", Namespace, "true");
        w.WriteEndElement();
    }
}
