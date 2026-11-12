// Data model of QD3D Annotate (JSON files under <persistentDataPath>/QD3D, see AnnotationStore).
// Plain classes with public fields and no Unity object types, so they serialise with Newtonsoft on the device
// (IL2CPP) and compile offline under mono. Units: metres and degrees unless the field name says otherwise.
// Times: ISO-8601 UTC strings. Vectors: float[3] = [x, y, z].
using System.Collections.Generic;
using Newtonsoft.Json;
using UnityEngine;

namespace QuestDetect3D.Annotation
{
    public static class Schema
    {
        public const int Version = 1;
    }

    // ------------------------------------------------------------------ catalog.json

    public sealed class Dims
    {
        [JsonProperty("w")] public float W;
        [JsonProperty("d")] public float D;
        [JsonProperty("h")] public float H;

        [JsonIgnore] public bool IsSet => W > 0f && D > 0f && H > 0f;
    }

    public sealed class ObjectEntry
    {
        [JsonProperty("id")] public string Id;
        [JsonProperty("name")] public string Name;
        /// <summary>ObjectCategory name ("Chair") or a custom string.</summary>
        [JsonProperty("category")] public string Category = "Chair";
        /// <summary>Tape-measured width / depth / height in centimetres (0 = not measured yet).</summary>
        [JsonProperty("tape_dims_cm")] public Dims TapeDimsCm = new Dims();
        /// <summary>"none", "2" (180° symmetric) or "4" (90° symmetric): used for symmetry-aware yaw error.</summary>
        [JsonProperty("symmetry")] public string Symmetry = "none";
        [JsonProperty("front_definition")] public string FrontDefinition = "";
        [JsonProperty("notes")] public string Notes = "";
        /// <summary>Photo file name relative to QD3D/photos, or null.</summary>
        [JsonProperty("photo")] public string Photo;
        [JsonProperty("created")] public string Created;
    }

    public sealed class CatalogFile
    {
        [JsonProperty("schema_version")] public int SchemaVersion = Schema.Version;
        [JsonProperty("objects")] public List<ObjectEntry> Objects = new List<ObjectEntry>();

        public ObjectEntry Find(string id)
        {
            foreach (var o in Objects)
            {
                if (o.Id == id)
                {
                    return o;
                }
            }

            return null;
        }
    }

    // ------------------------------------------------------------------ rooms/<roomId>.json

    public sealed class DriftCheck
    {
        [JsonProperty("timestamp")] public string Timestamp;
        /// <summary>"drift_cross" (second tape cross) or "room_cross" (re-touch of the room origin).</summary>
        [JsonProperty("kind")] public string Kind;
        /// <summary>"session_start", "session_end" or "on_demand".</summary>
        [JsonProperty("when")] public string When;
        [JsonProperty("session_id")] public string SessionId;
        /// <summary>Touched point in the room frame.</summary>
        [JsonProperty("position")] public float[] Position;
        /// <summary>Distance to the reference position of that cross, millimetres.</summary>
        [JsonProperty("delta_mm")] public float DeltaMm;
    }

    public sealed class RoomFile
    {
        [JsonProperty("schema_version")] public int SchemaVersion = Schema.Version;
        [JsonProperty("id")] public string Id;
        [JsonProperty("name")] public string Name;
        /// <summary>UUID of the persisted OVRSpatialAnchor at the room cross; null until the cross is placed.</summary>
        [JsonProperty("anchor_uuid")] public string AnchorUuid;
        [JsonProperty("anchor_saved")] public string AnchorSaved;
        [JsonProperty("frame_definition")] public string FrameDefinition = GroundTruth.RoomFrameDefinition;
        /// <summary>Reference position of the drift cross in the room frame (first touch); null until set.</summary>
        [JsonProperty("drift_reference")] public float[] DriftReference;
        [JsonProperty("drift_checks")] public List<DriftCheck> DriftChecks = new List<DriftCheck>();
        [JsonProperty("notes")] public string Notes = "";
        [JsonProperty("created")] public string Created;
    }

    // ------------------------------------------------------------------ annotations/<roomId>.json

    public sealed class BoxDto
    {
        [JsonProperty("bottom_center")] public float[] BottomCenter = new float[3];
        [JsonProperty("yaw_deg")] public float YawDeg;
        /// <summary>[width (local X), height (Y), depth (local Z)], metres.</summary>
        [JsonProperty("size")] public float[] Size = new float[3];
        /// <summary>How many 90° steps the operator turned the front arrow after the method set the yaw (0–3).
        /// Already applied to yaw_deg and size; kept for the audit trail.</summary>
        [JsonProperty("front_quarter")] public int FrontQuarter;

        public static BoxDto From(UprightBox box, int frontQuarter = 0)
        {
            return new BoxDto
            {
                BottomCenter = Vec.ToArray(box.BottomCenter),
                YawDeg = box.Yaw * Mathf.Rad2Deg,
                Size = Vec.ToArray(box.Size),
                FrontQuarter = frontQuarter,
            };
        }

        public UprightBox ToUpright()
        {
            return new UprightBox(Vec.FromArray(BottomCenter), YawDeg * Mathf.Deg2Rad, Vec.FromArray(Size));
        }
    }

    public sealed class TouchInput
    {
        /// <summary>"front_a", "front_b", "front", "back", "left", "right", "top".</summary>
        [JsonProperty("label")] public string Label;
        /// <summary>Touched / hit point in the room frame.</summary>
        [JsonProperty("point")] public float[] Point;
        /// <summary>Seconds since the annotation started.</summary>
        [JsonProperty("t")] public float T;
    }

