using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using UnityEngine;
using Object = UnityEngine.Object;

namespace FaceTrackingBuilder
{
    public sealed class ShapeDelta
    {
        public readonly Vector3[] vertices, normals, tangents;
        public ShapeDelta(int count) { vertices = new Vector3[count]; normals = new Vector3[count]; tangents = new Vector3[count]; }
        public void Add(ShapeDelta other, float weight)
        { for (int i = 0; i < vertices.Length; i++) { vertices[i] += other.vertices[i] * weight; normals[i] += other.normals[i] * weight; tangents[i] += other.tangents[i] * weight; } }
    }
    public static class MeshEngine
    {
        public static string Fingerprint(Mesh mesh)
        {
            using (var stream = new MemoryStream())
            using (var writer = new BinaryWriter(stream))
            {
                writer.Write(mesh.vertexCount); writer.Write(mesh.subMeshCount);
                foreach (var v in mesh.vertices) { writer.Write(v.x); writer.Write(v.y); writer.Write(v.z); }
                for (int s = 0; s < mesh.subMeshCount; s++) { writer.Write((int)mesh.GetTopology(s)); foreach (int i in mesh.GetIndices(s)) writer.Write(i); }
                using (var sha = SHA256.Create()) return Convert.ToBase64String(sha.ComputeHash(stream.ToArray()));
            }
        }
        public static void ValidateSource(RendererPlan plan)
        {
            if (!plan.source) throw new InvalidOperationException("元Meshがありません。アバターを再解析してください。");
            // Editor can read some imported meshes even when runtime Read/Write is disabled.
            try { if (Fingerprint(plan.source) != plan.fingerprint) throw new InvalidOperationException("元Meshの頂点またはトポロジーが変化しました。既存設定を別名保存し、再解析してください: " + plan.path); }
            catch (UnityException ex) { throw new InvalidOperationException("Meshを読み取れません。FBXを別フォルダへ複製し、複製側のRead/Writeを有効にしてください。元のImport設定は変更しません。", ex); }
        }
        static ShapeDelta ReadFrame(Mesh mesh, int index, int frame)
        {
            var result = new ShapeDelta(mesh.vertexCount);
            mesh.GetBlendShapeFrameVertices(index, frame, result.vertices, result.normals, result.tangents);
            return result;
        }
        // Piecewise linear interpolation with the implicit neutral frame at zero.
        public static ShapeDelta Sample(Mesh mesh, string shape, float weight)
        {
            int index = mesh.GetBlendShapeIndex(shape);
            if (index < 0) throw new InvalidOperationException("合成元BlendShapeがありません: " + shape);
            int count = mesh.GetBlendShapeFrameCount(index);
            var knots = new List<KeyValuePair<float, int>> { new KeyValuePair<float, int>(0, -1) };
            for (int i = 0; i < count; i++) { float w = mesh.GetBlendShapeFrameWeight(index, i); if (w == 0) knots.RemoveAll(k => k.Key == 0); knots.Add(new KeyValuePair<float, int>(w, i)); }
            knots.Sort((a, b) => a.Key.CompareTo(b.Key));
            int upper = knots.FindIndex(k => k.Key >= weight);
            if (upper < 0) upper = knots.Count - 1;
            if (upper == 0) upper = Math.Min(1, knots.Count - 1);
            int lower = Math.Max(0, upper - 1);
            var a = knots[lower]; var b = knots[upper];
            float t = a.Key == b.Key ? 0 : (weight - a.Key) / (b.Key - a.Key);
            var result = new ShapeDelta(mesh.vertexCount);
            if (a.Value >= 0) result.Add(ReadFrame(mesh, index, a.Value), 1 - t);
            if (b.Value >= 0) result.Add(ReadFrame(mesh, index, b.Value), t);
            return result;
        }
        public static float Falloff(float distance, float radius)
        { if (radius <= 0) return 0; float t = Mathf.Clamp01(1 - distance / radius); return t * t * (3 - 2 * t); }
        public static Vector3 ReflectPoint(Vector3 point, Vector3 origin, Vector3 right) => point - 2 * Vector3.Dot(point - origin, right.normalized) * right.normalized;
        public static Vector3 ReflectVector(Vector3 vector, Vector3 right) => vector - 2 * Vector3.Dot(vector, right.normalized) * right.normalized;

