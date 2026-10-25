// netfx 薄启动器：设置 DOTNET_ROOT 后用包内 dotnet.exe exec RdpPoolClient.dll
// 由 pack-client-win.ps1 用 Framework csc 编译为 RdpPoolClient.exe
using System;
using System.Diagnostics;
using System.IO;
using System.Text;

internal static class LaunchClient
{
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
                Console.Error.WriteLine("缺少 RdpPoolClient.dll，请完整解压安装包。");
                Console.Error.WriteLine("目录: " + root);
                Pause();
                return 90;
            }
            if (!File.Exists(dotnet))
            {
                Console.Error.WriteLine("缺少内置运行时 dotnet\\dotnet.exe，请完整解压安装包。");
                Pause();
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
                UseShellExecute = false
            };
            psi.EnvironmentVariables["DOTNET_ROOT"] = dotnetRoot;
            psi.EnvironmentVariables["DOTNET_MULTILEVEL_LOOKUP"] = "0";

            using (var p = Process.Start(psi))
            {
                if (p == null) return 92;
                p.WaitForExit();
                var code = p.ExitCode;
                if (code != 0) Pause();
                return code;
            }
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine("启动失败: " + ex.Message);
            Pause();
            return 93;
        }
    }

    static void Pause()
    {
        try
        {
            Console.WriteLine("按 Enter 退出...");
            Console.ReadLine();
        }
        catch { }
    }

    static string Quote(string s)
    {
        if (string.IsNullOrEmpty(s)) return "\"\"";
        if (s.IndexOfAny(new[] { ' ', '\t', '"' }) < 0) return s;
        return "\"" + s.Replace("\"", "\\\"") + "\"";
    }
}
