using System;
using System.Reflection;
using System.Runtime.Versioning;
using System.Threading;
using System.Windows;
using System.Windows.Interop;

[assembly: TargetFramework(".NETFramework,Version=v4.8", FrameworkDisplayName = ".NET Framework 4.8")]
[assembly: AssemblyTitle("Codex Token Meter")]
[assembly: AssemblyVersion("1.2.1.0")]
[assembly: AssemblyFileVersion("1.2.1.0")]

namespace CodexTokenMeter
{
    internal static class Program
    {
        [STAThread]
        private static void Main(string[] args)
        {
            bool first;
            using (var instance = new Mutex(true, @"Local\CodexTokenMeter.Desktop", out first))
            {
                if (!first) return;
                string id = SessionBinding.ParseId(args.Length == 0 ? Environment.GetEnvironmentVariable("CODEX_THREAD_ID") : args[0]);
                var app = new Application { ShutdownMode = ShutdownMode.OnMainWindowClose };
                var meter = new MeterWindow(id, false);
                app.MainWindow = meter;
                new WindowInteropHelper(meter).EnsureHandle();
                app.Run();
            }
        }
    }
}
