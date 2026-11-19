using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;
#if NETFRAMEWORK
using System.Web.Script.Serialization;
#else
using System.Text.Json;
#endif

namespace CodexTokenMeter
{
    internal sealed class TokenCounts
    {
        internal readonly long Total;
        internal readonly long? Input, Output, Cached, Reasoning;

        private TokenCounts(Dictionary<string, object> values)
        {
            Total = Json.Number(values, "total_tokens").Value;
            Input = Json.Number(values, "input_tokens");
            Output = Json.Number(values, "output_tokens");
            Cached = Json.Number(values, "cached_input_tokens");
            Reasoning = Json.Number(values, "reasoning_output_tokens");
        }

        internal static TokenCounts Read(Dictionary<string, object> record, string key)
        {
            var values = Json.Object(record, key);
            return Json.Number(values, "total_tokens").HasValue ? new TokenCounts(values) : null;
        }
    }

    internal sealed class UsageSnapshot
    {
        internal string Model, TurnId;
        internal bool Running, MissingUsage;
        internal TokenCounts Total, Last;
        internal DateTime? UpdatedAt;

        internal UsageSnapshot Copy() { return (UsageSnapshot)MemberwiseClone(); }
    }

    internal sealed class JsonReader
    {
        internal const int SizeLimit = 512 * 1024;
#if NETFRAMEWORK
        private readonly JavaScriptSerializer serializer = new JavaScriptSerializer { MaxJsonLength = SizeLimit };
#endif
        internal Dictionary<string, object> Read(string line)
        {
#if NETFRAMEWORK
            try { return serializer.Deserialize<Dictionary<string, object>>(line); }
            catch (ArgumentException) { return null; }
            catch (InvalidOperationException) { return null; }
#else
            try
            {
                using (var document = JsonDocument.Parse(line)) return Decode(document.RootElement) as Dictionary<string, object>;
            }
            catch (JsonException) { return null; }
#endif
        }

#if !NETFRAMEWORK
        private static object Decode(JsonElement element)
        {
            switch (element.ValueKind)
            {
                case JsonValueKind.Object:
                    var fields = new Dictionary<string, object>(StringComparer.Ordinal);
                    foreach (var property in element.EnumerateObject()) fields[property.Name] = Decode(property.Value);
                    return fields;
                case JsonValueKind.String:
                    return element.GetString();
                case JsonValueKind.Number:
                    long value;
                    return element.TryGetInt64(out value) ? (object)value : null;
                default:
                    return null;
            }
        }
#endif
    }

    internal static class Json
    {
        internal static Dictionary<string, object> Object(Dictionary<string, object> value, string key)
        {
            object result;
            return value != null && value.TryGetValue(key, out result) ? result as Dictionary<string, object> : null;
        }

        internal static string Text(Dictionary<string, object> value, string key)
        {
            object result;
            return value != null && value.TryGetValue(key, out result) ? result as string : null;
        }

        internal static long? Number(Dictionary<string, object> value, string key)
        {
            object result;
            if (value == null || !value.TryGetValue(key, out result)) return null;
            if (result is int && (int)result >= 0) return (int)result;
            if (result is long && (long)result >= 0) return (long)result;
            return null;
        }

        internal static DateTime? Timestamp(string value)
        {
            DateTime parsed;
            return DateTime.TryParse(value, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out parsed) ? parsed : (DateTime?)null;
        }
    }

    internal sealed class UsageReader
    {
        private static readonly Regex RecordType = new Regex("(?:^\\s*\\{|,)\\s*\"type\"\\s*:\\s*\"(turn_context|token_usage_record|event_msg)\"", RegexOptions.Compiled);
        private static readonly Regex MeterEvent = new Regex("\"type\"\\s*:\\s*\"(?:token_count|thread_settings_applied|task_started|task_complete|turn_aborted|model_reroute|model_rerouted)\"", RegexOptions.Compiled);
        private const int ReadBudget = 16 * 1024 * 1024;
        private readonly JsonReader json = new JsonReader();
        private readonly MemoryStream pending = new MemoryStream();
        private readonly byte[] chunk = new byte[64 * 1024];
        private readonly string path;
        private readonly TodayCounter counter;
        private UsageSnapshot usage = new UsageSnapshot();
        private long offset;
        private bool oversized;
        private DateTime creationTime;

