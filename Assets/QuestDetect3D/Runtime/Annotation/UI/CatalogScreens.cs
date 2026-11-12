// Catalog: objects with name (keyboard), category, tape-measured W/D/H, symmetry, front definition, notes and a
// photo from the passthrough camera. Every change is saved at once and written to the audit trail.
using System;
using System.IO;
using System.Threading.Tasks;
using Newtonsoft.Json.Linq;
using Unity.Collections;
using UnityEngine;
using UnityEngine.Experimental.Rendering;

namespace QuestDetect3D.Annotation
{
    public sealed class CatalogScreen : IAnnScreen
    {
        private const int PageSize = 5;
        private readonly AnnotationMode m_mode;
        private int m_page;

        public CatalogScreen(AnnotationMode mode) => m_mode = mode;

        public string Title => "Catalog";

        /// <summary>Paged object list (name, category, tape, photo, annotation count) and Add object.</summary>
        public void Build(AnnPanel p)
        {
            var objects = m_mode.Catalog.Objects;
            p.Label(objects.Count == 0 ? "No objects yet. Press Add object." : $"{objects.Count} object(s). Select one to edit:", 24, AnnPanel.DimText);
            var pages = Mathf.Max(1, (objects.Count + PageSize - 1) / PageSize);
            m_page = Mathf.Clamp(m_page, 0, pages - 1);
            for (var i = m_page * PageSize; i < Mathf.Min(objects.Count, (m_page + 1) * PageSize); i++)
            {
                var o = objects[i];
                var tape = o.TapeDimsCm != null && o.TapeDimsCm.IsSet
                    ? $"{o.TapeDimsCm.W:0.#}×{o.TapeDimsCm.D:0.#}×{o.TapeDimsCm.H:0.#} cm"
                    : "no tape dims";
                var reps = m_mode.CurrentRoom != null ? $"{m_mode.RepetitionCount(o.Id)} ann." : "";
                var photo = string.IsNullOrEmpty(o.Photo) ? "" : " · photo";
                p.Button($"{o.Name}  ·  {o.Category}  ·  {tape}{photo}  ·  {reps}", () => m_mode.Show(new ObjectEditScreen(m_mode, o)),
                    true, null, 58, 26);
            }

            Pager.Build(p, m_page, pages, delta =>
            {
                m_page += delta;
                m_mode.Refresh();
            });
            p.BeginRow();
            p.Button("Add object", () =>
            {
                var entry = m_mode.NewObject();
                m_mode.Show(new ObjectEditScreen(m_mode, entry));
            }, true, AnnPanel.PrimaryButton);
            p.Button("Back", () => m_mode.Show(new HomeScreen(m_mode)));
            p.EndRow();
        }
    }

    public sealed class ObjectEditScreen : IAnnScreen, IAnnLeaveScreen
    {
        private static readonly string[] Symmetries = { "none", "2", "4" };
        private readonly AnnotationMode m_mode;
        private readonly ObjectEntry m_obj;
        private int m_tab;
        private Texture2D m_thumb;
        private bool m_thumbLoaded;
        private string m_photoStatus = "";
        private PcaFrameSource m_frames;

        public ObjectEditScreen(AnnotationMode mode, ObjectEntry obj)
        {
            m_mode = mode;
            m_obj = obj;
        }

        public string Title => "Object: " + m_obj.Name;

        /// <summary>Tabs Identity / Tape dims / Photo and Back.</summary>
        public void Build(AnnPanel p)
        {
            p.BeginRow();
            p.Button("Identity", () => SetTab(0), true, m_tab == 0 ? AnnPanel.PrimaryButton : AnnPanel.ButtonColor, 56);
            p.Button("Tape dims", () => SetTab(1), true, m_tab == 1 ? AnnPanel.PrimaryButton : AnnPanel.ButtonColor, 56);
            p.Button("Photo", () => SetTab(2), true, m_tab == 2 ? AnnPanel.PrimaryButton : AnnPanel.ButtonColor, 56);
            p.EndRow();
            switch (m_tab)
            {
                case 0:
                    BuildIdentity(p);
                    break;
                case 1:
                    BuildDims(p);
                    break;
                default:
                    BuildPhoto(p);
                    break;
            }

            p.Button("Back to catalog", () => m_mode.Show(new CatalogScreen(m_mode)), true, null, 56);
        }

        /// <summary>Switches tab.</summary>
        private void SetTab(int tab)
        {
            m_tab = tab;
            m_mode.Refresh();
        }

