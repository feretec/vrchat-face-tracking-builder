using System;
using UnityEngine;

namespace FaceTrackingBuilder
{
    internal sealed class VertexTransformPreview : IDisposable
    {
        readonly SkinnedMeshRenderer renderer;
        readonly Mesh original;
        readonly float[] weights, mask;
        readonly Vector3[] baseline, initialSculpt, targetDelta, authoredNormals, geometryNormals;
        readonly Vector4[] authoredTangents, geometryTangents;
        readonly float maxDisplacement;
        Mesh working;
        internal bool IsCurrent => renderer && working && renderer.sharedMesh == working;

        internal static VertexTransformPreview Create(SkinnedMeshRenderer renderer, RendererPlan plan, ShapeRecipe recipe, Vector3[] initialSculpt)
        {
            int index = renderer.sharedMesh.GetBlendShapeIndex(recipe.name);
            if (index < 0 || renderer.GetBlendShapeWeight(index) != 100) return null;
            return new VertexTransformPreview(renderer, plan, recipe, initialSculpt);
        }
        VertexTransformPreview(SkinnedMeshRenderer renderer, RendererPlan plan, ShapeRecipe recipe, Vector3[] sculpt)
        {
            this.renderer = renderer; original = renderer.sharedMesh; initialSculpt = sculpt;
            weights = new float[original.blendShapeCount]; baseline = original.vertices; authoredNormals = original.normals; authoredTangents = original.tangents;
            targetDelta = MeshEngine.Sample(original, recipe.name, 100).vertices;
            maxDisplacement = plan.source.bounds.size.magnitude * .35f;
            mask = new float[baseline.Length]; var right = recipe.planeRight.normalized;
            for (int i = 0; i < mask.Length; i++)
            {
                float value = recipe.mask.Length == mask.Length ? Mathf.Clamp01(recipe.mask[i]) : 1;
                if (recipe.mode == GenerateMode.SplitLeft || recipe.mode == GenerateMode.SplitRight)
                {
                    float d = Vector3.Dot(baseline[i] - recipe.planeOrigin, right);
                    float t = recipe.feather <= 0 ? (d >= 0 ? 1 : 0) : Mathf.SmoothStep(0, 1, .5f + d / (2 * recipe.feather));
                    value *= recipe.mode == GenerateMode.SplitRight ? t : 1 - t;
                }
                if (recipe.mode == GenerateMode.Extract) value *= MeshEngine.Falloff(Vector3.Distance(baseline[i], recipe.regionCenter), recipe.regionRadius);
                mask[i] = value;
            }
            for (int shape = 0; shape < weights.Length; shape++)
            {
                weights[shape] = renderer.GetBlendShapeWeight(shape); if (weights[shape] == 0) continue;
                var delta = MeshEngine.Sample(original, original.GetBlendShapeName(shape), weights[shape]);
                for (int i = 0; i < baseline.Length; i++)
                {
                    baseline[i] += delta.vertices[i];
                    if (authoredNormals.Length == baseline.Length) authoredNormals[i] += delta.normals[i];
                    if (authoredTangents.Length == baseline.Length)
                    { var t = authoredTangents[i]; t.x += delta.tangents[i].x; t.y += delta.tangents[i].y; t.z += delta.tangents[i].z; authoredTangents[i] = t; }
                }
            }
            try
            {
                working = UnityEngine.Object.Instantiate(original); working.name = "FTB drag preview"; working.hideFlags = HideFlags.HideAndDontSave;
                working.ClearBlendShapes(); working.MarkDynamic(); working.vertices = baseline; working.RecalculateNormals();
                geometryNormals = working.normals;
                if (authoredNormals.Length != baseline.Length) authoredNormals = (Vector3[])geometryNormals.Clone();
                if (authoredTangents.Length == baseline.Length && working.uv.Length == baseline.Length)
                { working.RecalculateTangents(); geometryTangents = working.tangents; }
                working.normals = authoredNormals;
                if (authoredTangents.Length == baseline.Length) working.tangents = authoredTangents;
                working.RecalculateBounds(); renderer.sharedMesh = working;
            }
            catch { Dispose(); throw; }
        }
        internal void Apply(Vector3[] sculpt)
        {
            var vertices = new Vector3[baseline.Length];
            for (int i = 0; i < vertices.Length; i++)
            {
                var delta = (sculpt[i] - initialSculpt[i]) * mask[i]; vertices[i] = baseline[i] + delta;
                if (!VertexEditGeometry.Finite(vertices[i]) || (targetDelta[i] + delta).magnitude > maxDisplacement)
                    throw new InvalidOperationException("頂点の移動量が大きすぎます。操作を小さくしてください。");
            }
            working.vertices = vertices; working.RecalculateNormals();
            if (geometryTangents != null) working.RecalculateTangents();
            var normals = working.normals;
            for (int i = 0; i < normals.Length; i++) normals[i] = authoredNormals[i] + normals[i] - geometryNormals[i];
            working.normals = normals;
            if (geometryTangents != null)
            {
                var tangents = working.tangents;
                for (int i = 0; i < tangents.Length; i++)
                { tangents[i] = authoredTangents[i] + tangents[i] - geometryTangents[i]; tangents[i].w = authoredTangents[i].w; }
                working.tangents = tangents;
            }
            working.RecalculateBounds();
        }
        public void Dispose()
        {
            if (renderer && original && renderer.sharedMesh == working)
            {
                renderer.sharedMesh = original;
                for (int i = 0; i < weights.Length; i++) renderer.SetBlendShapeWeight(i, weights[i]);
            }
            if (working) UnityEngine.Object.DestroyImmediate(working); working = null;
        }
    }
}
