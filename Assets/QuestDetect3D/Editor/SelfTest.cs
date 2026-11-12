#if UNITY_EDITOR
// Headset-free self-test, run inside Unity (menu QuestDetect3D → Run Self-Test, and before every batch build).
//   1. The 3D pipeline on a synthetic room (Tests/PipelineTests): YOLO decoding, depth box fitting from many
//      viewpoints, a full walk-around with tracking/class voting, backrest side.
//   2. The YOLO model variants on Unity Inference Engine (CPU backend, works in -nographics batch mode) on chair
//      photos, compared with onnxruntime FP32 results computed offline (TestImages/expected.txt): Meta's prebuilt
//      uint8 .sentis and our own FP32 / FP16 / uint8 exports. Proves the model loads, the output layout/decoding
//      and top-left → viewport conversion are right, and measures what weight quantisation costs (RQ1).
//      The FP16 export (what the app ships) must pass.
//   3. QD3D Annotate pure logic (Tests/AnnotationTests): JSON persistence, ids, box conversion.
// Writes Builds/selftest.txt. Never throws into the build.
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Text;
using QuestDetect3D.Tests;
using Unity.InferenceEngine;
using UnityEditor;
using UnityEngine;
using Debug = UnityEngine.Debug;

namespace QuestDetect3D.EditorTools
{
    public static class SelfTest
    {
        private const string ImageFolder = "Assets/QuestDetect3D/Editor/TestImages";
        public const string ReportPath = "Builds/selftest.txt";

        [MenuItem("QuestDetect3D/Run Self-Test")]
        public static void RunMenu()
        {
            Run();
            EditorUtility.DisplayDialog("QuestDetect3D self-test", File.ReadAllText(ReportPath), "OK");
        }

        public static void Run()
        {
            string text;
            try
            {
                text = RunInternal();
            }
            catch (Exception e)
            {
                text = "SELFTEST EXCEPTION\n" + e;
            }

            Directory.CreateDirectory("Builds");
            File.WriteAllText(ReportPath, text);
            Debug.Log("QuestDetect3D SELFTEST\n" + text);
        }

        private static string RunInternal()
        {
            var sb = new StringBuilder();
            sb.AppendLine($"QuestDetect3D self-test {DateTime.Now:yyyy-MM-dd HH:mm:ss}  unity={Application.unityVersion}");
            sb.AppendLine("--- 1. 3D pipeline on synthetic room ---");
            var pipeline = PipelineTests.RunAll();
            sb.Append(pipeline);
            sb.AppendLine("--- 2. YOLO11n@320 variants (Inference Engine CPU) on photos vs onnxruntime FP32 ---");
            var failed = 0;
            foreach (var precision in new[] { YoloModelExport.Precision.Fp32, YoloModelExport.Precision.Uint8, YoloModelExport.Precision.Fp16 })
            {
                var name = "ours-" + precision.ToString().ToLowerInvariant();
                var (p, f, text) = YoloOnPhotos(YoloModelExport.Build(precision), name);
                sb.Append(text);
                sb.AppendLine($"== {name}: {p} passed, {f} failed{(precision == YoloModelExport.Precision.Fp16 ? "  (deployed model: gates the self-test)" : "")} ==");
                if (precision == YoloModelExport.Precision.Fp16)
                {
                    failed += f;
                }
            }

            sb.AppendLine("--- 3. QD3D Annotate logic ---");
            var annotation = AnnotationTests.RunAll();
            sb.Append(annotation);
            var total = pipeline.Failed + failed + annotation.Failed;
            sb.AppendLine(total == 0 ? "SELFTEST OK" : $"SELFTEST FAILED ({total} failures)");
            return sb.ToString();
        }

