using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Rendering;

namespace FaceTrackingBuilder
{
    internal static class VertexProjection
    {
        internal static void Project(Vector3[] world, Camera camera, Vector2 bottomLeft, float pixelsPerPoint,
            Vector2[] screen, float[] depth, bool[] inView)
        {
            var view = camera.worldToCameraMatrix; var matrix = camera.projectionMatrix * view;
            float width = camera.pixelWidth / pixelsPerPoint, height = camera.pixelHeight / pixelsPerPoint;
            for (int i = 0; i < world.Length; i++)
            {
                var p = world[i]; var clip = matrix * new Vector4(p.x, p.y, p.z, 1);
                float z = -view.MultiplyPoint3x4(p).z;
                float x = clip.x / clip.w * .5f + .5f, y = clip.y / clip.w * .5f + .5f;
                depth[i] = z; screen[i] = bottomLeft + new Vector2(x * width, -y * height);
                inView[i] = z > camera.nearClipPlane && z < camera.farClipPlane && x >= 0 && x <= 1 && y >= 0 && y <= 1;
            }
        }
    }

    internal sealed class VertexPointDisplay : IDisposable
    {
        readonly List<Vector3> positions = new List<Vector3>();
        readonly List<Color32> colors = new List<Color32>();
        readonly List<int> indices = new List<int>();
        Mesh mesh;
        Material material;
        Material depthMaterial;
        CommandBuffer depthCommands;
        Renderer[] occluders;
        internal void InvalidateOccluders() { occluders = null; depthCommands?.Clear(); }
        internal void SetOccluders(GameObject root)
        {
            if (occluders == null) occluders = root.GetComponentsInChildren<Renderer>(true);
        }
        internal Mesh Mesh => mesh;
        internal Material Material => material;

        internal void Build(Vector3[] world, bool[] inView, float[] depth, bool[] selected, float[] weights, Camera camera, float pixelsPerPoint)
        {
            if (!mesh)
            {
                mesh = new Mesh { name = "FTB vertex markers", hideFlags = HideFlags.HideAndDontSave, indexFormat = IndexFormat.UInt32 };
                mesh.MarkDynamic();
                var shader = Shader.Find("Hidden/Internal-Colored");
                if (!shader) throw new InvalidOperationException("頂点表示用のUnity標準シェーダーが見つかりません。");
                material = new Material(shader) { hideFlags = HideFlags.HideAndDontSave };
                material.SetInt("_SrcBlend", (int)BlendMode.SrcAlpha); material.SetInt("_DstBlend", (int)BlendMode.OneMinusSrcAlpha);
                material.SetInt("_Cull", (int)CullMode.Off); material.SetInt("_ZWrite", 0);
                depthMaterial = new Material(shader) { hideFlags = HideFlags.HideAndDontSave };
                depthMaterial.SetInt("_SrcBlend", (int)BlendMode.Zero); depthMaterial.SetInt("_DstBlend", (int)BlendMode.One);
                depthMaterial.SetInt("_Cull", (int)CullMode.Off); depthMaterial.SetInt("_ZWrite", 1); depthMaterial.SetInt("_ZTest", (int)CompareFunction.LessEqual);
                depthCommands = new CommandBuffer { name = "FTB vertex occlusion" };
            }
            positions.Clear(); colors.Clear(); indices.Clear();
            var right = camera.transform.right; var up = camera.transform.up; var forward = camera.transform.forward;
            float pixelX = 2 * pixelsPerPoint / (camera.pixelWidth * Mathf.Abs(camera.projectionMatrix.m00));
            float pixelY = 2 * pixelsPerPoint / (camera.pixelHeight * Mathf.Abs(camera.projectionMatrix.m11));
            for (int pass = 0; pass < 2; pass++)
                for (int i = 0; i < world.Length; i++)
                {
                    if (!inView[i] || selected[i] != (pass == 1)) continue;
                    float distance = camera.orthographic ? 1 : depth[i];
                    var horizontal = right * (pixelX * distance); var vertical = up * (pixelY * distance);
                    var center = world[i] - forward * Mathf.Max(.00001f, depth[i] * .00001f);
                    float half = selected[i] ? 4 : 3;
                    Quad(center, horizontal * half, vertical * half, new Color(.06f, .07f, .08f, 1));
                    Quad(center, horizontal * (half - 1), vertical * (half - 1), VertexEditGeometry.MarkerColor(selected[i], weights[i]));
                }
            mesh.Clear(); mesh.SetVertices(positions); mesh.SetColors(colors); mesh.SetTriangles(indices, 0);
        }
        void Quad(Vector3 center, Vector3 right, Vector3 up, Color32 color)
        {
            int start = positions.Count;
            positions.Add(center - right - up); positions.Add(center - right + up);
            positions.Add(center + right + up); positions.Add(center + right - up);
            for (int i = 0; i < 4; i++) colors.Add(color);
            indices.Add(start); indices.Add(start + 1); indices.Add(start + 2);
            indices.Add(start); indices.Add(start + 2); indices.Add(start + 3);
        }
        internal void Draw(Camera camera, bool through)
        {
            if (!mesh || indices.Count == 0) return;
            material.SetInt("_ZTest", (int)(through ? CompareFunction.Always : CompareFunction.LessEqual));
            bool wireframe = GL.wireframe; GL.PushMatrix();
            try
            {
                GL.wireframe = false;
                if (!through && occluders != null)
                {
                    depthCommands.Clear(); depthCommands.SetViewProjectionMatrices(camera.worldToCameraMatrix, camera.projectionMatrix);
                    foreach (var renderer in occluders)
                    {
                        if (!renderer || !renderer.enabled || !renderer.gameObject.activeInHierarchy) continue;
                        var skinned = renderer as SkinnedMeshRenderer;
                        var filter = renderer is MeshRenderer ? renderer.GetComponent<MeshFilter>() : null;
                        var source = skinned ? skinned.sharedMesh : filter ? filter.sharedMesh : null;
                        if (!source) continue;
                        for (int s = 0; s < source.subMeshCount; s++) depthCommands.DrawRenderer(renderer, depthMaterial, s, 0);
                    }
                    Graphics.ExecuteCommandBuffer(depthCommands);
                }
                GL.LoadProjectionMatrix(camera.projectionMatrix); GL.modelview = camera.worldToCameraMatrix;
                if (material.SetPass(0)) Graphics.DrawMeshNow(mesh, Matrix4x4.identity);
            }
            finally { GL.PopMatrix(); GL.wireframe = wireframe; }
        }
        public void Dispose()
        {
            if (mesh) UnityEngine.Object.DestroyImmediate(mesh);
            if (material) UnityEngine.Object.DestroyImmediate(material);
            if (depthMaterial) UnityEngine.Object.DestroyImmediate(depthMaterial);
            depthCommands?.Dispose(); depthCommands = null; occluders = null;
            mesh = null; material = null; depthMaterial = null;
        }
    }
}
