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
        readonly VisualElement editing, brushControls, selectionControls, proportionalControls, transformControls;
        readonly Label title, state, help;
        readonly PopupField<string> operation;
        readonly Button neutral, expression;
        readonly Toggle hideOtherObjects;
        readonly Button brushMode, vertexMode, brushSelect, linkedSelect, loopSelect;
        readonly PopupField<string> transformTool, coordinateSpace;
        readonly Label selectedCount, selectionMessage, selectionModeLabel, loopNotice;
        readonly Slider selectionRadius;
        readonly Vector3Field coordinates;
        int coordinateRevision = -1;
        bool suppressRightRelease;
        readonly IVisualElementScheduledItem refresh;
        public SceneView View { get; }
        public VisualElement Root { get; }
        public bool Navigating { get; private set; }
        public bool HasTextFocus
        {
            get
            {
                var element = Root.panel?.focusController.focusedElement as VisualElement;
                if (element == null) return false;
                for (var current = element; current != null && current != View.rootVisualElement; current = current.parent)
                    if (current is TextField || current is FloatField || current is IntegerField || current is Vector3Field || current is Slider || current is SliderInt) return true;
                return false;
            }
        }
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
            int displayIndex = view.cameraMode.drawMode == DrawCameraMode.TexturedWire ? 1 : view.cameraMode.drawMode == DrawCameraMode.Wireframe ? 2 : 0;
            var display = new PopupField<string>("表示", new List<string> { "通常", "ワイヤー重ね表示", "ワイヤーのみ" }, displayIndex) { name = "brushDisplayMode" };
            display.RegisterValueChangedCallback(e => SetDisplayMode(display.index)); scroll.Add(display);
            hideOtherObjects = new Toggle("他のオブジェクトを非表示") { name = "hideOtherObjects", value = editor.HideOtherObjects,
                tooltip = "編集中のメッシュだけを表示します。元のアバターや保存結果には反映されません。" };
            hideOtherObjects.RegisterValueChangedCallback(e => Run(() => editor.SetHideOtherObjects(e.newValue))); scroll.Add(hideOtherObjects);
            Root.RegisterCallback<DetachFromPanelEvent>(OnDetach);
            editing = new VisualElement { name = "brushEditingControls" }; scroll.Add(editing);
            var modes = new VisualElement(); modes.AddToClassList("choice-row"); editing.Add(modes);
            brushMode = new Button(() => Run(() => editor.SetSelectionMode(false))) { text = "ブラシで変形", name = "brushEditMode" };
            vertexMode = new Button(() => Run(() => editor.SetSelectionMode(true))) { text = "選択して編集", name = "vertexEditMode" };
            modes.Add(brushMode); modes.Add(vertexMode);
            brushControls = new VisualElement { name = "brushOptions" }; editing.Add(brushControls);
            operation = new PopupField<string>("操作", new List<string> { "ブラシで変形", "影響範囲を編集", "視点操作のみ" }, editor.brush ? (editor.paintMask ? 1 : 0) : 2) { name = "brushOperation" };
            operation.RegisterValueChangedCallback(e => { editor.brush = operation.index != 2; editor.paintMask = operation.index == 1; Refresh(); }); brushControls.Add(operation);
            var radius = new Slider("大きさ", .001f, .1f) { name = "brushRadius", value = editor.radius, showInputField = true };
            radius.tooltip = "影響半径（ワールド単位）";
            radius.RegisterValueChangedCallback(e => editor.radius = Mathf.Clamp(e.newValue, .001f, .1f)); brushControls.Add(radius);
            var strength = new Slider("強さ", 0, 100) { name = "brushStrength", value = editor.strength * 100, showInputField = true };
            strength.RegisterValueChangedCallback(e => editor.strength = Mathf.Clamp01(e.newValue / 100)); brushControls.Add(strength);
            selectionControls = new VisualElement { name = "vertexSelectionControls" }; editing.Add(selectionControls);
            selectedCount = new Label { name = "selectedVertexCount" }; selectedCount.AddToClassList("vertex-count"); selectionControls.Add(selectedCount);
            selectionModeLabel = new Label { name = "vertexSelectionMode" }; selectionModeLabel.AddToClassList("vertex-mode-label"); selectionControls.Add(selectionModeLabel);
            var selectionTools = new VisualElement { name = "vertexSelectionTools" }; selectionTools.AddToClassList("vertex-tools"); selectionControls.Add(selectionTools);
            brushSelect = SelectionToolButton(selectionTools, VertexSelectTool.Brush, BuilderIcon.BrushSelect, "ブラシ", "vertexBrushSelect", "ブラシ選択 (C)\nなぞった範囲を選択。右クリックで通常に戻ります。");
            linkedSelect = SelectionToolButton(selectionTools, VertexSelectTool.Linked, BuilderIcon.LinkSelect, "リンク", "vertexLinkedSelect", "リンク選択 (L)\nつながったパーツを選択。右クリックで通常に戻ります。");
            loopSelect = SelectionToolButton(selectionTools, VertexSelectTool.Loop, BuilderIcon.LoopSelect, "ループ", "vertexLoopSelect", "ループ選択 (Alt＋クリック)\n辺の中央付近をクリックして頂点列を選択。三角形メッシュでは形状から推定し、曖昧な分岐では停止します。");
            loopNotice = new Label { name = "vertexLoopNotice" }; loopNotice.AddToClassList("muted"); selectionControls.Add(loopNotice);
            selectionRadius = new Slider("選択半径", 4, 100) { name = "vertexSelectionRadius", value = editor.selectionRadius, showInputField = true };
            selectionRadius.tooltip = "画面上のブラシ選択の半径";
            selectionRadius.RegisterValueChangedCallback(e => { editor.selectionRadius = Mathf.Clamp(e.newValue, 4, 100); editor.SelectionSettingsChanged(); }); selectionControls.Add(selectionRadius);
            var through = new Toggle("裏側も含めて選択") { name = "vertexSelectThrough", value = editor.selectThrough };
            through.tooltip = "OFFでは手前のメッシュに隠れた頂点を除外します。透過テクスチャの抜けは考慮しません。";
            through.RegisterValueChangedCallback(e => { editor.selectThrough = e.newValue; editor.SelectionSettingsChanged(); }); selectionControls.Add(through);
            var seams = new Toggle("同じ位置の頂点をつなぐ") { name = "vertexJoinCoincident", value = editor.joinCoincident };
            seams.tooltip = "UV境界などで分かれた頂点を一緒に選択・変形します。別パーツが重なっている場合はOFFにしてください。";
            seams.RegisterValueChangedCallback(e => editor.SetCoincidentSelection(e.newValue)); selectionControls.Add(seams);
            var selectionActions = new VisualElement(); selectionActions.AddToClassList("choice-row"); selectionControls.Add(selectionActions);
            selectionActions.Add(new Button(() => Run(editor.SelectAllVertices)) { text = "すべて選択", name = "selectAllVertices" });
            selectionActions.Add(new Button(() => Run(editor.ClearVertexSelection)) { text = "選択を解除", name = "clearVertexSelection" });
            transformControls = new VisualElement { name = "vertexTransformControls" }; transformControls.AddToClassList("vertex-transform"); selectionControls.Add(transformControls);
            transformTool = new PopupField<string>("編集", new List<string> { "移動", "回転", "拡大・縮小" }, (int)editor.transformTool) { name = "vertexTransformTool" };
            transformTool.RegisterValueChangedCallback(e => { editor.transformTool = (VertexTransformTool)transformTool.index; editor.SelectionSettingsChanged(true); }); transformControls.Add(transformTool);
            coordinateSpace = new PopupField<string>("座標軸", new List<string> { "アバター基準", "ワールド" }, editor.avatarAxes ? 0 : 1) { name = "vertexCoordinateSpace" };
            coordinateSpace.RegisterValueChangedCallback(e => { editor.avatarAxes = coordinateSpace.index == 0; editor.SelectionSettingsChanged(true); }); transformControls.Add(coordinateSpace);
            coordinates = new Vector3Field("中心位置 (m)") { name = "vertexCoordinates" }; coordinates.AddToClassList("vertex-coordinate"); transformControls.Add(coordinates);
            coordinates.tooltip = "移動は選択した頂点の中心座標、回転と倍率は選択の中心を基準に適用します。";
            transformControls.Add(new Button(() =>
            {
                try { editor.ApplyNumericTransform(coordinates.value); selectionMessage.text = ""; Refresh(); }
                catch (Exception ex) { selectionMessage.text = ex.Message; }
            }) { text = "数値を適用", name = "applyVertexCoordinates" });
            selectionMessage = new Label { name = "vertexSelectionMessage" }; selectionMessage.AddToClassList("muted"); transformControls.Add(selectionMessage);
            var proportional = new Toggle("プロポーショナル編集") { name = "vertexProportional", value = editor.proportional };
            proportional.tooltip = "選択した頂点の周囲も滑らかに動かします。水色が濃い頂点ほど強く動きます。";
            proportional.RegisterValueChangedCallback(e => { editor.proportional = e.newValue; editor.SelectionSettingsChanged(); }); transformControls.Add(proportional);
            proportionalControls = new VisualElement { name = "vertexProportionalOptions" }; transformControls.Add(proportionalControls);
            var influenceRadius = new Slider("影響半径 (m)", .001f, .15f) { name = "vertexProportionalRadius", value = editor.proportionalRadius, showInputField = true };
            influenceRadius.RegisterValueChangedCallback(e => { editor.proportionalRadius = Mathf.Clamp(e.newValue, .001f, .15f); editor.SelectionSettingsChanged(); }); proportionalControls.Add(influenceRadius);
            var falloff = new PopupField<string>("弱まり方", new List<string> { "なめらか", "直線", "強く減衰", "球状" }, (int)editor.falloff) { name = "vertexFalloff" };
            falloff.RegisterValueChangedCallback(e => { editor.falloff = (VertexFalloff)falloff.index; editor.SelectionSettingsChanged(); }); proportionalControls.Add(falloff);
            var connected = new Toggle("つながっている部分だけ") { name = "vertexConnectedOnly", value = editor.connectedOnly };
            connected.tooltip = "面のつながりに沿った距離で影響を計算します。近くにある別パーツへは伝わりません。";
            connected.RegisterValueChangedCallback(e => { editor.connectedOnly = e.newValue; editor.SelectionSettingsChanged(); }); proportionalControls.Add(connected);
            var symmetric = new Toggle("左右対称に編集") { name = "brushSymmetric", value = editor.symmetric };
            symmetric.tooltip = "この表情の両側を動かします。反対側の表情を別に作る設定とは異なります。";
            symmetric.RegisterValueChangedCallback(e => { editor.symmetric = e.newValue; editor.SelectionSettingsChanged(); }); editing.Add(symmetric);
            var history = new VisualElement(); history.AddToClassList("choice-row"); editing.Add(history);
            history.Add(new Button(() => Run(() => { editor.EndSelectionInteraction(false); Undo.PerformUndo(); })) { text = "元に戻す" });
            history.Add(new Button(() => Run(() => { editor.EndSelectionInteraction(false); Undo.PerformRedo(); })) { text = "やり直す" });
            help = new Label(); help.AddToClassList("muted"); scroll.Add(help);
            scroll.Add(new Button(() => Run(editor.Frame)) { text = "顔を中央に表示" });
            foreach (var row in Root.Query(className: "choice-row").ToList()) row[row.childCount - 1].AddToClassList("row-end");
            Root.RegisterCallback<PointerEnterEvent>(e => editor.pointerOverControls = true);
            Root.RegisterCallback<PointerLeaveEvent>(e => editor.pointerOverControls = false);
            Root.RegisterCallback<DetachFromPanelEvent>(e => editor.pointerOverControls = false);
            View.rootVisualElement.Add(Root);
            View.rootVisualElement.RegisterCallback<KeyDownEvent>(OnKeyDown, TrickleDown.TrickleDown);
            View.rootVisualElement.RegisterCallback<PointerDownEvent>(OnPointerDown, TrickleDown.TrickleDown);
            View.rootVisualElement.RegisterCallback<PointerUpEvent>(OnPointerUp, TrickleDown.TrickleDown);
            View.rootVisualElement.RegisterCallback<PointerMoveEvent>(OnPointerMove, TrickleDown.TrickleDown);
            View.rootVisualElement.RegisterCallback<PointerLeaveEvent>(OnPointerLeave);
            refresh = Root.schedule.Execute(Refresh).Every(100); Refresh();
        }
        Button SelectionToolButton(VisualElement parent, VertexSelectTool tool, BuilderIcon kind, string caption, string name, string tooltip)
        {
            var option = new VisualElement(); option.AddToClassList("vertex-tool-option"); parent.Add(option);
            var button = new Button(() => Run(() => { editor.ToggleSelectionTool(tool); FocusViewport(); })) { name = name, tooltip = tooltip };
            button.AddToClassList("vertex-tool"); option.Add(button);
            var icon = new BuilderIconElement(kind); icon.AddToClassList("vertex-tool-icon"); button.Add(icon);
            var label = new Label(caption) { pickingMode = PickingMode.Ignore }; label.AddToClassList("vertex-tool-caption"); option.Add(label);
            return button;
        }
        void OnKeyDown(KeyDownEvent e)
        {
            try
            {
                if (!editor.HandleSelectionShortcut(View, e.keyCode, e.modifiers, HasTextFocus, Navigating)) return;
                e.StopImmediatePropagation(); e.PreventDefault(); e.imguiEvent?.Use();
            }
            catch (Exception ex) { editor.enabled = false; editor.failed?.Invoke(ex); }
        }
        void OnPointerDown(PointerDownEvent e)
        {
            Navigating = (e.pressedButtons & 6) != 0;
            editor.pointerOverControls = Root.worldBound.Contains(e.position);
            if (!editor.pointerOverControls && e.target is IMGUIContainer)
            {
                FocusViewport(); editor.ObserveVertexPointer((e.pressedButtons & 1) != 0);
                if (e.button == 1 && !e.altKey && editor.ResetSelectionTool())
                {
                    Navigating = false; suppressRightRelease = true;
                    e.StopImmediatePropagation(); e.PreventDefault(); e.imguiEvent?.Use();
                }
            }
        }
        void OnPointerUp(PointerUpEvent e)
        {
            Navigating = (e.pressedButtons & 6) != 0; editor.ObserveVertexPointer((e.pressedButtons & 1) != 0);
            if (e.button == 1 && suppressRightRelease)
            { suppressRightRelease = false; e.StopImmediatePropagation(); e.PreventDefault(); e.imguiEvent?.Use(); }
        }
        void OnPointerMove(PointerMoveEvent e)
        {
            Navigating = (e.pressedButtons & 6) != 0; editor.pointerOverControls = Root.worldBound.Contains(e.position);
            editor.ObserveVertexPointer((e.pressedButtons & 1) != 0);
        }
        void OnPointerLeave(PointerLeaveEvent e) { Navigating = false; editor.pointerOverControls = false; editor.ObserveVertexPointer(false); }
        void OnDetach(DetachFromPanelEvent e) { if (e.target == Root && editor.HideOtherObjects) Run(() => editor.SetHideOtherObjects(false)); }
        internal void FocusViewport()
        {
            Root.panel?.focusController.focusedElement?.Blur();
            View.rootVisualElement.Q<IMGUIContainer>()?.Focus();
        }
        internal void UpdateViewportPointer(Vector2 screenPoint, bool mouseDown)
        {
            editor.pointerOverControls = Root.worldBound.Contains(screenPoint - View.position.position);
            if (mouseDown && !editor.pointerOverControls && HasTextFocus) FocusViewport();
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
            hideOtherObjects.SetValueWithoutNotify(editor.HideOtherObjects); hideOtherObjects.SetEnabled(active);
            string key = editor.recipe?.trackingKey ?? editor.session?.Profile.mappings.FirstOrDefault(m => m.shape == editor.recipe?.name)?.key;
            title.text = editor.recipe == null ? "表情の編集" : string.IsNullOrEmpty(key) ? editor.recipe.name : ExpressionGuide.Title(key);
            operation.SetValueWithoutNotify(operation.choices[editor.brush ? (editor.paintMask ? 1 : 0) : 2]);
            brushMode.EnableInClassList("selected-choice", !editor.selectionMode); vertexMode.EnableInClassList("selected-choice", editor.selectionMode);
            brushControls.EnableInClassList("hidden", editor.selectionMode); selectionControls.EnableInClassList("hidden", !editor.selectionMode);
            brushSelect.EnableInClassList("selected-choice", editor.selectionTool == VertexSelectTool.Brush);
            linkedSelect.EnableInClassList("selected-choice", editor.selectionTool == VertexSelectTool.Linked);
            loopSelect.EnableInClassList("selected-choice", editor.selectionTool == VertexSelectTool.Loop);
            selectionModeLabel.text = editor.selectionTool == VertexSelectTool.Normal ? "通常：クリック・ドラッグで選択" :
                (editor.selectionTool == VertexSelectTool.Brush ? "ブラシ選択" : editor.selectionTool == VertexSelectTool.Linked ? "リンク選択" : "ループ選択") + "（右クリックで解除）";
            loopNotice.text = editor.SelectionNotice; loopNotice.EnableInClassList("hidden", string.IsNullOrEmpty(editor.SelectionNotice));
            transformTool.SetValueWithoutNotify(transformTool.choices[(int)editor.transformTool]); coordinateSpace.SetValueWithoutNotify(coordinateSpace.choices[editor.avatarAxes ? 0 : 1]);
            selectionRadius.EnableInClassList("hidden", editor.selectionTool != VertexSelectTool.Brush);
            proportionalControls.EnableInClassList("hidden", !editor.proportional);
            proportionalControls.Q<Slider>("vertexProportionalRadius").SetValueWithoutNotify(editor.proportionalRadius);
            selectedCount.text = "選択: " + editor.SelectedCount + " 頂点";
            transformControls.SetEnabled(editor.SelectedCount > 0 && !editor.TransformInProgress);
            if (editor.selectionMode && active && !before && editor.SelectedCount > 0 && coordinateRevision != editor.SelectionRevision && !editor.TransformInProgress)
            {
                coordinates.label = editor.transformTool == VertexTransformTool.Move ? "中心位置 (m)" : editor.transformTool == VertexTransformTool.Rotate ? "回転量 (°)" : "倍率";
                coordinates.SetValueWithoutNotify(editor.transformTool == VertexTransformTool.Move ? editor.SelectionCoordinates : editor.transformTool == VertexTransformTool.Scale ? Vector3.one : Vector3.zero);
                coordinateRevision = editor.SelectionRevision;
            }
            help.text = editor.selectionMode ? "黒: 未選択 / オレンジ: 選択中\nShiftで追加・解除\nAlt＋クリック: ループ選択\nC: ブラシ / L: リンク / 右クリック: 通常\nG: 移動 / R: 回転 / S: 拡大・縮小\nA: 全選択 / Shift＋A: 選択解除\n数値入力後はモデルをクリックで戻る\n変形中のホイールで影響半径を変更\nEscで操作を取り消す\nAlt＋ドラッグで視点操作" : editor.paintMask ? "ドラッグで影響を減らす・Shiftで戻す\nAlt＋ドラッグで視点操作" : "ドラッグで編集・Shiftで反転\nAlt＋ドラッグで視点操作";
            state.text = !active ? "作成画面で「形状を編集」を選択" : before ? "元の顔を表示中（編集できません）" : editor.brush || editor.selectionMode ? "作成中の表情を編集中" : "作成中の表情を表示中";
            neutral.EnableInClassList("selected-choice", before); expression.EnableInClassList("selected-choice", !before);
            neutral.SetEnabled(active); expression.SetEnabled(active); editing.SetEnabled(active && !before);
        }
        public void Dispose()
        {
            refresh.Pause();
            Root.UnregisterCallback<DetachFromPanelEvent>(OnDetach);
            if (View)
            {
                View.rootVisualElement.UnregisterCallback<KeyDownEvent>(OnKeyDown, TrickleDown.TrickleDown);
                View.rootVisualElement.UnregisterCallback<PointerDownEvent>(OnPointerDown, TrickleDown.TrickleDown);
                View.rootVisualElement.UnregisterCallback<PointerUpEvent>(OnPointerUp, TrickleDown.TrickleDown);
                View.rootVisualElement.UnregisterCallback<PointerMoveEvent>(OnPointerMove, TrickleDown.TrickleDown);
                View.rootVisualElement.UnregisterCallback<PointerLeaveEvent>(OnPointerLeave);
            }
            Root.RemoveFromHierarchy(); editor.pointerOverControls = false;
        }
    }
}
