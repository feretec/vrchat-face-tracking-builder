using System;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;

namespace FaceTrackingBuilder
{
    public sealed class VertexLoopTopology
    {
        sealed class Edge
        {
            public int a, b;
            public readonly List<int> faces = new List<int>(2);
            public int Other(int vertex) => a == vertex ? b : a;
        }
        public sealed class Result
        {
            public int[] vertices;
            public bool closed, ambiguous;
        }
        readonly Edge[] edges;
        readonly int[][] incident;
        public Vector2Int[] Edges => edges.Select(e => new Vector2Int(e.a, e.b)).ToArray();
        public VertexLoopTopology(Vector3[] vertices, int[] triangles, bool joinCoincident)
        {
            var representative = Enumerable.Range(0, vertices.Length).ToArray();
            if (joinCoincident)
            {
                var first = new Dictionary<Vector3, int>();
                for (int i = 0; i < vertices.Length; i++)
                {
                    if (first.TryGetValue(vertices[i], out int index)) representative[i] = index;
                    else first.Add(vertices[i], i);
                }
            }
            var byPair = new Dictionary<long, Edge>();
            for (int i = 0; i + 2 < triangles.Length; i += 3)
            {
                int a = representative[triangles[i]], b = representative[triangles[i + 1]], c = representative[triangles[i + 2]];
                if (a == b || b == c || c == a) continue;
                Add(a, b, i / 3); Add(b, c, i / 3); Add(c, a, i / 3);
            }
            void Add(int a, int b, int face)
            {
                if (a > b) { int swap = a; a = b; b = swap; }
                long key = ((long)a << 32) | (uint)b;
                if (!byPair.TryGetValue(key, out var edge)) byPair.Add(key, edge = new Edge { a = a, b = b });
                edge.faces.Add(face);
            }
            edges = byPair.Values.ToArray();
            var lists = Enumerable.Range(0, vertices.Length).Select(_ => new List<int>()).ToArray();
            for (int i = 0; i < edges.Length; i++) { lists[edges[i].a].Add(i); lists[edges[i].b].Add(i); }
            incident = lists.Select(l => l.ToArray()).ToArray();
        }
        public int PickEdge(Vector2[] screen, bool[] eligible, Vector2 point, float radius, float[] depth = null)
        {
            int best = -1; float distance = radius, bestDepth = float.MaxValue;
            for (int i = 0; i < edges.Length; i++)
            {
                var edge = edges[i];
                if (!eligible[edge.a] || !eligible[edge.b] || (screen[edge.a] - screen[edge.b]).sqrMagnitude < .01f) continue;
                float d = VertexEditGeometry.SegmentDistance(point, screen[edge.a], screen[edge.b]);
                float z = depth == null ? 0 : (depth[edge.a] + depth[edge.b]) * .5f;
                if (d < distance - .01f || (Mathf.Abs(d - distance) <= .01f && z < bestDepth))
                { best = i; distance = d; bestDepth = z; }
            }
            return best;
        }
        public Result Trace(int seed, Vector3[] world)
        {
            if (seed < 0 || seed >= edges.Length) return new Result { vertices = Array.Empty<int>() };
            var start = edges[seed]; var selected = new HashSet<int> { start.a, start.b };
            bool closed = false, ambiguous = start.faces.Count > 2;
            if (!ambiguous)
            {
                Walk(start.a, start.b, seed, start.a);
                if (!closed) Walk(start.b, start.a, seed, start.b);
            }
            void Walk(int previous, int current, int incoming, int end)
            {
                for (int step = 0; step < world.Length; step++)
                {
                    int nextEdge = Continue(previous, current, incoming, world, out bool uncertain);
                    ambiguous |= uncertain;
                    if (nextEdge < 0) return;
                    int next = edges[nextEdge].Other(current);
                    if (next == end) { closed = true; return; }
                    if (!selected.Add(next)) { ambiguous = true; return; }
                    previous = current; current = next; incoming = nextEdge;
                }
                ambiguous = true;
            }
            return new Result { vertices = selected.ToArray(), closed = closed, ambiguous = ambiguous };
        }
        int Continue(int previous, int current, int incoming, Vector3[] world, out bool ambiguous)
        {
            ambiguous = false;
            if (incident[current].Any(i => edges[i].faces.Count > 2)) { ambiguous = true; return -1; }
            bool boundary = edges[incoming].faces.Count == 1;
            var candidates = incident[current].Where(i => i != incoming &&
                (boundary ? edges[i].faces.Count == 1 : edges[i].faces.Count == 2 && !edges[i].faces.Any(edges[incoming].faces.Contains))).ToArray();
            if (candidates.Length == 0) return -1;
            if (boundary)
            {
                if (candidates.Length != 1) { ambiguous = true; return -1; }
                return candidates[0];
            }
            var direction = (world[current] - world[previous]).normalized;
            int best = -1; float score = -.001f, second = -1;
            foreach (int i in candidates)
            {
                float dot = Vector3.Dot(direction, (world[edges[i].Other(current)] - world[current]).normalized);
                if (dot > score) { second = score; score = dot; best = i; } else second = Mathf.Max(second, dot);
            }
            if (best < 0 || score < .25f || score - second < .08f) { ambiguous = true; return -1; }
            return best;
        }
    }
}
