#if UNITY_EDITOR
// Exports the YOLOv9-t ONNX (Meta MultiObjectDetection sample, MIT) to Inference Engine .sentis with the same
// post-processing tail as Meta's converter (box centres → corners, class argmax, max score), at a chosen weight
// precision. The self-test found that Meta's prebuilt uint8 .sentis loses a lot of class confidence on furniture
// (chair 0.79 → 0.45, TV and a chair-seen-as-bench drop below threshold), so the app ships FP16 weights.
using System.IO;
using Unity.InferenceEngine;
using UnityEditor;
using UnityEngine;

namespace QuestDetect3D.EditorTools
{
    public static class YoloModelExport
    {
        public const string OnnxPath = "Assets/PassthroughCameraApiSamples/MultiObjectDetection/SentisInference/Model/yolov9onnx.onnx";
        public const string ExportFolder = "Assets/QuestDetect3D/Models";
        public const string Fp16Path = ExportFolder + "/yolov9t_fp16.sentis";

        public enum Precision
        {
            Fp32,
            Fp16,
            Uint8,
        }

        /// <summary>Build the deployable graph (outputs: corners N×4, classIds N, scores N) at the given precision.</summary>
        public static Model Build(Precision precision)
        {
            var onnx = AssetDatabase.LoadAssetAtPath<ModelAsset>(OnnxPath);
            if (onnx == null)
            {
                throw new System.Exception($"QuestDetect3D: ONNX model missing at {OnnxPath}");
            }

            var model = ModelLoader.Load(onnx);
            var graph = new FunctionalGraph();
            var input = graph.AddInput(model, 0);
            var centersToCorners = Functional.Constant(new TensorShape(4, 4), new[]
            {
                1f, 0f, 1f, 0f,
                0f, 1f, 0f, 1f,
                -0.5f, 0f, 0.5f, 0f,
                0f, -0.5f, 0f, 0.5f,
            });
            var output = Functional.Forward(model, input)[0];            // (1, 84, N)
            var boxCoords = output[0, ..4, ..].Transpose(0, 1);           // (N, 4) cx, cy, w, h
            var allScores = output[0, 4.., ..].Transpose(0, 1);           // (N, 80)
            var scores = Functional.ReduceMax(allScores, 1);
            var classIds = Functional.ArgMax(allScores, 1);
            var corners = Functional.MatMul(boxCoords, centersToCorners); // (N, 4) x1, y1, x2, y2
            var final = graph.Compile(corners, classIds, scores);
            if (precision == Precision.Fp16)
            {
                ModelQuantizer.QuantizeWeights(QuantizationType.Float16, ref final);
            }
            else if (precision == Precision.Uint8)
            {
                ModelQuantizer.QuantizeWeights(QuantizationType.Uint8, ref final);
            }

            return final;
        }

        /// <summary>Write the FP16 model used by the app (re-exported when the ONNX is newer).</summary>
        [MenuItem("QuestDetect3D/Export YOLO FP16 Model")]
        public static void EnsureExported()
        {
            if (File.Exists(Fp16Path) && File.GetLastWriteTimeUtc(Fp16Path) >= File.GetLastWriteTimeUtc(OnnxPath))
            {
                return;
            }

            Directory.CreateDirectory(ExportFolder);
            ModelWriter.Save(Fp16Path, Build(Precision.Fp16));
            AssetDatabase.ImportAsset(Fp16Path, ImportAssetOptions.ForceUpdate);
            Debug.Log($"QuestDetect3D: exported {Fp16Path} ({new FileInfo(Fp16Path).Length / 1024} KB)");
        }
    }
}
#endif
