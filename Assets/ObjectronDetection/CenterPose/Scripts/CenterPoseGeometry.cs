using UnityEngine;

namespace QuestObjectron.CenterPose
{
    /// <summary>Affine crop / inverse transform matching Isaac ROS CenterPose Python post-processing.</summary>
    public static class CenterPoseGeometry
    {
        public const int InputSize = 512;
        public static readonly Vector2 OutputField = new(128, 128);

        private static readonly Vector3 Mean = new(0.408f, 0.447f, 0.470f);
        private static readonly Vector3 InvStd = new(1f / 0.289f, 1f / 0.274f, 1f / 0.278f);

        public static float[] BuildInputTensor(Color32[] rgba, int width, int height, CenterPosePreprocessMeta meta)
        {
            var cropped = CropImage(rgba, width, height, meta.Center, meta.Scale, InputSize);
            var tensor = new float[3 * InputSize * InputSize];
            var plane = InputSize * InputSize;

            for (var y = 0; y < InputSize; y++)
            {
                for (var x = 0; x < InputSize; x++)
                {
                    var c = cropped[y * InputSize + x];
                    var i = y * InputSize + x;
                    tensor[i] = (c.r / 255f - Mean.x) * InvStd.x;
                    tensor[plane + i] = (c.g / 255f - Mean.y) * InvStd.y;
                    tensor[2 * plane + i] = (c.b / 255f - Mean.z) * InvStd.z;
                }
            }

            return tensor;
        }

        public static CenterPosePreprocessMeta BuildMeta(int width, int height)
        {
            var center = new Vector2(width * 0.5f, height * 0.5f);
            var scale = Mathf.Max(height, width);
            return new CenterPosePreprocessMeta(center, scale, width, height);
        }

        public static Color32[] CropImage(Color32[] src, int srcW, int srcH, Vector2 center, float scale, int outputSize)
        {
            var forward = GetAffineTransform(center, scale, 0f, new Vector2(outputSize, outputSize), inv: false);
            var dst = new Color32[outputSize * outputSize];

            for (var y = 0; y < outputSize; y++)
            {
                for (var x = 0; x < outputSize; x++)
                {
                    var srcPt = AffineTransform(new Vector2(x, y), forward);
                    dst[y * outputSize + x] = SampleBilinear(src, srcW, srcH, srcPt.x, srcPt.y);
                }
            }

            return dst;
        }

        public static Vector2[] TransformPreds(Vector2[] coords, CenterPosePreprocessMeta meta)
        {
            var inverse = GetAffineTransform(meta.Center, meta.Scale, 0f, OutputField, inv: true);
            var result = new Vector2[coords.Length];
            for (var i = 0; i < coords.Length; i++)
            {
                var p = coords[i];
                if (p.x <= -10000f || p.y <= -10000f)
                {
                    result[i] = new Vector2(-10000f, -10000f);
                    continue;
                }

                result[i] = AffineTransform(p, inverse);
            }

            return result;
        }

        private static Color32 SampleBilinear(Color32[] src, int w, int h, float x, float y)
        {
            if (x < 0f || y < 0f || x >= w - 1f || y >= h - 1f)
            {
                return new Color32(0, 0, 0, 255);
            }

            var x0 = Mathf.FloorToInt(x);
            var y0 = Mathf.FloorToInt(y);
            var x1 = x0 + 1;
            var y1 = y0 + 1;
            var tx = x - x0;
            var ty = y - y0;

            var c00 = src[y0 * w + x0];
            var c10 = src[y0 * w + x1];
            var c01 = src[y1 * w + x0];
            var c11 = src[y1 * w + x1];

            return new Color32(
                (byte)Mathf.Lerp(Mathf.Lerp(c00.r, c10.r, tx), Mathf.Lerp(c01.r, c11.r, tx), ty),
                (byte)Mathf.Lerp(Mathf.Lerp(c00.g, c10.g, tx), Mathf.Lerp(c01.g, c11.g, tx), ty),
                (byte)Mathf.Lerp(Mathf.Lerp(c00.b, c10.b, tx), Mathf.Lerp(c01.b, c11.b, tx), ty),
                255);
        }

        private static Vector2 AffineTransform(Vector2 pt, Matrix3x2 t)
        {
            return new Vector2(
                t.m00 * pt.x + t.m01 * pt.y + t.m02,
                t.m10 * pt.x + t.m11 * pt.y + t.m12);
        }

