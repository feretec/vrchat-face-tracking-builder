using System;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;

namespace FaceTrackingBuilder
{
    public enum VertexSelectTool { Normal = 0, Brush = 2, Linked = 3, Loop = 4 }
    public enum VertexSelectOperation { Replace, Add, Remove, Toggle }
    public enum VertexTransformTool { Move, Rotate, Scale }
    public enum VertexFalloff { Smooth, Linear, Sharp, Sphere }

    public sealed class VertexSelectionState : ScriptableObject
    {
        public string target;
        public bool[] selected = Array.Empty<bool>();
    }

    public sealed class VertexGraph
    {
        public readonly int[][] neighbors;
        readonly int[][] coincident;
        public VertexGraph(Vector3[] vertices, int[] triangles, bool joinCoincident)
        {
            var links = Enumerable.Range(0, vertices.Length).Select(_ => new HashSet<int>()).ToArray();
            for (int i = 0; i + 2 < triangles.Length; i += 3)
                for (int e = 0; e < 3; e++)
                {
                    int a = triangles[i + e], b = triangles[i + (e + 1) % 3];
                    links[a].Add(b); links[b].Add(a);
                }
            coincident = new int[vertices.Length][];
            if (joinCoincident)
            {
                var groups = new Dictionary<Vector3, List<int>>();
                for (int i = 0; i < vertices.Length; i++)
                {
                    if (!groups.TryGetValue(vertices[i], out var group)) groups.Add(vertices[i], group = new List<int>());
                    group.Add(i);
                }
                foreach (var group in groups.Values.Where(g => g.Count > 1))
                {
                    var indices = group.ToArray();
                    foreach (int i in indices)
                    {
                        coincident[i] = indices;
                        links[indices[0]].Add(i); links[i].Add(indices[0]);
                    }
                }
            }
            neighbors = links.Select(g => g.ToArray()).ToArray();
        }
        public int[] Linked(int seed)
        {
            if (seed < 0 || seed >= neighbors.Length) return Array.Empty<int>();
            var seen = new bool[neighbors.Length]; var queue = new Queue<int>(); var result = new List<int>();
            seen[seed] = true; queue.Enqueue(seed);
            while (queue.Count > 0)
            {
                int i = queue.Dequeue(); result.Add(i);
                foreach (int j in neighbors[i]) if (!seen[j]) { seen[j] = true; queue.Enqueue(j); }
            }
            return result.ToArray();
        }
        public IEnumerable<int> ExpandCoincident(IEnumerable<int> indices)
        {
            var result = new HashSet<int>();
            foreach (int i in indices)
            {
                if (i < 0 || i >= neighbors.Length) continue;
                if (coincident[i] == null) result.Add(i); else foreach (int j in coincident[i]) result.Add(j);
            }
            return result;
        }
        public float[] Distances(Vector3[] world, bool[] selected, float limit)
        {
            var result = Enumerable.Repeat(float.PositiveInfinity, world.Length).ToArray(); var heap = new DistanceHeap();
            for (int i = 0; i < world.Length; i++) if (selected[i]) { result[i] = 0; heap.Add(i, 0); }
            while (heap.Count > 0)
            {
                var current = heap.Pop(); if (current.Value > result[current.Key] || current.Value >= limit) continue;
                foreach (int other in neighbors[current.Key])
                {
                    float distance = current.Value + Vector3.Distance(world[current.Key], world[other]);
                    if (distance >= limit || distance >= result[other]) continue;
                    result[other] = distance; heap.Add(other, distance);
                }
            }
            return result;
        }
        sealed class DistanceHeap
        {
            readonly List<KeyValuePair<int, float>> values = new List<KeyValuePair<int, float>>();
            public int Count => values.Count;
            public void Add(int vertex, float distance)
            {
                var item = new KeyValuePair<int, float>(vertex, distance); int i = values.Count; values.Add(item);
                while (i > 0)
                {
                    int parent = (i - 1) / 2; if (values[parent].Value <= distance) break;
                    values[i] = values[parent]; i = parent;
                }
                values[i] = item;
            }
            public KeyValuePair<int, float> Pop()
            {
                var result = values[0]; var last = values[values.Count - 1]; values.RemoveAt(values.Count - 1);
                if (values.Count == 0) return result;
                int i = 0;
                while (i * 2 + 1 < values.Count)
                {
                    int child = i * 2 + 1;
                    if (child + 1 < values.Count && values[child + 1].Value < values[child].Value) child++;
                    if (last.Value <= values[child].Value) break;
                    values[i] = values[child]; i = child;
                }
                values[i] = last; return result;
            }
        }
    }

