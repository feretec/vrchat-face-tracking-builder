using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using UnityEditor;
using UnityEditor.UIElements;
using UnityEngine;
using UnityEngine.UIElements;

namespace FaceTrackingBuilder
{
    public sealed class SceneBrushPanel : IDisposable
    {
        readonly SceneExpressionEditor editor;
        readonly VisualElement editing;
        readonly Label title, state, help;
        readonly PopupField<string> operation;
        readonly Button neutral, expression;
        readonly IVisualElementScheduledItem refresh;
        public SceneView View { get; }
        public VisualElement Root { get; }
        public SceneBrushPanel(SceneExpressionEditor editor, SceneView view)
        {
            this.editor = editor; View = view;
            Root = new VisualElement { name = "ftbBrushPanel" }; Root.AddToClassList("brush-panel");
            Root.EnableInClassList("light-theme", !EditorGUIUtility.isProSkin);
            string script = AssetDatabase.FindAssets("FaceTrackingBuilderWindow t:MonoScript").Select(AssetDatabase.GUIDToAssetPath)
                .First(p => p.EndsWith("/FaceTrackingBuilderWindow.cs", StringComparison.Ordinal));
            Root.styleSheets.Add(AssetDatabase.LoadAssetAtPath<StyleSheet>(Path.GetDirectoryName(script).Replace('\\', '/') + "/FaceTrackingBuilderWindow.uss"));
            title = new Label(); title.AddToClassList("mapping-title"); Root.Add(title);
            var choices = new VisualElement(); choices.AddToClassList("choice-row"); Root.Add(choices);
            neutral = new Button(() => Run(() => editor.Compare(true))) { text = "元の顔", name = "showNeutral" };
            expression = new Button(() => Run(() => editor.Compare(false))) { text = "作成中の表情", name = "showExpression" };
            choices.Add(neutral); choices.Add(expression);
            state = new Label { name = "brushState" }; state.AddToClassList("brush-state"); Root.Add(state);
            var scroll = new ScrollView(); scroll.AddToClassList("brush-scroll"); Root.Add(scroll);
            var display = new PopupField<string>("表示", new List<string> { "通常", "ワイヤー重ね表示", "ワイヤーのみ" }, 0) { name = "brushDisplayMode" };
            display.RegisterValueChangedCallback(e => SetDisplayMode(display.index)); scroll.Add(display);
            editing = new VisualElement { name = "brushEditingControls" }; scroll.Add(editing);
            operation = new PopupField<string>("操作", new List<string> { "ブラシで変形", "影響範囲を編集", "視点操作のみ" }, editor.brush ? (editor.paintMask ? 1 : 0) : 2) { name = "brushOperation" };
            operation.RegisterValueChangedCallback(e => { editor.brush = operation.index != 2; editor.paintMask = operation.index == 1; Refresh(); }); editing.Add(operation);
            var radius = new Slider("大きさ", .001f, .1f) { name = "brushRadius", value = editor.radius, showInputField = true };
            radius.tooltip = "影響半径（ワールド単位）";
            radius.RegisterValueChangedCallback(e => editor.radius = Mathf.Clamp(e.newValue, .001f, .1f)); editing.Add(radius);
            var strength = new Slider("強さ", 0, 100) { name = "brushStrength", value = editor.strength * 100, showInputField = true };
            strength.RegisterValueChangedCallback(e => editor.strength = Mathf.Clamp01(e.newValue / 100)); editing.Add(strength);
            var symmetric = new Toggle("左右対称に編集") { name = "brushSymmetric", value = editor.symmetric };
            symmetric.tooltip = "この表情の両側を動かします。反対側の表情を別に作る設定とは異なります。";
            symmetric.RegisterValueChangedCallback(e => editor.symmetric = e.newValue); editing.Add(symmetric);
            var history = new VisualElement(); history.AddToClassList("choice-row"); editing.Add(history);
            history.Add(new Button(() => Run(Undo.PerformUndo)) { text = "元に戻す" });
            history.Add(new Button(() => Run(Undo.PerformRedo)) { text = "やり直す" });
            help = new Label(); help.AddToClassList("muted"); scroll.Add(help);
            scroll.Add(new Button(() => Run(editor.Frame)) { text = "顔を中央に表示" });
            Root.RegisterCallback<PointerEnterEvent>(e => editor.pointerOverControls = true);
            Root.RegisterCallback<PointerLeaveEvent>(e => editor.pointerOverControls = false);
            Root.RegisterCallback<DetachFromPanelEvent>(e => editor.pointerOverControls = false);
            View.rootVisualElement.Add(Root);
            refresh = Root.schedule.Execute(Refresh).Every(100); Refresh();
        }
        void Run(Action action)
        {
            try { action(); Refresh(); }
            catch (Exception ex) { editor.enabled = false; editor.failed?.Invoke(ex); }
        }
        public void SetDisplayMode(int index)
        {
            if (!View) return;
            var mode = index == 1 ? DrawCameraMode.TexturedWire : index == 2 ? DrawCameraMode.Wireframe : DrawCameraMode.Textured;
            View.cameraMode = SceneView.GetBuiltinCameraMode(mode); View.Repaint();
        }
        public void Refresh()
        {
            bool active = editor.enabled && editor.session?.Clone != null && editor.recipe != null && string.IsNullOrEmpty(editor.recipe.mirrorOf);
            bool before = editor.session != null && editor.session.Comparing;
            string key = editor.recipe?.trackingKey ?? editor.session?.Profile.mappings.FirstOrDefault(m => m.shape == editor.recipe?.name)?.key;
            title.text = editor.recipe == null ? "表情の編集" : string.IsNullOrEmpty(key) ? editor.recipe.name : ExpressionGuide.Title(key);
            operation.SetValueWithoutNotify(operation.choices[editor.brush ? (editor.paintMask ? 1 : 0) : 2]);
            help.text = editor.paintMask ? "ドラッグで影響を減らす・Shiftで戻す\nAlt＋ドラッグで視点操作" : "ドラッグで編集・Shiftで反転\nAlt＋ドラッグで視点操作";
            state.text = !active ? "作成画面で「ブラシで編集」を選択" : before ? "元の顔を表示中（編集できません）" : editor.brush ? "作成中の表情を編集中" : "作成中の表情を表示中";
            neutral.EnableInClassList("selected-choice", before); expression.EnableInClassList("selected-choice", !before);
            neutral.SetEnabled(active); expression.SetEnabled(active); editing.SetEnabled(active && !before);
        }
        public void Dispose() { refresh.Pause(); Root.RemoveFromHierarchy(); editor.pointerOverControls = false; }
    }
}
