using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Automation;

namespace CodexTokenMeter
{
    internal sealed class MeterSettings
    {
        internal string SessionId;
        internal double OffsetX, OffsetY;
        internal bool Follow = true;
        private readonly string path;

        internal MeterSettings(string file = null)
        {
            path = file ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "CodexTokenMeter", "placement.txt");
            try
            {
                if (!File.Exists(path)) return;
                var lines = File.ReadAllLines(path);
                if (lines.Length < 2) return;
                OffsetX = ReadOffset(lines[0]);
                OffsetY = ReadOffset(lines[1]);
                if (lines.Length >= 3) SessionId = SessionBinding.ParseId(lines[2]);
                if (lines.Length >= 4) Follow = lines[3] != "manual";
            }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }

        private static double ReadOffset(string value)
        {
            double number;
            return Double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out number) && Math.Abs(number) <= 20000 ? number : 0;
        }

        internal void Save()
        {
            string draft = path + ".tmp";
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(path));
                File.WriteAllLines(draft, new[] { OffsetX.ToString(CultureInfo.InvariantCulture), OffsetY.ToString(CultureInfo.InvariantCulture), SessionId ?? "", Follow ? "auto" : "manual" });
                if (File.Exists(path)) File.Replace(draft, path, null);
                else File.Move(draft, path);
            }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
    }

    internal sealed class HostView
    {
        internal string Title, Header, Route, SelectedModel;
        internal Rect Composer = Rect.Empty;
    }

    internal static class Desktop
    {
        internal const uint PositionWithoutActivation = 0x0010 | 0x0001 | 0x0004;
        private static readonly Dictionary<int, bool> CodexProcesses = new Dictionary<int, bool>();

        [StructLayout(LayoutKind.Sequential)]
        internal struct Point { internal int X, Y; }

        [StructLayout(LayoutKind.Sequential)]
        internal struct Box
        {
            internal int Left, Top, Right, Bottom;
            internal int Width { get { return Right - Left; } }
            internal int Height { get { return Bottom - Top; } }
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct MonitorInfo
        {
            internal int Size;
            internal Box Area, Work;
            internal uint Flags;
        }

        [DllImport("user32.dll")] internal static extern IntPtr GetForegroundWindow();
        [DllImport("user32.dll")] private static extern IntPtr GetAncestor(IntPtr window, uint flags);
        [DllImport("user32.dll")] internal static extern bool GetWindowRect(IntPtr window, out Box box);
        [DllImport("user32.dll")] internal static extern bool GetCursorPos(out Point point);
        [DllImport("user32.dll")] internal static extern bool IsIconic(IntPtr window);
        [DllImport("user32.dll")] internal static extern bool IsWindow(IntPtr window);
        [DllImport("user32.dll")] internal static extern uint GetDpiForWindow(IntPtr window);
        [DllImport("user32.dll")] internal static extern bool SetWindowPos(IntPtr window, IntPtr after, int x, int y, int width, int height, uint flags);
        [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(IntPtr window, out int id);
        [DllImport("user32.dll")] private static extern IntPtr MonitorFromWindow(IntPtr window, uint flags);
        [DllImport("user32.dll")] private static extern bool GetMonitorInfo(IntPtr monitor, ref MonitorInfo info);
        [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int GetClassName(IntPtr window, System.Text.StringBuilder name, int capacity);

        internal static IntPtr RootOwner(IntPtr window)
        {
            const uint rootOwner = 3;
            return GetAncestor(window, rootOwner);
        }

        internal static bool SameProcess(IntPtr first, IntPtr second)
        {
            int firstId, secondId;
            return GetWindowThreadProcessId(first, out firstId) != 0
                && GetWindowThreadProcessId(second, out secondId) != 0
                && firstId == secondId;
        }

        internal static double Scale(IntPtr window)
        {
            try { return Math.Max(96, GetDpiForWindow(window)) / 96.0; }
            catch (EntryPointNotFoundException) { return 1; }
        }

        internal static Box WorkArea(IntPtr window)
        {
            var info = new MonitorInfo { Size = Marshal.SizeOf(typeof(MonitorInfo)) };
            if (GetMonitorInfo(MonitorFromWindow(window, 2), ref info)) return info.Work;
            Box rect;
            GetWindowRect(window, out rect);
            return rect;
        }

        internal static bool IsCodex(IntPtr window)
        {
            if (window == IntPtr.Zero) return false;
            int pid;
            if (GetWindowThreadProcessId(window, out pid) == 0) return false;
            bool found;
            if (CodexProcesses.TryGetValue(pid, out found)) return found && HasCodexWindowClass(window);

            try
            {
                using (var process = Process.GetProcessById(pid))
                {
                    string name = process.ProcessName;
                    if (name.Equals("ChatGPT", StringComparison.OrdinalIgnoreCase) || name.Equals("Codex", StringComparison.OrdinalIgnoreCase))
                    {
                        string executable = process.MainModule.FileName;
                        found = executable.IndexOf("\\WindowsApps\\OpenAI.Codex_", StringComparison.OrdinalIgnoreCase) >= 0
                            || executable.IndexOf("\\WindowsApps\\OpenAI.ChatGPT_", StringComparison.OrdinalIgnoreCase) >= 0
                            || name.Equals("Codex", StringComparison.OrdinalIgnoreCase);
                    }
                }
            }
            catch (ArgumentException) { }
            catch (System.ComponentModel.Win32Exception) { }
            catch (InvalidOperationException) { }

            if (CodexProcesses.Count >= 64) CodexProcesses.Clear();
            CodexProcesses[pid] = found;
            return found && HasCodexWindowClass(window);
        }

        private static bool HasCodexWindowClass(IntPtr window)
        {
            var windowClass = new System.Text.StringBuilder(128);
            GetClassName(window, windowClass, windowClass.Capacity);
            return windowClass.ToString().StartsWith("Chrome_WidgetWin_", StringComparison.Ordinal);
        }

        internal static HostView Inspect(IntPtr host)
        {
            try
            {
                Box window;
                if (!GetWindowRect(host, out window)) return null;
                var root = AutomationElement.FromHandle(host);
                var edit = new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.Edit);
                var doc = new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.Document);
                var button = new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.Button);
                var cache = new CacheRequest();
                cache.AutomationElementMode = AutomationElementMode.None;
                cache.Add(AutomationElement.ControlTypeProperty);
                cache.Add(AutomationElement.NameProperty);
                cache.Add(AutomationElement.ClassNameProperty);
                cache.Add(AutomationElement.BoundingRectangleProperty);
                cache.Add(AutomationElement.IsOffscreenProperty);
                cache.Add(AutomationElement.HasKeyboardFocusProperty);
                cache.Add(ValuePattern.Pattern);
                cache.Add(ValuePattern.ValueProperty);
                AutomationElementCollection fields;
                using (cache.Activate()) fields = root.FindAll(TreeScope.Descendants, new OrCondition(edit, doc, button));

                var view = new HostView();
                double documentArea = 0;
                var buttons = new List<AutomationElement.AutomationElementInformation>();
                foreach (AutomationElement field in fields)
                {
                    var current = field.Cached;
                    Rect bounds = current.BoundingRectangle;
                    if (current.IsOffscreen || bounds.IsEmpty) continue;
                    if (current.ControlType == ControlType.Document && bounds.Width > window.Width * .55 && bounds.Height > window.Height * .55 && bounds.Width * bounds.Height > documentArea)
                    {
                        documentArea = bounds.Width * bounds.Height;
                        view.Title = current.Name;
                        object route;
                        view.Route = field.TryGetCachedPattern(ValuePattern.Pattern, out route) ? ((ValuePattern)route).Cached.Value : null;
                    }
                    if (current.ControlType == ControlType.Button)
                    {
                        if ((current.ClassName ?? "").Contains("text-conversation-title") && bounds.Top < window.Top + 150 * Scale(host)) view.Header = current.Name;
                        buttons.Add(current);
                        continue;
                    }
                    if (bounds.Width < 180 || bounds.Height < 16 || bounds.Height > 360) continue;
                    if (bounds.Top < window.Top + window.Height * .58 || bounds.Right > window.Right + 4 || bounds.Bottom > window.Bottom + 4) continue;
                    string name = current.Name ?? "";
                    bool composer = name.IndexOf("message", StringComparison.OrdinalIgnoreCase) >= 0 || name.IndexOf("ask", StringComparison.OrdinalIgnoreCase) >= 0
                        || name.Contains("消息") || name.Contains("输入") || (current.ClassName ?? "").IndexOf("ProseMirror", StringComparison.OrdinalIgnoreCase) >= 0;
                    if (!composer && !(current.ControlType == ControlType.Edit && current.HasKeyboardFocus)) continue;
                    if (view.Composer.IsEmpty || bounds.Bottom > view.Composer.Bottom) view.Composer = bounds;
                }
                if (documentArea == 0 && view.Header == null) return null;
                if (!view.Composer.IsEmpty)
                {
                    foreach (var control in buttons)
                    {
                        Rect bounds = control.BoundingRectangle;
                        if (bounds.Top < view.Composer.Top || bounds.Top > view.Composer.Bottom + 75 * Scale(host) || bounds.Left < view.Composer.Left || bounds.Right > view.Composer.Right + 12) continue;
                        string model = ModelLabels.FromPicker(control.Name);
                        if (model != null) { view.SelectedModel = model; break; }
                    }
                }
                return view;
            }
            catch (ElementNotAvailableException) { return null; }
            catch (InvalidOperationException) { return null; }
            catch (ArgumentException) { return null; }
            catch (COMException) { return null; }
        }
    }
}
