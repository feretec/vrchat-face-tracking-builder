using System;
using System.Linq;
using UnityEditor;
using UnityEngine;

namespace FaceTrackingBuilder
{
    public sealed class SceneExpressionEditor : IDisposable
    {
        public BuilderSession session;
        public RendererPlan plan;
        public ShapeRecipe recipe;
        public bool enabled, brush, paintMask, symmetric;
        public float radius = .02f, strength = .3f;
        public Action changed;
        public Action displayChanged;
        public Action<Exception> failed;
        public bool pointerOverControls;
        SceneBrushPanel controls;
        public SceneBrushPanel Controls => controls;
        public bool CanEdit => enabled && session?.Clone != null && !session.Comparing && recipe != null && string.IsNullOrEmpty(recipe.mirrorOf);
        int control, undoGroup = -1;
        Mesh baked;
        Matrix4x4[] skinMatrices;
        int[] mirrorPairs;
        Vector3[] baseVertices;
        public SceneExpressionEditor() { SceneView.duringSceneGui += Draw; }
        public void Invalidate() { skinMatrices = null; mirrorPairs = null; }
        public void Dispose() { controls?.Dispose(); controls = null; SceneView.duringSceneGui -= Draw; if (baked) UnityEngine.Object.DestroyImmediate(baked); baked = null; }
        public void ShowControls()
        {
            var view = session.ShowView();
            if (controls == null || controls.View != view) { controls?.Dispose(); controls = new SceneBrushPanel(this, view); }
            controls.Refresh();
        }
        public void Compare(bool neutral) { session.Compare(neutral); controls?.Refresh(); displayChanged?.Invoke(); }
        void Prepare(SkinnedMeshRenderer r)
        {
            if (skinMatrices != null) return;
            baseVertices = plan.source.vertices;
            skinMatrices = Enumerable.Repeat(r.localToWorldMatrix, baseVertices.Length).ToArray();
            var bindposes = plan.source.bindposes; var bones = r.bones;
            if (bindposes.Length == 0 || bones.Length == 0) return;
            var perVertex = plan.source.GetBonesPerVertex(); var all = plan.source.GetAllBoneWeights();
            try
            {
                int cursor = 0;
                for (int i = 0; i < baseVertices.Length; i++)
                {
                    var m = Matrix4x4.zero; float total = 0;
                    int count = i < perVertex.Length ? perVertex[i] : 0;
                    for (int j = 0; j < count; j++)
                    {
                        var bw = all[cursor++]; int b = bw.boneIndex;
                        if (b >= bones.Length || b >= bindposes.Length || !bones[b]) continue;
                        var bm = bones[b].localToWorldMatrix * bindposes[b];
                        for (int k = 0; k < 16; k++) m[k] += bm[k] * bw.weight;
                        total += bw.weight;
                    }
                    if (total > .99f && Mathf.Abs(m.determinant) > 1e-10f) skinMatrices[i] = m;
                }
            }
            finally { perVertex.Dispose(); all.Dispose(); }
        }
        public void Frame()
        {
            if (session?.Clone == null || plan == null) return;
            var view = session.ShowView();
            var r = session.Renderer(plan);
            var b = r.bounds;
            // View position from descriptor is a useful initial head location, independent of mesh axes.
            var descriptor = session.Clone.GetComponent<VRC.SDK3.Avatars.Components.VRCAvatarDescriptor>();
            if (descriptor) b = new Bounds(session.Clone.transform.TransformPoint(descriptor.ViewPosition), Vector3.one * .35f);
            view.LookAt(b.center, Quaternion.LookRotation(-session.Clone.transform.forward, session.Clone.transform.up), b.extents.magnitude, false, true);
        }
        void Draw(SceneView view)
        {
            if (session == null || view != session.View) return;
            if (undoGroup >= 0 && (Event.current.type == EventType.MouseUp || !CanEdit || pointerOverControls || Event.current.alt))
            {
                if (GUIUtility.hotControl == control) GUIUtility.hotControl = 0;
                Undo.CollapseUndoOperations(undoGroup); undoGroup = -1;
            }
            if (!CanEdit || plan == null || pointerOverControls || EditorApplication.isPlayingOrWillChangePlaymode) return;
            try { DrawInternal(view); }
            catch (Exception ex) { enabled = false; GUIUtility.hotControl = 0; failed?.Invoke(ex); }
        }
        void DrawInternal(SceneView view)
        {
            var r = session.Renderer(plan); Prepare(r);
            Event e = Event.current;
            if (!brush) return;
            // Alt orbit, middle/right mouse navigation and other windows retain their events.
            if (e.alt || e.button > 0) return;
            control = GUIUtility.GetControlID("FTBBrush".GetHashCode(), FocusType.Passive);
            if (e.type == EventType.Layout) HandleUtility.AddDefaultControl(control);
            if (!baked) baked = new Mesh();
            r.BakeMesh(baked);
            var worldVertices = baked.vertices.Select(r.transform.TransformPoint).ToArray();
            var ray = HandleUtility.GUIPointToWorldRay(e.mousePosition);
            if (Hit(ray, worldVertices, baked.triangles, out var hit, out var normal, out int nearest))
            {
                Handles.color = paintMask ? new Color(1, .6f, .1f, .8f) : new Color(.1f, .9f, 1, .8f);
                Handles.DrawWireDisc(hit, normal, radius);
                // Sparse influence samples keep dense meshes responsive.
                int step = Math.Max(1, worldVertices.Length / 1800);
                for (int i = 0; i < worldVertices.Length; i += step)
                {
                    float f = MeshEngine.Falloff(Vector3.Distance(worldVertices[i], hit), radius);
                    if (f <= 0) continue;
                    Handles.color = new Color(1, 1 - f, 0, .8f);
                    Handles.DotHandleCap(0, worldVertices[i], Quaternion.identity, HandleUtility.GetHandleSize(worldVertices[i]) * .006f, EventType.Repaint);
                }
                if (e.type == EventType.MouseDown && e.button == 0)
                {
                    GUIUtility.hotControl = control;
                    Undo.IncrementCurrentGroup(); undoGroup = Undo.GetCurrentGroup();
                    Undo.RegisterCompleteObjectUndo(session.Profile, "表情ブラシ");
                    Stroke(worldVertices, hit, normal, e.shift);
                    e.Use();
                }
                else if (e.type == EventType.MouseDrag && GUIUtility.hotControl == control && brush) { Stroke(worldVertices, hit, normal, e.shift); e.Use(); }
            }
            if (e.type == EventType.MouseUp && GUIUtility.hotControl == control)
            {
                GUIUtility.hotControl = 0;
                if (undoGroup >= 0) Undo.CollapseUndoOperations(undoGroup);
                undoGroup = -1; e.Use();
            }
            if (e.type == EventType.MouseMove) view.Repaint();
        }
        void Stroke(Vector3[] world, Vector3 hit, Vector3 normal, bool inverse)
        {
            if (recipe.sculpt.Length != world.Length) recipe.sculpt = new Vector3[world.Length];
            if (recipe.mask.Length != world.Length) recipe.mask = Enumerable.Repeat(1f, world.Length).ToArray();
            if (symmetric && mirrorPairs == null) mirrorPairs = MeshEngine.MirrorPairs(baseVertices, recipe.planeOrigin, recipe.planeRight, recipe.mirrorTolerance);
            var applied = new bool[world.Length];
            for (int i = 0; i < world.Length; i++)
            {
                float f = MeshEngine.Falloff(Vector3.Distance(world[i], hit), radius);
                if (f == 0 || applied[i]) continue;
                Vector3 movement = skinMatrices[i].inverse.MultiplyVector(normal * (strength * radius * .08f * f * (inverse ? -1 : 1)));
                if (paintMask) recipe.mask[i] = Mathf.Clamp01(recipe.mask[i] + strength * f * (inverse ? 1 : -1));
                else recipe.sculpt[i] += movement;
                applied[i] = true;
                int other = symmetric ? mirrorPairs[i] : -1;
                if (other >= 0 && other != i && !applied[other])
                {
                    if (paintMask) recipe.mask[other] = recipe.mask[i]; else recipe.sculpt[other] += MeshEngine.ReflectVector(movement, recipe.planeRight);
                    applied[other] = true;
                }
            }
            Commit();
        }
        void Commit() { EditorUtility.SetDirty(session.Profile); session.Rebuild(); session.Weight(plan.path, recipe.name, 100); changed?.Invoke(); }
        static bool Hit(Ray ray, Vector3[] v, int[] triangles, out Vector3 point, out Vector3 normal, out int nearest)
        {
            float closest = float.MaxValue; point = Vector3.zero; normal = Vector3.up; nearest = 0;
            for (int i = 0; i + 2 < triangles.Length; i += 3)
            {
                int a = triangles[i], b = triangles[i + 1], c = triangles[i + 2];
                Vector3 e1 = v[b] - v[a], e2 = v[c] - v[a]; var p = Vector3.Cross(ray.direction, e2); float det = Vector3.Dot(e1, p);
                if (Mathf.Abs(det) < 1e-9f) continue;
                var t = ray.origin - v[a]; float u = Vector3.Dot(t, p) / det;
                if (u < 0 || u > 1) continue;
                var q = Vector3.Cross(t, e1); float w = Vector3.Dot(ray.direction, q) / det;
                if (w < 0 || u + w > 1) continue;
                float d = Vector3.Dot(e2, q) / det; if (d < 0 || d >= closest) continue;
                closest = d; point = ray.GetPoint(d); normal = Vector3.Cross(e1, e2).normalized;
                nearest = new[] { a, b, c }.OrderBy(index => Vector3.Distance(v[index], ray.GetPoint(d))).First();
            }
            return closest < float.MaxValue;
        }
    }
}