        private static Matrix3x2 GetAffineTransform(Vector2 center, float scale, float rotDeg, Vector2 outputSize, bool inv)
        {
            var scaleVec = new Vector2(scale, scale);
            var srcW = scaleVec.x;
            var dstW = outputSize.x;
            var dstH = outputSize.y;

            var rotRad = rotDeg * Mathf.Deg2Rad;
            var srcDir = RotateDir(new Vector2(0f, srcW * -0.5f), rotRad);
            var dstDir = new Vector2(0f, dstW * -0.5f);

            var src0 = center;
            var src1 = center + srcDir;
            var src2 = ThirdPoint(src0, src1);

            var dst0 = new Vector2(dstW * 0.5f, dstH * 0.5f);
            var dst1 = dst0 + dstDir;
            var dst2 = ThirdPoint(dst0, dst1);

            if (inv)
            {
                return GetAffineFrom3Points(dst0, dst1, dst2, src0, src1, src2);
            }

            return GetAffineFrom3Points(src0, src1, src2, dst0, dst1, dst2);
        }

        private static Vector2 RotateDir(Vector2 src, float rotRad)
        {
            var sn = Mathf.Sin(rotRad);
            var cs = Mathf.Cos(rotRad);
            return new Vector2(src.x * cs - src.y * sn, src.x * sn + src.y * cs);
        }

        private static Vector2 ThirdPoint(Vector2 a, Vector2 b)
        {
            var direct = a - b;
            return b + new Vector2(-direct.y, direct.x);
        }

        private static Matrix3x2 GetAffineFrom3Points(Vector2 s0, Vector2 s1, Vector2 s2, Vector2 d0, Vector2 d1, Vector2 d2)
        {
            // Solve 2x3 affine mapping three source points to three destination points.
            var src = new[,]
            {
                { s0.x, s0.y, 1f, 0f, 0f, 0f },
                { 0f, 0f, 0f, s0.x, s0.y, 1f },
                { s1.x, s1.y, 1f, 0f, 0f, 0f },
                { 0f, 0f, 0f, s1.x, s1.y, 1f },
                { s2.x, s2.y, 1f, 0f, 0f, 0f },
                { 0f, 0f, 0f, s2.x, s2.y, 1f },
            };
            var dst = new[] { d0.x, d0.y, d1.x, d1.y, d2.x, d2.y };
            var sol = Solve6x6(src, dst);
            return new Matrix3x2(sol[0], sol[1], sol[2], sol[3], sol[4], sol[5]);
        }

        private static float[] Solve6x6(float[,] a, float[] b)
        {
            var n = 6;
            var aug = new float[n, n + 1];
            for (var i = 0; i < n; i++)
            {
                for (var j = 0; j < n; j++)
                {
                    aug[i, j] = a[i, j];
                }

                aug[i, n] = b[i];
            }

            for (var col = 0; col < n; col++)
            {
                var pivot = col;
                for (var row = col + 1; row < n; row++)
                {
                    if (Mathf.Abs(aug[row, col]) > Mathf.Abs(aug[pivot, col]))
                    {
                        pivot = row;
                    }
                }

                for (var j = 0; j <= n; j++)
                {
                    (aug[col, j], aug[pivot, j]) = (aug[pivot, j], aug[col, j]);
                }

                var div = aug[col, col];
                if (Mathf.Abs(div) < 1e-8f)
                {
                    div = 1e-8f;
                }

                for (var j = 0; j <= n; j++)
                {
                    aug[col, j] /= div;
                }

                for (var row = 0; row < n; row++)
                {
                    if (row == col)
                    {
                        continue;
                    }

                    var factor = aug[row, col];
                    for (var j = 0; j <= n; j++)
                    {
                        aug[row, j] -= factor * aug[col, j];
                    }
                }
            }

            var x = new float[n];
            for (var i = 0; i < n; i++)
            {
                x[i] = aug[i, n];
            }

            return x;
        }

        private readonly struct Matrix3x2
        {
            public readonly float m00, m01, m02, m10, m11, m12;

            public Matrix3x2(float m00, float m01, float m02, float m10, float m11, float m12)
            {
                this.m00 = m00;
                this.m01 = m01;
                this.m02 = m02;
                this.m10 = m10;
                this.m11 = m11;
                this.m12 = m12;
            }
        }
    }
}