        internal UsageReader(string path, TodayCounter counter = null) { this.path = path; this.counter = counter; }

        internal UsageSnapshot Poll()
        {
            using (var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
            {
                DateTime currentCreationTime = File.GetCreationTimeUtc(path);
                if (file.Length < offset || currentCreationTime != creationTime)
                {
                    pending.SetLength(0);
                    offset = 0;
                    oversized = false;
                    usage = new UsageSnapshot();
                    creationTime = currentCreationTime;
                    if (counter != null) counter.Reset();
                }

                long end = file.Length;
                file.Seek(offset, SeekOrigin.Begin);
                int remaining = ReadBudget;
                while (remaining > 0 && offset < end)
                {
                    int size = file.Read(chunk, 0, (int)Math.Min(end - offset, Math.Min(remaining, chunk.Length)));
                    if (size == 0) break;
                    ReadChunk(size);
                    offset += size;
                    remaining -= size;
                }

                return offset < end ? null : usage.Copy();
            }
        }

        private void ReadChunk(int size)
        {
            int start = 0;
            while (start < size)
            {
                int newline = Array.IndexOf(chunk, (byte)'\n', start, size - start);
                int end = newline < 0 ? size : newline;
                if (!oversized && pending.Length + end - start <= JsonReader.SizeLimit)
                {
                    pending.Write(chunk, start, end - start);
                }
                else
                {
                    oversized = true;
                }

                if (newline < 0) break;
                if (!oversized && pending.Length > 0)
                {
                    byte[] bytes = pending.GetBuffer();
                    string header = Encoding.UTF8.GetString(bytes, 0, (int)Math.Min(pending.Length, 320)).TrimStart('\uFEFF');
                    Match relevant = RecordType.Match(header);
                    if (relevant.Success)
                    {
                        Accept(Encoding.UTF8.GetString(bytes, 0, (int)pending.Length).TrimStart('\uFEFF'), relevant.Groups[1].Value);
                    }
                }
                pending.SetLength(0);
                oversized = false;
                start = newline + 1;
            }
        }

        private void Accept(string line, string type)
        {
            if (type == "event_msg" && !MeterEvent.IsMatch(line)) return;
            var record = json.Read(line);
            if (record == null)
            {
                if (type == "token_usage_record") usage.MissingUsage = true;
                return;
            }
            if (Json.Text(record, "type") != type) return;
            var payload = Json.Object(record, "payload");
            if (payload == null) return;
            if (counter != null) counter.Accept(type, Json.Text(record, "timestamp"), payload);

            switch (type)
            {
                case "turn_context":
                    usage.TurnId = Json.Text(payload, "turn_id") ?? usage.TurnId;
                    SetModel(Json.Text(payload, "model"));
                    break;
                case "token_usage_record":
                    SetModel(Json.Text(payload, "model") ?? Json.Text(payload, "model_slug"));
                    TokenCounts request = TokenCounts.Read(payload, "usage");
                    if (request == null) usage.MissingUsage = true;
                    SetCounts(record, TokenCounts.Read(payload, "thread_token_usage"), request);
                    break;
                case "event_msg":
                    AcceptEvent(record, payload);
                    break;
            }
        }

        private void AcceptEvent(Dictionary<string, object> record, Dictionary<string, object> payload)
        {
            string turnId = Json.Text(payload, "turn_id");
            switch (Json.Text(payload, "type"))
            {
                case "token_count":
                    var info = Json.Object(payload, "info");
                    SetCounts(record, TokenCounts.Read(info, "total_token_usage"), TokenCounts.Read(info, "last_token_usage"));
                    break;
                case "thread_settings_applied":
                    SetModel(Json.Text(Json.Object(payload, "thread_settings"), "model"));
                    break;
                case "model_reroute":
                case "model_rerouted":
                    SetModel(Json.Text(payload, "to_model") ?? Json.Text(payload, "toModel"));
                    break;
                case "task_started":
                    usage.TurnId = turnId;
                    usage.Running = true;
                    break;
                case "task_complete":
                case "turn_aborted":
                    if (turnId == null || turnId == usage.TurnId) usage.Running = false;
                    break;
            }
        }

        private void SetModel(string model)
        {
            if (!String.IsNullOrWhiteSpace(model)) usage.Model = model;
        }

        private void SetCounts(Dictionary<string, object> record, TokenCounts total, TokenCounts last)
        {
            if (total == null || (usage.Total != null && total.Total < usage.Total.Total)) return;
            if (usage.Total != null && total.Total == usage.Total.Total && usage.Last != null) return;
            usage.Total = total;
            usage.Last = last;
            usage.UpdatedAt = Json.Timestamp(Json.Text(record, "timestamp")) ?? usage.UpdatedAt;
        }
    }

