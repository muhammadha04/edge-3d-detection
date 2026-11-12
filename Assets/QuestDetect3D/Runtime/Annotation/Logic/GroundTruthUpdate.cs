// Recomputes an object's accepted ground truth after every confirmed repetition (pure, unit-tested).
//   accepted_by_method[m] = robust mean of the accepted repetitions of method m (M1, M2, M4)
//   accepted              = M1 if it has any accepted repetition, else M2. Assisted (M4) boxes never become the
//                           primary ground truth.
using System.Collections.Generic;

namespace QuestDetect3D.Annotation
{
    public static class GroundTruthUpdate
    {
        public static readonly string[] PrimaryMethods = { "M1", "M2" };

        /// <summary>Recomputes accepted_by_method and the primary accepted box after a repetition was added or rejected.</summary>
        public static void Recompute(ObjectAnnotations obj, string symmetry, string timestamp)
        {
            var period = Repeatability.PeriodFor(symmetry);
            obj.AcceptedByMethod = new Dictionary<string, AcceptedBox>();
            var byMethod = new Dictionary<string, List<UprightBox>>();
            foreach (var rep in obj.Repetitions)
            {
                if (!rep.Accepted || rep.Box == null || string.IsNullOrEmpty(rep.Method))
                {
                    continue;
                }

                if (!byMethod.TryGetValue(rep.Method, out var list))
                {
                    list = new List<UprightBox>();
                    byMethod[rep.Method] = list;
                }

                list.Add(rep.Box.ToUpright());
            }

            foreach (var kv in byMethod)
            {
                var stats = Repeatability.Compute(kv.Value, period);
                obj.AcceptedByMethod[kv.Key] = new AcceptedBox
                {
                    Box = BoxDto.From(stats.Box),
                    GtSigma = new GtSigma { CentreCm = stats.CentreCm, YawDeg = stats.YawDeg, SizePct = stats.SizePct },
                    N = stats.N,
                    Method = kv.Key,
                    Computed = timestamp,
                };
            }

            obj.Accepted = null;
            foreach (var m in PrimaryMethods)
            {
                if (obj.AcceptedByMethod.TryGetValue(m, out var accepted))
                {
                    obj.Accepted = accepted;
                    break;
                }
            }
        }

        /// <summary>Next 1-based repetition index for this object and method.</summary>
        public static int NextIndex(ObjectAnnotations obj, string method)
        {
            var max = 0;
            foreach (var rep in obj.Repetitions)
            {
                if (rep.Method == method && rep.Index > max)
                {
                    max = rep.Index;
                }
            }

            return max + 1;
        }
    }
}
