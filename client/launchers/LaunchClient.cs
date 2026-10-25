// netfx WinExe 启动器：设 DOTNET_ROOT，CreateNoWindow，无黑框
using System;
using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Text;
using System.Windows.Forms;

[assembly: AssemblyTitle("RDP 资源池客户端")]
[assembly: AssemblyDescription("RDP Pool Client launcher")]
[assembly: AssemblyCompany("RDP Pool Lab")]
[assembly: AssemblyProduct("RDP Pool Client")]
[assembly: AssemblyCopyright("Copyright (c) RDP Pool Lab")]
[assembly: AssemblyVersion("1.0.0.0")]
[assembly: AssemblyFileVersion("1.0.0.0")]

internal static class LaunchClient
{
    [STAThread]
    static int Main(string[] args)
    {
        try
        {
            var root = AppDomain.CurrentDomain.BaseDirectory;
            var dotnetRoot = Path.Combine(root, "dotnet");
            var dotnet = Path.Combine(dotnetRoot, "dotnet.exe");
            var dll = Path.Combine(root, "RdpPoolClient.dll");

            if (!File.Exists(dll))
            {
                Fail("缺少 RdpPoolClient.dll，请完整解压安装包。\n" + root);
                return 90;
            }
            if (!File.Exists(dotnet))
            {
                Fail("缺少内置运行时 dotnet\\dotnet.exe，请完整解压安装包。");
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
                CreateNoWindow = true // 隐藏 dotnet.exe 控制台子系统窗口
            };
            psi.EnvironmentVariables["DOTNET_ROOT"] = dotnetRoot;
            psi.EnvironmentVariables["DOTNET_MULTILEVEL_LOOKUP"] = "0";

            using (var p = Process.Start(psi))
            {
                if (p == null)
                {
                    Fail("无法启动运行时进程。");
                    return 92;
                }
                p.WaitForExit();
                // 非 0 时主程序一般已 MessageBox；此处不重复打扰
                return p.ExitCode;
            }
        }
        catch (Exception ex)
        {
            Fail("启动失败: " + ex.Message);
            return 93;
        }
    }

    static void Fail(string msg)
    {
        MessageBox.Show(msg, "RDP 资源池客户端", MessageBoxButtons.OK, MessageBoxIcon.Error);
    }

    static string Quote(string s)
    {
        if (string.IsNullOrEmpty(s)) return "\"\"";
        if (s.IndexOfAny(new[] { ' ', '\t', '"' }) < 0) return s;
        return "\"" + s.Replace("\"", "\\\"") + "\"";
    }
}