    internal sealed class SessionBinding
    {
        private static readonly Regex IdPattern = new Regex("[0-9a-f]{8}-(?:[0-9a-f]{4}-){3}[0-9a-f]{12}", RegexOptions.IgnoreCase | RegexOptions.Compiled);
        private readonly string home;
        private readonly string id;
        private DateTime searchAfter;
        private UsageReader reader;

        internal SessionBinding(string home, string id)
        {
            this.home = home;
            this.id = id;
        }

        internal static string ParseId(string text)
        {
            if (String.IsNullOrWhiteSpace(text)) return null;
            string input = text.Trim();
            Guid parsed;
            if (Guid.TryParseExact(input, "D", out parsed)) return parsed.ToString("D");

            Uri link;
            if (!Uri.TryCreate(input, UriKind.Absolute, out link)) return null;
            if (link.Scheme != "codex" && !(link.Scheme == "https" && link.Host == "chatgpt.com")) return null;
            Match match = IdPattern.Match(link.AbsolutePath);
            return match.Success ? match.Value.ToLowerInvariant() : null;
        }

        internal static string FileId(string file)
        {
            string name = Path.GetFileNameWithoutExtension(file);
            if (name.Length < 37 || name[name.Length - 37] != '-') return null;
            return ParseId(name.Substring(name.Length - 36));
        }

        internal static string ParseRoute(string raw)
        {
            Uri uri;
            if (!Uri.TryCreate(raw, UriKind.Absolute, out uri)) return null;
            if (uri.Scheme != "codex" && uri.Scheme != "app" && !(uri.Scheme == "https" && uri.Host == "chatgpt.com")) return null;
            string path = uri.Fragment.StartsWith("#/", StringComparison.Ordinal) ? uri.Fragment.Substring(1) : uri.AbsolutePath;
            string[] parts = path.Trim('/').Split('/');
            if (parts.Length == 1 && uri.Scheme == "codex" && (uri.Host == "threads" || uri.Host == "thread")) return ParseId(parts[0]);
            if (parts.Length >= 2 && (parts[parts.Length - 2] == "threads" || parts[parts.Length - 2] == "thread" || parts[parts.Length - 2] == "c")) return ParseId(parts[parts.Length - 1]);
            return null;
        }

        internal string FindFile()
        {
            string root = Path.Combine(home, "sessions");
            foreach (string directory in new[] { root, Path.Combine(home, "archived_sessions") })
            {
                if (!Directory.Exists(directory)) continue;
                foreach (string file in Directory.EnumerateFiles(directory, "*-" + id + ".jsonl", SearchOption.AllDirectories))
                {
                    if (FileId(file) == id) return file;
                }
            }
            return null;
        }

        internal UsageSnapshot Poll()
        {
            if (reader == null)
            {
                if (DateTime.UtcNow < searchAfter) return null;
                searchAfter = DateTime.UtcNow.AddSeconds(3);
                string path = FindFile();
                if (path == null) return null;
                reader = new UsageReader(path);
            }

            try { return reader.Poll(); }
            catch (FileNotFoundException) { reader = null; return null; }
            catch (DirectoryNotFoundException) { reader = null; return null; }
        }
    }
}
