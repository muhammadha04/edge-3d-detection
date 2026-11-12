#if UNITY_EDITOR || QD3D_OFFLINE
// Tests of the QD3D Annotate pure logic (no headset): JSON persistence, ids, box conversion.
// Run by SelfTest before every build and offline by Tools/offline/run_tests.sh.
using System;
using System.Collections.Generic;
using System.IO;
using QuestDetect3D.Annotation;
using UnityEngine;
using Report = QuestDetect3D.Tests.PipelineTests.Report;

namespace QuestDetect3D.Tests
{
    public static class AnnotationTests
    {
        public static Report RunAll()
        {
            var r = new Report();
            Run(r, "ann-ids", TestIds);
            Run(r, "ann-box-dto", TestBoxDto);
            Run(r, "ann-json-roundtrip", TestJsonRoundTrip);
            Run(r, "ann-store-restart", TestStoreRestart);
            Run(r, "ann-store-corrupt", TestCorruptFile);
            AnnotationLogicTests.RunAll(r);
            return r;
        }

        private static void Run(Report r, string name, Action<Report> test)
        {
            try
            {
                test(r);
            }
            catch (Exception e)
            {
                r.Check(name, false, "exception " + e);
            }
        }

        private static string TempRoot()
        {
            var dir = Path.Combine(Path.GetTempPath(), "qd3d_ann_test_" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(dir);
            return dir;
        }

        private static void Cleanup(string dir)
        {
            try
            {
                Directory.Delete(dir, true);
            }
            catch (Exception)
            {
                // temp folder: leave it
            }
        }

        private static void TestIds(Report r)
        {
            r.Check("ann-slug", AnnotationStore.Slug("Chair 01 (lab)") == "chair_01_lab" && AnnotationStore.Slug("  ") == "item",
                $"'{AnnotationStore.Slug("Chair 01 (lab)")}' '{AnnotationStore.Slug("  ")}'");
            var ids = new List<string> { "chair_01", "chair_01_2" };
            r.Check("ann-unique-id", AnnotationStore.UniqueId("chair_01", ids) == "chair_01_3" &&
                                     AnnotationStore.UniqueId("desk", ids) == "desk",
                AnnotationStore.UniqueId("chair_01", ids));
            var names = new List<string> { "Chair_01", "Chair_07", "Chair_x", "Table_09" };
            r.Check("ann-next-name", AnnotationStore.NextName("Chair", names) == "Chair_08" &&
                                     AnnotationStore.NextName("Room", names) == "Room_01",
                AnnotationStore.NextName("Chair", names));
        }

        private static void TestBoxDto(Report r)
        {
            var box = new UprightBox(new Vector3(1.25f, 0f, -2.5f), 37.5f * Mathf.Deg2Rad, new Vector3(0.62f, 0.95f, 0.58f));
            var dto = BoxDto.From(box, 1);
            var back = AnnotationStore.FromJson<BoxDto>(AnnotationStore.ToJson(dto)).ToUpright();
            var err = (back.BottomCenter - box.BottomCenter).magnitude + (back.Size - box.Size).magnitude +
                      Mathf.Abs(back.Yaw - box.Yaw);
            r.Check("ann-box-dto", err < 1e-5f && Mathf.Abs(dto.YawDeg - 37.5f) < 1e-4f && dto.Size[1] == 0.95f,
                $"err={err:E1} yaw_deg={dto.YawDeg} size=[{dto.Size[0]},{dto.Size[1]},{dto.Size[2]}]");
        }

        private static AnnotationFile SampleAnnotations()
        {
            var file = new AnnotationFile { RoomId = "lab" };
            var obj = file.GetOrAdd("chair_01");
            for (var i = 1; i <= 3; i++)
            {
                var rep = new Repetition
                {
                    Method = "M1",
                    Index = i,
                    Blind = true,
                    Box = BoxDto.From(new UprightBox(new Vector3(1f + 0.001f * i, 0f, 2f), 0.5f, new Vector3(0.6f, 0.9f, 0.6f))),
                    Operator = "mh",
                    Timestamp = AnnotationStore.Timestamp(),
                    DurationS = 61.5f,
                    SelfRating = 4,
                };
                rep.RawInputs.Touches.Add(new TouchInput { Label = "front_a", Point = new[] { 0.9f, 0.45f, 2.3f }, T = 3.2f });
                rep.RawInputs.Edits.Add(new EditOp { Op = "face_push:+x", Value = new[] { 0.004f }, T = 40f });
                rep.RawInputs.BoxBeforeEdit = rep.Box;
                rep.RawInputs.TipOffset = new[] { 0f, -0.03f, 0.05f };
                rep.RawInputs.Controller = "right";
                obj.Repetitions.Add(rep);
            }

            obj.Accepted = new AcceptedBox
            {
                Box = obj.Repetitions[1].Box,
                GtSigma = new GtSigma { CentreCm = 0.4f, YawDeg = 0.7f, SizePct = 1.1f },
                N = 3,
                Method = "M1",
                Computed = AnnotationStore.Timestamp(),
            };
            obj.Verify = "ok";
            return file;
        }

        private static void TestJsonRoundTrip(Report r)
        {
            // Every file type: serialise → parse → serialise must give the same text (nothing lost or reordered).
            var catalog = new CatalogFile();
            catalog.Objects.Add(new ObjectEntry
            {
                Id = "chair_01",
                Name = "Chair_01",
                Category = "Chair",
                TapeDimsCm = new Dims { W = 62.5f, D = 60f, H = 96f },
                Symmetry = "none",
                FrontDefinition = "seat front edge",
                Notes = "5-star base, \"armrests\" count; ünïcödé",
                Photo = "chair_01.jpg",
                Created = AnnotationStore.Timestamp(),
            });
            var room = new RoomFile
            {
                Id = "lab",
                Name = "Lab",
                AnchorUuid = "0f8fad5b-d9cb-469f-a165-70867728950e",
                DriftReference = new[] { 2.1f, 0f, 0.4f },
                Created = AnnotationStore.Timestamp(),
            };
            room.DriftChecks.Add(new DriftCheck
            {
                Timestamp = AnnotationStore.Timestamp(),
                Kind = "drift_cross",
                When = "session_start",
                Position = new[] { 2.103f, 0.001f, 0.398f },
                DeltaMm = 3.7f,
            });
            var calibration = new CalibrationFile { Operator = "mh" };
            calibration.Tips.Add(new TipCalibration
            {
                Controller = "right",
                Offset = new[] { 0.001f, -0.032f, 0.051f },
                ResidualMm = 1.8f,
                NPoses = 100,
                IsDefault = false,
                Timestamp = AnnotationStore.Timestamp(),
            });

            var ok = true;
            var detail = "";
            ok &= RoundTrip<CatalogFile>(catalog, ref detail);
            ok &= RoundTrip<RoomFile>(room, ref detail);
            ok &= RoundTrip<AnnotationFile>(SampleAnnotations(), ref detail);
            ok &= RoundTrip<CalibrationFile>(calibration, ref detail);
            var json = AnnotationStore.ToJson(SampleAnnotations());
            var hasKeys = json.Contains("\"schema_version\": 1") && json.Contains("\"gt_definition\"") &&
                          json.Contains("\"bottom_center\"") && json.Contains("\"gt_sigma\"") && json.Contains("\"yaw_deg\"");
            r.Check("ann-json-roundtrip", ok && hasKeys, ok && hasKeys ? "catalog, room, annotations, calibration stable" : detail + " keys=" + hasKeys);

            var parsed = AnnotationStore.FromJson<AnnotationFile>(json);
            var rep = parsed.Find("chair_01").Repetitions[2];
            r.Check("ann-json-values",
                parsed.GtDefinition == GroundTruth.Definition && rep.Index == 3 && rep.Blind && !rep.Assisted && rep.Accepted &&
                rep.RawInputs.Touches[0].Label == "front_a" && Mathf.Abs(rep.Box.BottomCenter[0] - 1.003f) < 1e-6f &&
                parsed.Find("chair_01").Accepted.N == 3,
                $"rep={rep.Index} x={rep.Box.BottomCenter[0]} touches={rep.RawInputs.Touches.Count}");
        }

        private static bool RoundTrip<T>(T value, ref string detail)
        {
            var a = AnnotationStore.ToJson(value);
            var b = AnnotationStore.ToJson(AnnotationStore.FromJson<T>(a));
            if (a == b)
            {
                return true;
            }

            detail += typeof(T).Name + " changed after round trip; ";
            return false;
        }

        private static void TestStoreRestart(Report r)
        {
            var root = TempRoot();
            try
            {
                // "Session 1": create a room and an object, annotate.
                var store = new AnnotationStore(root);
                var room = store.CreateRoom(AnnotationStore.NextName("Room", new List<string>()));
                var catalog = store.LoadCatalog();
                var chair = store.CreateObject(catalog, "Chair_01");
                var second = store.CreateObject(catalog, "Chair 01"); // same slug → unique id
                var ann = SampleAnnotations();
                ann.RoomId = room.Id;
                store.SaveAnnotations(ann);
                store.SaveAnnotations(ann); // second save exercises the replace + .bak path
                store.AppendEvent("room_created", new Newtonsoft.Json.Linq.JObject { ["room_id"] = room.Id });
                store.AppendEvent("mode_exit");

                // "Restart": a fresh store on the same folder.
                var store2 = new AnnotationStore(root);
                var rooms = store2.ListRooms();
                var catalog2 = store2.LoadCatalog();
                var ann2 = store2.LoadAnnotations(room.Id);
                var ok = rooms.Count == 1 && rooms[0].Id == "room_01" && rooms[0].Name == "Room_01" &&
                         catalog2.Objects.Count == 2 && catalog2.Find(chair.Id).Name == "Chair_01" &&
                         second.Id == "chair_01_2" &&
                         ann2.Find("chair_01").Repetitions.Count == 3 &&
                         File.Exists(store2.AnnotationsPath(room.Id) + ".bak") &&
                         !File.Exists(store2.AnnotationsPath(room.Id) + ".tmp") &&
                         File.ReadAllLines(store2.EventsPath).Length == 2 &&
                         File.ReadAllLines(store2.EventsPath)[0].Contains("\"type\":\"room_created\",\"room_id\":\"room_01\"") &&
                         store2.LoadRoom("nope") == null && store2.LoadAnnotations("nope").Objects.Count == 0;
                r.Check("ann-store-restart", ok,
                    $"rooms={rooms.Count} objects={catalog2.Objects.Count} reps={ann2.Find("chair_01")?.Repetitions.Count} id2={second.Id}");
            }
            finally
            {
                Cleanup(root);
            }
        }

        private static void TestCorruptFile(Report r)
        {
            var root = TempRoot();
            try
            {
                var store = new AnnotationStore(root);
                var catalog = store.LoadCatalog();
                store.CreateObject(catalog, "Chair_01");
                store.CreateObject(catalog, "Chair_02"); // catalog.json.bak now holds the 1-object version
                File.WriteAllText(store.CatalogPath, "{ \"objects\": [ { \"id\": \"chair_01\", "); // truncated write
                var restored = new AnnotationStore(root).LoadCatalog();
                var aside = Directory.GetFiles(root, "catalog.json.corrupt-*").Length;
                r.Check("ann-store-corrupt", restored.Objects.Count == 1 && restored.Objects[0].Id == "chair_01" && aside == 1,
                    $"restored_objects={restored.Objects.Count} corrupt_files_kept={aside} (2 error lines above are expected)");
            }
            finally
            {
                Cleanup(root);
            }
        }
    }
}
#endif
