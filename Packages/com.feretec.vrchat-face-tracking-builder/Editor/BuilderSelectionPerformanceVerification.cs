using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using UnityEditor;
using UnityEngine;
using UnityEngine.Rendering;
using Object = UnityEngine.Object;

namespace FaceTrackingBuilder
{
    public static class BuilderSelectionPerformanceVerification
    {
        const BindingFlags Flags = BindingFlags.Instance | BindingFlags.NonPublic;
        static object Call(SceneExpressionEditor editor, string name, params object[] args) => typeof(SceneExpressionEditor).GetMethod(name, Flags).Invoke(editor, args);
        static T Field<T>(SceneExpressionEditor editor, string name) => (T)typeof(SceneExpressionEditor).GetField(name, Flags).GetValue(editor);
        static double Time(Action action, int iterations = 5)
        {
            action(); var times = new List<double>();
            for (int i = 0; i < iterations; i++) { var clock = Stopwatch.StartNew(); action(); times.Add(clock.Elapsed.TotalMilliseconds); }
            times.Sort(); return times[times.Count / 2];
        }
        static void Check(bool value, string message) { if (!value) throw new Exception(message); }
        public static string Run(string avatarPath)
        {
            var report = new BuilderVerification.Report { unity = Application.unityVersion, time = DateTime.UtcNow.ToString("o") };
            void Test(string name, Action test)
            {
                try { test(); report.results.Add(new BuilderVerification.Result { name = name, passed = true, detail = "OK" }); }
                catch (Exception ex) { report.results.Add(new BuilderVerification.Result { name = name, passed = false, detail = ex.ToString() }); }
            }
            void Metric(string name, double milliseconds) => report.results.Add(new BuilderVerification.Result { name = name, passed = true, detail = milliseconds.ToString("F3", System.Globalization.CultureInfo.InvariantCulture) + " ms (median)" });
            var cameraObject = new GameObject("FTB performance camera") { hideFlags = HideFlags.HideAndDontSave };
            var camera = cameraObject.AddComponent<Camera>(); camera.enabled = false; camera.pixelRect = new Rect(0, 0, 800, 600);
            camera.transform.position = new Vector3(0, 1, 2); camera.transform.rotation = Quaternion.Euler(0, 180, 0); camera.nearClipPlane = .01f;
            var profile = AvatarAnalysis.Analyze(AssetDatabase.LoadAssetAtPath<GameObject>(avatarPath));
            try
            {
                var plan = profile.renderers.First(p => p.selected); var recipe = new ShapeRecipe { name = "FTB_Performance", mode = GenerateMode.Landmarks };
                plan.recipes.Add(recipe);
                var otherPlan = profile.renderers.First(p => p != plan);
                otherPlan.recipes.Add(new ShapeRecipe { name = "FTB_OtherMesh", mode = GenerateMode.Landmarks });
                using (var session = new BuilderSession(profile))
                using (var editor = new SceneExpressionEditor { session = session, plan = plan, recipe = recipe, enabled = true, selectionMode = true })
                {
                    Call(editor, "EnsureSelectionGeometry");
                    Test("未操作の表示・移動では他メッシュのBakeや遮蔽探索を行わない", () => Check(Field<List<VertexPickSurface>>(editor, "surfaces").Count == 0, "eager occluder capture"));
                    var renderer = session.Renderer(plan); var originalMesh = plan.source; var fingerprint = MeshEngine.Fingerprint(originalMesh);
                    var otherMesh = session.Renderer(otherPlan).sharedMesh;
                    editor.SelectVertices(new[] { 0 }, VertexSelectOperation.Replace);
                    editor.ApplyNumericTransform(editor.SelectionCoordinates + Vector3.up * .0001f);
                    Test("変形中に対象外の生成Meshを作り直さない", () => Check(session.Renderer(otherPlan).sharedMesh == otherMesh, "unrelated mesh replaced"));
                    Call(editor, "EnsureSelectionGeometry"); Call(editor, "EnsureOccluders");
                    int otherId = session.Renderer(otherPlan).GetInstanceID();
                    var otherSurface = Field<Dictionary<int, VertexPickSurface>>(editor, "surfaceCache")[otherId];
                    var verticesField = typeof(VertexPickSurface).GetField("vertices", Flags);
                    var captured = verticesField.GetValue(otherSurface);
                    editor.ApplyNumericTransform(editor.SelectionCoordinates + Vector3.up * .0001f);
                    Call(editor, "EnsureSelectionGeometry"); Call(editor, "EnsureOccluders");
                    Test("他メッシュの頂点と遮蔽用ツリーを再利用", () => Check(ReferenceEquals(captured, verticesField.GetValue(otherSurface)), "static occluder baked again"));
                    var otherRenderer = session.Renderer(otherPlan); otherRenderer.transform.localPosition += Vector3.right * .0001f;
                    editor.SelectionSettingsChanged(); typeof(SceneExpressionEditor).GetField("occludersDirty", Flags).SetValue(editor, true); Call(editor, "EnsureOccluders");
                    Test("他メッシュのTransform変更はキャッシュへ反映", () => Check(!ReferenceEquals(captured, verticesField.GetValue(otherSurface)), "stale occluder"));
                    var world = Field<Vector3[]>(editor, "selectionWorld");
                    var screen = new Vector2[world.Length]; var depths = new float[world.Length]; var visible = new bool[world.Length];
                    Test("高速投影がUnityの投影と一致・透視/平行投影/HiDPI", () =>
                    {
                        foreach (bool ortho in new[] { false, true }) foreach (float dpi in new[] { 1f, 2f })
                        {
                            camera.orthographic = ortho; camera.orthographicSize = 1;
                            VertexProjection.Project(world, camera, new Vector2(0, 600 / dpi), dpi, screen, depths, visible);
                            for (int i = 0; i < world.Length; i += 13)
                            {
                                var p = camera.WorldToScreenPoint(world[i]);
                                Check(Vector2.Distance(screen[i], new Vector2(p.x / dpi, (600 - p.y) / dpi)) < .001f, "projection mismatch");
                            }
                        }
                        camera.orthographic = false;
                    });
                    var surfaces = Field<List<VertexPickSurface>>(editor, "surfaces");
                    Test("早期終了する遮蔽判定が従来の最近傍判定と一致", () =>
                    {
                        for (int i = 0; i < world.Length; i += 3)
                        {
                            var ray = camera.ScreenPointToRay(camera.WorldToScreenPoint(world[i]));
                            float distance = Vector3.Dot(world[i] - ray.origin, ray.direction), limit = distance - Mathf.Max(.00001f, distance * .00001f);
                            bool old = distance > 0 && !surfaces.Any(s => s.Hit(ray, limit, out _, out _));
                            Check(old == VertexPickSurface.Visible(ray, world[i], surfaces), "occlusion mismatch " + i);
                        }
                    });
                    foreach (int count in new[] { 8, 64 })
                    {
                        var template = session.Renderer(otherPlan);
                        while (session.Clone.GetComponentsInChildren<SkinnedMeshRenderer>().Length < count)
                        {
                            var copy = Object.Instantiate(template.gameObject, session.Clone.transform);
                            copy.name = "FTB stress renderer"; copy.transform.position += Vector3.right * (10 + session.Clone.transform.childCount);
                        }
                        Metric(count + " mesh: target geometry update", Time(() => { typeof(SceneExpressionEditor).GetField("geometryDirty", Flags).SetValue(editor, true); Call(editor, "EnsureSelectionGeometry"); }));
                        Call(editor, "EnsureOccluders");
                        Metric(count + " mesh: camera projection", Time(() => VertexProjection.Project(world, camera, new Vector2(0, 600), 1, screen, depths, visible)));
                        editor.BeginVertexTransform();
                        Metric(count + " mesh: transform and target refresh", Time(() =>
                        { editor.PreviewVertexTransform(Vector3.up * .0005f, Quaternion.identity, Vector3.one); Call(editor, "EnsureSelectionGeometry"); }));
                        editor.EndSelectionInteraction(false);
                        var legacySurfaces = new Dictionary<int, VertexPickSurface>(); var temporary = new Mesh();
                        try
                        {
                            Metric(count + " mesh: legacy full geometry update", Time(() =>
                            {
                                foreach (var r in session.Clone.GetComponentsInChildren<SkinnedMeshRenderer>())
                                {
                                    r.BakeMesh(temporary); var p = temporary.vertices.Select(r.transform.TransformPoint).ToArray(); int id = r.GetInstanceID();
                                    if (legacySurfaces.TryGetValue(id, out var surface)) surface.Refit(p); else legacySurfaces.Add(id, new VertexPickSurface(p, temporary.triangles));
                                }
                            }, 3));
                            Metric(count + " mesh: legacy camera occlusion", Time(() =>
                            {
                                for (int i = 0; i < world.Length; i++)
                                {
                                    var ray = camera.ScreenPointToRay(camera.WorldToScreenPoint(world[i])); float distance = Vector3.Dot(world[i] - ray.origin, ray.direction);
                                    float limit = distance - Mathf.Max(.00001f, distance * .00001f);
                                    foreach (var s in legacySurfaces.Values) if (s.Hit(ray, limit, out _, out _)) break;
                                }
                            }, 3));
                        }
                        finally { Object.DestroyImmediate(temporary); }
                    }
                    Test("元Meshの頂点・BlendShapeを保持", () => Check(MeshEngine.Fingerprint(originalMesh) == fingerprint, "source changed"));
                    Test("軽量プレビューと確定結果の頂点が一致・合成/抽出/左右分割/ミラー/複数フレーム", () =>
                    {
                        var probe = new ShapeRecipe { name = "FTB_PreviewParity", planeOrigin = plan.source.bounds.center, planeRight = Vector3.right,
                            regionCenter = plan.source.bounds.center, regionRadius = plan.source.bounds.size.magnitude,
                            frames = new List<float> { 50, 150 }, mask = Enumerable.Range(0, plan.source.vertexCount).Select(i => i % 3 * .5f).ToArray() };
                        probe.sources.Add(new ShapeSource { name = plan.source.GetBlendShapeName(0), coefficient = .05f }); plan.recipes.Add(probe);
                        var temporary = new Mesh();
                        try
                        {
                            foreach (var mode in new[] { GenerateMode.Combine, GenerateMode.Landmarks, GenerateMode.Extract, GenerateMode.SplitLeft, GenerateMode.SplitRight, GenerateMode.Mirror })
                            {
                                probe.mode = mode; probe.sculpt = new Vector3[plan.source.vertexCount];
                                session.Rebuild(plan); session.ResetWeights(); session.Weight(plan.path, probe.name, 100);
                                var original = renderer.sharedMesh; Vector3[] predicted;
                                using (var preview = VertexTransformPreview.Create(renderer, plan, probe, (Vector3[])probe.sculpt.Clone()))
                                {
                                    Check(preview != null, "preview not created");
                                    for (int i = 0; i < probe.sculpt.Length; i++) probe.sculpt[i] = Vector3.up * (.0002f * (i % 5));
                                    preview.Apply(probe.sculpt); renderer.BakeMesh(temporary); predicted = temporary.vertices;
                                    Check(renderer.sharedMesh != original && renderer.sharedMesh.blendShapeCount == 0, "not using transient vertices");
                                }
                                Check(renderer.sharedMesh == original && renderer.GetBlendShapeWeight(original.GetBlendShapeIndex(probe.name)) == 100, "mesh / weight restoration");
                                session.Rebuild(plan); session.Weight(plan.path, probe.name, 100); renderer.BakeMesh(temporary);
                                var actual = temporary.vertices;
                                Check(actual.Select((v, i) => Vector3.Distance(v, predicted[i])).Max() < .00001f, "preview / commit mismatch: " + mode);
                            }
                            using (var interrupted = VertexTransformPreview.Create(renderer, plan, probe, (Vector3[])probe.sculpt.Clone()))
                            {
                                session.Rebuild(plan); var newer = renderer.sharedMesh;
                                Check(!interrupted.IsCurrent, "interrupted preview detected");
                                interrupted.Dispose(); Check(renderer.sharedMesh == newer, "newer mesh must not be replaced by stale preview");
                            }
                        }
                        finally { Object.DestroyImmediate(temporary); plan.recipes.Remove(probe); }
                    });
                }
                Test("GPU頂点描画・遮蔽・透過選択・固定サイズ", () => VerifyDisplay(camera));
            }
            finally { Undo.ClearUndo(profile); Object.DestroyImmediate(profile); Object.DestroyImmediate(cameraObject); }
            var json = JsonUtility.ToJson(report, true);
            Directory.CreateDirectory("Library/SelectionPerformance"); File.WriteAllText("Library/SelectionPerformance/verification.json", json); return json;
        }
        static void VerifyDisplay(Camera camera)
        {
            camera.transform.position = Vector3.back * 2; camera.transform.rotation = Quaternion.identity;
            camera.orthographic = true; camera.orthographicSize = 1;
            var rt = new RenderTexture(128, 128, 24); rt.Create(); var previous = RenderTexture.active;
            var read = new Texture2D(128, 128, TextureFormat.RGBA32, false); var block = new Mesh();
            var material = new Material(Shader.Find("Hidden/Internal-Colored"));
            var commands = new CommandBuffer();
            var occluder = new GameObject("FTB depth verification", typeof(MeshFilter), typeof(MeshRenderer)) { hideFlags = HideFlags.HideAndDontSave };
            using (var points = new VertexPointDisplay())
            try
            {
                camera.targetTexture = rt;
                block.vertices = new[] { new Vector3(-1, -1, 0), new Vector3(-1, 1, 0), new Vector3(1, 1, 0), new Vector3(1, -1, 0) };
                block.colors = Enumerable.Repeat(Color.white, 4).ToArray(); block.triangles = new[] { 0, 1, 2, 0, 2, 3 };
                occluder.GetComponent<MeshFilter>().sharedMesh = block; occluder.GetComponent<MeshRenderer>().sharedMaterial = material;
                material.SetInt("_Cull", (int)CullMode.Off); material.SetInt("_ZWrite", 1); material.SetInt("_ZTest", (int)CompareFunction.LessEqual);
                var failures = new List<string>();
                foreach (bool wire in new[] { false, true }) foreach (float z in new[] { -.2f, .2f }) foreach (bool through in new[] { false, true })
                {
                    points.InvalidateOccluders(); if (wire) points.SetOccluders(occluder);
                    points.Build(new[] { new Vector3(0, 0, z) }, new[] { true }, new[] { 2 + z }, new[] { true }, new[] { 1f }, camera, 1);
                    points.Material.SetInt("_ZTest", (int)(through ? CompareFunction.Always : CompareFunction.LessEqual));
                    commands.Clear(); commands.SetRenderTarget(rt); commands.ClearRenderTarget(true, true, Color.gray);
                    commands.SetViewProjectionMatrices(camera.worldToCameraMatrix, camera.projectionMatrix);
                    if (!wire) commands.DrawMesh(block, Matrix4x4.identity, material);
                    Graphics.ExecuteCommandBuffer(commands); RenderTexture.active = rt;
                    bool previousWire = GL.wireframe;
                    try { GL.wireframe = wire; points.Draw(camera, through); Check(GL.wireframe == wire, "wireframe state restored"); }
                    finally { GL.wireframe = previousWire; }
                    read.ReadPixels(new Rect(0, 0, 128, 128), 0, 0); read.Apply();
                    var color = read.GetPixel(64, 64); bool orange = color.r > .8f && color.g < .8f && color.b < .3f;
                    if (orange != (z < 0 || through)) failures.Add("GPU occlusion wire=" + wire + " z=" + z + " through=" + through + ": " + color);
                    float size = (camera.WorldToScreenPoint(points.Mesh.vertices[2]) - camera.WorldToScreenPoint(points.Mesh.vertices[0])).x;
                    Check(Mathf.Abs(size - 8) < .01f, "marker size");
                }
                var dense = Enumerable.Repeat(Vector3.zero, 10000).ToArray();
                points.Build(dense, Enumerable.Repeat(true, dense.Length).ToArray(), Enumerable.Repeat(2f, dense.Length).ToArray(), new bool[dense.Length], new float[dense.Length], camera, 2);
                Check(points.Mesh.vertexCount == 80000 && points.Mesh.indexFormat == IndexFormat.UInt32, "dense marker mesh index overflow");
                foreach (bool ortho in new[] { false, true }) foreach (float distance in new[] { .2f, 2, 20 })
                {
                    camera.orthographic = ortho; camera.transform.position = Vector3.back * distance;
                    points.Build(new[] { Vector3.zero }, new[] { true }, new[] { distance }, new[] { false }, new[] { 0f }, camera, 2);
                    float size = (camera.WorldToScreenPoint(points.Mesh.vertices[2]) - camera.WorldToScreenPoint(points.Mesh.vertices[0])).x;
                    Check(Mathf.Abs(size - 12) < .01f, "HiDPI neutral marker size " + size);
                    Check(points.Mesh.colors32[4].Equals((Color32)Color.black), "neutral marker is black");
                }
                Check(failures.Count == 0, string.Join("\n", failures));
            }
            finally
            {
                camera.targetTexture = null; RenderTexture.active = previous; commands.Dispose();
                Object.DestroyImmediate(occluder); Object.DestroyImmediate(block); Object.DestroyImmediate(material); Object.DestroyImmediate(read); rt.Release(); Object.DestroyImmediate(rt);
            }
        }
    }
}