    public sealed class EditOp
    {
        /// <summary>e.g. "face_push:+x", "move", "rotate", "resize", "floor_snap", "front_quarter".</summary>
        [JsonProperty("op")] public string Op;
        [JsonProperty("value")] public float[] Value;
        [JsonProperty("t")] public float T;
    }

    public sealed class RawInputs
    {
        [JsonProperty("touches")] public List<TouchInput> Touches = new List<TouchInput>();
        [JsonProperty("edits")] public List<EditOp> Edits = new List<EditOp>();
        /// <summary>Box straight out of the method, before the editor.</summary>
        [JsonProperty("box_before_edit")] public BoxDto BoxBeforeEdit;
        /// <summary>Floor height used (room frame y).</summary>
        [JsonProperty("floor_y")] public float FloorY;
        /// <summary>Tip offset in the controller frame that was active, and whether it was the shipped default.</summary>
        [JsonProperty("tip_offset")] public float[] TipOffset;
        [JsonProperty("tip_is_default")] public bool TipIsDefault = true;
        [JsonProperty("controller")] public string Controller;
    }

    public sealed class Repetition
    {
        /// <summary>"M1" Touch, "M2" Point, "M4" Assisted.</summary>
        [JsonProperty("method")] public string Method;
        /// <summary>True when the box started from detector output: never used as primary ground truth.</summary>
        [JsonProperty("assisted")] public bool Assisted;
        /// <summary>1-based index within this object + method.</summary>
        [JsonProperty("repetition")] public int Index;
        /// <summary>True when earlier boxes of this object were hidden while annotating.</summary>
        [JsonProperty("blind")] public bool Blind;
        [JsonProperty("box")] public BoxDto Box;
        [JsonProperty("raw_inputs")] public RawInputs RawInputs = new RawInputs();
        [JsonProperty("operator")] public string Operator;
        [JsonProperty("timestamp")] public string Timestamp;
        [JsonProperty("duration_s")] public float DurationS;
        /// <summary>Operator's own confidence, 1 (unsure) – 5 (sure); 0 = not rated.</summary>
        [JsonProperty("self_rating")] public int SelfRating;
        /// <summary>False when the operator discarded this repetition (kept on file, excluded from the mean).</summary>
        [JsonProperty("accepted")] public bool Accepted = true;
    }

    public sealed class GtSigma
    {
        [JsonProperty("centre_cm")] public float CentreCm;
        [JsonProperty("yaw_deg")] public float YawDeg;
        [JsonProperty("size_pct")] public float SizePct;
    }

    public sealed class AcceptedBox
    {
        [JsonProperty("box")] public BoxDto Box;
        [JsonProperty("gt_sigma")] public GtSigma GtSigma = new GtSigma();
        /// <summary>Number of repetitions the robust mean was computed from.</summary>
        [JsonProperty("n")] public int N;
        [JsonProperty("method")] public string Method;
        [JsonProperty("computed")] public string Computed;
    }

    public sealed class ObjectAnnotations
    {
        [JsonProperty("object_id")] public string ObjectId;
        /// <summary>Every repetition ever made, in order. Never deleted.</summary>
        [JsonProperty("repetitions")] public List<Repetition> Repetitions = new List<Repetition>();
        /// <summary>Robust mean of the accepted, non-assisted repetitions; null until there is one.</summary>
        [JsonProperty("accepted")] public AcceptedBox Accepted;
        /// <summary>"ok", "redo" or null (not verified yet).</summary>
        [JsonProperty("verify")] public string Verify;
        [JsonProperty("verify_time")] public string VerifyTime;
    }

    public sealed class AnnotationFile
    {
        [JsonProperty("schema_version")] public int SchemaVersion = Schema.Version;
        [JsonProperty("room_id")] public string RoomId;
        [JsonProperty("gt_definition")] public string GtDefinition = GroundTruth.Definition;
        [JsonProperty("gt_definition_version")] public int GtDefinitionVersion = GroundTruth.DefinitionVersion;
        [JsonProperty("box_convention")] public string BoxConvention = GroundTruth.BoxConvention;
        [JsonProperty("objects")] public List<ObjectAnnotations> Objects = new List<ObjectAnnotations>();

        public ObjectAnnotations Find(string objectId)
        {
            foreach (var o in Objects)
            {
                if (o.ObjectId == objectId)
                {
                    return o;
                }
            }

            return null;
        }

        public ObjectAnnotations GetOrAdd(string objectId)
        {
            var o = Find(objectId);
            if (o == null)
            {
                o = new ObjectAnnotations { ObjectId = objectId };
                Objects.Add(o);
            }

            return o;
        }
    }

    // ------------------------------------------------------------------ calibration.json

    public sealed class TipCalibration
    {
        /// <summary>"left" or "right".</summary>
        [JsonProperty("controller")] public string Controller;
        /// <summary>Tip position in the controller frame, metres.</summary>
        [JsonProperty("offset")] public float[] Offset;
        [JsonProperty("residual_mm")] public float ResidualMm;
        [JsonProperty("n_poses")] public int NPoses;
        [JsonProperty("is_default")] public bool IsDefault = true;
        [JsonProperty("timestamp")] public string Timestamp;
    }

    public sealed class CalibrationFile
    {
        [JsonProperty("schema_version")] public int SchemaVersion = Schema.Version;
        [JsonProperty("operator")] public string Operator = "";
        [JsonProperty("tips")] public List<TipCalibration> Tips = new List<TipCalibration>();
    }

    public static class Vec
    {
        public static float[] ToArray(Vector3 v) => new[] { v.x, v.y, v.z };

        public static Vector3 FromArray(float[] a)
        {
            return a != null && a.Length >= 3 ? new Vector3(a[0], a[1], a[2]) : new Vector3(0f, 0f, 0f);
        }
    }
}
