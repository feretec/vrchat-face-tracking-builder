using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using UnityEditor;
using UnityEditor.UIElements;
using UnityEngine;
using UnityEngine.UIElements;
using VRC.SDK3.Avatars.Components;
using Object = UnityEngine.Object;

namespace FaceTrackingBuilder
{
    // Executable through unity-mcp, no scene or original avatar modifications.
    public static class BuilderVerification
    {
        [Serializable] public class Result { public string name; public bool passed; public string detail; }
        [Serializable] public class Report { public string unity; public string time; public List<Result> results = new List<Result>(); public string output; }
        static void Check(bool condition, string message) { if (!condition) throw new Exception(message); }
        static void Near(Vector3 a, Vector3 b, string message) { Check(Vector3.Distance(a, b) < .00001f, message + " " + a + " != " + b); }
        static void Test(Report report, string name, Action action)
        {
            try { action(); report.results.Add(new Result { name = name, passed = true, detail = "OK" }); }
            catch (Exception e) { report.results.Add(new Result { name = name, passed = false, detail = e.ToString() }); }
        }
        static void Throws(Action action) { bool threw = false; try { action(); } catch (InvalidOperationException) { threw = true; } Check(threw, "Expected safety rejection"); }
        static Mesh Fixture()
        {
            var m = new Mesh { name = "FTB_NumericalFixture" };
            m.vertices = new[] { new Vector3(-1, -1, 0), new Vector3(1, -1, 0), new Vector3(-1, 1, 0), new Vector3(1, 1, 0) };
            m.normals = Enumerable.Repeat(Vector3.forward, 4).ToArray();
            m.tangents = Enumerable.Repeat(new Vector4(1, 0, 0, 1), 4).ToArray();
            m.uv = new[] { Vector2.zero, Vector2.right, Vector2.up, Vector2.one };
            m.bindposes = new[] { Matrix4x4.identity };
            m.boneWeights = Enumerable.Repeat(new BoneWeight { boneIndex0 = 0, weight0 = 1 }, 4).ToArray();
            m.subMeshCount = 2; m.SetTriangles(new[] { 0, 1, 2 }, 0); m.SetTriangles(new[] { 2, 1, 3 }, 1);
            var zero = new Vector3[4];
            m.AddBlendShapeFrame("Both", 50, Enumerable.Repeat(Vector3.up * .01f, 4).ToArray(), Enumerable.Repeat(Vector3.right * .001f, 4).ToArray(), zero);
            m.AddBlendShapeFrame("Both", 100, Enumerable.Repeat(Vector3.up * .04f, 4).ToArray(), Enumerable.Repeat(Vector3.right * .004f, 4).ToArray(), zero);
            m.AddBlendShapeFrame("Push", 100, Enumerable.Repeat(Vector3.forward * .02f, 4).ToArray(), zero, zero);
            return m;
        }
        public static string Run(string avatarPath = "")
        {
            var report = new Report { unity = Application.unityVersion, time = DateTime.UtcNow.ToString("O") };
            var mesh = Fixture();
            try
            {
                Test(report, "複数フレーム補間・外挿・法線差分", () =>
                {
                    Near(MeshEngine.Sample(mesh, "Both", 25).vertices[0], Vector3.up * .005f, "implicit zero");
                    Near(MeshEngine.Sample(mesh, "Both", 75).vertices[0], Vector3.up * .025f, "interpolation");
                    Near(MeshEngine.Sample(mesh, "Both", 125).vertices[0], Vector3.up * .055f, "extrapolation");
                    Near(MeshEngine.Sample(mesh, "Both", 75).normals[0], Vector3.right * .0025f, "normal interpolation");
                });
                Test(report, "比率付き合成", () =>
                {
                    var recipe = new ShapeRecipe { sources = new List<ShapeSource> { new ShapeSource { name = "Both", coefficient = .5f }, new ShapeSource { name = "Push", coefficient = 2 } } };
                    Near(MeshEngine.Generate(mesh, recipe, 100, out _).vertices[0], new Vector3(0, .02f, .04f), "weighted composition");
                });
                Test(report, "回転した中心面で左右分割・再合成", () =>
                {
                    var recipe = new ShapeRecipe { mode = GenerateMode.SplitLeft, planeRight = Vector3.up, feather = .1f, sources = new List<ShapeSource> { new ShapeSource { name = "Both" } } };
                    var left = MeshEngine.Generate(mesh, recipe, 100, out _); recipe.mode = GenerateMode.SplitRight; var right = MeshEngine.Generate(mesh, recipe, 100, out _);
                    Near(left.vertices[0], Vector3.up * .04f, "left by y"); Near(left.vertices[2], Vector3.zero, "opposite side");
                    for (int i = 0; i < 4; i++) Near(left.vertices[i] + right.vertices[i], Vector3.up * .04f, "partition unity");
                });
                Test(report, "部分抽出・特徴点の滑らかな減衰", () =>
                {
                    var recipe = new ShapeRecipe { mode = GenerateMode.Extract, regionCenter = mesh.vertices[0], regionRadius = .5f, sources = new List<ShapeSource> { new ShapeSource { name = "Both" } } };
                    var d = MeshEngine.Generate(mesh, recipe, 100, out _); Near(d.vertices[0], Vector3.up * .04f, "inside"); Near(d.vertices[1], Vector3.zero, "outside");
                    recipe.mode = GenerateMode.Landmarks; recipe.landmarks.Add(new Landmark { position = mesh.vertices[0], movement = Vector3.forward * .02f, radius = .5f });
                    d = MeshEngine.Generate(mesh, recipe, 100, out _); Near(d.vertices[0], Vector3.forward * .02f, "landmark"); Near(d.vertices[1], Vector3.zero, "locality");
                    Check(Math.Abs(MeshEngine.Falloff(.25f, .5f) - .5f) < .0001f, "smooth falloff");
                });
                Test(report, "ミラー対応・非対称頂点の拒否", () =>
                {
                    var pairs = MeshEngine.MirrorPairs(mesh.vertices, Vector3.zero, Vector3.right, .001f); Check(pairs[0] == 1 && pairs[2] == 3, "mirror pair");
                    var v = mesh.vertices; v[1] += Vector3.forward * .1f; pairs = MeshEngine.MirrorPairs(v, Vector3.zero, Vector3.right, .001f); Check(pairs[0] == -1 && pairs[1] == -1, "asymmetric rejection");
                    Near(MeshEngine.ReflectVector(new Vector3(1, 2, 3), Vector3.right), new Vector3(-1, 2, 3), "delta reflection");
                });
                Test(report, "既存全フレーム・Mesh構造保持・再生成の冪等性", () =>
                {
                    var plan = new RendererPlan { source = mesh, fingerprint = MeshEngine.Fingerprint(mesh) };
                    plan.recipes.Add(new ShapeRecipe { name = "Generated", frames = new List<float> { 100, 50 }, sources = new List<ShapeSource> { new ShapeSource { name = "Both" } } });
                    var a = MeshEngine.Build(plan, out _); var b = MeshEngine.Build(plan, out _);
                    try
                    {
                        Check(a.blendShapeCount == 3 && b.blendShapeCount == 3 && mesh.blendShapeCount == 2, "shape count");
                        Check(a.vertices.SequenceEqual(mesh.vertices) && a.uv.SequenceEqual(mesh.uv) && a.bindposes.SequenceEqual(mesh.bindposes) && a.boneWeights.SequenceEqual(mesh.boneWeights) && a.tangents.SequenceEqual(mesh.tangents), "mesh attributes");
                        Check(a.subMeshCount == 2 && a.GetIndices(1).SequenceEqual(mesh.GetIndices(1)), "submeshes");
                        Check(a.GetBlendShapeFrameCount(0) == 2 && a.GetBlendShapeFrameWeight(2, 0) == 50, "frames");
                        Near(MeshEngine.Sample(a, "Both", 75).vertices[0], MeshEngine.Sample(mesh, "Both", 75).vertices[0], "original unchanged");
                    }
                    finally { Object.DestroyImmediate(a); Object.DestroyImmediate(b); }
                });
                Test(report, "トポロジー変化・名前衝突・不正変位の停止", () =>
                {
                    var p = new RendererPlan { source = mesh, fingerprint = "wrong" }; Throws(() => MeshEngine.Build(p, out _));
                    p.fingerprint = MeshEngine.Fingerprint(mesh); p.recipes.Add(new ShapeRecipe { name = "Both" }); Throws(() => MeshEngine.Build(p, out _));
                    var bad = new ShapeRecipe { sources = new List<ShapeSource> { new ShapeSource { name = "Push", coefficient = float.NaN } } }; Throws(() => MeshEngine.Generate(mesh, bad, 100, out _));
                    bad.sources[0].coefficient = 1000; Throws(() => MeshEngine.Generate(mesh, bad, 100, out _));
                });
                Test(report, "生成表情を合成・ミラー入力に使用・依存順と全フレーム保持", () =>
                {
                    var p = new RendererPlan { source = mesh, fingerprint = MeshEngine.Fingerprint(mesh) };
                    var right = new ShapeRecipe { name = "Right", mode = GenerateMode.SplitRight, frames = new List<float> { 50, 100 }, sources = new List<ShapeSource> { new ShapeSource { name = "Both" } } };
                    var left = new ShapeRecipe { name = "Left", mode = GenerateMode.Mirror, frames = new List<float> { 50, 100 }, sources = new List<ShapeSource> { new ShapeSource { name = "Right" } } };
                    var both = new ShapeRecipe { name = "Combined", sources = new List<ShapeSource> { new ShapeSource { name = "Right" }, new ShapeSource { name = "Left" } } };
                    p.recipes.AddRange(new[] { both, left, right });
                    var built = MeshEngine.Build(p, out _); var again = MeshEngine.Build(p, out _);
                    try
                    {
                        Check(built.blendShapeCount == 5 && again.blendShapeCount == 5 && mesh.blendShapeCount == 2, "idempotent and non-destructive");
                        Near(MeshEngine.Sample(built, "Left", 75).vertices[0], Vector3.up * .025f, "generated source multi-frame interpolation");
                        Near(MeshEngine.Sample(built, "Left", 100).vertices[1], Vector3.zero, "right side remains neutral");
                        Near(MeshEngine.Sample(built, "Left", 100).normals[0], Vector3.left * .004f, "normal displacement mirrored");
                        foreach (var v in MeshEngine.Sample(built, "Combined", 100).vertices) Near(v, Vector3.up * .04f, "composition of generated sources");
                        Check(RecipeSources.Frames(p, "Left").SequenceEqual(new float[] { 50, 100 }), "generated frame selection");
                    }
                    finally { Object.DestroyImmediate(built); Object.DestroyImmediate(again); }
                    Check(!RecipeSources.Available(p, right).Contains("Combined") && !RecipeSources.Available(p, right).Contains("Left"), "descendants excluded");
                    Check(RecipeSources.Available(p, left).Contains("Right") && !RecipeSources.Available(p, left).Contains("Left"), "self excluded, source available");
                    left.sources[0].name = "Combined"; Throws(() => MeshEngine.Build(p, out _));
                    left.sources[0].name = "Left"; Throws(() => MeshEngine.Build(p, out _));
                    left.sources[0].name = "Missing"; Throws(() => MeshEngine.Build(p, out _));
                    var subset = MeshEngine.Build(p, out _, "Right");
                    try { Check(subset.blendShapeCount == 3, "source preview ignores unrelated unfinished targets"); }
                    finally { Object.DestroyImmediate(subset); }
                });
                Test(report, "任意の左右セット・ブラシ変位の連動・再命名・保存・削除保護", () =>
                {
                    var profile = ScriptableObject.CreateInstance<BuilderProfile>();
                    try
                    {
                        var plan = new RendererPlan { source = mesh, fingerprint = MeshEngine.Fingerprint(mesh), selected = true }; profile.renderers.Add(plan);
                        var right = new ShapeRecipe { name = "FTB_CheekPuffRight", trackingKey = "CheekPuffRight", mode = GenerateMode.Landmarks, sculpt = new Vector3[4], frames = new List<float> { 50, 100 } };
                        right.sculpt[1] = new Vector3(.03f, 0, .02f); plan.recipes.Add(right);
                        Check(RecipeSources.OppositeKey("CheekPuffRight") == "CheekPuffLeft" && RecipeSources.OppositeKey("EyeLookOutLeft") == "EyeLookOutRight", "anatomical pairing");
                        Check(RecipeSources.OppositeKey("JawOpen") == null && RecipeSources.OppositeKey("TongueUp") == null, "no automatic up-down inversion");
                        var left = RecipeSources.CreatePair(profile, plan, right, right.trackingKey);
                        Check(ReferenceEquals(left, RecipeSources.CreatePair(profile, plan, right, right.trackingKey)) && plan.recipes.Count == 2, "no duplicate pair");
                        var built = MeshEngine.Build(plan, out _);
                        try { Near(MeshEngine.Sample(built, left.name, 100).vertices[0], new Vector3(-.03f, 0, .02f), "brush mirrored"); }
                        finally { Object.DestroyImmediate(built); }
                        RecipeSources.Rename(profile, plan, right, "RenamedRight"); right.sculpt[1] *= 2; right.frames = new List<float> { 60, 100 };
                        Check(RecipeSources.Inputs(plan, left).Single() == right.name && RecipeSources.Frames(plan, left.name).First() == 60, "name and frames follow source");
                        var restored = Object.Instantiate(profile);
                        try
                        {
                            var output = MeshEngine.Build(restored.renderers[0], out _);
                            try { Near(MeshEngine.Sample(output, left.name, 100).vertices[0], new Vector3(-.06f, 0, .04f), "serialized relationship follows edits"); }
                            finally { Object.DestroyImmediate(output); }
                        }
                        finally { Object.DestroyImmediate(restored); }
                        Throws(() => RecipeSources.Remove(profile, plan, right));
                        RecipeSources.Detach(plan, left);
                        RecipeSources.Rename(profile, plan, right, "RenamedAgain");
                        Check(left.sources.Single().name == right.name, "manual mirror input survives rename");
                        Throws(() => RecipeSources.CreatePair(profile, plan, right, right.trackingKey));
                        Check(plan.recipes.Count == 2 && profile.mappings.Single().shape == left.name, "existing counterpart preserved");
                        RecipeSources.Remove(profile, plan, left); RecipeSources.Remove(profile, plan, right);
                        Check(plan.recipes.Count == 0 && string.IsNullOrEmpty(profile.mappings.Single().shape), "explicit removal clears mapping");
                    }
                    finally { Object.DestroyImmediate(profile); }
                });
                Test(report, "選択中の表情を編集・複数メッシュと他キーの割り当て保持", () =>
                {
                    var root = new GameObject("MappingEditFixture"); var profile = ScriptableObject.CreateInstance<BuilderProfile>();
                    try
                    {
                        root.AddComponent<SkinnedMeshRenderer>().sharedMesh = mesh;
                        var second = new GameObject("Second"); second.transform.SetParent(root.transform); second.AddComponent<SkinnedMeshRenderer>().sharedMesh = mesh;
                        profile.sourceAvatar = root;
                        profile.renderers.Add(new RendererPlan { path = "", source = mesh, fingerprint = MeshEngine.Fingerprint(mesh), selected = true });
                        profile.renderers.Add(new RendererPlan { path = "Second", source = mesh, fingerprint = MeshEngine.Fingerprint(mesh), selected = true });
                        var mapping = new TrackingMapping { key = "EyeClosedLeft", shape = "Both", multiplier = .7f, excluded = true };
                        var other = new TrackingMapping { key = "EyeClosedRight", shape = "Both" }; profile.mappings.Add(mapping); profile.mappings.Add(other);
                        var plan = profile.renderers[0]; var edited = RecipeSources.PrepareEditing(profile, mapping, plan);
                        Check(edited.mode == GenerateMode.Combine && edited.sources.Single().name == "Both" && edited.frames.SequenceEqual(new float[] { 50, 100 }), "current shape and frames retained");
                        Check(other.shape == "Both" && mapping.multiplier == .7f && mapping.excluded, "other mapping and usage settings untouched");
                        Check(profile.renderers.All(p => p.recipes.Single().name == mapping.shape), "same SDK shape name present on all original meshes");
                        Check(ReferenceEquals(edited, RecipeSources.PrepareEditing(profile, mapping, plan)) && plan.recipes.Count == 1, "reopen without duplication");
                        edited.sculpt = new Vector3[4]; edited.sculpt[0] = Vector3.forward * .002f;
                        var a = MeshEngine.Build(plan, out _); var b = MeshEngine.Build(profile.renderers[1], out _);
                        try
                        {
                            Near(MeshEngine.Sample(a, mapping.shape, 100).vertices[0], Vector3.up * .04f + Vector3.forward * .002f, "selected mesh editable");
                            Near(MeshEngine.Sample(b, mapping.shape, 100).vertices[0], Vector3.up * .04f, "other mesh keeps original expression");
                            Near(MeshEngine.Sample(a, "Both", 100).vertices[0], Vector3.up * .04f, "shared source kept");
                            Check(mesh.blendShapeCount == 2, "source asset unchanged");
                        }
                        finally { Object.DestroyImmediate(a); Object.DestroyImmediate(b); }
                        other.shape = mapping.shape;
                        var ownCopy = RecipeSources.PrepareEditing(profile, mapping, plan);
                        Check(ownCopy != edited && ownCopy.sources.Single().name == other.shape && mapping.shape != other.shape, "shared generated expression isolated for selected key");
                    }
                    finally { Object.DestroyImmediate(profile); Object.DestroyImmediate(root); }
                });
                Test(report, "SDK Auto-Fill再利用・左右判定", () =>
                {
                    Check(AvatarAnalysis.SdkAutoFillAvailable, "SDK autofill reflection");
                    var go = new GameObject("MappingFixture"); var p = ScriptableObject.CreateInstance<BuilderProfile>();
                    var m = Object.Instantiate(mesh);
                    try
                    {
                        m.AddBlendShapeFrame("eyeBlinkLeft", 100, new Vector3[4], null, null);
                        var r = go.AddComponent<SkinnedMeshRenderer>(); r.sharedMesh = m;
                        p.renderers.Add(new RendererPlan { source = m, selected = true }); AvatarAnalysis.Suggest(p);
                        var mapping = p.mappings.First(x => x.key == "EyeClosedLeft"); Check(mapping.shape == "eyeBlinkLeft" && mapping.reason.Contains("SDK") && !mapping.confirmed, "sdk candidate");
                        Check(p.mappings.First(x => x.key == "EyeClosedRight").shape != "eyeBlinkLeft", "opposite side mismatch");
                    }
                    finally { Object.DestroyImmediate(p); Object.DestroyImmediate(go); Object.DestroyImmediate(m); }
                });
                Test(report, "候補は既定で使用・除外候補はSDK適用対象外", () =>
                {
                    var root = new GameObject("DefaultCandidateFixture"); var p = ScriptableObject.CreateInstance<BuilderProfile>();
                    try
                    {
                        root.AddComponent<SkinnedMeshRenderer>().sharedMesh = mesh;
                        var descriptor = root.AddComponent<VRCAvatarDescriptor>();
                        p.renderers.Add(new RendererPlan { path = "", source = mesh, selected = true });
                        p.mappings.Add(new TrackingMapping { key = "EyeClosedLeft", shape = "Both" });
                        p.mappings.Add(new TrackingMapping { key = "EyeClosedRight", shape = "Push", excluded = true });
                        p.mappings.Add(new TrackingMapping { key = "JawOpen", shape = "" });
                        Check(p.mappings[0].Included && !p.mappings[0].confirmed, "no per-expression confirmation required");
                        NativeSdk.Apply(p, root);
                        var set = descriptor.faceTrackingBlendshapesSetList.Last();
                        Check(set.faceTrackingBlendshapeNames[(int)VRCAvatarDescriptor.FaceTrackingKey.EyeClosedLeft] == "Both", "candidate applied");
                        Check(string.IsNullOrEmpty(set.faceTrackingBlendshapeNames[(int)VRCAvatarDescriptor.FaceTrackingKey.EyeClosedRight]), "excluded candidate skipped");
                        Check(!NativeSdk.Diff(p, root).Any(line => line.Contains("EyeClosedRight →")), "review matches export");
                        var restored = Object.Instantiate(p);
                        try { Check(restored.mappings[1].excluded && !restored.mappings[1].Included, "exclusion survives serialization"); }
                        finally { Object.DestroyImmediate(restored); }
                        AvatarAnalysis.Suggest(p); Check(p.mappings.First(m => m.key == "EyeClosedRight").excluded, "refresh retains exclusions");
                    }
                    finally { Object.DestroyImmediate(root); Object.DestroyImmediate(p); }
                });
                Test(report, "参考図の眉の左右対称性と片眉の動き", () =>
                {
                    foreach (string key in AvatarAnalysis.Keys.Where(k => !k.StartsWith("Brow")))
                        foreach (bool inner in new[] { false, true })
                        {
                            var a = ExpressionDiagram.BrowPoint(key, -1, inner); var b = ExpressionDiagram.BrowPoint(key, 1, inner);
                            Check(Mathf.Abs(a.x + b.x - 1) < .00001f && Mathf.Abs(a.y - b.y) < .00001f, "neutral brow symmetry " + key);
                        }
                    Check(ExpressionDiagram.BrowPoint("BrowInnerUpRight", -1, true).y < ExpressionDiagram.BrowPoint("BrowInnerUpRight", 1, true).y, "intended unilateral movement retained");
                });
                Test(report, "任意生成の視線分割提案・左右・解析のみでは生成しない", () =>
                {
                    var m = Object.Instantiate(mesh);
                    try
                    {
                        foreach (string name in new[] { "LookRight", "LookLeft", "LookUp", "LookDown" }) m.AddBlendShapeFrame(name, 100, new Vector3[4], null, null);
                        var p = new RendererPlan { source = m };
                        foreach (string key in AvatarAnalysis.Keys.Where(k => k.StartsWith("EyeLook")))
                        {
                            var suggestion = ExpressionRecipes.Suggest(p, key); Check(suggestion != null, "suggestion " + key);
                            string expected = key.Contains("Up") ? "LookUp" : key.Contains("Down") ? "LookDown" : key == "EyeLookOutRight" || key == "EyeLookInLeft" ? "LookRight" : "LookLeft";
                            Check(suggestion.source == expected, "source " + key);
                            Check(suggestion.mode == (key.EndsWith("Right") ? GenerateMode.SplitRight : GenerateMode.SplitLeft), "side " + key);
                        }
                        Check(p.recipes.Count == 0, "suggestion must not generate or mutate");
                        Check(ExpressionRecipes.Suggest(p, "TongueRoll") == null, "no invented recipe");
                        Check(AvatarAnalysis.Keys.All(k => ExpressionGuide.Title(k) != k), "all SDK keys have Japanese titles");
                        Check(ExpressionGuide.Matches("EyeLookOutRight", "外側"), "Japanese search");
                    }
                    finally { Object.DestroyImmediate(m); }
                });
                Test(report, "複数メッシュ・既存Set保持・同名衝突停止", () =>
                {
                    var root = new GameObject("MultiMeshFixture"); var p = ScriptableObject.CreateInstance<BuilderProfile>();
                    try
                    {
                        var d = root.AddComponent<VRCAvatarDescriptor>();
                        d.faceTrackingBlendshapesSetList = new List<VRCAvatarDescriptor.FaceTrackingBlendshapesSet> { new VRCAvatarDescriptor.FaceTrackingBlendshapesSet { setName = "Existing", setWeight = .4f } };
                        foreach (string name in new[] { "Face", "Lashes" }) { var go = new GameObject(name); go.transform.parent = root.transform; go.AddComponent<SkinnedMeshRenderer>().sharedMesh = mesh; p.renderers.Add(new RendererPlan { path = name, source = mesh, selected = true }); }
                        p.mappings.Add(new TrackingMapping { key = "EyeClosedLeft", shape = "Both", confirmed = true });
                        NativeSdk.Apply(p, root); Check(d.faceTrackingMeshes.Count == 2 && d.faceTrackingBlendshapesSetList.Count == 2 && d.faceTrackingBlendshapesSetList[0].setWeight == .4f, "merge settings");
                        Check(d.faceTrackingBlendshapesSetList[1].faceTrackingBlendshapeNames[(int)VRCAvatarDescriptor.FaceTrackingKey.EyeClosedLeft] == "Both", "key index");
                        Throws(() => NativeSdk.Apply(p, root));
                    }
                    finally { Object.DestroyImmediate(root); Object.DestroyImmediate(p); }
                });
                Test(report, "Eye Look・音声干渉設定・不足マッピングの検証", () =>
                {
                    var root = new GameObject("GazeFixture"); var p = ScriptableObject.CreateInstance<BuilderProfile>();
                    try
                    {
                        root.AddComponent<SkinnedMeshRenderer>().sharedMesh = mesh;
                        var d = root.AddComponent<VRCAvatarDescriptor>();
                        d.lipSync = VRC.SDKBase.VRC_AvatarDescriptor.LipSyncStyle.VisemeBlendShape;
                        d.VisemeSkinnedMesh = root.GetComponent<SkinnedMeshRenderer>(); d.VisemeBlendShapes = Enumerable.Repeat("Both", 15).ToArray();
                        p.renderers.Add(new RendererPlan { path = "", source = mesh, selected = true });
                        p.routeGaze = true; p.routeVisemes = true; p.configureDampening = true; p.dampenMode = VRCAvatarDescriptor.VisemeDampenMode.DampenVisemes; p.dampenAmount = .6f;
                        Check(NativeSdk.Validate(p, root).Any(s => s.Contains("8つ")), "missing gaze blocked");
                        foreach (string key in AvatarAnalysis.Keys.Where(k => k.StartsWith("EyeLook"))) p.mappings.Add(new TrackingMapping { key = key, shape = "Push", confirmed = true });
                        NativeSdk.Apply(p, root);
                        Check(d.enableEyeLook && d.customEyeLookSettings.eyeGazeMode == VRCAvatarDescriptor.CustomEyeLookSettings.EyeGazeMode.Both, "gaze both");
                        Check(d.visemeDampenMode == VRCAvatarDescriptor.VisemeDampenMode.DampenVisemes && Math.Abs(d.visemeDampenAmount - .6f) < .0001f, "dampening applied");
                    }
                    finally { Object.DestroyImmediate(root); Object.DestroyImmediate(p); }
                });
            }
            finally { Object.DestroyImmediate(mesh); }
            if (!string.IsNullOrEmpty(avatarPath)) Integration(report, avatarPath);
            string json = JsonUtility.ToJson(report, true); File.WriteAllText("Library/FaceTrackingBuilderVerification.json", json); return json;
        }
        static void Integration(Report report, string avatarPath)
        {
            var source = AssetDatabase.LoadAssetAtPath<GameObject>(avatarPath);
            if (!source) { report.results.Add(new Result { name = "テストPrefab", passed = false, detail = "Not found: " + avatarPath }); return; }
            string before = EditorJsonUtility.ToJson(source.GetComponent<VRCAvatarDescriptor>());
            var profile = AvatarAnalysis.Analyze(source); profile.sourceName = source.name + "_EditorValidation";
            foreach (var mapping in profile.mappings) mapping.excluded = true;
            var body = profile.renderers.First(r => r.selected);
            var renderer = AvatarAnalysis.Resolve(source, body.path);
            var right = renderer.transform.InverseTransformDirection(source.transform.right);
            foreach (var side in new[] { "Left", "Right" })
            {
                body.recipes.Add(new ShapeRecipe { name = "FTB_EyeClosed" + side, mode = side == "Left" ? GenerateMode.SplitLeft : GenerateMode.SplitRight, planeRight = right, sources = new List<ShapeSource> { new ShapeSource { name = "blink" } } });
                var mapping = profile.mappings.First(m => m.key == "EyeClosed" + side); mapping.shape = "FTB_EyeClosed" + side; mapping.confirmed = true; mapping.excluded = false;
            }
            profile.routeVisemes = true; profile.routeEyelids = true;
            Test(report, "Rusk解析・元設定の取得", () => { Check(body.source.blendShapeCount == 219, "Expected fixture shapes"); Check(source.GetComponent<VRCAvatarDescriptor>().lipSync == VRC.SDKBase.VRC_AvatarDescriptor.LipSyncStyle.VisemeBlendShape, "original visemes"); });
            Test(report, "Rusk複製プレビュー・複数ウェイト・比較・終了復元", () =>
            {
                int sceneCount = UnityEngine.SceneManagement.SceneManager.sceneCount;
                var active = UnityEngine.SceneManagement.SceneManager.GetActiveScene();
                Mesh originalMesh = renderer.sharedMesh; float originalWeight = renderer.GetBlendShapeWeight(0);
                using (var session = new BuilderSession(profile))
                {
                    session.Weight(body.path, "FTB_EyeClosedLeft", 60); session.Weight(body.path, "FTB_EyeClosedRight", 35);
                    var r = session.Renderer(body); Check(r.sharedMesh != originalMesh, "isolated mesh");
                    Check(r.GetBlendShapeWeight(r.sharedMesh.GetBlendShapeIndex("FTB_EyeClosedLeft")) == 60, "preview weight");
                    session.Compare(true); Check(r.GetBlendShapeWeight(r.sharedMesh.GetBlendShapeIndex("FTB_EyeClosedLeft")) == 0, "compare before");
                    session.Compare(false); Check(r.GetBlendShapeWeight(r.sharedMesh.GetBlendShapeIndex("FTB_EyeClosedRight")) == 35, "compare after");
                    session.ResetWeights(); Check(r.GetBlendShapeWeight(r.sharedMesh.GetBlendShapeIndex("FTB_EyeClosedLeft")) == 0, "reset");
                    Check(UnityEngine.SceneManagement.SceneManager.GetActiveScene() == active, "active scene preserved");
                    Check(session.MissingShaderCount == 0, "installed avatar shaders are used");
                }
                Check(UnityEngine.SceneManagement.SceneManager.sceneCount == sceneCount, "preview scene closed");
                Check(renderer.sharedMesh == originalMesh && renderer.GetBlendShapeWeight(0) == originalWeight, "source state");
            });
            Test(report, "Rusk出力保存・再読み込み・二重生成・既存設定保持", () =>
            {
                string first = BuilderStorage.Export(profile); string second = BuilderStorage.Export(profile); Check(first != second, "immutable revisions"); report.output = second;
                AssetDatabase.ImportAsset(second, ImportAssetOptions.ForceUpdate);
                var output = AssetDatabase.LoadAssetAtPath<GameObject>(second); var d = output.GetComponent<VRCAvatarDescriptor>(); var original = source.GetComponent<VRCAvatarDescriptor>();
                var r = AvatarAnalysis.Resolve(output, body.path);
                Check(r.sharedMesh.blendShapeCount == body.source.blendShapeCount + 2, "idempotent shapes");
                Check(r.sharedMesh.vertexCount == body.source.vertexCount && r.sharedMesh.uv.SequenceEqual(body.source.uv) && r.sharedMesh.boneWeights.SequenceEqual(body.source.boneWeights), "vertex data");
                Check(d.enableFaceTracking && d.useAllTrackingKeys && d.faceTrackingMeshes.Count == 1, "native enabled");
                Check(d.faceTrackingBlendshapesSetList.Count == (original.faceTrackingBlendshapesSetList?.Count ?? 0) + 1, "set count");
                Check(d.faceTrackingBlendshapesSetList.Last().faceTrackingBlendshapeNames[9] == "FTB_EyeClosedLeft", "left mapping");
                Check(d.faceTrackingBlendshapesSetList.Last().visemeBlendshapeNames.SequenceEqual(original.VisemeBlendShapes), "visemes preserved");
                Check(d.expressionsMenu == original.expressionsMenu && d.expressionParameters == original.expressionParameters, "expressions");
                Check(d.baseAnimationLayers.Select(l => l.animatorController).SequenceEqual(original.baseAnimationLayers.Select(l => l.animatorController)), "animator layers");
                Check(d.customEyeLookSettings.leftEye != original.customEyeLookSettings.leftEye && d.customEyeLookSettings.leftEye.IsChildOf(output.transform), "internal bone references remapped");
                Check(EditorJsonUtility.ToJson(original) == before, "original descriptor unchanged");
                var snapshot = AssetDatabase.LoadAssetAtPath<BuilderProfile>(Path.GetDirectoryName(second).Replace('\\', '/') + "/GenerationSettings.asset"); Check(snapshot && snapshot.renderers[0].recipes.Count == 2, "generation settings reload");
                var sdkIssues = FaceTrackingMappingValidator.CollectIssues(d); Check(sdkIssues.UnresolvedBlendshapes.Count == 0 && sdkIssues.AmbiguousMeshPaths.Count == 0 && !sdkIssues.HasMappingsButNoMeshes, "SDK validator");
                Check(NativeSdk.Performance(output).Contains("Face Tracking Operations: 17"), "2 eye mappings + 15 visemes");
            });
            Test(report, "SceneViewブラシ・マスク・Undo/Redo（Editor API）", () =>
            {
                var localProfile = Object.Instantiate(profile);
                try
                {
                    using (var session = new BuilderSession(localProfile))
                    using (var edit = new SceneExpressionEditor())
                    {
                        edit.session = session; edit.plan = localProfile.renderers.First(p => p.selected); edit.recipe = edit.plan.recipes[0];
                        edit.radius = .008f; edit.strength = .2f;
                        var r = session.Renderer(edit.plan); var baked = new Mesh();
                        try
                        {
                            var flags = System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic;
                            typeof(SceneExpressionEditor).GetMethod("Prepare", flags).Invoke(edit, new object[] { r });
                            r.BakeMesh(baked); var world = baked.vertices.Select(r.transform.TransformPoint).ToArray();
                            int target = Enumerable.Range(0, world.Length).OrderByDescending(i => world[i].y).First();
                            Vector3 hit = world[target];
                            Undo.IncrementCurrentGroup(); Undo.RegisterCompleteObjectUndo(localProfile, "Brush verification");
                            typeof(SceneExpressionEditor).GetMethod("Stroke", flags).Invoke(edit, new object[] { world, hit, Vector3.forward, false });
                            Undo.FlushUndoRecordObjects();
                            Check(edit.recipe.sculpt.Any(v => v.sqrMagnitude > 0), "brush changed displacement");
                            Undo.PerformUndo(); Check(localProfile.renderers.First(p => p.selected).recipes[0].sculpt.Length == 0, "undo restored");
                            Undo.PerformRedo(); edit.plan = localProfile.renderers.First(p => p.selected); edit.recipe = edit.plan.recipes[0];
                            Check(edit.recipe.sculpt.Any(v => v.sqrMagnitude > 0), "redo restored brush");
                            edit.paintMask = true;
                            typeof(SceneExpressionEditor).GetMethod("Stroke", flags).Invoke(edit, new object[] { world, hit, Vector3.forward, false });
                            Check(edit.recipe.mask[target] < 1, "mask painted");
                            Check(renderer.sharedMesh == body.source && body.recipes[0].sculpt.Length == 0, "original recipe unchanged");
                        }
                        finally { Object.DestroyImmediate(baked); Undo.ClearUndo(localProfile); }
                    }
                }
                finally { Object.DestroyImmediate(localProfile); }
            });
            Test(report, "UI Toolkitの全画面を構築", () =>
            {
                var window = EditorWindow.GetWindow<FaceTrackingBuilderWindow>(); window.LoadProfile(profile);
                var field = typeof(FaceTrackingBuilderWindow).GetField("tab", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
                for (int i = 0; i < 2; i++) { field.SetValue(window, i); window.CreateGUI(); Check(window.rootVisualElement.childCount > 0, "tab " + i); }
                Check(window.rootVisualElement.Q("tabs").childCount == 2, "exactly two main tabs");
                field.SetValue(window, 0); window.CreateGUI();
            });
            Test(report, "基本セットアップは生成しない・確認から保存への導線", () =>
            {
                var p = AvatarAnalysis.Analyze(source); var window = EditorWindow.GetWindow<FaceTrackingBuilderWindow>();
                var flags = System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance;
                try
                {
                    window.LoadProfile(p);
                    Check(p.renderers.All(r => r.recipes.Count == 0), "setup must keep recipes empty");
                    var list = window.rootVisualElement.Q<ListView>("mappingList");
                    Check(list.itemsSource.Count == p.mappings.Count(m => !string.IsNullOrEmpty(m.shape)), "show candidates only");
                    Check(window.rootVisualElement.Query<Toggle>().ToList().All(t => !(t.text + t.label).Contains("この表情を使用")), "no repetitive confirmation toggle");
                    Check(window.rootVisualElement.Query<Foldout>().ToList().All(f => !f.text.Contains("候補の根拠")), "removed unexplained settings");
                    Check(window.rootVisualElement.Q("actions").Q<Button>().enabledSelf, "ready to review without checking each candidate");
                    ExpressionListNavigation.Move(list, 1); Check(list.selectedIndex == 1, "next expression");
                    Check((string)typeof(FaceTrackingBuilderWindow).GetField("selectedKey", flags).GetValue(window) == ((TrackingMapping)list.itemsSource[1]).key, "preview follows selection");
                    ExpressionListNavigation.Move(list, -1); ExpressionListNavigation.Move(list, -1); Check(list.selectedIndex == 0, "previous expression and top boundary");
                    Check(!window.rootVisualElement.Query<Button>().ToList().Any(b => b.text.Contains("生成設定を作成")), "no generation button in setup");
                    Check(window.rootVisualElement.Query<Foldout>().ToList().First(f => f.text == "対象メッシュ・解析の詳細").value == false, "diagnostics collapsed");
                    typeof(FaceTrackingBuilderWindow).GetField("setupStep", flags).SetValue(window, 1); window.CreateGUI();
                    Check(window.rootVisualElement.Query<Label>().ToList().Any(l => l.text == "既存のBlendShapeのみ使用"), "existing-shape review");
                    Check(p.renderers.All(r => r.recipes.Count == 0), "review must not generate");
                }
                finally { window.LoadProfile(profile); Object.DestroyImmediate(p); }
            });
            Test(report, "セットアップから選択中の表情を編集・再入場・戻る導線", () =>
            {
                var p = AvatarAnalysis.Analyze(source); var window = EditorWindow.GetWindow<FaceTrackingBuilderWindow>();
                var flags = System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance;
                try
                {
                    var mapping = p.mappings.First(m => m.key == "EyeClosedRight"); string previous = mapping.shape;
                    var plan = RecipeSources.MappedPlans(p, previous).First();
                    window.LoadProfile(p);
                    var list = window.rootVisualElement.Q<ListView>("mappingList");
                    list.selectedIndex = list.itemsSource.Cast<TrackingMapping>().ToList().IndexOf(mapping);
                    Check(window.rootVisualElement.Q<Button>("editMappedExpression").enabledSelf, "setup edit entry enabled for selection");
                    var edit = typeof(FaceTrackingBuilderWindow).GetMethod("EditMapping", flags); edit.Invoke(window, new object[] { mapping, plan });
                    Check((int)typeof(FaceTrackingBuilderWindow).GetField("tab", flags).GetValue(window) == 1, "editing tab opened");
                    Check(plan.recipes.Single().sources.Single().name == previous && plan.recipes.Single().mode == GenerateMode.Combine, "current selection used without alternate heuristic");
                    Check(window.rootVisualElement.Query<Button>().ToList().Any(b => b.text == "‹ セットアップへ戻る"), "return to originating setup");
                    Check(window.rootVisualElement.Q<Button>("editFromNeutral").text == "元の顔から作る", "clear baseline label");
                    edit.Invoke(window, new object[] { mapping, plan }); Check(plan.recipes.Count == 1, "no duplicated recipe");
                }
                finally { window.LoadProfile(profile); Undo.ClearUndo(p); Object.DestroyImmediate(p); }
            });
            Test(report, "検索付きBlendShape選択・仮選択とキャンセルの非破壊性", () =>
            {
                string snapshot = EditorJsonUtility.ToJson(profile); string accepted = null;
                using (var picker = new ShapePicker(profile, body, "blink", name => accepted = name, () => { }))
                {
                    EditorWindow.GetWindow<FaceTrackingBuilderWindow>().rootVisualElement.Add(picker);
                    try
                    {
                    var list = picker.Q<ListView>("shapeList"); var query = picker.Q<ToolbarSearchField>("shapeSearch");
                    Check(picker.Selected == "blink", "initial preview selection");
                    query.value = "LookRight";
                    Check(list.itemsSource.Cast<string>().All(n => n.IndexOf("LookRight", StringComparison.OrdinalIgnoreCase) >= 0), "filtered list");
                    if (list.itemsSource.Count > 0) list.selectedIndex = 0;
                    Check(accepted == null && EditorJsonUtility.ToJson(profile) == snapshot, "browsing is temporary");
                    query.value = "no-such-shape-83291"; Check(list.itemsSource.Count == 0 && !picker.Q<Button>("useShape").enabledSelf, "empty results cannot apply");
                    }
                    finally { picker.RemoveFromHierarchy(); }
                }
                Check(accepted == null && EditorJsonUtility.ToJson(profile) == snapshot, "cancel does not change profile");
            });
            Test(report, "任意の視線分割作成・再選択の冪等性・合成元UI", () =>
            {
                var p = AvatarAnalysis.Analyze(source); var window = EditorWindow.GetWindow<FaceTrackingBuilderWindow>();
                var flags = System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance;
                try
                {
                    window.LoadProfile(p);
                    var add = typeof(FaceTrackingBuilderWindow).GetMethod("AddRecipe", flags);
                    add.Invoke(window, new object[] { "EyeLookOutRight" }); add.Invoke(window, new object[] { "EyeLookOutRight" });
                    var plan = p.renderers.First(r => r.selected); var recipe = plan.recipes.Single();
                    Check(recipe.mode == GenerateMode.SplitRight && recipe.sources.Single().name == "LookRight", "optional gaze split recipe");
                    Check(!p.mappings.First(m => m.key == "EyeLookOutRight").confirmed, "result still requires review");
                    typeof(FaceTrackingBuilderWindow).GetField("tab", flags).SetValue(window, 1);
                    typeof(FaceTrackingBuilderWindow).GetField("editingRecipe", flags).SetValue(window, true); window.CreateGUI();
                    Check(window.rootVisualElement.Query<Button>().ToList().Any(b => b.text == "BlendShapeを選んで追加"), "search picker entry");
                    Check(window.rootVisualElement.Query<PopupField<string>>().ToList().All(f => f.choices.Count < 64), "no giant blendshape dropdown");
                    using (var session = new BuilderSession(p))
                    {
                        var mesh = session.Renderer(plan).sharedMesh;
                        Check(mesh.blendShapeCount == plan.source.blendShapeCount + 1, "only requested shape generated");
                        var result = MeshEngine.Sample(mesh, recipe.name, 100); var input = MeshEngine.Sample(plan.source, "LookRight", 100);
                        var vertices = mesh.vertices; int changed = 0;
                        for (int i = 0; i < vertices.Length; i++)
                        {
                            float side = Vector3.Dot(vertices[i] - recipe.planeOrigin, recipe.planeRight.normalized);
                            if (side < -recipe.feather) Near(result.vertices[i], Vector3.zero, "opposite side excluded");
                            if (side > recipe.feather) { Near(result.vertices[i], input.vertices[i], "requested side retained"); if (result.vertices[i].sqrMagnitude > 0) changed++; }
                        }
                        Check(changed > 0, "actual Rusk gaze displacement");
                    }
                    Check(AvatarAnalysis.Category("LookRight") == "目", "gaze included in eye filter");
                }
                finally { window.LoadProfile(profile); Object.DestroyImmediate(p); }
            });
            Test(report, "Rusk生成表情ピッカー・左右セットUI・保存後のSDK設定", () =>
            {
                var p = AvatarAnalysis.Analyze(source); var window = EditorWindow.GetWindow<FaceTrackingBuilderWindow>();
                var flags = System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance;
                try
                {
                    window.LoadProfile(p); typeof(FaceTrackingBuilderWindow).GetMethod("AddRecipe", flags).Invoke(window, new object[] { "CheekPuffRight" });
                    var plan = p.renderers.First(x => x.selected); var recipe = plan.recipes.Single(); recipe.mode = GenerateMode.Landmarks;
                    recipe.sculpt = new Vector3[plan.source.vertexCount];
                    var pairs = MeshEngine.MirrorPairs(plan.source.vertices, recipe.planeOrigin, recipe.planeRight, recipe.mirrorTolerance);
                    int vertex = Enumerable.Range(0, pairs.Length).First(i => pairs[i] >= 0 && pairs[i] != i && Vector3.Dot(plan.source.vertices[i] - recipe.planeOrigin, recipe.planeRight) > .001f);
                    for (int i = 0; i < recipe.sculpt.Length; i++)
                        recipe.sculpt[i] = recipe.planeRight * .001f * MeshEngine.Falloff(Vector3.Distance(plan.source.vertices[i], plan.source.vertices[vertex]), .005f);
                    typeof(FaceTrackingBuilderWindow).GetField("tab", flags).SetValue(window, 1);
                    typeof(FaceTrackingBuilderWindow).GetField("editingRecipe", flags).SetValue(window, true); window.CreateGUI();
                    var toggle = window.rootVisualElement.Q<Toggle>("createMirrorPair");
                    Check(toggle != null && toggle.enabledSelf && !toggle.value && plan.recipes.Count == 1, "pair creation is opt-in");
                    Undo.IncrementCurrentGroup(); toggle.value = true; Undo.FlushUndoRecordObjects();
                    Check(plan.recipes.Count == 2, "toggle adds counterpart");
                    Undo.PerformUndo(); Check(p.renderers.First(x => x.selected).recipes.Count == 1, "undo pair creation");
                    Undo.PerformRedo(); plan = p.renderers.First(x => x.selected); recipe = plan.recipes.First(x => x.trackingKey == "CheekPuffRight");
                    Check(plan.recipes.Count == 2, "redo pair creation");
                    var left = plan.recipes.First(x => x.trackingKey == "CheekPuffLeft");
                    var unfinished = new ShapeRecipe { name = "Unfinished", mode = GenerateMode.Mirror, sources = new List<ShapeSource> { new ShapeSource { name = "Missing" } } }; plan.recipes.Add(unfinished);
                    using (var picker = new ShapePicker(p, plan, recipe.name, _ => { }, () => { }, left))
                    {
                        Check(picker.Selected == recipe.name, "generated source is selected");
                        Check(!picker.Q<ListView>("shapeList").itemsSource.Cast<string>().Contains(left.name), "current recipe excluded");
                        var preview = (BuilderSession)typeof(ShapePicker).GetField("session", flags).GetValue(picker);
                        Check(preview != null && preview.Renderer(plan).sharedMesh.GetBlendShapeIndex(recipe.name) >= 0, "actual generated mesh previews despite unfinished target");
                        Near(MeshEngine.Sample(preview.Renderer(plan).sharedMesh, recipe.name, 100).vertices[vertex], recipe.sculpt[vertex], "sculpt source preview");
                    }
                    plan.recipes.Remove(unfinished);
                    var originalMesh = AvatarAnalysis.Resolve(source, plan.path).sharedMesh;
                    string path = BuilderStorage.Export(p); AssetDatabase.ImportAsset(path, ImportAssetOptions.ForceUpdate);
                    var output = AssetDatabase.LoadAssetAtPath<GameObject>(path); var built = AvatarAnalysis.Resolve(output, plan.path).sharedMesh;
                    Near(MeshEngine.Sample(built, left.name, 100).vertices[pairs[vertex]], MeshEngine.ReflectVector(recipe.sculpt[vertex], recipe.planeRight), "saved mirrored shape");
                    Check(built.blendShapeCount == originalMesh.blendShapeCount + 2 && AvatarAnalysis.Resolve(source, plan.path).sharedMesh == originalMesh, "source unchanged");
                    var set = output.GetComponent<VRCAvatarDescriptor>().faceTrackingBlendshapesSetList.Last();
                    Check(set.faceTrackingBlendshapeNames[(int)VRCAvatarDescriptor.FaceTrackingKey.CheekPuffRight] == recipe.name &&
                        set.faceTrackingBlendshapeNames[(int)VRCAvatarDescriptor.FaceTrackingKey.CheekPuffLeft] == left.name, "both native SDK mappings");
                }
                finally { window.LoadProfile(profile); Undo.ClearUndo(p); if (!AssetDatabase.Contains(p)) Object.DestroyImmediate(p); }
            });
            Test(report, "インライン顔プレビュー・別コピーの描画隔離・表情変化・解放", () =>
            {
                var active = RenderTexture.active; RenderTexture result = null; var texture = new Texture2D(384, 384, TextureFormat.RGB24, false);
                try
                {
                    using (var session = new BuilderSession(profile))
                    using (var other = new BuilderSession(profile))
                    {
                        session.ResetWeights(); result = session.RenderPortrait(); RenderTexture.active = result;
                        texture.ReadPixels(new Rect(0, 0, 384, 384), 0, 0); texture.Apply(); var beforePixels = texture.GetPixels32();
                        session.Weight(body.path, "blink", 100); session.RenderPortrait(); texture.ReadPixels(new Rect(0, 0, 384, 384), 0, 0); texture.Apply(); var afterPixels = texture.GetPixels32();
                        Check(beforePixels.Distinct().Count() > 100, "render contains avatar");
                        Check(!beforePixels.SequenceEqual(afterPixels), "blendshape visibly changes portrait");
                        other.Weight(body.path, "blink", 100); other.RenderPortrait(); session.RenderPortrait(); RenderTexture.active = result;
                        texture.ReadPixels(new Rect(0, 0, 384, 384), 0, 0); texture.Apply();
                        var isolated = texture.GetPixels32();
                        int changedPixels = afterPixels.Zip(isolated, (a, b) => a.Equals(b) ? 0 : 1).Sum();
                        int maxChange = afterPixels.Zip(isolated, (a, b) => Math.Max(Math.Abs(a.r - b.r), Math.Max(Math.Abs(a.g - b.g), Math.Abs(a.b - b.b)))).Max();
                        // Repeated renders of this shader alone vary by one 8-bit level in a few pixels.
                        Check(changedPixels <= 32 && maxChange <= 1, "other preview cannot affect this camera beyond measured render noise; pixels=" + changedPixels + ", maximum channel difference=" + maxChange);
                        RenderTexture.active = active;
                    }
                    Check(!result, "owned texture destroyed");
                }
                finally { RenderTexture.active = active; Object.DestroyImmediate(texture); }
            });
            Test(report, "Undo後のSceneView編集対象の再接続", () =>
            {
                var window = EditorWindow.GetWindow<FaceTrackingBuilderWindow>();
                var copy = Object.Instantiate(profile); var flags = System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance;
                try
                {
                    window.LoadProfile(copy);
                    typeof(FaceTrackingBuilderWindow).GetMethod("StartEditing", flags).Invoke(window, null);
                    Undo.IncrementCurrentGroup(); Undo.RegisterCompleteObjectUndo(copy, "Window undo verification");
                    copy.renderers.First(r => r.selected).recipes[0].landmarks.Add(new Landmark { name = "Verification" });
                    Undo.FlushUndoRecordObjects(); Undo.PerformUndo();
                    var editor = (SceneExpressionEditor)typeof(FaceTrackingBuilderWindow).GetField("sceneEditor", flags).GetValue(window);
                    Check(editor != null && ReferenceEquals(editor.recipe, copy.renderers.First(r => r.selected).recipes[0]), "restored recipe binding");
                    Check(editor.recipe.landmarks.Count == 0, "undo removes landmark");
                    Undo.PerformRedo();
                    Check(ReferenceEquals(editor.recipe, copy.renderers.First(r => r.selected).recipes[0]) && editor.recipe.landmarks.Count == 1, "redo retains live binding");
                }
                finally { window.LoadProfile(profile); Undo.ClearUndo(copy); Object.DestroyImmediate(copy); }
            });
        }
    }
}