    public static class VertexEditGeometry
    {
        public static bool Finite(float value) => !float.IsNaN(value) && !float.IsInfinity(value);
        public static bool Finite(Vector3 value) => Finite(value.x) && Finite(value.y) && Finite(value.z);
        public static float Falloff(float distance, float radius, VertexFalloff mode)
        {
            if (radius <= 0 || distance >= radius || !Finite(distance)) return 0;
            float t = Mathf.Clamp01(distance / radius), value = 1 - t;
            switch (mode)
            {
                case VertexFalloff.Linear: return value;
                case VertexFalloff.Sharp: return value * value;
                case VertexFalloff.Sphere: return Mathf.Sqrt(1 - t * t);
                default: return value * value * (3 - 2 * value);
            }
        }
        public static float[] Weights(Vector3[] world, bool[] selected, VertexGraph graph, bool proportional, float radius, VertexFalloff falloff, bool connected)
        {
            if (selected.Length != world.Length) throw new ArgumentException("選択データと頂点数が一致しません。");
            var weights = new float[world.Length];
            if (!proportional || radius <= 0 || !Finite(radius))
            {
                for (int i = 0; i < weights.Length; i++) weights[i] = selected[i] ? 1 : 0;
                return weights;
            }
            var distances = connected ? graph.Distances(world, selected, radius) : null;
            var tree = connected ? null : new PointTree(world.Where((p, i) => selected[i]).ToArray());
            for (int i = 0; i < weights.Length; i++)
                weights[i] = selected[i] ? 1 : Falloff(connected ? distances[i] : tree.Distance(world[i], radius), radius, falloff);
            return weights;
        }
        public static Vector3 Center(Vector3[] world, bool[] selected)
        {
            Vector3 result = Vector3.zero; int count = 0;
            for (int i = 0; i < world.Length; i++) if (selected[i]) { result += world[i]; count++; }
            return count == 0 ? Vector3.zero : result / count;
        }
        public static void Select(bool[] selected, IEnumerable<int> indices, VertexSelectOperation operation)
        {
            if (operation == VertexSelectOperation.Replace) Array.Clear(selected, 0, selected.Length);
            foreach (int i in operation == VertexSelectOperation.Toggle ? indices.Distinct() : indices)
                if (i >= 0 && i < selected.Length) selected[i] = operation == VertexSelectOperation.Toggle ? !selected[i] : operation != VertexSelectOperation.Remove;
        }
        public static Rect MarkerRect(Vector2 screen, bool selected)
        {
            float size = selected ? 8 : 6;
            return new Rect(screen.x - size * .5f, screen.y - size * .5f, size, size);
        }
        public static Color MarkerColor(bool selected, float influence) => selected ? new Color(1, .48f, .06f, 1) :
            Color.Lerp(Color.black, new Color(.1f, .75f, 1, 1), Mathf.Clamp01(influence));
        public static bool IsBoxDrag(Vector2 start, Vector2 current) => (current - start).sqrMagnitude >= 16;
        public static int[] NormalSelection(Vector2[] screen, bool[] eligible, Vector2 start, Vector2 end, bool box)
        {
            if (box) return Box(screen, eligible, Rect.MinMaxRect(Mathf.Min(start.x, end.x), Mathf.Min(start.y, end.y), Mathf.Max(start.x, end.x), Mathf.Max(start.y, end.y)));
            int index = Click(screen, eligible, end, 10);
            return index < 0 ? Array.Empty<int>() : new[] { index };
        }
        public static float SegmentDistance(Vector2 point, Vector2 a, Vector2 b)
        {
            Vector2 delta = b - a;
            float t = delta.sqrMagnitude < .00001f ? 0 : Mathf.Clamp01(Vector2.Dot(point - a, delta) / delta.sqrMagnitude);
            return Vector2.Distance(point, a + delta * t);
        }
        public static int[] Box(Vector2[] screen, bool[] eligible, Rect rect) =>
            Enumerable.Range(0, screen.Length).Where(i => eligible[i] && rect.Contains(screen[i])).ToArray();
        public static int[] Brush(Vector2[] screen, bool[] eligible, Vector2 a, Vector2 b, float radius) =>
            Enumerable.Range(0, screen.Length).Where(i => eligible[i] && SegmentDistance(screen[i], a, b) <= radius).ToArray();
        public static int Click(Vector2[] screen, bool[] eligible, Vector2 point, float radius)
        {
            int nearest = -1; float best = radius * radius;
            for (int i = 0; i < screen.Length; i++) if (eligible[i])
            {
                float distance = (screen[i] - point).sqrMagnitude;
                if (distance < best) { best = distance; nearest = i; }
            }
            return nearest;
        }
        public static Vector3[] Transform(Vector3[] world, Matrix4x4[] inverseSkin, Vector3[] originalSculpt, float[] weights,
            Vector3 pivot, Quaternion axes, Vector3 translation, Quaternion rotation, Vector3 scale, int[] mirrorPairs, bool[] selected, Vector3 mirrorNormal)
        {
            if (!Finite(translation) || !Finite(scale) || !Finite(rotation.x) || !Finite(rotation.y) || !Finite(rotation.z) || !Finite(rotation.w))
                throw new InvalidOperationException("移動・回転・倍率には有限の数値を指定してください。");
            if (inverseSkin.Length != world.Length || weights.Length != world.Length || originalSculpt.Length != world.Length)
                throw new InvalidOperationException("編集対象の頂点構造が変わりました。編集を開き直してください。");
            var delta = new Vector3[world.Length]; var result = (Vector3[])originalSculpt.Clone();
            for (int i = 0; i < world.Length; i++)
            {
                Vector3 relative = Quaternion.Inverse(axes) * (world[i] - pivot);
                Vector3 target = pivot + translation + rotation * (axes * Vector3.Scale(relative, scale));
                delta[i] = inverseSkin[i].MultiplyVector((target - world[i]) * weights[i]);
            }
            if (mirrorPairs != null)
            {
                var primary = (Vector3[])delta.Clone();
                for (int i = 0; i < delta.Length; i++)
                {
                    int other = mirrorPairs[i];
                    if (other >= 0 && other != i && !selected[other] && weights[i] > weights[other])
                        delta[other] = MeshEngine.ReflectVector(primary[i], mirrorNormal);
                }
            }
            for (int i = 0; i < result.Length; i++)
            {
                result[i] += delta[i];
                if (!Finite(result[i])) throw new InvalidOperationException("頂点の移動量が大きすぎます。入力値を小さくしてください。");
            }
            return result;
        }

