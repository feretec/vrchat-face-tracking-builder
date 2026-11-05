using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using UnityEditor;
using UnityEditor.UIElements;
using UnityEngine;
using UnityEngine.UIElements;

namespace FaceTrackingBuilder
{
    public static class BuilderUiVerification
    {
        static readonly BindingFlags Flags = BindingFlags.Instance | BindingFlags.NonPublic;
        public static void Run(BuilderProfile profile)
        {
            var window = EditorWindow.GetWindow<FaceTrackingBuilderWindow>(); window.LoadProfile(profile);
            var originalWidth = window.rootVisualElement.style.width;
            float wideSearchWidth = 0;
            var report = new BuilderVerification.Report { unity = Application.unityVersion, time = DateTime.UtcNow.ToString("o") };
            var header = window.rootVisualElement.Q("header"); var fold = header.Q<Foldout>(); fold.value = true;
            int stage = 0; double next = EditorApplication.timeSinceStartup + .3;
            EditorApplication.CallbackFunction tick = null;
            void Check(bool condition, string name)
            {
                report.results.Add(new BuilderVerification.Result { name = name, passed = condition, detail = condition ? "OK" : "Failed" });
            }
            void Finish()
            {
                EditorApplication.update -= tick;
                window.rootVisualElement.style.width = originalWidth;
                window.LoadProfile(profile);
                File.WriteAllText("Library/FaceTrackingBuilderUiVerification.json", JsonUtility.ToJson(report, true));
            }
            tick = () =>
            {
                if (EditorApplication.timeSinceStartup < next) return;
                try
                {
                    if (stage == 0)
                    {
                        var tabs = window.rootVisualElement.Q("tabs"); var content = window.rootVisualElement.Q("content"); var actions = window.rootVisualElement.Q("actions");
                        Check(header.worldBound.yMax <= tabs.worldBound.yMin + 1 && tabs.worldBound.yMax <= content.worldBound.yMin + 1, "設定展開時のヘッダー・タブ・本文の重なりなし");
                        var buttons = header.Query<Button>(className: "action-button").ToList();
                        Check(buttons.Count > 1 && buttons.Zip(buttons.Skip(1), (a, b) => a.worldBound.yMax <= b.worldBound.yMin + 1).All(v => v), "設定内のボタン同士の重なりなし");
                        Check(content.worldBound.yMax <= actions.worldBound.yMin + 1, "固定アクションと本文の重なりなし");
                        fold.value = false; stage++; next = EditorApplication.timeSinceStartup + .2; return;
                    }
                    if (stage == 1)
                    {
                        string selected = AvatarAnalysis.Shapes(profile).Last();
                        typeof(FaceTrackingBuilderWindow).GetMethod("PickShape", Flags).Invoke(window, new object[] { null, selected, new Action<string>(_ => { }) });
                        stage++; next = EditorApplication.timeSinceStartup + .7; return;
                    }
                    if (stage == 2)
                    {
                        var list = window.rootVisualElement.Q<ListView>("shapeList"); var viewport = list.Q<ScrollView>().contentViewport;
                        var item = list.GetRootElementForIndex(list.selectedIndex);
                        Check(list.selectedIndex == list.itemsSource.Count - 1 && item != null && item.worldBound.yMin >= viewport.worldBound.yMin - 1 && item.worldBound.yMax <= viewport.worldBound.yMax + 1,
                            "候補変更の初期スクロールで選択中の末尾表情が見える");
                        var search = window.rootVisualElement.Q<ToolbarSearchField>("shapeSearch"); search.value = "no-such-shape-93820";
                        Check(list.itemsSource.Count == 0, "初期スクロール後も検索できる");
                        window.LoadProfile(profile); window.rootVisualElement.style.width = 1200;
                        stage++; next = EditorApplication.timeSinceStartup + .3; return;
                    }
                    if (stage == 3)
                    {
                        var search = window.rootVisualElement.Q<ToolbarSearchField>("mappingSearch");
                        wideSearchWidth = search.worldBound.width;
                        Check(search.worldBound.xMax <= search.parent.worldBound.xMax + 1 && wideSearchWidth > 300, "広い画面で検索欄が親の幅に追従");
                        window.rootVisualElement.style.width = 700; stage++; next = EditorApplication.timeSinceStartup + .3; return;
                    }
                    if (stage == 4)
                    {
                        var search = window.rootVisualElement.Q<ToolbarSearchField>("mappingSearch");
                        var detail = window.rootVisualElement.Q(className: "detail-column");
                        Check(search.worldBound.width < wideSearchWidth - 50 && search.worldBound.xMax <= search.parent.worldBound.xMax + 1 &&
                            search.worldBound.xMax < detail.worldBound.xMin, "幅700への縮小で検索欄が縮み詳細欄と重ならない");
                        search.value = "no-such-expression-57298";
                        Check(window.rootVisualElement.Q<ListView>("mappingList").itemsSource.Count == 0, "縮小後も検索操作が有効");
                        window.rootVisualElement.style.width = 1200; stage++; next = EditorApplication.timeSinceStartup + .3; return;
                    }
                    if (stage == 5)
                    {
                        var search = window.rootVisualElement.Q<ToolbarSearchField>("mappingSearch");
                        Check(Mathf.Abs(search.worldBound.width - wideSearchWidth) < 2, "再拡大すると検索欄の幅も戻る");
                        search.value = ""; Finish();
                    }
                }
                catch (Exception ex)
                {
                    report.results.Add(new BuilderVerification.Result { name = "UIレイアウト検証", passed = false, detail = ex.ToString() }); Finish();
                }
            };
            EditorApplication.update += tick;
        }
    }
}
