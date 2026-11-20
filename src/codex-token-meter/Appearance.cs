using System;
using System.Diagnostics;
using System.IO;
using System.Text.RegularExpressions;

namespace CodexTokenMeter
{
    internal sealed class CodexAppearance
    {
        private const string Desktop = @"(?:desktop|""desktop""|'desktop')";
        private const string Theme = @"(?:appearanceTheme|""appearanceTheme""|'appearanceTheme')";
        // Match the app's setting and table headers, skipping comments and quoted values in other settings.
        private static readonly Regex ConfigTokens = new Regex(
            @"^[ \t]*(?:(?<table>\[[^\r\n]*?\])[ \t]*(?=#|\r?$)|"
            + @"(?:(?<dotted>" + Desktop + @")[ \t]*\.[ \t]*)?" + Theme
            + @"[ \t]*=[ \t]*(?<quote>[""'])(?<mode>light|dark|system)\k<quote>[ \t]*(?=#|\r?$))"
            + @"|#[^\r\n]*|""{3}(?:\\[\s\S]|(?!""{3})[^\\])*""{3}|'{3}[\s\S]*?'{3}"
            + @"|""(?:\\.|[^""\r\n\\])*""|'[^'\r\n]*'",
            RegexOptions.Multiline | RegexOptions.CultureInvariant, TimeSpan.FromSeconds(1));
        private static readonly Regex DesktopTable = new Regex(@"^\[[ \t]*" + Desktop + @"[ \t]*\]$");

        private readonly string path;
        private readonly Stopwatch elapsed = Stopwatch.StartNew();
        private long nextCheck;
        private DateTime lastWrite;
        private long lastLength = -1;
        private bool? dark;

        internal CodexAppearance(string codexHome)
        {
            path = Path.Combine(codexHome, "config.toml");
        }

        internal bool? ReadDarkPreference()
        {
            if (elapsed.ElapsedMilliseconds < nextCheck) return dark;
            nextCheck = elapsed.ElapsedMilliseconds + 600;
            try
            {
                var file = new FileInfo(path);
                long length = file.Length;
                DateTime modified = file.LastWriteTimeUtc;
                if (modified == lastWrite && length == lastLength) return dark;

                using (var input = file.Open(FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
                using (var reader = new StreamReader(input))
                    dark = Parse(reader.ReadToEnd());
                lastWrite = modified;
                lastLength = length;
            }
            catch (FileNotFoundException) { Reset(); }
            catch (DirectoryNotFoundException) { Reset(); }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
            catch (RegexMatchTimeoutException) { }
            return dark;
        }

        private void Reset()
        {
            dark = null;
            lastLength = -1;
        }

        internal static bool? Parse(string config)
        {
            bool root = true;
            bool desktop = false;
            foreach (Match token in ConfigTokens.Matches(config))
            {
                if (token.Groups["table"].Success)
                {
                    root = false;
                    desktop = DesktopTable.IsMatch(token.Groups["table"].Value);
                }
                else if (token.Groups["mode"].Success && (token.Groups["dotted"].Success ? root : desktop))
                {
                    switch (token.Groups["mode"].Value)
                    {
                        case "dark": return true;
                        case "light": return false;
                        default: return null;
                    }
                }
            }
            return null;
        }
    }

    internal sealed class MeterPalette
    {
        internal static readonly MeterPalette Light = new MeterPalette(
            "#FFFFFF", "#202124", "#52616A", "#D9DFDE", "#047968", "#DCE8E4", "#EAF2EF", "#F3F5F5");
        internal static readonly MeterPalette Dark = new MeterPalette(
            "#181818", "#F0F2F4", "#AEB9C1", "#393B3E", "#5ACFB6", "#303F3D", "#303735", "#222425");

        internal readonly string Surface, Ink, Quiet, Stroke, Signal, Track, Hover, Inset;

        private MeterPalette(string surface, string ink, string quiet, string stroke, string signal, string track, string hover, string inset)
        {
            Surface = surface;
            Ink = ink;
            Quiet = quiet;
            Stroke = stroke;
            Signal = signal;
            Track = track;
            Hover = hover;
            Inset = inset;
        }
    }

    internal static class MeterLayout
    {
        internal const string ChevronDown = "M 1.5,2.75 L 5,6.25 L 8.5,2.75";
        internal const string ChevronUp = "M 1.5,6.25 L 5,2.75 L 8.5,6.25";
        internal const double ChevronWidth = 10, ChevronHeight = 9;
        internal const double StatusDotSize = 6;

        internal static double? CacheShare(TokenCounts counts)
        {
            if (counts == null || !counts.Cached.HasValue || counts.Input.GetValueOrDefault() <= 0) return null;
            return Math.Max(0, Math.Min(1, (double)counts.Cached.Value / counts.Input.Value));
        }
    }
}
