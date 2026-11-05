using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using UnityEditor;
using UnityEngine;
using Object = UnityEngine.Object;

namespace FaceTrackingBuilder
{
    public static class BuilderSelectionVerification
    {
        static void Check(bool value, string message) { if (!value) throw new Exception(message); }
        static void Near(Vector3 actual, Vector3 expected, string message) => Check(Vector3.Distance(actual, expected) < .00001f, message + ": " + actual + " / " + expected);
        static void Test(BuilderVerification.Report report, string name, Action action)
        {
            try { action(); report.results.Add(new BuilderVerification.Result { name = name, passed = true, detail = "OK" }); }
            catch (Exception ex) { report.results.Add(new BuilderVerification.Result { name = name, passed = false, detail = ex.ToString() }); }
        }
        public static string Run(string avatarPath = "")
        {
            var report = new BuilderVerification.Report { unity = Application.unityVersion, time = DateTime.UtcNow.ToString("o") };
            var vertices = new[] { Vector3.zero, Vector3.right, Vector3.up, new Vector3(0, 0, .01f), new Vector3(1, 0, .01f), new Vector3(0, 1, .01f) };
            var triangles = new[] { 0, 1, 2, 3, 4, 5 }; var graph = new VertexGraph(vertices, triangles, false);
            var selection = new[] { true, false, false, false, false, false };
            Test(report, "リンク選択は独立したパーツだけを選ぶ", () => Check(graph.Linked(0).OrderBy(i => i).SequenceEqual(new[] { 0, 1, 2 }), "disconnected component"));
            Test(report, "頂点の追加・解除・置き換え", () =>
            {
                var selected = new bool[6]; VertexEditGeometry.Select(selected, new[] { 0, 1 }, VertexSelectOperation.Replace);
                VertexEditGeometry.Select(selected, new[] { 2 }, VertexSelectOperation.Add); VertexEditGeometry.Select(selected, new[] { 1 }, VertexSelectOperation.Remove);
                Check(selected.Select((s, i) => s ? i : -1).Where(i => i >= 0).SequenceEqual(new[] { 0, 2 }), "add / subtract");
                VertexEditGeometry.Select(selected, new[] { 4 }, VertexSelectOperation.Replace); Check(selected.Count(s => s) == 1 && selected[4], "replace");
            });
            Test(report, "ボックス・クリックは可視判定を尊重", () =>
            {
                var points = new[] { new Vector2(10, 10), new Vector2(20, 20), new Vector2(10, 10), new Vector2(100, 100) };
                var visible = new[] { true, true, false, true };
                Check(VertexEditGeometry.Box(points, visible, new Rect(0, 0, 30, 30)).SequenceEqual(new[] { 0, 1 }), "box visible");
                Check(VertexEditGeometry.Click(points, visible, new Vector2(10, 10), 6) == 0, "click visible");
                Check(VertexEditGeometry.Box(points, Enumerable.Repeat(true, 4).ToArray(), new Rect(0, 0, 30, 30)).Length == 3, "through selection");
                Check(VertexEditGeometry.Click(points, visible, new Vector2(50, 50), 6) == -1, "empty click");
            });
            Test(report, "Shift選択は既存を解除・未選択を追加し重複を一度だけ処理", () =>
            {
                var selected = new[] { true, false, true };
                VertexEditGeometry.Select(selected, new[] { 0, 1, 1 }, VertexSelectOperation.Toggle);
                Check(selected.SequenceEqual(new[] { false, true, true }), "toggle mixed selection");
                VertexEditGeometry.Select(selected, Array.Empty<int>(), VertexSelectOperation.Toggle);
                Check(selected.SequenceEqual(new[] { false, true, true }), "empty shift click preserves selection");
            });
            Test(report, "通常選択はクリックとドラッグを区別・微小な揺れはクリック", () =>
            {
                var points = new[] { new Vector2(20, 20), new Vector2(30, 30), new Vector2(80, 80) };
                var visible = new[] { true, true, false };
                Check(!VertexEditGeometry.IsBoxDrag(new Vector2(20, 20), new Vector2(22, 21)), "small pointer jitter");
                Check(VertexEditGeometry.NormalSelection(points, visible, new Vector2(20, 20), new Vector2(22, 21), false).SequenceEqual(new[] { 0 }), "single click");
                Check(VertexEditGeometry.IsBoxDrag(new Vector2(10, 10), new Vector2(50, 50)) &&
                    VertexEditGeometry.NormalSelection(points, visible, new Vector2(50, 50), new Vector2(10, 10), true).SequenceEqual(new[] { 0, 1 }), "reverse drag rectangle");
                Check(VertexEditGeometry.NormalSelection(points, visible, new Vector2(60, 60), new Vector2(60, 60), false).Length == 0, "empty space");
                var selected = new[] { true, false, true };
                VertexEditGeometry.Select(selected, VertexEditGeometry.NormalSelection(points, visible, new Vector2(10, 10), new Vector2(50, 50), true), VertexSelectOperation.Toggle);
                Check(selected.SequenceEqual(new[] { false, true, true }), "shift rectangle toggle");
            });
            Test(report, "頂点マーカーはズーム距離に依存せず6点・8点の画面サイズ", () =>
            {
                var cameraObject = new GameObject("Marker projection verification");
                try
                {
                    var camera = cameraObject.AddComponent<Camera>(); camera.pixelRect = new Rect(0, 0, 1000, 700);
                    foreach (bool ortho in new[] { false, true })
                    foreach (float zoom in new[] { .1f, 1, 10 })
                    {
                        camera.orthographic = ortho; camera.orthographicSize = zoom; camera.transform.position = Vector3.back * zoom;
                        var screen = (Vector2)camera.WorldToScreenPoint(new Vector3(.01f, .02f, .03f));
                        Check(VertexEditGeometry.MarkerRect(screen, false).size == Vector2.one * 6 && VertexEditGeometry.MarkerRect(screen, true).size == Vector2.one * 8,
                            "screen-space size across perspective and orthographic zoom");
                    }
                    Check(VertexEditGeometry.MarkerColor(false, 0) == Color.black && VertexEditGeometry.MarkerColor(false, 0) != VertexEditGeometry.MarkerColor(true, 1) &&
                        VertexEditGeometry.MarkerColor(false, 1) != VertexEditGeometry.MarkerColor(true, 1), "selection distinct from neutral and influence");
                }
                finally { Object.DestroyImmediate(cameraObject); }
            });
            Test(report, "ブラシ選択は速いドラッグの間も選ぶ", () =>
            {
                var points = new[] { new Vector2(50, 5), new Vector2(60, 40), new Vector2(30, 0) };
                Check(VertexEditGeometry.Brush(points, new[] { true, true, false }, Vector2.zero, new Vector2(100, 0), 10).SequenceEqual(new[] { 0 }), "swept circle");
            });
            Test(report, "重複頂点の接続は明示的に有効化", () =>
            {
                var split = (Vector3[])vertices.Clone(); split[3] = split[0];
                Check(new VertexGraph(split, triangles, false).Linked(0).Length == 3, "no implicit welding");
                var joined = new VertexGraph(split, triangles, true);
                Check(joined.Linked(0).Length == 6 && joined.ExpandCoincident(new[] { 0 }).OrderBy(i => i).SequenceEqual(new[] { 0, 3 }), "seam group");
            });
            Test(report, "接続のみの影響は近い別パーツへ伝わらない", () =>
            {
                var weights = VertexEditGeometry.Weights(vertices, selection, graph, true, 2, VertexFalloff.Linear, true);
                Check(weights[0] == 1 && Mathf.Abs(weights[1] - .5f) < .00001f && weights.Skip(3).All(w => w == 0), "geodesic distance");
                var spatial = VertexEditGeometry.Weights(vertices, selection, graph, true, 2, VertexFalloff.Linear, false);
                Check(spatial[3] > .99f, "spatial distance opt-in");
            });
            Test(report, "接続距離は面の経路に沿って計算", () =>
            {
                var folded = new[] { Vector3.zero, Vector3.right * 2, Vector3.up * 2, Vector3.right * .01f };
                var topology = new VertexGraph(folded, new[] { 0, 1, 2, 1, 2, 3 }, false);
                var weights = VertexEditGeometry.Weights(folded, new[] { true, false, false, false }, topology, true, 1, VertexFalloff.Smooth, true);
                Check(weights[3] == 0, "nearby folded surface must not move");
            });
            Test(report, "影響の減衰4種類・境界・空選択", () =>
            {
                foreach (VertexFalloff mode in Enum.GetValues(typeof(VertexFalloff)))
                {
                    Check(VertexEditGeometry.Falloff(0, 1, mode) == 1 && VertexEditGeometry.Falloff(1, 1, mode) == 0, "endpoints");
                    float previous = 1;
                    for (int i = 1; i < 100; i++) { float value = VertexEditGeometry.Falloff(i / 100f, 1, mode); Check(value <= previous && value >= 0, "monotonic"); previous = value; }
                }
                Check(VertexEditGeometry.Weights(vertices, new bool[6], graph, true, 1, VertexFalloff.Smooth, false).All(w => w == 0), "empty KD tree");
                Check(VertexEditGeometry.Weights(vertices, selection, graph, false, 1, VertexFalloff.Smooth, true).Count(w => w > 0) == 1, "proportional off");
            });
            Test(report, "複数選択の空間距離を最近傍と照合", () =>
            {
                var random = new System.Random(56); var points = Enumerable.Range(0, 300).Select(_ => new Vector3((float)random.NextDouble(), (float)random.NextDouble(), (float)random.NextDouble())).ToArray();
                var selected = Enumerable.Range(0, points.Length).Select(i => i % 11 == 0).ToArray();
                var weights = VertexEditGeometry.Weights(points, selected, null, true, .4f, VertexFalloff.Linear, false);
                for (int i = 0; i < points.Length; i++)
                {
                    float distance = points.Where((p, j) => selected[j]).Min(p => Vector3.Distance(points[i], p));
                    Check(Mathf.Abs(weights[i] - VertexEditGeometry.Falloff(distance, .4f, VertexFalloff.Linear)) < .00001f, "KD tree vs reference");
                }
            });
            Test(report, "手前・裏側・自身の表面の可視判定", () =>
            {
                var surface = new VertexPickSurface(new[] { new Vector3(-2, -2, 0), new Vector3(2, -2, 0), new Vector3(0, 2, 0) }, new[] { 0, 1, 2 });
                var ray = new Ray(new Vector3(0, 0, -2), Vector3.forward);
                Check(surface.Hit(ray, 10, out float distance, out _) && Mathf.Abs(distance - 2) < .00001f, "ray hit");
                Check(VertexPickSurface.Visible(ray, Vector3.zero, new[] { surface }), "own surface");
                Check(!VertexPickSurface.Visible(ray, Vector3.forward, new[] { surface }), "occluded vertex");
                Check(VertexPickSurface.Visible(ray, Vector3.back, new[] { surface }), "front vertex");
            });
            Test(report, "BVHは多数の面から最近接を取得", () =>
            {
                var points = new List<Vector3>(); var indices = new List<int>();
                for (int i = 0; i < 80; i++) { int start = points.Count; points.AddRange(new[] { new Vector3(-2, -2, i), new Vector3(2, -2, i), new Vector3(0, 2, i) }); indices.AddRange(new[] { start, start + 1, start + 2 }); }
                var surface = new VertexPickSurface(points.ToArray(), indices.ToArray());
                Check(surface.Hit(new Ray(new Vector3(0, 0, -2), Vector3.forward), 100, out float distance, out _) && Mathf.Abs(distance - 2) < .00001f, "nearest BVH hit");
                surface.Refit(points.Select(v => v + Vector3.right * 10).ToArray());
                Check(!surface.Hit(new Ray(new Vector3(0, 0, -2), Vector3.forward), 100, out _, out _), "refit removes old hits");
                Check(surface.Hit(new Ray(new Vector3(10, 0, -2), Vector3.forward), 100, out distance, out _) && Mathf.Abs(distance - 2) < .00001f, "refit updates all bounds");
            });
            Test(report, "移動量をスキニング前の座標へ変換", () =>
            {
                var skin = Matrix4x4.TRS(new Vector3(3, 2, 1), Quaternion.Euler(0, 0, 90), new Vector3(2, 3, 1));
                var world = new[] { skin.MultiplyPoint3x4(Vector3.right), skin.MultiplyPoint3x4(Vector3.left) };
                var sculpt = new[] { Vector3.up * .1f, Vector3.zero };
                var result = VertexEditGeometry.Transform(world, new[] { skin.inverse, skin.inverse }, sculpt, new[] { 1f, 0 }, world[0], Quaternion.identity,
                    Vector3.up * .02f, Quaternion.identity, Vector3.one, null, new[] { true, false }, Vector3.right);
                Near(skin.MultiplyVector(result[0] - sculpt[0]), Vector3.up * .02f, "posed world movement"); Near(result[1], Vector3.zero, "unselected untouched");
                Near(sculpt[0], Vector3.up * .1f, "snapshot immutable");
            });
            Test(report, "選択中心の回転・ローカル軸の拡大縮小", () =>
            {
                var points = new[] { Vector3.right, Vector3.left }; var matrices = new[] { Matrix4x4.identity, Matrix4x4.identity };
                var rotation = VertexEditGeometry.Transform(points, matrices, new Vector3[2], new[] { 1f, .5f }, Vector3.zero, Quaternion.identity,
                    Vector3.zero, Quaternion.Euler(0, 0, 90), Vector3.one, null, new[] { true, false }, Vector3.right);
                Near(points[0] + rotation[0], Vector3.up, "rotation"); Near(points[1] + rotation[1], new Vector3(-.5f, -.5f, 0), "weighted rotation");
                var scale = VertexEditGeometry.Transform(points, matrices, new Vector3[2], new[] { 1f, 1f }, Vector3.zero, Quaternion.Euler(0, 0, 90),
                    Vector3.zero, Quaternion.identity, new Vector3(2, 1, 1), null, new[] { true, true }, Vector3.right);
                Near(scale[0], Vector3.zero, "rotated scale axes");
            });
            Test(report, "左右対称は片側へ反映・両側の明示選択を保持", () =>
            {
                var points = new[] { Vector3.right, Vector3.left }; var matrices = new[] { Matrix4x4.identity, Matrix4x4.identity };
                var result = VertexEditGeometry.Transform(points, matrices, new Vector3[2], new[] { 1f, 0f }, Vector3.zero, Quaternion.identity,
                    Vector3.right * .02f, Quaternion.identity, Vector3.one, new[] { 1, 0 }, new[] { true, false }, Vector3.right);
                Near(result[0], Vector3.right * .02f, "primary"); Near(result[1], Vector3.left * .02f, "mirrored");
                result = VertexEditGeometry.Transform(points, matrices, new Vector3[2], new[] { 1f, 1f }, Vector3.zero, Quaternion.identity,
                    Vector3.right * .02f, Quaternion.identity, Vector3.one, new[] { 1, 0 }, new[] { true, true }, Vector3.right);
                Near(result[1], Vector3.right * .02f, "explicit opposite selection");
            });
            Test(report, "無効な数値は元の編集データを変えず拒否", () =>
            {
                var sculpt = new[] { Vector3.up }; bool rejected = false;
                try { VertexEditGeometry.Transform(new[] { Vector3.zero }, new[] { Matrix4x4.identity }, sculpt, new[] { 1f }, Vector3.zero, Quaternion.identity, new Vector3(float.NaN, 0, 0), Quaternion.identity, Vector3.one, null, new[] { true }, Vector3.right); }
                catch (InvalidOperationException) { rejected = true; }
                Check(rejected && sculpt[0] == Vector3.up, "NaN rejected");
            });
            LoopTests(report);
            if (!string.IsNullOrEmpty(avatarPath)) AvatarTests(report, avatarPath);
            string json = JsonUtility.ToJson(report, true); File.WriteAllText("Library/FaceTrackingBuilderSelectionVerification.json", json); return json;
        }
        static void LoopTests(BuilderVerification.Report report)
        {
            const int count = 18;
            var vertices = Enumerable.Range(0, count * 3).Select(i =>
                new Vector3(Mathf.Cos(i % count * Mathf.PI * 2 / count), i / count * .5f, Mathf.Sin(i % count * Mathf.PI * 2 / count))).ToArray();
            var triangles = new List<int>();
            for (int row = 0; row < 2; row++) for (int col = 0; col < count; col++)
            {
                int a = row * count + col, b = row * count + (col + 1) % count, c = b + count, d = a + count;
                triangles.AddRange(new[] { a, b, c, a, c, d });
            }
            var topology = new VertexLoopTopology(vertices, triangles.ToArray(), false);
            int Edge(VertexLoopTopology t, int a, int b) => Array.FindIndex(t.Edges, e => e.x == a && e.y == b || e.x == b && e.y == a);
            Test(report, "三角形化された円筒の内側ループを両方向へ追跡", () =>
            {
                var result = topology.Trace(Edge(topology, count + 1, count + 2), vertices);
                Check(result.closed && !result.ambiguous && result.vertices.OrderBy(i => i).SequenceEqual(Enumerable.Range(count, count)), "middle row closes without leaking to adjacent rows");
            });
            Test(report, "メッシュ境界のループは曲がり角を含めて追跡", () =>
            {
                var square = new[] { Vector3.zero, Vector3.right, new Vector3(1, 1, 0), Vector3.up };
                var t = new VertexLoopTopology(square, new[] { 0, 1, 2, 0, 2, 3 }, false);
                var result = t.Trace(Edge(t, 0, 1), square);
                Check(result.closed && result.vertices.Length == 4 && !result.ambiguous, "boundary square");
                Check(t.Trace(Edge(t, 0, 2), square).vertices.Length == 2, "diagonal does not select a boundary loop");
            });
            Test(report, "UV境界をまたぐループは接続設定を尊重", () =>
            {
                var split = new[] { Vector3.zero, Vector3.right, new Vector3(1, 1, 0), Vector3.zero, new Vector3(1, 1, 0), Vector3.up };
                var faces = new[] { 0, 1, 2, 3, 4, 5 };
                var separate = new VertexLoopTopology(split, faces, false); var joined = new VertexLoopTopology(split, faces, true);
                Check(separate.Trace(Edge(separate, 0, 1), split).vertices.Length == 3, "split islands remain separate");
                var loop = joined.Trace(Edge(joined, 0, 1), split);
                Check(loop.closed && loop.vertices.Length == 4 && new VertexGraph(split, faces, true).ExpandCoincident(loop.vertices).Count() == 6, "joined seam selects both copies");
            });
            Test(report, "ループ選択は非多様体・曖昧な分岐で停止", () =>
            {
                var branch = new[] { Vector3.zero, Vector3.left, new Vector3(-.5f, -1), new Vector3(.9f, -.1f), Vector3.right, new Vector3(.9f, .1f), new Vector3(-.5f, 1) };
                var faces = new List<int>(); for (int i = 1; i <= 6; i++) faces.AddRange(new[] { 0, i, i == 6 ? 1 : i + 1 });
                var t = new VertexLoopTopology(branch, faces.ToArray(), false); var result = t.Trace(Edge(t, 0, 1), branch);
                Check(result.ambiguous && result.vertices.Length == 2, "ambiguous near-parallel branches");
                var nonmanifold = new VertexLoopTopology(branch, new[] { 0, 1, 2, 1, 0, 3, 0, 1, 4 }, false);
                Check(nonmanifold.Trace(Edge(nonmanifold, 0, 1), branch).ambiguous, "nonmanifold seed");
            });
            Test(report, "ループの起点はクリックした辺・不可視の辺を除外", () =>
            {
                var world = new[] { Vector3.zero, Vector3.right, Vector3.up };
                var t = new VertexLoopTopology(world, new[] { 0, 1, 2 }, false);
                var screen = new[] { new Vector2(10, 10), new Vector2(100, 10), new Vector2(10, 100) };
                Check(t.PickEdge(screen, new[] { true, true, true }, new Vector2(55, 11), 9) == Edge(t, 0, 1), "nearest edge midpoint");
                Check(t.PickEdge(screen, new[] { false, false, true }, new Vector2(55, 11), 9) == -1, "hidden edge");
                Check(t.PickEdge(screen, new[] { true, true, true }, new Vector2(200, 200), 9) == -1, "no distant edge");
            });
            Test(report, "Altクリックのみ選択・Altドラッグは視点操作", () =>
            {
                using (var editor = new SceneExpressionEditor())
                {
                    editor.BeginAltLoop(Vector2.zero, true);
                    Check(editor.EndAltLoop(Vector2.one, out bool shift) && shift, "shift alt click");
                    editor.BeginAltLoop(Vector2.zero, false); editor.MoveAltLoop(new Vector2(12, 0));
                    Check(!editor.EndAltLoop(Vector2.zero, out _), "drag returning to origin remains navigation");
                    Check(!editor.EndAltLoop(Vector2.zero, out _), "no duplicate release");
                }
            });
        }
        static void AvatarTests(BuilderVerification.Report report, string path)
        {
            var source = AssetDatabase.LoadAssetAtPath<GameObject>(path); Check(source, "test avatar");
            var profile = AvatarAnalysis.Analyze(source); var plan = profile.renderers.First(p => p.selected);
            string original = MeshEngine.Fingerprint(plan.source); int originalShapes = plan.source.blendShapeCount;
            Test(report, "実アバターの三角形メッシュから頂点列を推定・元Meshを保持", () =>
            {
                var topology = new VertexLoopTopology(plan.source.vertices, plan.source.triangles, true);
                int step = Math.Max(1, topology.Edges.Length / 64);
                var loops = Enumerable.Range(0, topology.Edges.Length).Where(i => i % step == 0).Select(i => topology.Trace(i, plan.source.vertices)).ToArray();
                Check(loops.Any(r => r.vertices.Length > 3) && loops.All(r => r.vertices.Distinct().Count() == r.vertices.Length) && MeshEngine.Fingerprint(plan.source) == original, "useful paths without duplicate vertices or source changes");
            });
            var recipe = new ShapeRecipe { name = "FTB_VertexSelectionVerification", trackingKey = "BrowInnerUpRight", mode = GenerateMode.Landmarks };
            plan.recipes.Add(recipe);
            try
            {
                using (var session = new BuilderSession(profile))
                using (var editor = new SceneExpressionEditor { session = session, plan = plan, recipe = recipe, enabled = true, selectionMode = true, avatarAxes = false })
                {
                    var topology = new VertexGraph(plan.source.vertices, plan.source.triangles, false);
                    int[] part = topology.Linked(0);
                    Test(report, "実アバターでリンク選択・中心位置の数値編集", () =>
                    {
                        editor.SelectVertices(part, VertexSelectOperation.Replace); Check(editor.SelectedCount == part.Length, "linked count");
                        editor.ApplyNumericTransform(editor.SelectionCoordinates + Vector3.up * .001f);
                        var delta = MeshEngine.Sample(session.Renderer(editor.plan).sharedMesh, recipe.name, 100);
                        Check(delta.vertices.Where((v, i) => part.Contains(i)).Any(v => v.magnitude > 0), "selected moved");
                        Check(delta.vertices.Where((v, i) => !part.Contains(i)).All(v => v == Vector3.zero), "other parts untouched");
                    });
                    Test(report, "座標編集のUndo・Redoとプレビュー復元", () =>
                    {
                        Undo.PerformUndo(); Check(editor.recipe.sculpt.Length == 0 || editor.recipe.sculpt.All(v => v == Vector3.zero), "undo sculpt");
                        Check(MeshEngine.Sample(session.Renderer(editor.plan).sharedMesh, recipe.name, 100).vertices.All(v => v == Vector3.zero), "undo preview");
                        Undo.PerformRedo(); Check(editor.recipe.sculpt.Any(v => v.magnitude > 0), "redo");
                    });
                    Test(report, "ドラッグ更新は開始時から計算・キャンセルで復元", () =>
                    {
                        editor.SelectVertices(new[] { 0 }, VertexSelectOperation.Replace); var before = (Vector3[])editor.recipe.sculpt.Clone();
                        editor.BeginVertexTransform(); editor.PreviewVertexTransform(Vector3.up * .001f, Quaternion.identity, Vector3.one);
                        var first = editor.recipe.sculpt[0] - before[0]; editor.PreviewVertexTransform(Vector3.up * .002f, Quaternion.identity, Vector3.one);
                        Near(editor.recipe.sculpt[0] - before[0], first * 2, "no cumulative drift");
                        editor.EndSelectionInteraction(true); Check(editor.recipe.sculpt.SequenceEqual(before), "cancel restores snapshot");
                    });
                    Test(report, "頂点選択のUndo・Redo", () =>
                    {
                        editor.SelectVertices(new[] { 1, 2 }, VertexSelectOperation.Replace); Undo.PerformUndo();
                        Check(editor.SelectedVertices[0] && editor.SelectedCount == 1, "undo selection"); Undo.PerformRedo();
                        Check(!editor.SelectedVertices[0] && editor.SelectedVertices[1] && editor.SelectedVertices[2] && editor.SelectedCount == 2, "redo selection");
                    });
                    Test(report, "変形中の影響半径変更で移動量を保持", () =>
                    {
                        editor.proportional = true; editor.proportionalRadius = .01f; editor.SelectVertices(new[] { 0 }, VertexSelectOperation.Replace);
                        var before = (Vector3[])editor.recipe.sculpt.Clone(); editor.BeginVertexTransform();
                        editor.PreviewVertexTransform(Vector3.up * .001f, Quaternion.identity, Vector3.one); var moved = editor.recipe.sculpt[0];
                        editor.ResizeProportionalRadius(.04f); Near(editor.recipe.sculpt[0], moved, "selected movement retained");
                        editor.EndSelectionInteraction(true); Check(editor.recipe.sculpt.SequenceEqual(before), "radius edit cancels with gesture"); editor.proportional = false;
                    });
                    Test(report, "Unityハンドルが制御を解放したら変形処理を終了・Undoを維持", () =>
                    {
                        editor.SelectVertices(new[] { 0 }, VertexSelectOperation.Replace); var before = (Vector3[])editor.recipe.sculpt.Clone();
                        editor.BeginVertexTransform();
                        var field = typeof(SceneExpressionEditor).GetField("transformGesture", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic);
                        var gesture = field.GetValue(editor); gesture.GetType().GetField("control").SetValue(gesture, 123456);
                        editor.PreviewVertexTransform(Vector3.up * .001f, Quaternion.identity, Vector3.one);
                        editor.CompleteReleasedHandle(123456); Check(editor.TransformInProgress, "owned control still dragging");
                        editor.CompleteReleasedHandle(0); Check(!editor.TransformInProgress, "control released even if mouse-up was consumed");
                        Undo.PerformUndo(); Check(editor.recipe.sculpt.SequenceEqual(before), "released transform remains one undo step");
                    });
                    Test(report, "元の顔を比較中は座標編集・選択を禁止", () =>
                    {
                        var before = (Vector3[])editor.recipe.sculpt.Clone(); var selected = editor.SelectedVertices;
                        editor.Compare(true); editor.ApplyNumericTransform(Vector3.one); editor.ClearVertexSelection();
                        Check(before.SequenceEqual(editor.recipe.sculpt) && selected.SequenceEqual(editor.SelectedVertices), "comparison protected"); editor.Compare(false);
                    });
                    Test(report, "Shiftブラシは重なった範囲をストローク中に再反転しない", () =>
                    {
                        editor.SetCoincidentSelection(true); editor.SelectVertices(new[] { 0 }, VertexSelectOperation.Replace);
                        var before = editor.SelectedVertices;
                        var method = typeof(SceneExpressionEditor).GetMethod("ChangeGestureSelection", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic);
                        method.Invoke(editor, new object[] { new[] { 0, 1 }, VertexSelectOperation.Toggle }); var first = editor.SelectedVertices;
                        method.Invoke(editor, new object[] { new[] { 0, 1 }, VertexSelectOperation.Toggle });
                        Check(!first[0] && first[1] == !before[1] && editor.SelectedVertices.SequenceEqual(first), "toggle each vertex once per stroke including seams");
                        editor.SetCoincidentSelection(false);
                    });
                    Test(report, "設定再読み込み・生成Meshの保存と再読み込み", () =>
                    {
                        var restored = ScriptableObject.CreateInstance<BuilderProfile>(); Mesh mesh = null;
                        try
                        {
                            EditorJsonUtility.FromJsonOverwrite(EditorJsonUtility.ToJson(profile), restored);
                            var restoredPlan = restored.renderers.First(p => p.path == editor.plan.path); mesh = MeshEngine.Build(restoredPlan, out _);
                            Check(mesh.vertexCount == plan.source.vertexCount && mesh.blendShapeCount == originalShapes + 1, "topology and existing shapes");
                            string assetPath = AssetDatabase.GenerateUniqueAssetPath(BuilderStorage.OutputRoot + "/VertexSelectionValidation.asset");
                            AssetDatabase.CreateAsset(mesh, assetPath); AssetDatabase.SaveAssetIfDirty(mesh); mesh = null;
                            var loaded = AssetDatabase.LoadAssetAtPath<Mesh>(assetPath);
                            Check(MeshEngine.Sample(loaded, recipe.name, 100).vertices.Any(v => v.sqrMagnitude > 0), "persisted vertex edits");
                        }
                        finally { if (mesh) Object.DestroyImmediate(mesh); Object.DestroyImmediate(restored); }
                    });
                    Test(report, "実アバターの元Mesh・既存BlendShapeを保持", () => Check(MeshEngine.Fingerprint(plan.source) == original && plan.source.blendShapeCount == originalShapes, "source immutable"));
                }
            }
            finally { Undo.ClearUndo(profile); Object.DestroyImmediate(profile); }
        }
    }
}
