// JSON persistence for QD3D Annotate. All files live under one root (on the headset:
// <persistentDataPath>/QD3D, pulled with PullSession.bat):
//   catalog.json              objects
//   rooms/<roomId>.json       room frame (anchor UUID), drift checks
//   annotations/<roomId>.json ground-truth boxes in the room frame, every repetition
//   calibration.json          controller tip offsets, operator name
//   events.jsonl              audit trail: one JSON line per action (append-only, never rewritten)
//   photos/, sessions/, exports/
// Writes are atomic (temp file, then replace) and keep the previous version as <file>.bak. A file that fails to
// parse is never overwritten silently: it is moved aside as <file>.corrupt-<time> and the .bak is tried.
// No Unity API here, so it runs in the offline tests.
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace QuestDetect3D.Annotation
{
    public sealed class AnnotationStore
    {
        private static readonly JsonSerializerSettings s_json = new JsonSerializerSettings
        {
            Formatting = Formatting.Indented,
            NullValueHandling = NullValueHandling.Include,
            Culture = CultureInfo.InvariantCulture,
        };

        /// <summary>Replaceable clock (tests).</summary>
        public static Func<DateTime> UtcNow = () => DateTime.UtcNow;

        public string Root { get; }

        public AnnotationStore(string root)
        {
            Root = root;
            Directory.CreateDirectory(Root);
            Directory.CreateDirectory(RoomsDir);
            Directory.CreateDirectory(AnnotationsDir);
            Directory.CreateDirectory(PhotosDir);
            Directory.CreateDirectory(SessionsDir);
        }

        public string CatalogPath => Path.Combine(Root, "catalog.json");
        public string CalibrationPath => Path.Combine(Root, "calibration.json");
        public string EventsPath => Path.Combine(Root, "events.jsonl");
        public string RoomsDir => Path.Combine(Root, "rooms");
        public string AnnotationsDir => Path.Combine(Root, "annotations");
        public string PhotosDir => Path.Combine(Root, "photos");
        public string SessionsDir => Path.Combine(Root, "sessions");
        public string ExportsDir => Path.Combine(Root, "exports");
        /// <summary>rooms/&lt;roomId&gt;.json</summary>
        public string RoomPath(string roomId) => Path.Combine(RoomsDir, roomId + ".json");
        /// <summary>annotations/&lt;roomId&gt;.json</summary>
        public string AnnotationsPath(string roomId) => Path.Combine(AnnotationsDir, roomId + ".json");

        // ------------------------------------------------------------------ typed load / save

        public CatalogFile LoadCatalog() => Load<CatalogFile>(CatalogPath) ?? new CatalogFile();
        /// <summary>Writes catalog.json atomically.</summary>
        public void SaveCatalog(CatalogFile catalog) => Save(CatalogPath, catalog);

        public CalibrationFile LoadCalibration() => Load<CalibrationFile>(CalibrationPath) ?? new CalibrationFile();
        /// <summary>Writes calibration.json atomically.</summary>
        public void SaveCalibration(CalibrationFile calibration) => Save(CalibrationPath, calibration);

        /// <summary>Null when the room does not exist (or its file is unreadable: see the log).</summary>
        public RoomFile LoadRoom(string roomId) => Load<RoomFile>(RoomPath(roomId));
        /// <summary>Writes rooms/&lt;id&gt;.json atomically.</summary>
        public void SaveRoom(RoomFile room) => Save(RoomPath(room.Id), room);

        /// <summary>All ground truth of a room (empty file object when none yet).</summary>
        public AnnotationFile LoadAnnotations(string roomId)
        {
            return Load<AnnotationFile>(AnnotationsPath(roomId)) ?? new AnnotationFile { RoomId = roomId };
        }

        /// <summary>Writes annotations/&lt;room&gt;.json atomically.</summary>
        public void SaveAnnotations(AnnotationFile file) => Save(AnnotationsPath(file.RoomId), file);

        /// <summary>Every readable room file, sorted by id.</summary>
        public List<RoomFile> ListRooms()
        {
            var rooms = new List<RoomFile>();
            var files = Directory.GetFiles(RoomsDir, "*.json");
            Array.Sort(files, StringComparer.Ordinal);
            foreach (var path in files)
            {
                var room = Load<RoomFile>(path);
                if (room != null)
                {
                    rooms.Add(room);
                }
            }

            return rooms;
        }

        // ------------------------------------------------------------------ creation helpers

        /// <summary>Creates and saves a room with a unique id derived from its name.</summary>
        public RoomFile CreateRoom(string name)
        {
            var ids = new List<string>();
            foreach (var r in ListRooms())
            {
                ids.Add(r.Id);
            }

            var room = new RoomFile { Id = UniqueId(Slug(name), ids), Name = name, Created = Timestamp() };
            SaveRoom(room);
            return room;
        }

        /// <summary>Adds an object with a unique id to the catalog and saves it.</summary>
        public ObjectEntry CreateObject(CatalogFile catalog, string name, string category = "Chair")
        {
            var ids = new List<string>();
            foreach (var o in catalog.Objects)
            {
                ids.Add(o.Id);
            }

            var entry = new ObjectEntry { Id = UniqueId(Slug(name), ids), Name = name, Category = category, Created = Timestamp() };
            catalog.Objects.Add(entry);
            SaveCatalog(catalog);
            return entry;
        }

        /// <summary>"Chair" + {Chair_01, Chair_02} → "Chair_03".</summary>
        public static string NextName(string prefix, IEnumerable<string> existingNames)
        {
            var max = 0;
            foreach (var name in existingNames)
            {
                if (name != null && name.StartsWith(prefix + "_", StringComparison.OrdinalIgnoreCase) &&
                    int.TryParse(name.Substring(prefix.Length + 1), NumberStyles.None, CultureInfo.InvariantCulture, out var n))
                {
                    max = Math.Max(max, n);
                }
            }

            return prefix + "_" + (max + 1).ToString("00", CultureInfo.InvariantCulture);
        }

        /// <summary>File-name-safe id: lower-case letters, digits and '_' ("Chair 01 (lab)" → "chair_01_lab").</summary>
        public static string Slug(string name)
        {
            var sb = new StringBuilder();
            var lastUnderscore = true;
            foreach (var ch in name ?? "")
            {
                var c = char.ToLowerInvariant(ch);
                if ((c >= 'a' && c <= 'z') || (c >= '0' && c <= '9'))
                {
                    sb.Append(c);
                    lastUnderscore = false;
                }
                else if (!lastUnderscore)
                {
                    sb.Append('_');
                    lastUnderscore = true;
                }
            }

            var s = sb.ToString().Trim('_');
            return s.Length == 0 ? "item" : s;
        }

        /// <summary>baseId, or baseId_2, baseId_3 … when taken.</summary>
        public static string UniqueId(string baseId, ICollection<string> existing)
        {
            if (!existing.Contains(baseId))
            {
                return baseId;
            }

            for (var i = 2; ; i++)
            {
                var id = baseId + "_" + i.ToString(CultureInfo.InvariantCulture);
                if (!existing.Contains(id))
                {
                    return id;
                }
            }
        }

        /// <summary>Current time as ISO-8601 UTC with milliseconds (all files use this).</summary>
        public static string Timestamp() => UtcNow().ToString("yyyy-MM-dd'T'HH:mm:ss.fff'Z'", CultureInfo.InvariantCulture);

        // ------------------------------------------------------------------ audit trail

        /// <summary>Append one line {"t": time, "type": type, ...data} to events.jsonl.</summary>
        public void AppendEvent(string type, JObject data = null)
        {
            var line = new JObject { ["t"] = Timestamp(), ["type"] = type };
            if (data != null)
            {
                foreach (var property in data.Properties())
                {
                    line[property.Name] = property.Value;
                }
            }

            File.AppendAllText(EventsPath, line.ToString(Formatting.None) + "\n", new UTF8Encoding(false));
        }

        // ------------------------------------------------------------------ generic IO

        /// <summary>Indented JSON with invariant culture (what every QD3D file uses).</summary>
        public static string ToJson(object value) => JsonConvert.SerializeObject(value, s_json);
        public static T FromJson<T>(string json) => JsonConvert.DeserializeObject<T>(json, s_json);

        /// <summary>Atomic write: the file on disk is always either the old or the new complete version.</summary>
        public static void Save(string path, object value)
        {
            var json = ToJson(value);
            var dir = Path.GetDirectoryName(path);
            if (!string.IsNullOrEmpty(dir))
            {
                Directory.CreateDirectory(dir);
            }

            var tmp = path + ".tmp";
            File.WriteAllText(tmp, json, new UTF8Encoding(false));
            if (File.Exists(path))
            {
                var bak = path + ".bak";
                try
                {
                    File.Replace(tmp, path, bak);
                }
                catch (Exception)
                {
                    // Platforms without an atomic replace: keep the backup first, then swap.
                    File.Copy(path, bak, true);
                    File.Delete(path);
                    File.Move(tmp, path);
                }
            }
            else
            {
                File.Move(tmp, path);
            }
        }

        /// <summary>Null when the file does not exist. A corrupt file is moved aside and the backup is tried.</summary>
        public static T Load<T>(string path) where T : class
        {
            if (TryRead(path, out T value))
            {
                return value;
            }

            if (File.Exists(path))
            {
                var aside = path + ".corrupt-" + UtcNow().ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture);
                Log.Error($"ann_store_corrupt file={Path.GetFileName(path)} moved_to={Path.GetFileName(aside)}");
                try
                {
                    File.Move(path, aside);
                }
                catch (Exception e)
                {
                    Log.Error($"ann_store_move_failed {e.Message}");
                }
            }

            var bak = path + ".bak";
            if (TryRead(bak, out value))
            {
                Log.Warn($"ann_store_restored_from_backup file={Path.GetFileName(path)}");
                return value;
            }

            return null;
        }

        private static bool TryRead<T>(string path, out T value) where T : class
        {
            value = null;
            if (!File.Exists(path))
            {
                return false;
            }

            try
            {
                value = FromJson<T>(File.ReadAllText(path, Encoding.UTF8));
                return value != null;
            }
            catch (Exception e)
            {
                Log.Error($"ann_store_parse_failed file={Path.GetFileName(path)} error={e.Message}");
                return false;
            }
        }
    }
}