        /// <summary>Name, category, symmetry, front definition, notes.</summary>
        private void BuildIdentity(AnnPanel p)
        {
            p.BeginRow();
            p.Label($"Name: <b>{m_obj.Name}</b>");
            p.Button("Edit name", () => EditText("Object name", m_obj.Name, v =>
            {
                if (!string.IsNullOrEmpty(v))
                {
                    Change("name", m_obj.Name, v, () => m_obj.Name = v);
                }
            }), true, null, 56);
            p.EndRow();

            p.BeginRow();
            p.Button("<", () => CycleCategory(-1), true, null, 56);
            p.Button($"Category: {m_obj.Category}", null, false, null, 56);
            p.Button(">", () => CycleCategory(1), true, null, 56);
            p.Button("Custom", () => EditText("Category", m_obj.Category, v =>
            {
                if (!string.IsNullOrEmpty(v))
                {
                    Change("category", m_obj.Category, v, () => m_obj.Category = v);
                }
            }), true, null, 56);
            p.EndRow();

            p.BeginRow();
            p.Button("<", () => CycleSymmetry(-1), true, null, 56);
            p.Button($"Symmetry: {SymmetryText(m_obj.Symmetry)}", null, false, null, 56);
            p.Button(">", () => CycleSymmetry(1), true, null, 56);
            p.EndRow();

            p.BeginRow();
            p.Label("Front: " + (string.IsNullOrEmpty(m_obj.FrontDefinition) ? "<color=#8A96A3>(not set)</color>" : m_obj.FrontDefinition), 24);
            p.Button("Edit", () => EditText("What is the front?", m_obj.FrontDefinition, v =>
                Change("front_definition", m_obj.FrontDefinition, v, () => m_obj.FrontDefinition = v)), true, null, 56);
            p.EndRow();
            p.BeginRow();
            p.Label("Notes: " + (string.IsNullOrEmpty(m_obj.Notes) ? "<color=#8A96A3>(none)</color>" : m_obj.Notes), 24);
            p.Button("Edit", () => EditText("Notes", m_obj.Notes, v => Change("notes", m_obj.Notes, v, () => m_obj.Notes = v)), true, null, 56);
            p.EndRow();
        }

        /// <summary>Tape W / D / H rows.</summary>
        private void BuildDims(AnnPanel p)
        {
            p.Label("Tape-measured size of the ground-truth box (extremes, cm). Shown live against the annotated box.", 22, AnnPanel.DimText);
            DimRow(p, "W (width, left-right)", () => m_obj.TapeDimsCm.W, v => m_obj.TapeDimsCm.W = v, "tape_w_cm");
            DimRow(p, "D (depth, front-back)", () => m_obj.TapeDimsCm.D, v => m_obj.TapeDimsCm.D = v, "tape_d_cm");
            DimRow(p, "H (height, floor-top)", () => m_obj.TapeDimsCm.H, v => m_obj.TapeDimsCm.H = v, "tape_h_cm");
        }

        /// <summary>One dimension: value, −1 / −0.1 / +0.1 / +1 cm and Type (numeric keyboard).</summary>
        private void DimRow(AnnPanel p, string name, Func<float> get, Action<float> set, string field)
        {
            p.Label($"{name}: <b>{(get() > 0f ? get().ToString("0.0") + " cm" : "—")}</b>", 26);
            p.BeginRow();
            foreach (var step in new[] { -1f, -0.1f, 0.1f, 1f })
            {
                var s = step;
                p.Button(s > 0 ? $"+{s:0.#}" : $"{s:0.#}", () =>
                {
                    var old = get();
                    var v = Mathf.Max(0f, Mathf.Round((old + s) * 10f) / 10f);
                    Change(field, old.ToString("0.0"), v.ToString("0.0"), () => set(v));
                }, true, null, 52, 26);
            }

            p.Button("Type", () => EditText(name + " (cm)", get() > 0f ? get().ToString("0.0", System.Globalization.CultureInfo.InvariantCulture) : "", true, v =>
            {
                if (float.TryParse(v.Replace(',', '.'), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var f) &&
                    f >= 0f && f < 1000f)
                {
                    var old = get();
                    Change(field, old.ToString("0.0"), f.ToString("0.0"), () => set(f));
                }
            }), true, null, 52, 26);
            p.EndRow();
        }

        /// <summary>Thumbnail and Take photo.</summary>
        private void BuildPhoto(AnnPanel p)
        {
            if (!m_thumbLoaded)
            {
                LoadThumb();
            }

            if (m_thumb != null)
            {
                p.Image(m_thumb, 300f);
            }
            else
            {
                p.Label("No photo yet.", 26, AnnPanel.DimText);
            }

            if (!string.IsNullOrEmpty(m_photoStatus))
            {
                p.Label(m_photoStatus, 24, AnnPanel.Warning);
            }

            p.Button("Take photo (look at the object)", TakePhoto, m_mode.Context.Camera != null && m_mode.Context.Camera.IsPlaying,
                AnnPanel.PrimaryButton);
        }

        /// <summary>Loads photos/&lt;id&gt;.jpg for display.</summary>
        private void LoadThumb()
        {
            m_thumbLoaded = true;
            if (string.IsNullOrEmpty(m_obj.Photo))
            {
                return;
            }

            var path = Path.Combine(m_mode.Store.PhotosDir, m_obj.Photo);
            if (!File.Exists(path))
            {
                return;
            }

            m_thumb = new Texture2D(2, 2, TextureFormat.RGBA32, false);
            m_thumb.LoadImage(File.ReadAllBytes(path));
        }

