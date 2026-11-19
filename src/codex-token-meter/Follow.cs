using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;

namespace CodexTokenMeter
{
    internal sealed class FollowResult
    {
        internal readonly string Id, Problem;

        internal FollowResult(string id, string problem = null) { Id = id; Problem = problem; }
    }

    internal sealed class SessionTitles
    {
        private readonly string path;
        private readonly JsonReader json = new JsonReader();
        private Dictionary<string, string> idsByTitle = new Dictionary<string, string>(StringComparer.Ordinal);
        private long indexedLength = -1;
        private DateTime indexedTime;

        internal SessionTitles(string home) { path = Path.Combine(home, "session_index.jsonl"); }

        internal string Resolve(string title) { return Match(null, title, null).Id; }

        internal FollowResult Match(string accessibleRoute, string documentTitle, string headerTitle)
        {
            string direct = SessionBinding.ParseRoute(accessibleRoute);
            if (direct != null) return new FollowResult(direct);

            try
            {
                var index = new FileInfo(path);
                if (!index.Exists) return new FollowResult(null, "此对话尚无本地索引");
                if (index.Length != indexedLength || index.LastWriteTimeUtc != indexedTime) Reload(index);
            }
            catch (IOException) { return new FollowResult(null, "暂时无法读取本地对话索引"); }
            catch (UnauthorizedAccessException) { return new FollowResult(null, "无法访问本地对话索引"); }

            string documentId = Find(documentTitle), headerId = Find(headerTitle);
            if (documentId != headerId && Known(documentTitle) && Known(headerTitle))
                return new FollowResult(null, "正在确认切换后的对话");
            if (documentId != null || headerId != null) return new FollowResult(documentId ?? headerId);

            bool ambiguous = Known(documentTitle) || Known(headerTitle);
            return new FollowResult(null, ambiguous ? "检测到同名对话，可手动绑定 ID" : "当前对话暂无可匹配的本地记录");
        }

        private bool Known(string title) { return title != null && idsByTitle.ContainsKey(Normalize(title)); }

        private string Find(string title)
        {
            if (String.IsNullOrWhiteSpace(title)) return null;
            string label = Normalize(title), id;
            if (idsByTitle.TryGetValue(label, out id)) return id;
            foreach (string suffix in new[] { " — ChatGPT", " - ChatGPT", " — Codex", " - Codex" })
            {
                if (label.EndsWith(suffix, StringComparison.OrdinalIgnoreCase) && idsByTitle.TryGetValue(label.Substring(0, label.Length - suffix.Length), out id)) return id;
            }
            return null;
        }

        private static string Normalize(string title) { return title.Trim().Normalize(NormalizationForm.FormC); }

        private void Reload(FileInfo index)
        {
            var titlesById = new Dictionary<string, string>();
            using (var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
            using (var reader = new StreamReader(file))
            {
                string line;
                while ((line = reader.ReadLine()) != null)
                {
                    if (line.Length == 0 || line.Length > 16384) continue;
                    var row = json.Read(line);
                    string id = SessionBinding.ParseId(Json.Text(row, "id"));
                    string title = Json.Text(row, "thread_name");
                    if (id != null && !String.IsNullOrWhiteSpace(title)) titlesById[id] = Normalize(title);
                }
            }

            var map = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (var row in titlesById)
            {
                if (map.ContainsKey(row.Value)) map[row.Value] = null;
                else map[row.Value] = row.Key;
            }
            idsByTitle = map;
            indexedLength = index.Length;
            indexedTime = index.LastWriteTimeUtc;
        }
    }

    internal static class ModelLabels
    {
        private static readonly Regex Picker = new Regex(@"^\s*(GPT[- ]\d+(?:\.\d+)*(?:[- ](?:Sol|Astra|Terra|Codex|Mini|Nano|Pro|Spark|Thinking|Instant))*|o\d+(?:[- ](?:mini|pro))*)(?=\s|$)", RegexOptions.IgnoreCase | RegexOptions.Compiled);

        internal static string FromPicker(string label)
        {
            if (String.IsNullOrWhiteSpace(label)) return null;
            Match match = Picker.Match(label);
            return match.Success ? match.Groups[1].Value : null;
        }
    }
}
