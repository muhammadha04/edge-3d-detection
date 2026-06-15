using System;
using Mediapipe.Unity;
using UnityEngine;

namespace QuestObjectron.CenterPose
{
    /// <summary>Reads PCA camera texture into an oriented RGB buffer for CenterPose.</summary>
    public sealed class CenterPoseCameraReader : IDisposable
    {
        private RenderTexture m_readRt;
        private Texture2D m_readTex;
        private int m_lastWidth;
        private int m_lastHeight;

        public bool TryReadOriented(
            Texture source,
            RotationAngle rotation,
            bool flipHorizontal,
            out Color32[] pixels,
            out int width,
            out int height)
        {
            pixels = null;
            width = 0;
            height = 0;
            if (source == null)
            {
                return false;
            }

            EnsureBuffers(source.width, source.height);
            Graphics.Blit(source, m_readRt);
            var prev = RenderTexture.active;
            RenderTexture.active = m_readRt;
            m_readTex.ReadPixels(new Rect(0, 0, source.width, source.height), 0, 0, false);
            m_readTex.Apply(false, false);
            RenderTexture.active = prev;

            var raw = m_readTex.GetPixels32();
            var oriented = CenterPoseFrameOrientation.Orient(raw, source.width, source.height, rotation, flipHorizontal);
            width = rotation is RotationAngle.Rotation90 or RotationAngle.Rotation270
                ? source.height
                : source.width;
            height = rotation is RotationAngle.Rotation90 or RotationAngle.Rotation270
                ? source.width
                : source.height;
            pixels = oriented;
            return true;
        }

        private void EnsureBuffers(int width, int height)
        {
            if (m_readRt != null && m_lastWidth == width && m_lastHeight == height)
            {
                return;
            }

            ReleaseBuffers();
            m_lastWidth = width;
            m_lastHeight = height;
            m_readRt = new RenderTexture(width, height, 0, RenderTextureFormat.ARGB32)
            {
                enableRandomWrite = false
            };
            m_readRt.Create();
            m_readTex = new Texture2D(width, height, TextureFormat.RGBA32, false, false);
        }

        public void Dispose()
        {
            ReleaseBuffers();
        }

        private void ReleaseBuffers()
        {
            if (m_readRt != null)
            {
                m_readRt.Release();
                UnityEngine.Object.Destroy(m_readRt);
                m_readRt = null;
            }

            if (m_readTex != null)
            {
                UnityEngine.Object.Destroy(m_readTex);
                m_readTex = null;
            }
        }
    }
}
