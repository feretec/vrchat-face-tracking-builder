using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using UnityEditor;
using UnityEditor.UIElements;
using UnityEngine;
using UnityEngine.UIElements;

namespace FaceTrackingBuilder
{
    public partial class FaceTrackingBuilderWindow : EditorWindow
    {
        [SerializeField] BuilderProfile profile;
        [SerializeField] GameObject avatar;
        [SerializeField] int tab, rendererIndex, recipeIndex, setupStep;
        [SerializeField] string category = "すべて", search = "";
        [SerializeField] bool editingRecipe, editingFromSetup;
        [SerializeField] string selectedKey, targetKey = "EyeClosedRight";
        ShapePicker picker;
        BuilderSession session;
        SceneExpressionEditor sceneEditor;
        VisualElement content;
        Label message;
        string lastMessage = "";
        string reviewedFingerprint;
        static readonly string[] Tabs = { "セットアップ", "表情の追加・編集" };
        [MenuItem("Tools/VRChat Face Tracking Builder")]
        public static void Open() { var w = GetWindow<FaceTrackingBuilderWindow>(); w.titleContent = new GUIContent("Face Tracking Builder"); w.minSize = new Vector2(700, 600); }
        public void LoadProfile(BuilderProfile value) { EndPreview(); profile = value; avatar = value ? value.sourceAvatar : null; rendererIndex = value ? Math.Max(0, value.renderers.FindIndex(r => r.selected)) : 0; recipeIndex = setupStep = tab = 0; selectedKey = null; editingRecipe = editingFromSetup = false; reviewedFingerprint = recipePreviewed = null; lastMessage = ""; CreateGUI(); }
        void OnEnable() { Undo.undoRedoPerformed += UndoRedo; }
        void OnDisable()
        {
            Undo.undoRedoPerformed -= UndoRedo; EndPreview();
            if (profile && EditorUtility.IsPersistent(profile)) { EditorUtility.SetDirty(profile); AssetDatabase.SaveAssetIfDirty(profile); }
        }
        void UndoRedo()
        {
            reviewedFingerprint = recipePreviewed = null;
            Guard(() =>
            {
                if (sceneEditor != null && profile && profile.renderers.Count > 0)
                {
                    // Unity restores serialized nested objects; handles must bind to the restored recipe.
                    sceneEditor.plan = Plan(); sceneEditor.recipe = Recipe(); sceneEditor.Invalidate();
                }
                session?.Rebuild(); RefreshContent(); SceneView.RepaintAll();
            });
        }
        void Notify(string text) { lastMessage = text; if (message != null) message.text = text; }
        void Guard(Action action)
        {
            try { action(); }
            catch (Exception ex) { EndPreview(); Notify("処理を中断しました: " + ex.Message); }
        }
        void Change(string title, Action action) { Undo.RecordObject(profile, title); action(); EditorUtility.SetDirty(profile); reviewedFingerprint = recipePreviewed = null; QueueRecipePreview(); }
        Button Button(VisualElement parent, string text, Action action)
        {
            var b = new Button(() => Guard(action)) { text = text }; b.AddToClassList("action-button");
            if (parent.ClassListContains("row") || parent.ClassListContains("choice-row"))
            {
                foreach (var sibling in parent.Children()) sibling.RemoveFromClassList("row-end");
                b.AddToClassList("row-end");
            }
            parent.Add(b); return b;
        }
        static VisualElement Row(VisualElement parent) { var row = new VisualElement(); row.AddToClassList("row"); parent.Add(row); return row; }
        static VisualElement Card(VisualElement parent) { var card = new VisualElement(); card.AddToClassList("card"); parent.Add(card); return card; }
        static void Text(VisualElement parent, string text, string css = "wrap") { var label = new Label(text); label.AddToClassList(css); parent.Add(label); }
        void Bool(VisualElement parent, string title, bool value, Action<bool> write) { var field = new Toggle(title) { value = value }; field.RegisterValueChangedCallback(e => Change(title, () => write(e.newValue))); parent.Add(field); }
        void Float(VisualElement parent, string title, float value, Action<float> write) { var f = new FloatField(title) { value = value, isDelayed = true }; f.RegisterValueChangedCallback(e => Guard(() => Change(title, () => write(e.newValue)))); parent.Add(f); }
        void Vec(VisualElement parent, string title, Vector3 value, Action<Vector3> write) { var f = new Vector3Field(title) { value = value }; f.RegisterValueChangedCallback(e => Change(title, () => write(e.newValue))); parent.Add(f); }
        void String(VisualElement parent, string title, string value, Action<string> write) { var f = new TextField(title) { value = value ?? "", isDelayed = true }; f.RegisterValueChangedCallback(e => Guard(() => Change(title, () => write(e.newValue)))); parent.Add(f); }
        void Analysis()
        {
            Text(content, "解析結果", "section"); Text(content, profile.analysisSummary);
            Text(content, "Face Trackingの対象メッシュ", "section");
            foreach (var plan in profile.renderers)
            {
                var card = Card(content); Bool(card, plan.path + " を使用", plan.selected, value => plan.selected = value);
                Text(card, plan.evidence + " / 元Mesh: " + plan.source.name);
                var shapes = new Foldout { text = "BlendShape・フレーム一覧（" + plan.source.blendShapeCount + "）", value = false }; card.Add(shapes);
                for (int i = 0; i < plan.source.blendShapeCount; i++)
                {
                    string name = plan.source.GetBlendShapeName(i);
                    var frames = Enumerable.Range(0, plan.source.GetBlendShapeFrameCount(i)).Select(f => plan.source.GetBlendShapeFrameWeight(i, f).ToString(CultureInfo.InvariantCulture));
                    Text(shapes, "[" + AvatarAnalysis.Category(name) + "] " + name + " / ウェイト: " + string.Join(", ", frames));
                }
            }
            Text(content, "SDK Auto-Fill: " + (AvatarAnalysis.SdkAutoFillAvailable ? "利用可能（導入済みSDKの処理を使用）" : "利用不可。このSDKでは辞書と名前一致を使用"));
            Button(content, "選択メッシュから候補を更新", () => { Change("候補を更新", () => AvatarAnalysis.Suggest(profile)); tab = 0; CreateGUI(); });
        }
        RendererPlan Plan()
        {
            if (profile.renderers.Count == 0) throw new InvalidOperationException("SkinnedMeshRendererがありません。");
            rendererIndex = Mathf.Clamp(rendererIndex, 0, profile.renderers.Count - 1); return profile.renderers[rendererIndex];
        }
        ShapeRecipe Recipe() { var p = Plan(); if (p.recipes.Count == 0) return null; recipeIndex = Mathf.Clamp(recipeIndex, 0, p.recipes.Count - 1); return p.recipes[recipeIndex]; }
        void SelectRenderer()
        {
            if (profile.renderers.Count == 0) return;
            var names = profile.renderers.Select((r, i) => i + ": " + r.path).ToList(); rendererIndex = Mathf.Clamp(rendererIndex, 0, names.Count - 1);
            var select = new PopupField<string>("編集対象メッシュ", names, rendererIndex);
            select.RegisterValueChangedCallback(e => { rendererIndex = names.IndexOf(e.newValue); recipeIndex = 0; editingRecipe = false; if (sceneEditor != null) sceneEditor.enabled = false; RefreshContent(); }); content.Add(select);
        }
        void EnsurePreview()
        {
            if (session?.Clone) return;
            EndPreview(); session = new BuilderSession(profile);
            sceneEditor = new SceneExpressionEditor { session = session, changed = () => { reviewedFingerprint = recipePreviewed = null; renderRecipePortrait?.Invoke(); },
                displayChanged = () => renderRecipePortrait?.Invoke(), failed = ex => { EndPreview(); Notify("編集を中断しました: " + ex.Message); } };
        }
        void EndPreview() { recipeRefresh?.Pause(); picker?.Dispose(); picker = null; sceneEditor?.Dispose(); sceneEditor = null; session?.Dispose(); session = null; }
        void FocusFirst() { if (sceneEditor != null && profile.renderers.Count > 0) { sceneEditor.plan = Plan(); sceneEditor.Frame(); } }
        void StartEditing()
        {
            EnsurePreview(); var r = Recipe(); if (r == null) throw new InvalidOperationException("先に生成設定を追加してください。");
            if (!string.IsNullOrEmpty(r.mirrorOf)) throw new InvalidOperationException("元の表情を編集するか、「個別に編集」を選んでください。");
            session.Rebuild(); session.ResetWeights(); session.Weight(Plan().path, r.name, 100);
            bool newTarget = sceneEditor.recipe != r || !session.View;
            sceneEditor.plan = Plan(); sceneEditor.recipe = r; sceneEditor.enabled = sceneEditor.brush = true; sceneEditor.Invalidate();
            if (newTarget) FocusFirst(); else session.ShowView().Focus();
            sceneEditor.ShowControls(); renderRecipePortrait?.Invoke();
        }
        string CurrentFingerprint() => EditorJsonUtility.ToJson(profile) + (profile.sourceAvatar ? AssetDatabase.GetAssetDependencyHash(AssetDatabase.GetAssetPath(profile.sourceAvatar)).ToString() : "");
    }
}