        sealed class PointTree
        {
            readonly Vector3[] points;
            public PointTree(Vector3[] values) { points = values; Build(0, points.Length, 0); }
            void Build(int start, int count, int depth)
            {
                if (count <= 1) return; int axis = depth % 3;
                Array.Sort(points, start, count, Comparer<Vector3>.Create((a, b) => a[axis].CompareTo(b[axis])));
                int half = count / 2; Build(start, half, depth + 1); Build(start + half + 1, count - half - 1, depth + 1);
            }
            public float Distance(Vector3 point, float limit) { float best = limit * limit; Search(0, points.Length, 0, point, ref best); return Mathf.Sqrt(best); }
            void Search(int start, int count, int depth, Vector3 point, ref float best)
            {
                if (count == 0) return; int half = count / 2, middle = start + half, axis = depth % 3;
                best = Mathf.Min(best, (point - points[middle]).sqrMagnitude);
                float difference = point[axis] - points[middle][axis];
                if (difference <= 0)
                {
                    Search(start, half, depth + 1, point, ref best);
                    if (difference * difference <= best) Search(middle + 1, count - half - 1, depth + 1, point, ref best);
                }
                else
                {
                    Search(middle + 1, count - half - 1, depth + 1, point, ref best);
                    if (difference * difference <= best) Search(start, half, depth + 1, point, ref best);
                }
            }
        }
    }

