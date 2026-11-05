using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;
using Object = UnityEngine.Object;

namespace FaceTrackingBuilder
{
    public sealed class BuilderSession : IDisposable
    {
        public GameObject Clone { get; private set; }
        public BuilderProfile Profile { get; }
        readonly Dictionary<string, Mesh> ownedMeshes = new Dictionary<string, Mesh>();
        readonly Dictionary<string, float[]> originalWeights = new Dictionary<string, float[]>();
        readonly Dictionary<string, float[]> compareWeights = new Dictionary<string, float[]>();
        readonly List<Material> previewMaterials = new List<Material>();
        readonly List<GameObject> previewLights = new List<GameObject>();
        readonly bool includeGenerated;
        readonly string previewShape, previewPath;
        Camera portraitCamera;
        RenderTexture portrait;
        public int MissingShaderCount { get; private set; }
        Scene previewScene;
        public SceneView View { get; private set; }
        public bool Comparing { get; private set; }
        public string Diagnostics { get; private set; }
        public BuilderSession(BuilderProfile profile, bool includeGenerated = true, string previewShape = null, string previewPath = null)
        {
            Profile = profile;
            this.includeGenerated = includeGenerated;
            this.previewShape = previewShape; this.previewPath = previewPath;
            if (!profile.sourceAvatar) throw new InvalidOperationException("保存された元アバター参照がありません。ルートを再指定してください。");
            try
            {
                // PreviewScene works even when the user's current scene is untitled and unsaved.
                previewScene = EditorSceneManager.NewPreviewScene();
                Clone = PrefabUtility.IsPartOfPrefabAsset(profile.sourceAvatar)
                    ? (GameObject)PrefabUtility.InstantiatePrefab(profile.sourceAvatar, previewScene)
                    : Object.Instantiate(profile.sourceAvatar);
                Clone.name = profile.sourceName + " [FTB Preview]";
                SceneManager.MoveGameObjectToScene(Clone, previewScene);
                foreach (var tr in Clone.GetComponentsInChildren<Transform>(true)) tr.gameObject.hideFlags = HideFlags.DontSaveInEditor | HideFlags.DontSaveInBuild;
                foreach (var behaviour in Clone.GetComponentsInChildren<Behaviour>(true)) behaviour.enabled = false;
                foreach (var renderer in Clone.GetComponentsInChildren<SkinnedMeshRenderer>(true))
                {
                    if (!renderer.sharedMesh) continue;
                    renderer.updateWhenOffscreen = true;
                    renderer.forceMatrixRecalculationPerRender = true;
                    string path = AnimationUtility.CalculateTransformPath(renderer.transform, Clone.transform);
                    originalWeights[path] = Enumerable.Range(0, renderer.sharedMesh.blendShapeCount).Select(renderer.GetBlendShapeWeight).ToArray();
                }
                foreach (var renderer in Clone.GetComponentsInChildren<Renderer>(true))
                {
                    var materials = renderer.sharedMaterials;
                    for (int i = 0; i < materials.Length; i++)
                    {
                        var source = materials[i];
                        if (source && source.shader && source.shader.name != "Hidden/InternalErrorShader") continue;
                        MissingShaderCount++;
                        var fallback = new Material(Shader.Find("Unlit/Transparent Cutout")) { name = "FTB Preview Fallback", hideFlags = HideFlags.HideAndDontSave };
                        if (source)
                        {
                            var so = new SerializedObject(source); var textures = so.FindProperty("m_SavedProperties.m_TexEnvs");
                            for (int j = 0; textures != null && j < textures.arraySize; j++)
                            {
                                var pair = textures.GetArrayElementAtIndex(j);
                                if (pair.FindPropertyRelative("first").stringValue != "_MainTex") continue;
                                fallback.mainTexture = pair.FindPropertyRelative("second.m_Texture").objectReferenceValue as Texture;
                                fallback.mainTextureScale = pair.FindPropertyRelative("second.m_Scale").vector2Value;
                                fallback.mainTextureOffset = pair.FindPropertyRelative("second.m_Offset").vector2Value;
                            }
                        }
                        materials[i] = fallback; previewMaterials.Add(fallback);
                    }
                    renderer.sharedMaterials = materials;
                }
                for (int i = 0; i < 2; i++)
                {
                    var go = new GameObject("FTB Preview Light", typeof(Light)) { hideFlags = HideFlags.HideAndDontSave };
                    SceneManager.MoveGameObjectToScene(go, previewScene);
                    go.transform.rotation = Quaternion.LookRotation(-Clone.transform.forward + Clone.transform.up * -.4f + Clone.transform.right * (i == 0 ? .4f : -.5f));
                    var light = go.GetComponent<Light>(); light.type = LightType.Directional; light.intensity = i == 0 ? 1 : .35f;
                    previewLights.Add(go);
                }
                AssemblyReloadEvents.beforeAssemblyReload += Dispose;
                EditorApplication.playModeStateChanged += PlayMode;
                EditorApplication.quitting += Dispose;
                Rebuild();
            }
            catch { Dispose(); throw; }
        }
        void PlayMode(PlayModeStateChange state) { if (state == PlayModeStateChange.ExitingEditMode) Dispose(); }
        public SceneView ShowView()
        {
            if (View) return View;
            // Verified against the installed Unity 2022.3 Editor. No existing SceneView is changed.
            var customScene = typeof(SceneView).GetProperty("customScene", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Public);
            if (customScene == null || !customScene.CanWrite) throw new InvalidOperationException("このUnityでは隔離SceneView APIを利用できません。Unity 2022.3を使用してください。");
            View = ScriptableObject.CreateInstance<SceneView>(); View.titleContent = new GUIContent("表情の編集"); View.minSize = new Vector2(760, 520);
            customScene.SetValue(View, previewScene);
            var culling = typeof(SceneView).GetProperty("overrideSceneCullingMask", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Public);
            culling?.SetValue(View, EditorSceneManager.GetSceneCullingMask(previewScene));
            View.sceneLighting = true; View.cameraMode = SceneView.GetBuiltinCameraMode(DrawCameraMode.Textured); View.Show(); View.camera.scene = previewScene; return View;
        }
        public SkinnedMeshRenderer Renderer(RendererPlan plan) => AvatarAnalysis.Resolve(Clone, plan.path);
        public void Rebuild()
        {
            if (!Clone) return;
            var next = new Dictionary<string, Mesh>(); var logs = new List<string>();
            try
            {
                if (includeGenerated)
                    foreach (var plan in Profile.renderers.Where(r => r.recipes.Count > 0 && (previewPath == null || r.path == previewPath) &&
                        (previewShape == null || r.recipes.Any(recipe => recipe.name == previewShape))))
                    { next[plan.path] = MeshEngine.Build(plan, out var log, previewShape); logs.Add(log); }
                foreach (var plan in Profile.renderers)
                {
                    var r = AvatarAnalysis.Resolve(Clone, plan.path);
                    var weights = new Dictionary<string, float>();
                    for (int i = 0; i < r.sharedMesh.blendShapeCount; i++) weights[r.sharedMesh.GetBlendShapeName(i)] = r.GetBlendShapeWeight(i);
                    r.sharedMesh = next.TryGetValue(plan.path, out var built) ? built : plan.source;
                    for (int i = 0; i < r.sharedMesh.blendShapeCount; i++) r.SetBlendShapeWeight(i, weights.TryGetValue(r.sharedMesh.GetBlendShapeName(i), out var value) ? value : 0);
                }
                foreach (var mesh in ownedMeshes.Values) Object.DestroyImmediate(mesh);
                ownedMeshes.Clear(); foreach (var item in next) ownedMeshes.Add(item.Key, item.Value);
                Diagnostics = string.Join("\n", logs); SceneView.RepaintAll();
            }
            catch { foreach (var mesh in next.Values) if (!ownedMeshes.ContainsValue(mesh)) Object.DestroyImmediate(mesh); throw; }
        }
        public void Weight(string path, string shape, float value)
        {
            if (!Clone) return;
            var r = AvatarAnalysis.Resolve(Clone, path); int i = r.sharedMesh.GetBlendShapeIndex(shape);
            if (i >= 0) r.SetBlendShapeWeight(i, Mathf.Clamp(value, 0, 100));
            SceneView.RepaintAll();
        }
        public void ResetWeights()
        {
            if (!Clone) return;
            Comparing = false; compareWeights.Clear();
            foreach (var r in Clone.GetComponentsInChildren<SkinnedMeshRenderer>(true))
            {
                if (!r.sharedMesh) continue;
                string path = AnimationUtility.CalculateTransformPath(r.transform, Clone.transform);
                originalWeights.TryGetValue(path, out var values);
                for (int i = 0; i < r.sharedMesh.blendShapeCount; i++) r.SetBlendShapeWeight(i, values != null && i < values.Length ? values[i] : 0);
            }
            SceneView.RepaintAll();
        }
        public void Compare(bool before)
        {
            if (!Clone || before == Comparing) return;
            foreach (var plan in Profile.renderers.Where(r => r.selected))
            {
                var r = Renderer(plan);
                if (before) { compareWeights[plan.path] = Enumerable.Range(0, r.sharedMesh.blendShapeCount).Select(r.GetBlendShapeWeight).ToArray(); for (int i = 0; i < r.sharedMesh.blendShapeCount; i++) r.SetBlendShapeWeight(i, 0); }
                else if (compareWeights.TryGetValue(plan.path, out var values)) for (int i = 0; i < Math.Min(values.Length, r.sharedMesh.blendShapeCount); i++) r.SetBlendShapeWeight(i, values[i]);
            }
            Comparing = before; SceneView.RepaintAll();
        }
        public RenderTexture RenderPortrait(float yaw = 0, float zoom = 1)
        {
            if (!Clone) throw new InvalidOperationException("プレビューを開き直してください。");
            if (!portraitCamera)
            {
                var go = new GameObject("FTB Portrait Camera", typeof(Camera)) { hideFlags = HideFlags.HideAndDontSave };
                SceneManager.MoveGameObjectToScene(go, previewScene);
                portraitCamera = go.GetComponent<Camera>(); portraitCamera.enabled = false; portraitCamera.scene = previewScene;
                var sceneMask = typeof(Camera).GetProperty("overrideSceneCullingMask", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic);
                if (sceneMask == null || !sceneMask.CanWrite) throw new InvalidOperationException("このUnityではプレビューシーンを隔離できません。Unity 2022.3を使用してください。");
                sceneMask.SetValue(portraitCamera, EditorSceneManager.GetSceneCullingMask(previewScene));
                portraitCamera.clearFlags = CameraClearFlags.SolidColor; portraitCamera.backgroundColor = new Color(.16f, .18f, .21f);
                portraitCamera.fieldOfView = 32; portraitCamera.nearClipPlane = .001f; portraitCamera.farClipPlane = 100;
                portrait = new RenderTexture(384, 384, 24) { name = "FTB Portrait", hideFlags = HideFlags.HideAndDontSave };
                portrait.Create(); portraitCamera.targetTexture = portrait;
            }
            var descriptor = Clone.GetComponent<VRC.SDK3.Avatars.Components.VRCAvatarDescriptor>();
            float scale = Mathf.Max(.01f, Clone.transform.lossyScale.magnitude / Mathf.Sqrt(3));
            Vector3 center = descriptor ? Clone.transform.TransformPoint(descriptor.ViewPosition) : Clone.transform.position + Clone.transform.up * 1.4f * scale;
            var direction = Quaternion.AngleAxis(yaw, Clone.transform.up) * Clone.transform.forward;
            portraitCamera.transform.position = center + direction * (.48f * scale / Mathf.Clamp(zoom, .4f, 2));
            portraitCamera.transform.rotation = Quaternion.LookRotation(-direction, Clone.transform.up);
            portraitCamera.Render(); return portrait;
        }
        public void Dispose()
        {
            AssemblyReloadEvents.beforeAssemblyReload -= Dispose;
            EditorApplication.playModeStateChanged -= PlayMode;
            EditorApplication.quitting -= Dispose;
            if (View) View.Close(); View = null;
            if (portraitCamera) { portraitCamera.targetTexture = null; Object.DestroyImmediate(portraitCamera.gameObject); } portraitCamera = null;
            if (portrait) { if (RenderTexture.active == portrait) RenderTexture.active = null; portrait.Release(); Object.DestroyImmediate(portrait); } portrait = null;
            if (Clone) Object.DestroyImmediate(Clone); Clone = null;
            foreach (var light in previewLights) if (light) Object.DestroyImmediate(light);
            previewLights.Clear();
            foreach (var mesh in ownedMeshes.Values) if (mesh) Object.DestroyImmediate(mesh);
            foreach (var material in previewMaterials) if (material) Object.DestroyImmediate(material);
            previewMaterials.Clear();
            ownedMeshes.Clear();
            if (previewScene.IsValid()) EditorSceneManager.ClosePreviewScene(previewScene);
            previewScene = default;
            SceneView.RepaintAll();
        }
    }
    public static class BuilderStorage
    {
        public const string OutputRoot = "Assets/FaceTrackingBuilderGenerated";
        public static void EnsureFolder(string path)
        {
            if (AssetDatabase.IsValidFolder(path)) return;
            string parent = Path.GetDirectoryName(path).Replace('\\', '/'); EnsureFolder(parent);
            AssetDatabase.CreateFolder(parent, Path.GetFileName(path));
        }
        static string Safe(string name) => string.Concat(name.Select(c => char.IsLetterOrDigit(c) || c == '_' || c == '-' ? c : '_'));
        public static string SaveProfile(BuilderProfile profile)
        {
            EnsureFolder(OutputRoot);
            if (profile.sourceAvatar && !EditorUtility.IsPersistent(profile.sourceAvatar))
            {
                // Persist scene overrides as a new snapshot; asset profiles cannot retain scene references.
                string snapshotPath = AssetDatabase.GenerateUniqueAssetPath(OutputRoot + "/" + Safe(profile.sourceName) + "_SourceSnapshot.prefab");
                var snapshot = PrefabUtility.SaveAsPrefabAsset(profile.sourceAvatar, snapshotPath, out bool ok);
                if (!ok) throw new InvalidOperationException("シーン上の元アバターを複製保存できませんでした。");
                profile.sourceAvatar = snapshot;
            }
            if (!EditorUtility.IsPersistent(profile)) AssetDatabase.CreateAsset(profile, AssetDatabase.GenerateUniqueAssetPath(OutputRoot + "/" + Safe(profile.sourceName) + "_Settings.asset"));
            EditorUtility.SetDirty(profile); AssetDatabase.SaveAssetIfDirty(profile); return AssetDatabase.GetAssetPath(profile);
        }
        public static string Export(BuilderProfile profile)
        {
            if (!profile.sourceAvatar) throw new InvalidOperationException("元アバターを指定してください。");
            var issues = NativeSdk.Validate(profile, profile.sourceAvatar);
            if (issues.Count > 0) throw new InvalidOperationException(string.Join("\n", issues));
            if (profile.mappings.All(m => !m.Included)) throw new InvalidOperationException("使用する表情を1つ以上割り当ててください。");
            // Build every mesh before creating any asset. Every export is an immutable revision.
            var meshes = new Dictionary<string, Mesh>(); GameObject clone = null; Scene staging = default;
            try
            {
                foreach (var plan in profile.renderers.Where(r => r.recipes.Count > 0)) meshes[plan.path] = MeshEngine.Build(plan, out _);
                EnsureFolder(OutputRoot);
                string folder = AssetDatabase.GenerateUniqueAssetPath(OutputRoot + "/" + Safe(profile.sourceName) + "_" + DateTime.Now.ToString("yyyyMMdd_HHmmss"));
                EnsureFolder(folder);
                staging = EditorSceneManager.NewPreviewScene();
                clone = PrefabUtility.IsPartOfPrefabAsset(profile.sourceAvatar) ? (GameObject)PrefabUtility.InstantiatePrefab(profile.sourceAvatar, staging) : Object.Instantiate(profile.sourceAvatar);
                if (clone.scene != staging) SceneManager.MoveGameObjectToScene(clone, staging);
                if (PrefabUtility.IsPartOfPrefabInstance(clone)) PrefabUtility.UnpackPrefabInstance(clone, PrefabUnpackMode.Completely, InteractionMode.AutomatedAction);
                clone.name = profile.sourceName + "_FaceTracking";
                foreach (var item in meshes)
                {
                    string path = AssetDatabase.GenerateUniqueAssetPath(folder + "/" + Safe(item.Key) + "_Mesh.asset");
                    AssetDatabase.CreateAsset(item.Value, path); AvatarAnalysis.Resolve(clone, item.Key).sharedMesh = item.Value;
                }
                NativeSdk.Apply(profile, clone);
                string prefabPath = folder + "/" + Safe(clone.name) + ".prefab";
                var prefab = PrefabUtility.SaveAsPrefabAsset(clone, prefabPath, out bool success);
                if (!success || !prefab) throw new InvalidOperationException("Prefabの保存に失敗しました。元データは変更していません。保存先: " + folder);
                var snapshot = Object.Instantiate(profile); snapshot.lastExport = prefabPath;
                AssetDatabase.CreateAsset(snapshot, folder + "/GenerationSettings.asset");
                File.WriteAllText(folder + "/Changes.txt", string.Join("\n", NativeSdk.Diff(profile, profile.sourceAvatar)) + "\n" + NativeSdk.Performance(clone));
                AssetDatabase.ImportAsset(folder + "/Changes.txt");
                profile.lastExport = prefabPath; SaveProfile(profile);
                return prefabPath;
            }
            finally
            {
                if (clone) Object.DestroyImmediate(clone);
                if (staging.IsValid()) EditorSceneManager.ClosePreviewScene(staging);
                foreach (var mesh in meshes.Values) if (mesh && !EditorUtility.IsPersistent(mesh)) Object.DestroyImmediate(mesh);
            }
        }
    }
}
