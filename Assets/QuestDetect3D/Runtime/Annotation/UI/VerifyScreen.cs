// Verify: every accepted ground-truth box of the room overlaid (one colour per object, edge thickness grows with
// gt_sigma). Walk around and mark each object OK or Redo. Optionally show all repetitions.
using Newtonsoft.Json.Linq;
using UnityEngine;

namespace QuestDetect3D.Annotation
{
    public sealed class VerifyScreen : IAnnTickScreen
    {
        private const int PageSize = 4;
        private static readonly Color[] Palette =
        {
            new Color(0.3f, 0.9f, 1f, 1f), new Color(1f, 0.75f, 0.2f, 1f), new Color(0.5f, 1f, 0.45f, 1f),
            new Color(1f, 0.45f, 0.75f, 1f), new Color(0.75f, 0.6f, 1f, 1f), new Color(1f, 1f, 0.45f, 1f),
        };

        private readonly AnnotationMode m_mode;
        private bool m_showReps;
        private int m_page;

        public VerifyScreen(AnnotationMode mode) => m_mode = mode;

        public string Title => "Verify ground truth";

        /// <summary>Per object: accepted method, n, σ, verdict, OK / Redo; show repetitions toggle.</summary>
        public void Build(AnnPanel p)
        {
            var objects = m_mode.Catalog.Objects;
            var withGt = 0;
            foreach (var o in objects)
            {
                if (m_mode.Annotations?.Find(o.Id)?.Accepted != null)
                {
                    withGt++;
                }
            }

            p.Label($"{withGt} object(s) with an accepted box. Walk around: boxes must stay on the objects from every side.", 24);
            var pages = Mathf.Max(1, (objects.Count + PageSize - 1) / PageSize);
            m_page = Mathf.Clamp(m_page, 0, pages - 1);
            for (var i = m_page * PageSize; i < Mathf.Min(objects.Count, (m_page + 1) * PageSize); i++)
            {
                var o = objects[i];
                var ann = m_mode.Annotations?.Find(o.Id);
                var acc = ann?.Accepted;
                p.BeginRow();
                var status = acc == null ? "no box" : $"{acc.Method} n={acc.N} σ {acc.GtSigma.CentreCm:F1} cm  [{ann.Verify ?? "?"}]";
                p.Button($"{o.Name}: {status}", null, false, null, 54, 24);
                p.Button("OK", () => Mark(o, ann, "ok"), acc != null, ann?.Verify == "ok" ? AnnPanel.PrimaryButton : AnnPanel.ButtonColor, 54);
                p.Button("Redo", () => Mark(o, ann, "redo"), acc != null, ann?.Verify == "redo" ? AnnPanel.DangerButton : AnnPanel.ButtonColor, 54);
                p.EndRow();
            }

            Pager.Build(p, m_page, pages, d =>
            {
                m_page += d;
                m_mode.Refresh();
            });
            p.BeginRow();
            p.Button(m_showReps ? "Hide repetitions" : "Show repetitions", () =>
            {
                m_showReps = !m_showReps;
                m_mode.Refresh();
            });
            p.Button("Back", () => m_mode.Show(new HomeScreen(m_mode)));
            p.EndRow();
        }

        /// <summary>Stores ok / redo (Redo excludes the object from evaluation until re-annotated) with the head position.</summary>
        private void Mark(ObjectEntry o, ObjectAnnotations ann, string verdict)
        {
            ann.Verify = verdict;
            ann.VerifyTime = AnnotationStore.Timestamp();
            m_mode.SaveAnnotations();
            m_mode.Audit("verify", new JObject
            {
                ["object_id"] = o.Id,
                ["verdict"] = verdict,
                ["accepted_method"] = ann.Accepted?.Method,
                ["accepted_n"] = ann.Accepted?.N,
                ["head_room"] = m_mode.Room.HasFrame && m_mode.Context.Head != null
                    ? new JArray(Vec.ToArray(m_mode.Room.Frame.WorldToRoom(m_mode.Context.Head.position)))
                    : null,
            });
            Log.Info($"ann_verify object={o.Id} verdict={verdict}");
            m_mode.Refresh();
        }

        /// <summary>Draws all accepted boxes (colour per object, thickness grows with σ) and labels.</summary>
        public void Tick()
        {
            if (!m_mode.Room.HasFrame || m_mode.Annotations == null)
            {
                return;
            }

            var frame = m_mode.Room.Frame;
            var objects = m_mode.Catalog.Objects;
            for (var i = 0; i < objects.Count; i++)
            {
                var o = objects[i];
                var ann = m_mode.Annotations.Find(o.Id);
                if (ann == null)
                {
                    continue;
                }

                var color = Palette[i % Palette.Length];
                if (m_showReps)
                {
                    foreach (var r in ann.Repetitions)
                    {
                        if (r.Accepted && r.Box != null)
                        {
                            var c = r.Assisted ? new Color(1f, 0.3f, 0.9f, 0.6f) : new Color(color.r, color.g, color.b, 0.45f);
                            m_mode.Draw.Box(frame.BoxToWorld(r.Box.ToUpright()), c, 0.001f, default, false);
                        }
                    }
                }

                if (ann.Accepted == null)
                {
                    continue;
                }

                var box = frame.BoxToWorld(ann.Accepted.Box.ToUpright());
                var thickness = Mathf.Clamp(0.0015f + ann.Accepted.GtSigma.CentreCm * 0.0015f, 0.0015f, 0.012f);
                m_mode.Draw.Box(box, color, thickness, new Color(color.r, color.g, color.b, 0.07f), true);
                m_mode.Draw.FrontArrow(box, color);
                var verdict = ann.Verify == "ok" ? " ✓" : ann.Verify == "redo" ? " REDO" : "";
                m_mode.Draw.Label(box.TopCenter + Vector3.up * 0.07f,
                    $"{o.Name}{verdict}\n{ann.Accepted.Method} n={ann.Accepted.N} σ {ann.Accepted.GtSigma.CentreCm:F1} cm", color, 0.022f);
            }
        }
    }
}
