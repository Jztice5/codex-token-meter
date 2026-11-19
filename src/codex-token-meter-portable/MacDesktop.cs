using System.Runtime.InteropServices;
using System.Text;

namespace CodexTokenMeter;

internal readonly record struct DesktopRect(double X, double Y, double Width, double Height)
{
    internal double Right => X + Width;
    internal double Bottom => Y + Height;
    internal bool Valid => Width > 0 && Height > 0;
}

internal sealed class MacView
{
    internal string Title, Header, Route, Model;
    internal DesktopRect Window, Composer;
}

internal static class MacDesktop
{
    private const string Core = "/System/Library/Frameworks/CoreFoundation.framework/CoreFoundation";
    private const string ApplicationServices = "/System/Library/Frameworks/ApplicationServices.framework/ApplicationServices";
    private const string ObjC = "/usr/lib/libobjc.A.dylib";
    private const uint Utf8 = 0x08000100;
    private static readonly Dictionary<string, IntPtr> Attributes = new();

    [StructLayout(LayoutKind.Sequential)]
    private struct Pair { internal double First, Second; }

    [DllImport(ObjC)] private static extern IntPtr objc_getClass(string name);
    [DllImport(ObjC)] private static extern IntPtr sel_registerName(string name);
    [DllImport(ObjC, EntryPoint = "objc_msgSend")] private static extern IntPtr Send(IntPtr target, IntPtr selector);
    [DllImport(ObjC, EntryPoint = "objc_msgSend")] private static extern int SendInt(IntPtr target, IntPtr selector);
    [DllImport(ApplicationServices)] [return: MarshalAs(UnmanagedType.I1)] internal static extern bool AXIsProcessTrusted();
    [DllImport(ApplicationServices)] [return: MarshalAs(UnmanagedType.I1)] private static extern bool AXIsProcessTrustedWithOptions(IntPtr options);
    [DllImport(ApplicationServices)] private static extern IntPtr AXUIElementCreateApplication(int pid);
    [DllImport(ApplicationServices)] private static extern int AXUIElementSetMessagingTimeout(IntPtr element, float timeout);
    [DllImport(ApplicationServices)] private static extern int AXUIElementCopyAttributeValue(IntPtr element, IntPtr attribute, out IntPtr value);
    [DllImport(ApplicationServices)] private static extern nuint AXValueGetTypeID();
    [DllImport(ApplicationServices)] [return: MarshalAs(UnmanagedType.I1)] private static extern bool AXValueGetValue(IntPtr value, int type, out Pair pair);
    [DllImport(Core)] private static extern IntPtr CFStringCreateWithCString(IntPtr allocator, [MarshalAs(UnmanagedType.LPUTF8Str)] string value, uint encoding);
    [DllImport(Core)] private static extern nuint CFGetTypeID(IntPtr value);
    [DllImport(Core)] private static extern nuint CFStringGetTypeID();
    [DllImport(Core)] private static extern nuint CFArrayGetTypeID();
    [DllImport(Core)] private static extern nuint CFURLGetTypeID();
    [DllImport(Core)] private static extern IntPtr CFURLGetString(IntPtr url);
    [DllImport(Core)] private static extern nint CFArrayGetCount(IntPtr array);
    [DllImport(Core)] private static extern IntPtr CFArrayGetValueAtIndex(IntPtr array, nint index);
    [DllImport(Core)] private static extern IntPtr CFRetain(IntPtr value);
    [DllImport(Core)] private static extern void CFRelease(IntPtr value);
    [DllImport(Core)] [return: MarshalAs(UnmanagedType.I1)] private static extern bool CFStringGetCString(IntPtr value, byte[] buffer, nint size, uint encoding);
    [DllImport(Core)] private static extern IntPtr CFDictionaryCreate(IntPtr allocator, IntPtr[] keys, IntPtr[] values, nint count, IntPtr keyCallbacks, IntPtr valueCallbacks);

