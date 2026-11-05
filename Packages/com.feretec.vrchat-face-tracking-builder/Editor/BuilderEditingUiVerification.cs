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
    public static class BuilderEditingUiVerification
    {
        static readonly BindingFlags Flags = BindingFlags.Instance | BindingFlags.NonPublic;
        public static void Run(BuilderProfile original)
        {
            var window = EditorWindow.GetWindow<FaceTrackingBuilderWindow>(); string windowState = EditorJsonUtility.ToJson(window);
            var originalWidth = window.rootVisualElement.style.width;
            var profile = AvatarAnalysis.Analyze(original.sourceAvatar);
            var report = new BuilderVerification.Report { unity = Application.unityVersion, time = DateTime.UtcNow.ToString("o") };
            var oldViews = Resources.FindObjectsOfTypeAll<SceneView>().ToDictionary(v => v, v => v.cameraMode);
            var type = typeof(FaceTrackingBuilderWindow);
            window.LoadProfile(profile); type.GetMethod("AddRecipe", Flags).Invoke(window, new object[] { "EyeLookOutRight" });
            type.GetField("tab", Flags).SetValue(window, 1); type.GetField("editingRecipe", Flags).SetValue(window, true); window.CreateGUI();
            var plan = profile.renderers.First(p => p.selected); var recipe = plan.recipes.Single();
            var details = window.rootVisualElement.Query<Foldout>().ToList().First(f => f.text.StartsWith("生成の詳細")); details.value = true;
            var scroll = window.rootVisualElement.Q<ScrollView>("content");
            var number = window.rootVisualElement.Q<FloatField>("sourceWeightNumber");
            number.value = 150; number.value = 50;
            int stage = 0; double next = EditorApplication.timeSinceStartup + .4; SceneExpressionEditor editor = null;
            Vector2 scrollBefore = Vector2.zero;
            EditorApplication.CallbackFunction tick = null;
            void Check(bool condition, string name)
            { report.results.Add(new BuilderVerification.Result { name = name, passed = condition, detail = condition ? "OK" : "Failed" }); }
            void Finish()
            {
                EditorApplication.update -= tick;
                window.rootVisualElement.style.width = originalWidth;
                window.LoadProfile(original); EditorJsonUtility.FromJsonOverwrite(windowState, window); window.CreateGUI();
                Undo.ClearUndo(profile); Object.DestroyImmediate(profile);
                File.WriteAllText("Library/FaceTrackingBuilderEditingUiVerification.json", JsonUtility.ToJson(report, true));
            }
            tick = () =>
            {
                if (EditorApplication.timeSinceStartup < next) return;
                try
                {
                    if (stage == 0)
                    {
                        var root = window.rootVisualElement;
                        Check(root.Query<Button>().ToList().All(b => b.text != "プレビューを開始" && b.text != "この表情を編集") &&
                            root.Query<Foldout>().ToList().All(f => !f.text.Contains("SceneView") && !f.text.Contains("複数同時プレビュー")), "重複する開始操作・折りたたみ編集メニューを廃止");
                        Check(root.Q<Button>("editFromBlendShapes") != null && root.Q<Button>("openBrushEditor").enabledSelf &&
                            root.Q("editingActions").Contains(root.Q<Button>("openBrushEditor")) && root.Q("editingActions").Contains(root.Q<Button>("editFromNeutral")) &&
                            !root.Q("actions").Contains(root.Q<Button>("openBrushEditor")), "編集方法とブラシ入口をグループ化・下部は確定操作のみ");
                        Check(Mathf.Abs(recipe.sources.Single().coefficient - .5f) < .00001f && root.Q<Slider>("sourceWeightSlider").value == 50, "数値とスライダーが同期・範囲縮小時も入力を保持");
                        var session = (BuilderSession)type.GetField("session", Flags).GetValue(window);
                        var actual = MeshEngine.Sample(session.Renderer(plan).sharedMesh, recipe.name, 100);
                        var expected = MeshEngine.Generate(plan.source, recipe, 100, out _);
                        Check(actual.vertices.Zip(expected.vertices, (a, b) => Vector3.Distance(a, b)).Max() < .00001f && root.Q<Image>("recipePortrait").image != null, "強さの変更が生成Meshと顔プレビューへ自動反映");
                        scroll.scrollOffset = new Vector2(0, 150); scrollBefore = scroll.scrollOffset;
                        type.GetMethod("StartEditing", Flags).Invoke(window, null);
                        editor = (SceneExpressionEditor)type.GetField("sceneEditor", Flags).GetValue(window);
                        Check(details.value && ReferenceEquals(details, root.Query<Foldout>().ToList().First(f => f.text.StartsWith("生成の詳細"))) && scroll.scrollOffset == scrollBefore, "編集開始でメニュー・スクロールが変化しない");
                        stage++; next = EditorApplication.timeSinceStartup + .4; return;
                    }
                    if (stage == 1)
                    {
                        var panel = editor.Controls.Root; var view = editor.session.View;
                        Check(view.titleContent.text == "表情の編集" && panel.panel != null && panel.Query<Foldout>().ToList().Count == 0, "専用ウィンドウに折りたたみなしのブラシUI");
                        Check(panel.worldBound.width >= 240 && panel.worldBound.height > 250 && panel.worldBound.xMin >= view.rootVisualElement.worldBound.xMin &&
                            panel.worldBound.xMax <= view.rootVisualElement.worldBound.xMax + 1 && panel.worldBound.yMax <= view.rootVisualElement.worldBound.yMax + 1, "ブラシパネルが編集ウィンドウ内に収まる");
                        panel.Q<Slider>("brushRadius").value = .033f; panel.Q<Slider>("brushStrength").value = 42; panel.Q<Toggle>("brushSymmetric").value = true;
                        Check(Mathf.Abs(editor.radius - .033f) < .00001f && Mathf.Abs(editor.strength - .42f) < .00001f && editor.symmetric, "ウィンドウ内の大きさ・強さ・左右対称が編集処理へ反映");
                        var operation = panel.Q<PopupField<string>>("brushOperation"); operation.index = 2;
                        type.GetMethod("StartEditing", Flags).Invoke(window, null);
                        Check(editor.brush && operation.index == 0 && ReferenceEquals(panel, editor.Controls.Root), "ブラシ再開時に操作表示が同期・ウィンドウを重複作成しない");
                        var modes = panel.Q<PopupField<string>>("brushDisplayMode"); modes.index = 1;
                        Check(view.cameraMode.drawMode == DrawCameraMode.TexturedWire, "ワイヤー重ね表示"); modes.index = 2;
                        Check(view.cameraMode.drawMode == DrawCameraMode.Wireframe, "ワイヤーのみ表示"); modes.index = 0;
                        Check(view.cameraMode.drawMode == DrawCameraMode.Textured && oldViews.All(v => !v.Key || v.Key.cameraMode.Equals(v.Value)), "通常表示への復帰・他のSceneViewは変更しない");
                        editor.Compare(true);
                        Check(!editor.CanEdit && !panel.Q("brushEditingControls").enabledSelf && panel.Q<Button>("showNeutral").ClassListContains("selected-choice") &&
                            window.rootVisualElement.Q<Label>("recipeDisplayState").text == "元の顔を表示中", "比較状態を両画面に表示・元の顔へのブラシ操作を無効化");
                        number.value = 25;
                        stage++; next = EditorApplication.timeSinceStartup + .3; return;
                    }
                    if (stage == 2)
                    {
                        Check(editor.session.Comparing && !editor.CanEdit, "強さを変更しても比較状態を保持");
                        editor.Compare(false);
                        Check(editor.CanEdit && editor.Controls.Root.Q("brushEditingControls").enabledSelf &&
                            editor.Controls.Root.Q<Button>("showExpression").ClassListContains("selected-choice"), "作成中の表情へ戻ると編集を再開");
                        recipe.landmarks.Add(new Landmark { name = "旧設定", position = plan.source.vertices[0], movement = Vector3.up * .0001f });
                        type.GetMethod("RefreshContent", Flags).Invoke(window, null);
                        Check(window.rootVisualElement.Query<Foldout>().ToList().First(f => f.text.StartsWith("生成の詳細")).value && recipe.landmarks.Count == 1,
                            "画面再構築でも開閉状態と旧特徴点データを保持");
                        stage++; next = EditorApplication.timeSinceStartup + .3; return;
                    }
                    if (stage == 3)
                    {
                        Check(Vector2.Distance(scroll.scrollOffset, scrollBefore) < 1, "画面再構築後のスクロール復元");
                        window.rootVisualElement.style.width = 700; stage++; next = EditorApplication.timeSinceStartup + .3; return;
                    }
                    var rootBounds = window.rootVisualElement.worldBound;
                    var methods = window.rootVisualElement.Q("editingMethods").worldBound;
                    var brush = window.rootVisualElement.Q<Button>("openBrushEditor").worldBound;
                    Check(methods.yMax + 6 <= brush.yMin && brush.xMax <= rootBounds.xMax && brush.xMin >= methods.xMin - 1,
                        "幅700でもブラシは切り替えボタンの下の別行に表示");
                    Finish();
                }
                catch (Exception ex)
                { report.results.Add(new BuilderVerification.Result { name = "編集UI検証", passed = false, detail = ex.ToString() }); Finish(); }
            };
            EditorApplication.update += tick;
        }
    }
}