        /// <summary>Captures the passthrough camera image, shows it and saves a JPEG on a worker thread.</summary>
        private void TakePhoto()
        {
            m_frames ??= new PcaFrameSource(m_mode.Context.Camera);
            m_photoStatus = "Capturing…";
            m_mode.Refresh();
            var ok = m_frames.TryCapture((rgba, ctx) =>
            {
                var w = ctx.Camera.Resolution.x;
                var h = ctx.Camera.Resolution.y;
                var bytes = new byte[rgba.Length];
                NativeArray<byte>.Copy(rgba, bytes, rgba.Length);
                // Thumbnail on the main thread (same bytes, bottom-up like every Unity texture).
                if (m_thumb == null || m_thumb.width != w || m_thumb.height != h)
                {
                    m_thumb = new Texture2D(w, h, TextureFormat.RGBA32, false);
                }

                m_thumb.LoadRawTextureData(bytes);
                m_thumb.Apply(false);
                var file = m_obj.Id + ".jpg";
                var path = Path.Combine(m_mode.Store.PhotosDir, file);
                var cam = ctx.Camera;
                Task.Run(() =>
                {
                    try
                    {
                        var jpg = ImageConversion.EncodeArrayToJPG(bytes, GraphicsFormat.R8G8B8A8_UNorm, (uint)w, (uint)h, 0, 90);
                        File.WriteAllBytes(path, jpg);
                        m_mode.RunOnMain(() => PhotoSaved(file, cam));
                    }
                    catch (Exception e)
                    {
                        m_mode.RunOnMain(() =>
                        {
                            // Some Unity versions only encode on the main thread.
                            try
                            {
                                File.WriteAllBytes(path, m_thumb.EncodeToJPG(90));
                                PhotoSaved(file, cam);
                            }
                            catch (Exception e2)
                            {
                                m_photoStatus = "Photo failed: " + e2.Message + " / " + e.Message;
                                m_mode.Refresh();
                            }
                        });
                    }
                });
            });
            if (!ok)
            {
                m_photoStatus = "Camera busy, try again.";
                m_mode.Refresh();
            }
        }

        /// <summary>Stores the photo file name in the catalog (audited, with the camera pose).</summary>
        private void PhotoSaved(string file, CameraModel cam)
        {
            var old = m_obj.Photo;
            m_obj.Photo = file;
            m_mode.SaveCatalog();
            m_mode.Audit("object_photo", new JObject
            {
                ["object_id"] = m_obj.Id,
                ["file"] = file,
                ["previous"] = old,
                ["camera"] = Recording.RecordJson.Camera(cam),
            });
            m_photoStatus = "Photo saved: photos/" + file;
            m_mode.Refresh();
        }

        /// <summary>Next / previous ObjectCategory.</summary>
        private void CycleCategory(int dir)
        {
            var cycle = Categories.Cycle;
            var index = Array.FindIndex(cycle, c => c.ToString() == m_obj.Category);
            var next = cycle[((index < 0 ? 0 : index) + dir + cycle.Length) % cycle.Length].ToString();
            Change("category", m_obj.Category, next, () => m_obj.Category = next);
        }

        /// <summary>none → 2-fold → 4-fold (used for yaw alignment and symmetry-aware yaw error).</summary>
        private void CycleSymmetry(int dir)
        {
            var index = Math.Max(0, Array.IndexOf(Symmetries, m_obj.Symmetry));
            var next = Symmetries[(index + dir + Symmetries.Length) % Symmetries.Length];
            Change("symmetry", m_obj.Symmetry, next, () => m_obj.Symmetry = next);
        }

        /// <summary>Display text of a symmetry value.</summary>
        public static string SymmetryText(string s) => s == "2" ? "2-fold (180°)" : s == "4" ? "4-fold (90°)" : "none";

        /// <summary>Opens the text keyboard.</summary>
        private void EditText(string title, string initial, Action<string> done) => EditText(title, initial, false, done);

        /// <summary>Opens the keyboard (text or numeric).</summary>
        private void EditText(string title, string initial, bool numeric, Action<string> done)
        {
            m_mode.Keyboard.Open(title, initial, numeric, done);
        }

        /// <summary>Applies a field change, saves the catalog and audits old → new.</summary>
        private void Change(string field, string oldValue, string newValue, Action apply)
        {
            if (oldValue == newValue)
            {
                return;
            }

            apply();
            m_mode.SaveCatalog();
            m_mode.Audit("object_edited", new JObject
            {
                ["object_id"] = m_obj.Id,
                ["field"] = field,
                ["old"] = oldValue,
                ["new"] = newValue,
            });
            Log.Info($"ann_object_edited id={m_obj.Id} {field}: '{oldValue}' -> '{newValue}'");
            m_mode.Refresh();
        }

        /// <summary>Frees the thumbnail texture.</summary>
        public void OnLeave()
        {
            if (m_thumb != null)
            {
                UnityEngine.Object.Destroy(m_thumb);
                m_thumb = null;
            }

            m_thumbLoaded = false;
        }
    }
}
