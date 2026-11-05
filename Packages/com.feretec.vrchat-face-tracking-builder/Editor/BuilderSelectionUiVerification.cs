using System;
using System.IO;
using System.Linq;
using System.Reflection;
using UnityEditor;
using UnityEditor.UIElements;
using UnityEngine;
using UnityEngine.UIElements;
using Object = UnityEngine.Object;

namespace FaceTrackingBuilder
{
    public static class BuilderSelectionUiVerification
    {
        const BindingFlags Flags = BindingFlags.Instance | BindingFlags.NonPublic;
        public static void Run(BuilderProfile original)
        {
            var window = EditorWindow.GetWindow<FaceTrackingBuilderWindow>(); string windowState = EditorJsonUtility.ToJson(window);
            var profile = AvatarAnalysis.Analyze(original.sourceAvatar); var plan = profile.renderers.First(p => p.selected);
            var recipe = new ShapeRecipe { name = "FTB_SelectionUiVerification", trackingKey = "BrowInnerUpRight", mode = GenerateMode.Landmarks };
            plan.recipes.Add(recipe); window.LoadProfile(profile);
            var type = typeof(FaceTrackingBuilderWindow); type.GetField("tab", Flags).SetValue(window, 1); type.GetField("editingRecipe", Flags).SetValue(window, true); window.CreateGUI();
            var report = new BuilderVerification.Report { unity = Application.unityVersion, time = DateTime.UtcNow.ToString("o") };
            SceneExpressionEditor editor = null; int stage = 0; double next = EditorApplication.timeSinceStartup + .4;
            EditorApplication.CallbackFunction tick = null;
            void Check(bool value, string name) { report.results.Add(new BuilderVerification.Result { name = name, passed = value, detail = value ? "OK" : "Failed" }); }
            void Finish()
            {
                EditorApplication.update -= tick;
                window.LoadProfile(original); EditorJsonUtility.FromJsonOverwrite(windowState, window); window.CreateGUI();
                Undo.ClearUndo(profile); Object.DestroyImmediate(profile);
                File.WriteAllText("Library/FaceTrackingBuilderSelectionUiVerification.json", JsonUtility.ToJson(report, true));
            }
            tick = () =>
            {
                if (EditorApplication.timeSinceStartup < next) return;
                try
                {
                    if (stage == 0)
                    {
                        var entry = window.rootVisualElement.Q<Button>("openBrushEditor");
                        Check(entry.Q<Label>().text == "形状を編集" && entry.Q<BuilderIconElement>("shapeEditIcon") != null, "形状を編集のタイトル右にブラシアイコン");
                        type.GetMethod("StartEditing", Flags).Invoke(window, null);
                        editor = (SceneExpressionEditor)type.GetField("sceneEditor", Flags).GetValue(window);
                        editor.SetSelectionMode(true); stage++; next = EditorApplication.timeSinceStartup + .4; return;
                    }
                    var root = editor.Controls.Root;
                    if (stage == 1)
                    {
                        Check(root.Q<Button>("vertexEditMode").ClassListContains("selected-choice") && root.Q("brushOptions").ClassListContains("hidden") &&
                            !root.Q("vertexSelectionControls").ClassListContains("hidden"), "選択モードだけに必要な操作を表示");
                        Check(!root.Q("vertexTransformControls").enabledSelf && root.Q<Label>("selectedVertexCount").text == "選択: 0 頂点", "未選択では変形を無効化");
                        var toolButtons = root.Q("vertexSelectionTools").Query<Button>().ToList();
                        Check(root.Q("vertexSelectTool") == null && toolButtons.Count == 3 && toolButtons.All(b => string.IsNullOrEmpty(b.text) && !string.IsNullOrEmpty(b.tooltip) &&
                            Mathf.Abs(b.worldBound.width - b.worldBound.height) < .1f && b.worldBound.width == 32), "選び方はツールチップ付きの正方形アイコン3個");
                        Check(toolButtons.All(b => Vector2.Distance(b.worldBound.center, b.Q<BuilderIconElement>().worldBound.center) < .1f), "全アイコンがボタン中央に配置される");
                        Check(root.Q("vertexSelectionTools").Query<Label>().ToList().Select(l => l.text).SequenceEqual(new[] { "ブラシ", "リンク", "ループ" }) &&
                            root.Q<Label>("vertexSelectionMode").text.Contains("通常"), "名前と現在の選択方法を表示");
                        Check(editor.selectionTool == VertexSelectTool.Normal && toolButtons.All(b => !b.ClassListContains("selected-choice")), "通常選択では全アイコンが非選択");
                        Check(editor.session.View.cameraMode.drawMode == DrawCameraMode.TexturedWire && root.Q<PopupField<string>>("brushDisplayMode").index == 1,
                            "ワイヤー重ね表示を初期値にする");
                        editor.ToggleSelectionTool(VertexSelectTool.Brush);
                        Check(root.Q<Button>("vertexBrushSelect").ClassListContains("selected-choice") && !root.Q<Button>("vertexLinkedSelect").ClassListContains("selected-choice"), "ブラシ選択だけを選択表示");
                        editor.ToggleSelectionTool(VertexSelectTool.Linked);
                        Check(!root.Q<Button>("vertexBrushSelect").ClassListContains("selected-choice") && root.Q<Button>("vertexLinkedSelect").ClassListContains("selected-choice"), "リンク選択とブラシ選択は排他");
                        editor.ToggleSelectionTool(VertexSelectTool.Linked);
                        Check(editor.selectionTool == VertexSelectTool.Normal && toolButtons.All(b => !b.ClassListContains("selected-choice")), "同じアイコンをもう一度選ぶと通常に戻る");
                        editor.ToggleSelectionTool(VertexSelectTool.Loop);
                        Check(root.Q<Button>("vertexLoopSelect").ClassListContains("selected-choice") && toolButtons.Count(b => b.ClassListContains("selected-choice")) == 1 &&
                            root.Q<Label>("vertexSelectionMode").text.Contains("ループ"), "ループモードを排他的に選択できる");
                        editor.ResetSelectionTool();
                        editor.ToggleSelectionTool(VertexSelectTool.Brush);
                        Check(root.Q("vertexSelectOperation") == null, "選択操作メニューを表示しない");
                        root.Q<Slider>("vertexSelectionRadius").value = 36;
                        root.Q<Toggle>("vertexSelectThrough").value = true;
                        root.Q<Toggle>("vertexJoinCoincident").value = true;
                        Check(editor.selectionTool == VertexSelectTool.Brush && editor.selectionRadius == 36 &&
                            editor.selectThrough && editor.joinCoincident && !root.Q("vertexSelectionRadius").ClassListContains("hidden"), "選択方法・半径・透過選択・境界接続を反映");
                        editor.SelectVertices(new[] { 0 }, VertexSelectOperation.Replace); editor.Controls.Refresh();
                        var selectedBeforeReset = editor.SelectedVertices;
                        Check(editor.ResetSelectionTool() && editor.selectionTool == VertexSelectTool.Normal && selectedBeforeReset.SequenceEqual(editor.SelectedVertices) &&
                            toolButtons.All(b => !b.ClassListContains("selected-choice")), "右クリックの解除処理は選択頂点を保ち通常に戻す");
                        Check(editor.SelectedCount > 0 && root.Q("vertexTransformControls").enabledSelf, "選択すると座標編集を有効化");
                        root.Q<PopupField<string>>("vertexCoordinateSpace").index = 1;
                        var coordinates = root.Q<Vector3Field>("vertexCoordinates");
                        Check(Vector3.Distance(coordinates.value, editor.SelectionCenter) < .00001f && coordinates.label == "中心位置 (m)", "ワールド座標の選択中心を表示");
                        Vector3 target = coordinates.value + Vector3.up * .001f; coordinates.value = target; editor.ApplyNumericTransform(coordinates.value);
                        Check(Vector3.Distance(editor.SelectionCenter, target) < .00001f, "数値の移動先へ選択頂点が移動");
                        Undo.PerformUndo(); Check(Vector3.Distance(editor.SelectionCenter, target - Vector3.up * .001f) < .00001f && editor.SelectedCount > 0, "メイン画面と連携したUndoでも選択を維持");
                        Undo.PerformRedo(); Check(Vector3.Distance(editor.SelectionCenter, target) < .00001f, "メイン画面と連携したRedoで座標を復元");
                        root.Q<PopupField<string>>("vertexTransformTool").index = 1;
                        Check(coordinates.label == "回転量 (°)" && coordinates.value == Vector3.zero && editor.transformTool == VertexTransformTool.Rotate, "回転は角度の相対入力");
                        root.Q<PopupField<string>>("vertexTransformTool").index = 2;
                        Check(coordinates.label == "倍率" && coordinates.value == Vector3.one && editor.transformTool == VertexTransformTool.Scale, "拡大縮小は倍率入力");
                        root.Q<Toggle>("vertexProportional").value = true;
                        root.Q<Slider>("vertexProportionalRadius").value = .04f;
                        root.Q<PopupField<string>>("vertexFalloff").index = 2;
                        root.Q<Toggle>("vertexConnectedOnly").value = false;
                        Check(editor.proportional && Mathf.Abs(editor.proportionalRadius - .04f) < .00001f && editor.falloff == VertexFalloff.Sharp && !editor.connectedOnly &&
                            !root.Q("vertexProportionalOptions").ClassListContains("hidden"), "プロポーショナル編集の設定を計算へ反映");
                        coordinates.value = Vector3.one * 1.05f; root.Q<Slider>("vertexProportionalRadius").value = .03f;
                        Check(coordinates.value == Vector3.one * 1.05f, "影響半径を変更しても適用前の入力値を保持");
                        editor.Compare(true);
                        Check(!editor.CanEdit && !root.Q("brushEditingControls").enabledSelf && root.Q<Button>("showNeutral").ClassListContains("selected-choice"), "比較中は選択・座標編集を無効化");
                        editor.Compare(false); editor.SetSelectionMode(false);
                        Check(root.Q("vertexSelectionControls").ClassListContains("hidden") && !root.Q("brushOptions").ClassListContains("hidden") && editor.SelectedCount > 0, "ブラシへ切り替えても選択を保持");
                        editor.SetSelectionMode(true); editor.Frame(); coordinates.Q<FloatField>().Focus();
                        stage++; next = EditorApplication.timeSinceStartup + .5; return;
                    }
                    if (stage == 2)
                    {
                        editor.session.View.Focus();
                        Check(editor.Controls.HasTextFocus && !editor.HandleSelectionShortcut(editor.session.View, KeyCode.A, EventModifiers.None, editor.Controls.HasTextFocus, false),
                            "数値入力中は選択ショートカットの対象から除外");
                        editor.Controls.FocusViewport(); stage++; next = EditorApplication.timeSinceStartup + .3; return;
                    }
                    Check(!editor.Controls.HasTextFocus, "モデルへ戻ると数値入力フォーカスを解除");
                    editor.pointerOverControls = true;
                    Check(editor.HandleSelectionShortcut(editor.session.View, KeyCode.B, EventModifiers.None, false, false) && editor.selectionTool == VertexSelectTool.Normal,
                        "マウスの過去の位置に依存せず通常選択へ戻れる");
                    Check(editor.HandleSelectionShortcut(editor.session.View, KeyCode.C, EventModifiers.None, false, false) && editor.selectionTool == VertexSelectTool.Brush &&
                        editor.HandleSelectionShortcut(editor.session.View, KeyCode.L, EventModifiers.None, false, false) && editor.selectionTool == VertexSelectTool.Linked,
                        "ブラシ選択とリンク選択のショートカットが動作");
                    Check(editor.HandleSelectionShortcut(editor.session.View, KeyCode.G, EventModifiers.None, false, false) && editor.transformTool == VertexTransformTool.Move &&
                        editor.HandleSelectionShortcut(editor.session.View, KeyCode.R, EventModifiers.None, false, false) && editor.transformTool == VertexTransformTool.Rotate &&
                        editor.HandleSelectionShortcut(editor.session.View, KeyCode.S, EventModifiers.None, false, false) && editor.transformTool == VertexTransformTool.Scale,
                        "移動・回転・拡大縮小のショートカットが動作");
                    foreach (var mode in new[] { VertexTransformTool.Move, VertexTransformTool.Rotate, VertexTransformTool.Scale })
                    {
                        editor.transformTool = mode; editor.ObserveVertexPointer(true); editor.BeginVertexTransform();
                        editor.PreviewVertexTransform(mode == VertexTransformTool.Move ? Vector3.up * .0001f : Vector3.zero,
                            mode == VertexTransformTool.Rotate ? Quaternion.Euler(0, 0, 1) : Quaternion.identity,
                            mode == VertexTransformTool.Scale ? Vector3.one * 1.001f : Vector3.one);
                        Check(!editor.HandleSelectionShortcut(editor.session.View, KeyCode.G, EventModifiers.None, false, false), mode + ": ドラッグ中はモードを変えない");
                        editor.ObserveVertexPointer(false);
                        Check(!editor.TransformInProgress && editor.HandleSelectionShortcut(editor.session.View, KeyCode.G, EventModifiers.None, false, false) &&
                            editor.HandleSelectionShortcut(editor.session.View, KeyCode.R, EventModifiers.None, false, false) &&
                            editor.HandleSelectionShortcut(editor.session.View, KeyCode.S, EventModifiers.None, false, false), mode + ": ポインター解放後はG・R・Sを受け付ける");
                    }
                    editor.BeginVertexTransform();
                    Check(editor.HandleSelectionShortcut(editor.session.View, KeyCode.R, EventModifiers.None, false, false) && !editor.TransformInProgress,
                        "終了イベントを見落とした変形状態が残っても次のショートカットで復帰");
                    Check(!editor.HandleSelectionShortcut(editor.session.View, KeyCode.B, EventModifiers.Control, false, false) &&
                        !editor.HandleSelectionShortcut(editor.session.View, KeyCode.B, EventModifiers.Alt, false, false) &&
                        !editor.HandleSelectionShortcut(editor.session.View, KeyCode.B, EventModifiers.None, false, true), "Ctrl操作と視点操作へ割り込まない");
                    window.Focus();
                    Check(!editor.HandleSelectionShortcut(editor.session.View, KeyCode.B, EventModifiers.None, false, false), "別ウィンドウのキー入力へ割り込まない");
                    editor.session.View.Focus();
                    Check(editor.HandleSelectionShortcut(editor.session.View, KeyCode.A, EventModifiers.None, false, false) && editor.SelectedCount == plan.source.vertexCount &&
                        editor.HandleSelectionShortcut(editor.session.View, KeyCode.A, EventModifiers.Shift, false, false) && editor.SelectedCount == 0,
                        "Aで全選択・Shift Aで解除");
                    editor.SelectVertices(new[] { 0 }, VertexSelectOperation.Replace);
                    editor.pointerOverControls = false;
                    var viewBounds = editor.session.View.rootVisualElement.worldBound; var panelBounds = root.worldBound;
                    Check(panelBounds.width >= 240 && panelBounds.xMin >= viewBounds.xMin && panelBounds.xMax <= viewBounds.xMax + 1 && panelBounds.yMax <= viewBounds.yMax + 1,
                        "選択パネルが編集ウィンドウ内に収まる");
                    var coordinatesBounds = root.Q<Vector3Field>("vertexCoordinates").worldBound;
                    Check(coordinatesBounds.width > 180 && coordinatesBounds.xMax <= panelBounds.xMax &&
                        root.Q<Vector3Field>("vertexCoordinates").Query<FloatField>().ToList().All(f => f.worldBound.width > 30 && f.worldBound.xMax <= panelBounds.xMax), "XYZの入力欄がパネル内に収まる");
                    Check(root.Query<Foldout>().ToList().Count == 0, "編集パネルに折りたたみメニューを追加しない");
                    Check(root.Query<TextElement>().ToList().All(t => t.resolvedStyle.unityFontStyleAndWeight == FontStyle.Normal) &&
                        window.rootVisualElement.Query<TextElement>().ToList().All(t => t.resolvedStyle.unityFontStyleAndWeight == FontStyle.Normal), "メイン画面と編集パネルの文字の太さを統一");
                    var editEntry = window.rootVisualElement.Q<Button>("openBrushEditor");
                    Check(editEntry.Q<BuilderIconElement>().worldBound.xMin >= editEntry.Q<Label>().worldBound.xMax &&
                        Mathf.Abs(editEntry.Q<BuilderIconElement>().worldBound.center.y - editEntry.Q<Label>().worldBound.center.y) < .1f,
                        "ブラシアイコンはタイトル右側で上下中央に揃う");
                    Finish();
                }
                catch (Exception ex) { report.results.Add(new BuilderVerification.Result { name = "選択UI検証", passed = false, detail = ex.ToString() }); Finish(); }
            };
            EditorApplication.update += tick;
        }
    }
}