        private static (int, int, string) YoloOnPhotos(Model model, string variant)
        {
            var sb = new StringBuilder();
            var passed = 0;
            var failed = 0;
            var labelsAsset = AssetDatabase.LoadAssetAtPath<TextAsset>(SceneBuilder.YoloLabelsPath);
            if (labelsAsset == null)
            {
                return (0, 1, $"[FAIL] labels missing at {SceneBuilder.YoloLabelsPath}\n");
            }

            var labels = labelsAsset.text.Replace("\r", "").Split(new[] { '\n' }, StringSplitOptions.RemoveEmptyEntries);
            var w = model.inputs[0].shape.Get(3);
            var h = model.inputs[0].shape.Get(2);
            sb.AppendLine($"[{variant}] inputs={model.inputs.Count} outputs={model.outputs.Count} input={w}x{h}");

            var expected = LoadExpected(Path.Combine(ImageFolder, "expected.txt"));
            using (var worker = new Worker(model, BackendType.CPU))
            {
                foreach (var image in expected.Keys)
                {
                    var path = Path.Combine(ImageFolder, image + ".jpg");
                    if (!File.Exists(path))
                    {
                        failed++;
                        sb.AppendLine($"[FAIL] {image}: missing {path}");
                        continue;
                    }

                    var tex = new Texture2D(2, 2, TextureFormat.RGBA32, false);
                    tex.LoadImage(File.ReadAllBytes(path));
                    var data = StretchToTensor(tex, w, h);
                    UnityEngine.Object.DestroyImmediate(tex);

                    var sw = Stopwatch.StartNew();
                    float[] boxes;
                    int[] classes;
                    float[] scores;
                    using (var input = new Tensor<float>(new TensorShape(1, 3, h, w), data))
                    {
                        worker.Schedule(input);
                        using (var b = (worker.PeekOutput(0) as Tensor<float>).ReadbackAndClone())
                        using (var c = (worker.PeekOutput(1) as Tensor<int>).ReadbackAndClone())
                        using (var s = (worker.PeekOutput(2) as Tensor<float>).ReadbackAndClone())
                        {
                            boxes = b.DownloadToArray();
                            classes = c.DownloadToArray();
                            scores = s.DownloadToArray();
                        }
                    }

                    var ms = sw.ElapsedMilliseconds;
                    var dets = new List<Detection2D>();
                    YoloDecode.Decode(boxes, classes, scores, scores.Length, new Vector2Int(w, h), labels, 0.25f, 0.55f, dets);
                    sb.AppendLine($"  {image}: {ms} ms (editor CPU) → {string.Join("; ", dets)}");

                    foreach (var exp in expected[image])
                    {
                        if (!Categories.TryFromCocoLabel(exp.Label, out _))
                        {
                            continue; // class not used by the app (e.g. teddy bear)
                        }

                        Detection2D match = null;
                        var bestIou = 0f;
                        foreach (var d in dets)
                        {
                            var iou = IoU(d.Viewport, exp.Viewport);
                            if (d.Label == exp.Label && iou > bestIou)
                            {
                                bestIou = iou;
                                match = d;
                            }
                        }

                        var ok = match != null && bestIou > 0.85f && Mathf.Abs(match.Score - exp.Score) < 0.12f;
                        if (ok)
                        {
                            passed++;
                        }
                        else
                        {
                            failed++;
                        }

                        sb.AppendLine($"  [{(ok ? "PASS" : "FAIL")}] {variant} {image}/{exp.Label}: onnx score {exp.Score:F2}, " +
                                      (match == null ? "no match" : $"got {match.Score:F2}, IoU {bestIou:F3}"));
                    }
                }
            }

            return (passed, failed, sb.ToString());
        }

        /// <summary>Bilinear stretch to the network input, CHW, [0,1], row 0 = image TOP (TextureConverter default).</summary>
        private static float[] StretchToTensor(Texture2D tex, int w, int h)
        {
            var px = tex.GetPixels32(); // row 0 = bottom
            var W = tex.width;
            var H = tex.height;
            var data = new float[3 * w * h];
            for (var y = 0; y < h; y++)
            {
                var sy = Mathf.Clamp((y + 0.5f) * H / h - 0.5f, 0f, H - 1); // image coordinates, top-down
                var y0 = Mathf.FloorToInt(sy);
                var y1 = Mathf.Min(y0 + 1, H - 1);
                var fy = sy - y0;
                for (var x = 0; x < w; x++)
                {
                    var sx = Mathf.Clamp((x + 0.5f) * W / w - 0.5f, 0f, W - 1);
                    var x0 = Mathf.FloorToInt(sx);
                    var x1 = Mathf.Min(x0 + 1, W - 1);
                    var fx = sx - x0;
                    var a = px[(H - 1 - y0) * W + x0];
                    var b = px[(H - 1 - y0) * W + x1];
                    var c = px[(H - 1 - y1) * W + x0];
                    var d = px[(H - 1 - y1) * W + x1];
                    for (var ch = 0; ch < 3; ch++)
                    {
                        float A = ch == 0 ? a.r : ch == 1 ? a.g : a.b;
                        float B = ch == 0 ? b.r : ch == 1 ? b.g : b.b;
                        float C = ch == 0 ? c.r : ch == 1 ? c.g : c.b;
                        float D = ch == 0 ? d.r : ch == 1 ? d.g : d.b;
                        var v = ((A * (1 - fx) + B * fx) * (1 - fy) + (C * (1 - fx) + D * fx) * fy) / 255f;
                        data[ch * w * h + y * w + x] = v;
                    }
                }
            }

            return data;
        }

        private struct Expected
        {
            public string Label;
            public float Score;
            public Rect Viewport;
        }

        private static Dictionary<string, List<Expected>> LoadExpected(string path)
        {
            var result = new Dictionary<string, List<Expected>>();
            foreach (var raw in File.ReadAllLines(path))
            {
                var line = raw.Trim();
                if (line.Length == 0 || line.StartsWith("#"))
                {
                    continue;
                }

                var f = line.Split('|');
                var inv = System.Globalization.CultureInfo.InvariantCulture;
                var e = new Expected
                {
                    Label = f[1],
                    Score = float.Parse(f[2], inv),
                    Viewport = Rect.MinMaxRect(float.Parse(f[3], inv), float.Parse(f[4], inv), float.Parse(f[5], inv),
                        float.Parse(f[6], inv)),
                };
                if (!result.TryGetValue(f[0], out var list))
                {
                    result[f[0]] = list = new List<Expected>();
                }

                list.Add(e);
            }

            return result;
        }

        private static float IoU(Rect a, Rect b)
        {
            var iw = Mathf.Max(0f, Mathf.Min(a.xMax, b.xMax) - Mathf.Max(a.xMin, b.xMin));
            var ih = Mathf.Max(0f, Mathf.Min(a.yMax, b.yMax) - Mathf.Max(a.yMin, b.yMin));
            var inter = iw * ih;
            var union = a.width * a.height + b.width * b.height - inter;
            return union > 0f ? inter / union : 0f;
        }
    }
}
#endif