    internal static (int Pid, bool Codex) Frontmost()
    {
        if (!OperatingSystem.IsMacOS()) return (0, false);
        IntPtr workspace = Send(objc_getClass("NSWorkspace"), sel_registerName("sharedWorkspace"));
        IntPtr app = Send(workspace, sel_registerName("frontmostApplication"));
        if (app == IntPtr.Zero) return (0, false);
        int pid = SendInt(app, sel_registerName("processIdentifier"));
        string name = NSString(Send(app, sel_registerName("localizedName"))) ?? "";
        string id = NSString(Send(app, sel_registerName("bundleIdentifier"))) ?? "";
        bool named = name.Equals("Codex", StringComparison.OrdinalIgnoreCase) || name.Equals("ChatGPT", StringComparison.OrdinalIgnoreCase)
            || (name.StartsWith("ChatGPT ", StringComparison.OrdinalIgnoreCase) && !name.Contains("Classic", StringComparison.OrdinalIgnoreCase));
        return (pid, named && (id.StartsWith("com.openai.", StringComparison.OrdinalIgnoreCase) || id.StartsWith("ai.openai.", StringComparison.OrdinalIgnoreCase)));
    }

    internal static bool Trusted => OperatingSystem.IsMacOS() && AXIsProcessTrusted();

    internal static void RequestAccess()
    {
        if (!OperatingSystem.IsMacOS()) return;
        if (AXIsProcessTrusted()) return;
        IntPtr library = NativeLibrary.Load(Core);
        try
        {
            IntPtr flag = Marshal.ReadIntPtr(NativeLibrary.GetExport(library, "kCFBooleanTrue"));
            IntPtr options = CFDictionaryCreate(IntPtr.Zero, new[] { Attribute("AXTrustedCheckOptionPrompt") }, new[] { flag }, 1, IntPtr.Zero, IntPtr.Zero);
            if (options != IntPtr.Zero)
            {
                try { _ = AXIsProcessTrustedWithOptions(options); }
                finally { CFRelease(options); }
            }
        }
        finally { NativeLibrary.Free(library); }
    }

    internal static MacView Inspect(int pid)
    {
        if (!Trusted) return null;
        IntPtr app = AXUIElementCreateApplication(pid);
        if (app == IntPtr.Zero) return null;
        try
        {
            AXUIElementSetMessagingTimeout(app, 0.10f);
            IntPtr window = Copy(app, "AXFocusedWindow");
            if (window == IntPtr.Zero) window = Copy(app, "AXMainWindow");
            return window == IntPtr.Zero ? null : InspectWindow(window);
        }
        finally { CFRelease(app); }
    }

    private static MacView InspectWindow(IntPtr window)
    {
        var queue = new Queue<(IntPtr Node, int Depth)>();
        queue.Enqueue((window, 0));
        var view = new MacView();
        var candidates = new List<(string Title, DesktopRect Bounds)>();
        double bestArea = 0;
        var timer = System.Diagnostics.Stopwatch.StartNew();
        int visited = 0;
        try
        {
            view.Window = Rect(window);
            view.Header = Label(window, "AXTitle");
            while (queue.Count > 0 && visited++ < 640 && timer.ElapsedMilliseconds < 525)
            {
                (IntPtr node, int depth) = queue.Dequeue();
                try
                {
                    string role = Label(node, "AXRole");
                    if (role == "AXWebArea")
                    {
                        DesktopRect bounds = Rect(node);
                        double area = bounds.Width * bounds.Height;
                        if ((bounds.Width > view.Window.Width * .55 && bounds.Height > view.Window.Height * .55 && area > bestArea)
                            || (!bounds.Valid && view.Title == null && depth < 9))
                        {
                            bestArea = area;
                            view.Title = Label(node, "AXTitle") ?? Label(node, "AXDescription");
                            view.Route = Label(node, "AXURL");
                        }
                    }
                    else if (role == "AXTextArea" || role == "AXTextField")
                    {
                        DesktopRect bounds = Rect(node);
                        if (bounds.Valid && bounds.Width >= 210 && bounds.Height >= 15 && bounds.Height <= 310
                            && bounds.Y > view.Window.Y + view.Window.Height * .56 && bounds.Bottom > view.Composer.Bottom)
                        {
                            string name = Label(node, "AXIdentifier") ?? Label(node, "AXDescription") ?? Label(node, "AXTitle") ?? "";
                            if (role == "AXTextArea" || name.Contains("prompt", StringComparison.OrdinalIgnoreCase) || name.Contains("composer", StringComparison.OrdinalIgnoreCase)
                                || name.Contains("message", StringComparison.OrdinalIgnoreCase) || name.Contains("输入") || name.Contains("消息")) view.Composer = bounds;
                        }
                    }
                    else if (role == "AXButton" || role == "AXPopUpButton" || role == "AXMenuButton")
                    {
                        string model = ModelLabels.FromPicker(Label(node, "AXTitle")) ?? ModelLabels.FromPicker(Label(node, "AXDescription"));
                        if (model != null) candidates.Add((model, Rect(node)));
                    }

                    if (depth < 25 && role != "AXStaticText" && role != "AXImage" && role != "AXLink") Enqueue(node, depth, queue);
                }
                finally { CFRelease(node); }
            }
        }
        finally
        {
            while (queue.Count > 0) CFRelease(queue.Dequeue().Node);
        }

        if (view.Composer.Valid)
        {
            foreach (var (title, bounds) in candidates)
            {
                if (bounds.Valid && bounds.Y >= view.Composer.Y && bounds.Y <= view.Composer.Bottom + 80
                    && bounds.X >= view.Composer.X - 5 && bounds.Right <= view.Composer.Right + 30)
                {
                    view.Model = title;
                    break;
                }
            }
        }
        return view;
    }

