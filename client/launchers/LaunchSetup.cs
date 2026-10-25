// netfx WinExe 启动器：设 DOTNET_ROOT，CreateNoWindow，无黑框
using System;
using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Text;
using System.Windows.Forms;

[assembly: AssemblyTitle("RDP 资源池配置")]
[assembly: AssemblyDescription("RDP Pool Setup launcher")]
[assembly: AssemblyCompany("RDP Pool Lab")]
[assembly: AssemblyProduct("RDP Pool Client")]
[assembly: AssemblyCopyright("Copyright (c) RDP Pool Lab")]
[assembly: AssemblyVersion("1.0.0.0")]
[assembly: AssemblyFileVersion("1.0.0.0")]

internal static class LaunchSetup
{
    [STAThread]
    static int Main(string[] args)
    {
        try
        {
            var root = AppDomain.CurrentDomain.BaseDirectory;
            var dotnetRoot = Path.Combine(root, "dotnet");
            var dotnet = Path.Combine(dotnetRoot, "dotnet.exe");
            var dll = Path.Combine(root, "RdpPoolSetup.dll");
            if (!File.Exists(dll))
            {
                MessageBox.Show("缺少 RdpPoolSetup.dll，请完整解压安装包。\n" + root,
                    "RDP 资源池", MessageBoxButtons.OK, MessageBoxIcon.Error);
                return 90;
            }
            if (!File.Exists(dotnet))
            {
                MessageBox.Show("缺少内置运行时 dotnet\\dotnet.exe，请完整解压安装包。",
                    "RDP 资源池", MessageBoxButtons.OK, MessageBoxIcon.Error);
                return 91;
            }

            var arg = new StringBuilder("exec \"");
            arg.Append(dll);
            arg.Append('"');
            foreach (var a in args)
            {
                arg.Append(' ');
                arg.Append(Quote(a));
            }

            var psi = new ProcessStartInfo
            {
                FileName = dotnet,
                Arguments = arg.ToString(),
                WorkingDirectory = root,
                UseShellExecute = false,
                CreateNoWindow = true
            };
            psi.EnvironmentVariables["DOTNET_ROOT"] = dotnetRoot;
            psi.EnvironmentVariables["DOTNET_MULTILEVEL_LOOKUP"] = "0";

            using (var p = Process.Start(psi))
            {
                if (p == null) return 92;
                p.WaitForExit();
                return p.ExitCode;
            }
        }
        catch (Exception ex)
        {
            MessageBox.Show("启动失败: " + ex.Message, "RDP 资源池",
                MessageBoxButtons.OK, MessageBoxIcon.Error);
            return 93;
        }
    }

    static string Quote(string s)
    {
        if (string.IsNullOrEmpty(s)) return "\"\"";
        if (s.IndexOfAny(new[] { ' ', '\t', '"' }) < 0) return s;
        return "\"" + s.Replace("\"", "\\\"") + "\"";
    }
}
