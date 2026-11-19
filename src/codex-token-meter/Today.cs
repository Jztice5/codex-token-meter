using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;

namespace CodexTokenMeter
{
    internal sealed class TodayCounter
    {
        internal const string UnknownModel = "";
        internal sealed class Entry
        {
            internal readonly string Model, CoveredSnapshot;
            internal readonly long Total;
            internal readonly bool Modern;
            internal readonly DateTime Timestamp;

            internal Entry(string model, long total, DateTime time, bool modern, string coveredSnapshot)
            {
                Model = String.IsNullOrWhiteSpace(model) ? UnknownModel : model.Trim();
                Total = total;
                Timestamp = time;
                Modern = modern;
                CoveredSnapshot = coveredSnapshot;
            }
        }

        private readonly DateTime from, until;
        private readonly string sessionId;
        private readonly Dictionary<string, string> modelsByTurn = new Dictionary<string, string>();
        internal readonly Dictionary<string, Entry> Entries = new Dictionary<string, Entry>();
        private long baseline;
        private string activeTurn;
        internal bool Partial { get; private set; }

        internal TodayCounter(string sessionId, DateTime from)
        {
            this.sessionId = sessionId;
            this.from = from;
            until = from.AddDays(1);
        }

        internal void Reset() { baseline = 0; activeTurn = null; Entries.Clear(); modelsByTurn.Clear(); Partial = false; }

        internal void Accept(string type, string timestamp, Dictionary<string, object> payload)
        {
            string turn = Json.Text(payload, "turn_id");
            if (type == "turn_context")
            {
                activeTurn = turn;
                SetModel(turn, Json.Text(payload, "model"));
                return;
            }
            if (type == "token_usage_record")
            {
                DateTime? time = Json.Timestamp(timestamp);
                var request = TokenCounts.Read(payload, "usage");
                var cumulative = TokenCounts.Read(payload, "thread_token_usage");
                long cumulativeTokens = cumulative == null ? 0 : cumulative.Total;
                if (cumulative != null) baseline = Math.Max(baseline, cumulativeTokens);
                if (!time.HasValue) { Partial = true; return; }
                if (!InDay(time.Value)) return;
                if (request == null) { Partial = true; return; }

                string responseId = Json.Text(payload, "response_id");
                string snapshotKey = SnapshotKey(turn, cumulativeTokens);
                string key = !String.IsNullOrEmpty(responseId) ? "response:" + responseId
                    : cumulative != null ? snapshotKey : "time:" + (turn ?? sessionId) + ":" + timestamp;
                Add(key, turn, request.Total, time.Value, true, cumulative == null ? null : snapshotKey,
                    Json.Text(payload, "model") ?? Json.Text(payload, "model_slug"));
                return;
            }

            switch (Json.Text(payload, "type"))
            {
                case "task_started":
                    activeTurn = turn;
                    break;
                case "model_reroute":
                case "model_rerouted":
                    SetModel(turn ?? activeTurn, Json.Text(payload, "to_model") ?? Json.Text(payload, "toModel"));
                    break;
                case "token_count":
                    AcceptLegacy(timestamp, Json.Object(payload, "info"));
                    break;
            }
        }

        private void AcceptLegacy(string timestamp, Dictionary<string, object> info)
        {
            var cumulative = TokenCounts.Read(info, "total_token_usage");
            if (cumulative == null || cumulative.Total <= baseline) return;
            var last = TokenCounts.Read(info, "last_token_usage");
            long growth = cumulative.Total - baseline;
            bool hadBaseline = baseline > 0;
            baseline = cumulative.Total;
            DateTime? time = Json.Timestamp(timestamp);
            if (!time.HasValue) { Partial = true; return; }
            if (!InDay(time.Value)) return;
            if (last == null || last.Total > growth) { Partial = true; return; }
            if (hadBaseline && last.Total < growth) Partial = true;
            // A legacy snapshot can resume from an earlier total. Only the last confirmed model request is attributed to today.
            Add(SnapshotKey(activeTurn, baseline), activeTurn, last.Total, time.Value, false, null, null);
        }

        private void SetModel(string turn, string model)
        {
            if (turn != null && !String.IsNullOrWhiteSpace(model)) modelsByTurn[turn] = model;
        }

        private void Add(string key, string turn, long count, DateTime time, bool modern, string coveredSnapshot, string model)
        {
            if (count <= 0) return;
            if (String.IsNullOrWhiteSpace(model) && turn != null) modelsByTurn.TryGetValue(turn, out model);
            Entry existing;
            var entry = new Entry(model, count, time, modern, coveredSnapshot);
            if (!Entries.TryGetValue(key, out existing) || Prefer(entry, existing)) Entries[key] = entry;
        }