    public sealed class VertexPickSurface
    {
        Vector3[] vertices;
        readonly int[] triangles, order;
        readonly Vector3[] centers;
        readonly List<Node> nodes = new List<Node>();
        public int VertexCount => vertices.Length;
        struct Node { public Bounds bounds; public int start, count, left, right; }
        public VertexPickSurface(Vector3[] world, int[] indices)
        {
            vertices = world; triangles = indices; order = Enumerable.Range(0, triangles.Length / 3).ToArray();
            centers = order.Select(t => (world[indices[t * 3]] + world[indices[t * 3 + 1]] + world[indices[t * 3 + 2]]) / 3).ToArray();
            if (order.Length > 0) Build(0, order.Length);
        }
        public void Refit(Vector3[] world)
        {
            if (world.Length != vertices.Length) throw new ArgumentException("ピッキング対象の頂点数が変化しました。");
            vertices = world;
            for (int i = nodes.Count - 1; i >= 0; i--)
            {
                var node = nodes[i]; Bounds bounds;
                if (node.left >= 0) { bounds = nodes[node.left].bounds; bounds.Encapsulate(nodes[node.right].bounds); }
                else
                {
                    bounds = new Bounds(vertices[triangles[order[node.start] * 3]], Vector3.zero);
                    for (int t = node.start; t < node.start + node.count; t++) for (int j = 0; j < 3; j++) bounds.Encapsulate(vertices[triangles[order[t] * 3 + j]]);
                    bounds.Expand(.000002f);
                }
                node.bounds = bounds; nodes[i] = node;
            }
        }
        int Build(int start, int count)
        {
            int index = nodes.Count; var bounds = new Bounds(vertices[triangles[order[start] * 3]], Vector3.zero);
            for (int i = start; i < start + count; i++) for (int j = 0; j < 3; j++) bounds.Encapsulate(vertices[triangles[order[i] * 3 + j]]);
            bounds.Expand(.000002f);
            var node = new Node { bounds = bounds, start = start, count = count, left = -1, right = -1 }; nodes.Add(node);
            if (count > 12)
            {
                int axis = bounds.size.x > bounds.size.y ? 0 : 1; if (bounds.size.z > bounds.size[axis]) axis = 2;
                Array.Sort(order, start, count, Comparer<int>.Create((a, b) => Centroid(a, axis).CompareTo(Centroid(b, axis))));
                int half = count / 2; node.left = Build(start, half); node.right = Build(start + half, count - half); nodes[index] = node;
            }
            return index;
        }
        float Centroid(int triangle, int axis) => centers[triangle][axis];
        public bool Hit(Ray ray, float limit, out float distance, out int vertex)
        {
            distance = limit; vertex = -1; if (nodes.Count > 0) Visit(0, ray, ref distance, ref vertex); return vertex >= 0;
        }
        void Visit(int index, Ray ray, ref float closest, ref int nearest)
        {
            var node = nodes[index]; if (!node.bounds.IntersectRay(ray, out float entry) || entry > closest) return;
            if (node.left >= 0) { Visit(node.left, ray, ref closest, ref nearest); Visit(node.right, ray, ref closest, ref nearest); return; }
            for (int i = node.start; i < node.start + node.count; i++)
            {
                int t = order[i] * 3, a = triangles[t], b = triangles[t + 1], c = triangles[t + 2];
                Vector3 e1 = vertices[b] - vertices[a], e2 = vertices[c] - vertices[a], p = Vector3.Cross(ray.direction, e2);
                float det = Vector3.Dot(e1, p); if (Mathf.Abs(det) < 1e-12f) continue;
                Vector3 relative = ray.origin - vertices[a]; float u = Vector3.Dot(relative, p) / det;
                if (u < -.00001f || u > 1.00001f) continue;
                Vector3 q = Vector3.Cross(relative, e1); float v = Vector3.Dot(ray.direction, q) / det;
                if (v < -.00001f || u + v > 1.00001f) continue;
                float distance = Vector3.Dot(e2, q) / det; if (distance < 0 || distance >= closest) continue;
                closest = distance; Vector3 hit = ray.GetPoint(distance); nearest = a;
                if ((vertices[b] - hit).sqrMagnitude < (vertices[nearest] - hit).sqrMagnitude) nearest = b;
                if ((vertices[c] - hit).sqrMagnitude < (vertices[nearest] - hit).sqrMagnitude) nearest = c;
            }
        }
        public static bool Visible(Ray ray, Vector3 vertex, IEnumerable<VertexPickSurface> surfaces)
        {
            float distance = Vector3.Dot(vertex - ray.origin, ray.direction);
            if (distance <= 0) return false;
            float limit = distance - Mathf.Max(.00001f, distance * .00001f);
            foreach (var surface in surfaces) if (surface.Hit(ray, limit, out _, out _)) return false;
            return true;
        }
    }
}