        // Spatial hash avoids O(n^2); mutual matches reject ambiguous/asymmetric correspondence.
        public static int[] MirrorPairs(Vector3[] vertices, Vector3 origin, Vector3 right, float tolerance)
        {
            if (tolerance <= 0 || right.sqrMagnitude < .0001f) throw new InvalidOperationException("ミラー許容距離と中心面の向きを指定してください。");
            var cells = new Dictionary<Vector3Int, List<int>>();
            Func<Vector3, Vector3Int> cell = p => new Vector3Int(Mathf.FloorToInt(p.x / tolerance), Mathf.FloorToInt(p.y / tolerance), Mathf.FloorToInt(p.z / tolerance));
            for (int i = 0; i < vertices.Length; i++) { var k = cell(vertices[i]); if (!cells.TryGetValue(k, out var list)) cells[k] = list = new List<int>(); list.Add(i); }
            var matches = Enumerable.Repeat(-1, vertices.Length).ToArray();
            for (int i = 0; i < vertices.Length; i++)
            {
                var p = ReflectPoint(vertices[i], origin, right); var c = cell(p); float best = tolerance * tolerance;
                for (int x = -1; x <= 1; x++) for (int y = -1; y <= 1; y++) for (int z = -1; z <= 1; z++)
                {
                    if (!cells.TryGetValue(c + new Vector3Int(x, y, z), out var list)) continue;
                    foreach (int j in list) { float d = (vertices[j] - p).sqrMagnitude; if (d <= best) { best = d; matches[i] = j; } }
                }
            }
            // Coincident UV seam vertices may share a destination; positions are equivalent.
            for (int i = 0; i < vertices.Length; i++) { int j = matches[i]; if (j >= 0 && (matches[j] < 0 || (vertices[matches[j]] - vertices[i]).sqrMagnitude > tolerance * tolerance)) matches[i] = -1; }
            return matches;
        }
        public static ShapeDelta Generate(Mesh source, ShapeRecipe recipe, float frameWeight, out string diagnostic)
        {
            if (!Finite(frameWeight) || frameWeight <= 0) throw new InvalidOperationException("生成フレームのウェイトは0より大きい有限値にしてください。");
            var vertices = source.vertices; var result = new ShapeDelta(vertices.Length);
            if (recipe.mode != GenerateMode.Landmarks)
                foreach (var input in recipe.sources) { if (!Finite(input.coefficient)) throw new InvalidOperationException("合成比率が不正です。"); result.Add(Sample(source, input.name, frameWeight), input.coefficient); }
            if (recipe.planeRight.sqrMagnitude < .0001f) throw new InvalidOperationException("顔の右方向ベクトルを設定してください。");
            var right = recipe.planeRight.normalized;
            diagnostic = "";
            if (recipe.mode == GenerateMode.Mirror)
            {
                var pairs = MirrorPairs(vertices, recipe.planeOrigin, right, recipe.mirrorTolerance);
                var mirrored = new ShapeDelta(vertices.Length);
                for (int i = 0; i < pairs.Length; i++) if (pairs[i] >= 0) { mirrored.vertices[i] = ReflectVector(result.vertices[pairs[i]], right); mirrored.normals[i] = ReflectVector(result.normals[pairs[i]], right); mirrored.tangents[i] = ReflectVector(result.tangents[pairs[i]], right); }
                diagnostic = "ミラー対応なし: " + pairs.Count(i => i < 0) + " / " + pairs.Length + " 頂点（変形を適用しません）";
                result = mirrored;
            }
            for (int i = 0; i < vertices.Length; i++)
            {
                float mask = 1;
                if (recipe.mode == GenerateMode.SplitLeft || recipe.mode == GenerateMode.SplitRight)
                {
                    float d = Vector3.Dot(vertices[i] - recipe.planeOrigin, right);
                    float t = recipe.feather <= 0 ? (d >= 0 ? 1 : 0) : Mathf.SmoothStep(0, 1, .5f + d / (2 * recipe.feather));
                    mask = recipe.mode == GenerateMode.SplitRight ? t : 1 - t;
                }
                if (recipe.mode == GenerateMode.Extract) mask *= Falloff(Vector3.Distance(vertices[i], recipe.regionCenter), recipe.regionRadius);
                foreach (var landmark in recipe.landmarks) result.vertices[i] += landmark.movement * Falloff(Vector3.Distance(vertices[i], landmark.position), landmark.radius) * (frameWeight / 100f);
                if (recipe.sculpt.Length == vertices.Length) result.vertices[i] += recipe.sculpt[i] * (frameWeight / 100f);
                if (recipe.mask.Length == vertices.Length) mask *= Mathf.Clamp01(recipe.mask[i]);
                result.vertices[i] *= mask; result.normals[i] *= mask; result.tangents[i] *= mask;
                if (!Finite(result.vertices[i]) || !Finite(result.normals[i]) || !Finite(result.tangents[i])) throw new InvalidOperationException("頂点変位にNaNまたはInfinityが含まれています。");
            }
            float maximum = result.vertices.Max(v => v.magnitude);
            if (maximum > source.bounds.size.magnitude * .35f) throw new InvalidOperationException("最大変位がMeshサイズの35%を超えています。合成比率・特徴点移動を小さくしてください。");
            diagnostic += " 最大変位: " + maximum.ToString("F5") + "（Mesh座標）";
            if (recipe.recalculateSurface || recipe.landmarks.Any(l => l.movement != Vector3.zero) || recipe.sculpt.Any(v => v != Vector3.zero)) RecalculateSurface(source, result);
            return result;
        }
        static bool Finite(float v) => !float.IsNaN(v) && !float.IsInfinity(v);
        static bool Finite(Vector3 v) => Finite(v.x) && Finite(v.y) && Finite(v.z);
        static void RecalculateSurface(Mesh source, ShapeDelta delta)
        {
            var temp = Object.Instantiate(source);
            try
            {
                temp.ClearBlendShapes(); temp.RecalculateNormals();
                bool tangents = source.uv.Length == source.vertexCount && source.tangents.Length == source.vertexCount;
                if (tangents) temp.RecalculateTangents();
                var n0 = temp.normals; var t0 = temp.tangents; var v = source.vertices;
                for (int i = 0; i < v.Length; i++) v[i] += delta.vertices[i];
                temp.vertices = v; temp.RecalculateNormals(); if (tangents) temp.RecalculateTangents();
                var n1 = temp.normals; var t1 = temp.tangents;
                for (int i = 0; i < v.Length; i++) { delta.normals[i] = n1[i] - n0[i]; if (tangents) delta.tangents[i] = (Vector3)(t1[i] - t0[i]); }
            }
            finally { Object.DestroyImmediate(temp); }
        }
        public static Mesh Build(RendererPlan plan, out string diagnostics, string target = null)
        {
            ValidateSource(plan);
            var source = plan.source;
            var recipes = RecipeSources.Ordered(plan, target);
            var output = Object.Instantiate(source); output.name = source.name + "_FTB";
            var log = new List<string>();
            try
            {
                foreach (var recipe in recipes)
                {
                    var frames = recipe.frames.Distinct().OrderBy(w => w).ToArray();
                    if (frames.Length == 0) throw new InvalidOperationException("生成フレームを1つ以上指定してください。");
                    foreach (float weight in frames) { var d = Generate(output, recipe, weight, out var info); output.AddBlendShapeFrame(recipe.name, weight, d.vertices, d.normals, d.tangents); log.Add(recipe.name + " @ " + weight + ": " + info); }
                }
                diagnostics = string.Join("\n", log); return output;
            }
            catch { Object.DestroyImmediate(output); throw; }
        }
    }
}