        internal static bool Prefer(Entry candidate, Entry existing)
        {
            if (candidate.Modern != existing.Modern) return candidate.Modern;
            if (candidate.Model == UnknownModel && existing.Model != UnknownModel && candidate.Total == existing.Total) return false;
            if (existing.Model == UnknownModel && candidate.Model != UnknownModel && candidate.Total == existing.Total) return true;
            return candidate.Timestamp > existing.Timestamp;
        }

        private string SnapshotKey(string turn, long value)
        {
            return "snapshot:" + (turn ?? sessionId) + ":" + value.ToString(CultureInfo.InvariantCulture);
        }

        private bool InDay(DateTime value) { return value >= from && value < until; }
    }

    internal sealed class TodaySummary
    {
        internal readonly DateTime Day;
        internal readonly long Total;
        internal readonly Dictionary<string, long> Models;
        internal readonly bool Partial;

        internal TodaySummary(DateTime day, Dictionary<string, long> models, bool partial)
        {
            Day = day;
            Models = models;
            Partial = partial;
            foreach (long amount in models.Values) Total += amount;
        }
    }

    internal sealed class TodayStore
    {
        internal static readonly TimeSpan BeijingOffset = TimeSpan.FromHours(8);
        private readonly string home;
        private DateTime activeDay;
        private readonly Dictionary<string, StreamState> streams = new Dictionary<string, StreamState>();

        private sealed class StreamState
        {
            internal readonly string Path;
            internal readonly TodayCounter Counter;
            internal readonly UsageReader Reader;
            internal long Length = -1;
            internal DateTime Modified;

            internal StreamState(string path, string id, DateTime startUtc)
            {
                Path = path;
                Counter = new TodayCounter(id, startUtc);
                Reader = new UsageReader(path, Counter);
            }
        }

        internal TodayStore(string home) { this.home = home; }

        internal static DateTime BeijingDay(DateTimeOffset now) { return now.ToOffset(BeijingOffset).Date; }

        internal TodaySummary Poll(DateTimeOffset now)
        {
            DateTime day = BeijingDay(now);
            DateTime startUtc = new DateTimeOffset(day, BeijingOffset).UtcDateTime;
            if (day != activeDay) { activeDay = day; streams.Clear(); }

            bool partial = false;
            var files = new Dictionary<string, FileInfo>();
            foreach (string source in new[] { "sessions", "archived_sessions" })
            {
                var folder = new DirectoryInfo(Path.Combine(home, source));
                if (!folder.Exists) continue;
                try
                {
                    foreach (FileInfo file in folder.EnumerateFiles("rollout-*.jsonl", SearchOption.AllDirectories))
                    {
                        if (file.LastWriteTimeUtc < startUtc) continue;
                        string id = SessionBinding.FileId(file.Name);
                        FileInfo existing;
                        if (id != null && (!files.TryGetValue(id, out existing) || file.LastWriteTimeUtc > existing.LastWriteTimeUtc)) files[id] = file;
                    }
                }
                catch (IOException) { partial = true; }
                catch (UnauthorizedAccessException) { partial = true; }
            }

            var requests = new Dictionary<string, TodayCounter.Entry>();
            foreach (var pair in files)
            {
                FileInfo file = pair.Value;
                StreamState stream;
                if (!streams.TryGetValue(pair.Key, out stream) || stream.Path != file.FullName)
                {
                    stream = new StreamState(file.FullName, pair.Key, startUtc);
                    streams[pair.Key] = stream;
                }
                try
                {
                    if (stream.Length != file.Length || stream.Modified != file.LastWriteTimeUtc)
                    {
                        while (stream.Reader.Poll() == null) { }
                        stream.Length = file.Length;
                        stream.Modified = file.LastWriteTimeUtc;
                    }
                }
                catch (IOException) { partial = true; }
                catch (UnauthorizedAccessException) { partial = true; }
                if (stream.Counter.Partial) partial = true;
                foreach (var entry in stream.Counter.Entries)
                {
                    TodayCounter.Entry existing;
                    if (!requests.TryGetValue(entry.Key, out existing) || TodayCounter.Prefer(entry.Value, existing)) requests[entry.Key] = entry.Value;
                }
            }

            var models = new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase);
            var covered = new HashSet<string>();
            foreach (var request in requests.Values) if (request.Modern && request.CoveredSnapshot != null) covered.Add(request.CoveredSnapshot);
            foreach (var entry in requests)
            {
                TodayCounter.Entry request = entry.Value;
                if (!request.Modern && covered.Contains(entry.Key)) continue;
                long previous;
                models.TryGetValue(request.Model, out previous);
                models[request.Model] = previous + request.Total;
            }
            return new TodaySummary(day, models, partial);
        }
    }
}