    private static void Enqueue(IntPtr node, int depth, Queue<(IntPtr Node, int Depth)> queue)
    {
        IntPtr children = Copy(node, "AXChildren");
        if (children == IntPtr.Zero) return;
        try
        {
            if (CFGetTypeID(children) != CFArrayGetTypeID()) return;
            int count = (int)Math.Min((long)CFArrayGetCount(children), 138);
            for (int i = 0; i < count && queue.Count < 740; i++)
            {
                IntPtr child = CFArrayGetValueAtIndex(children, i);
                if (child != IntPtr.Zero) queue.Enqueue((CFRetain(child), depth + 1));
            }
        }
        finally { CFRelease(children); }
    }

    private static string NSString(IntPtr value) => value == IntPtr.Zero ? null : Marshal.PtrToStringUTF8(Send(value, sel_registerName("UTF8String")));

    private static IntPtr Copy(IntPtr node, string attribute) => AXUIElementCopyAttributeValue(node, Attribute(attribute), out IntPtr result) == 0 ? result : IntPtr.Zero;

    private static IntPtr Attribute(string attribute)
    {
        lock (Attributes)
        {
            if (!Attributes.TryGetValue(attribute, out IntPtr value)) Attributes[attribute] = value = CFStringCreateWithCString(IntPtr.Zero, attribute, Utf8);
            return value;
        }
    }

    private static string Label(IntPtr node, string name)
    {
        IntPtr value = Copy(node, name);
        if (value == IntPtr.Zero) return null;
        try
        {
            IntPtr text = CFGetTypeID(value) == CFURLGetTypeID() ? CFURLGetString(value) : value;
            if (text == IntPtr.Zero || CFGetTypeID(text) != CFStringGetTypeID()) return null;
            var bytes = new byte[4096];
            if (!CFStringGetCString(text, bytes, bytes.Length, Utf8)) return null;
            int length = Array.IndexOf(bytes, (byte)0);
            return length > 0 ? Encoding.UTF8.GetString(bytes, 0, length) : null;
        }
        finally { CFRelease(value); }
    }

    private static DesktopRect Rect(IntPtr node)
    {
        IntPtr p = Copy(node, "AXPosition"), s = Copy(node, "AXSize");
        try
        {
            if (p == IntPtr.Zero || s == IntPtr.Zero || CFGetTypeID(p) != AXValueGetTypeID() || CFGetTypeID(s) != AXValueGetTypeID()) return default;
            return AXValueGetValue(p, 1, out Pair point) && AXValueGetValue(s, 2, out Pair size) ? new DesktopRect(point.First, point.Second, size.First, size.Second) : default;
        }
        finally
        {
            if (p != IntPtr.Zero) CFRelease(p);
            if (s != IntPtr.Zero) CFRelease(s);
        }
    }
}
