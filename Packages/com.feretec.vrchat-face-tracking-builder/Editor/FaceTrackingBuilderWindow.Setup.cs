using System;
using System.Collections.Generic;
using System.Linq;
using UnityEditor;
using UnityEditor.UIElements;
using UnityEngine;
using UnityEngine.UIElements;

namespace FaceTrackingBuilder
{
    public partial class FaceTrackingBuilderWindow
    {
        static readonly string[] Categories = { "すべて", "目", "眉", "口", "顎", "頬", "鼻", "舌", "喉・首" };
        VisualElement Actions => rootVisualElement.Q("actions");
        Button Primary(string title, Action action)
        {
            var button = Button(Actions, title, action); button.AddToClassList("primary"); return button;
        }
        readonly Dictionary<string, bool> sectionStates = new Dictionary<string, bool>();
        readonly Dictionary<string, Vector2> scrollPositions = new Dictionary<string, Vector2>();
        string contentKey;
        void RememberScroll()
        {
            if (picker == null && content is ScrollView scroll && contentKey != null) scrollPositions[contentKey] = scroll.scrollOffset;
        }
        Foldout Fold(VisualElement parent, string title, bool open = false)
        {
            var f = new Foldout { text = title, value = sectionStates.TryGetValue(title, out var saved) ? saved : open };
            f.RegisterValueChangedCallback(e => { if (e.target == f) sectionStates[title] = e.newValue; });
            f.AddToClassList("details"); parent.Add(f); return f;
        }
        static VisualElement Column(VisualElement parent, string css)
        {
            var c = new VisualElement(); c.AddToClassList(css); parent.Add(c); return c;
        }
        public void CreateGUI()
        {
            RememberScroll(); contentKey = null;
            picker?.Dispose(); picker = null; rootVisualElement.Clear(); tab = Mathf.Clamp(tab, 0, 1); minSize = new Vector2(700, 650);
            string directory = System.IO.Path.GetDirectoryName(AssetDatabase.GetAssetPath(MonoScript.FromScriptableObject(this))).Replace('\\', '/');
            var tree = AssetDatabase.LoadAssetAtPath<VisualTreeAsset>(directory + "/FaceTrackingBuilderWindow.uxml");
            if (!tree) { rootVisualElement.Add(new Label("UIファイルが見つかりません。EditorフォルダのUXML/USSを復元してください。")); return; }
            tree.CloneTree(rootVisualElement); rootVisualElement.EnableInClassList("light-theme", !EditorGUIUtility.isProSkin);
            content = rootVisualElement.Q<ScrollView>("content"); message = rootVisualElement.Q<Label>("message"); message.text = lastMessage;
            if (profile)
            {
                var settings = Fold(rootVisualElement.Q("header"), profile.sourceName + " — アバター・保存設定");
                AvatarFields(settings);
                var saved = new ObjectField("保存済みの作業設定") { objectType = typeof(BuilderProfile), allowSceneObjects = false, value = profile };
                saved.RegisterValueChangedCallback(e => { if (e.newValue) LoadProfile((BuilderProfile)e.newValue); }); settings.Add(saved);
                Button(settings, "作業設定を保存", () => Notify("保存しました: " + BuilderStorage.SaveProfile(profile)));
                Button(settings, "プレビューを終了", () => { EndPreview(); Notify("プレビューのコピーを閉じました。"); });
            }
            var tabs = rootVisualElement.Q("tabs");
            for (int i = 0; i < Tabs.Length; i++)
            {
                int index = i; var b = Button(tabs, Tabs[i], () => { tab = index; Notify(""); if (sceneEditor != null) sceneEditor.enabled = false; CreateGUI(); });
                b.EnableInClassList("active-tab", i == tab);
            }
            Guard(RefreshContent);
        }
        void AvatarFields(VisualElement parent)
        {
            var source = new ObjectField("アバター") { objectType = typeof(GameObject), allowSceneObjects = true, value = avatar };
            source.RegisterValueChangedCallback(e => avatar = (GameObject)e.newValue); parent.Add(source);
            var analyze = Button(parent, profile ? "指定したアバターで新しい設定を作る" : "既存の表情を調べる", () =>
            {
                var next = AvatarAnalysis.Analyze(avatar);
                try { BuilderStorage.SaveProfile(next); } catch { DestroyImmediate(next); throw; }
                LoadProfile(next); Notify("使いたい表情の候補を確認してください。");
            });
            if (!profile) analyze.AddToClassList("primary");
        }
        void RefreshContent()
        {
            if (content == null) return;
            RememberScroll(); recipeRefresh?.Pause(); renderRecipePortrait = null;
            string nextKey = (profile ? profile.GetInstanceID().ToString() : "none") + ":" + tab + ":" + setupStep + ":" + (editingRecipe && profile && profile.renderers.Count > 0 ? Recipe()?.id : "list");
            if (contentKey != nextKey && sceneEditor != null) sceneEditor.enabled = false;
            contentKey = nextKey;
            picker?.Dispose(); picker = null; content.Clear(); Actions?.Clear();
            if (!profile)
            {
                Text(content, "アバターを選択", "section");
                Text(content, "既存の表情を確認して、Face Trackingを設定したコピーを保存します。"); AvatarFields(content);
                var fold = Fold(content, "保存した作業を再開");
                var saved = new ObjectField("作業設定") { objectType = typeof(BuilderProfile), allowSceneObjects = false };
                saved.RegisterValueChangedCallback(e => { if (e.newValue) LoadProfile((BuilderProfile)e.newValue); }); fold.Add(saved); return;
            }
            if (tab == 1) { if (profile.renderers.Count == 0) Text(content, "編集できるメッシュがありません。"); else if (editingRecipe && Recipe() != null) Generation(); else Advanced(); }
            else if (setupStep == 1) Apply(); else if (setupStep == 2) Saved(); else Mappings();
            if (content is ScrollView scroll)
            {
                var offset = scrollPositions.TryGetValue(contentKey, out var previous) ? previous : Vector2.zero;
                string expected = contentKey; scroll.schedule.Execute(() => { if (contentKey == expected) scroll.scrollOffset = offset; }).ExecuteLater(1);
            }
        }
        void Mappings()
        {
            Text(content, "使う表情を確認", "section");
            Text(content, "一覧を選択すると、↑↓キーで前後の表情に移動できます。", "muted");
            var columns = Column(content, "columns"); var left = Column(columns, "browser-column"); var detail = Column(columns, "detail-column");
            var query = new ToolbarSearchField { name = "mappingSearch", value = search }; left.Add(query);
            var filter = new PopupField<string>("部位", Categories.ToList(), Categories.Contains(category) ? category : "すべて"); left.Add(filter);
            var all = new Toggle("割り当てのない項目も表示"); left.Add(all);
            var list = new ListView { name = "mappingList", selectionType = SelectionType.Single }; list.AddToClassList("mapping-list"); left.Add(list);
            ExpressionListNavigation.EnableArrowKeys(list);
            list.makeItem = () =>
            {
                var item = new VisualElement(); item.AddToClassList("mapping-item");
                item.Add(new Label { name = "mappingTitle" });
                var caption = new Label { name = "mappingShape" }; caption.AddToClassList("muted"); item.Add(caption); return item;
            };
            list.bindItem = (item, index) =>
            {
                var mapping = (TrackingMapping)list.itemsSource[index];
                item.Q<Label>("mappingTitle").text = ExpressionGuide.Title(mapping.key) + (mapping.excluded ? "（除外中）" : "");
                item.Q<Label>("mappingShape").text = string.IsNullOrEmpty(mapping.shape) ? "未割り当て" : mapping.shape;
                item.EnableInClassList("excluded", mapping.excluded);
            };
            var next = Primary("保存内容を確認", () => { setupStep = 1; reviewedFingerprint = CurrentFingerprint(); RefreshContent(); });
            void Detail()
            {
                detail.Clear();
                var mapping = list.selectedItem as TrackingMapping;
                selectedKey = mapping?.key;
                if (mapping != null) MappingDetail(detail, mapping, Populate);
                else Text(detail, "候補がありません。検索条件や対象メッシュを確認してください。");
            }
            list.selectionChanged += values => Guard(Detail);
            void Populate()
            {
                var rows = profile.mappings.Where(m => (all.value || !string.IsNullOrEmpty(m.shape)) && (category == "すべて" || AvatarAnalysis.Category(m.key) == category) &&
                    (ExpressionGuide.Matches(m.key, search) || (!string.IsNullOrWhiteSpace(search) && (m.shape ?? "").IndexOf(search.Trim(), StringComparison.OrdinalIgnoreCase) >= 0))).ToList();
                string previous = selectedKey; list.itemsSource = rows; list.Rebuild();
                if (rows.Count > 0) list.SetSelectionWithoutNotify(new[] { Math.Max(0, rows.FindIndex(m => m.key == previous)) });
                else list.ClearSelection();
                int count = profile.mappings.Count(m => m.Included); next.text = "保存内容を確認（" + count + "件）"; next.SetEnabled(count > 0);
                Detail(); ExpressionListNavigation.RevealSelection(list);
            }
            query.RegisterValueChangedCallback(e => { search = e.newValue; Guard(Populate); }); filter.RegisterValueChangedCallback(e => { category = e.newValue; Guard(Populate); }); all.RegisterValueChangedCallback(e => Guard(Populate)); Populate();
            var diagnostics = Fold(content, "対象メッシュ・解析の詳細");
            var outer = content; try { content = diagnostics; Analysis(); } finally { content = outer; }
        }
        void MappingDetail(VisualElement parent, TrackingMapping mapping, Action refresh)
        {
            Guide(parent, mapping.key);
            if (!string.IsNullOrEmpty(mapping.shape)) Text(parent, mapping.shape, "shape-name");
            var plans = RecipeSources.MappedPlans(profile, mapping.shape);
            var plan = plans.FirstOrDefault();
            if (plans.Length > 1)
            {
                var mesh = new PopupField<string>("編集するメッシュ", plans.Select(p => string.IsNullOrEmpty(p.path) ? "ルート" : p.path).ToList(), 0);
                mesh.RegisterValueChangedCallback(e => plan = plans[mesh.index]); parent.Add(mesh);
            }
            var buttons = Row(parent); buttons.name = "mappingActions"; buttons.AddToClassList("mapping-actions");
            Button(buttons, string.IsNullOrEmpty(mapping.shape) ? "既存の表情を選ぶ" : "使う表情を変更", () => PickShape(null, mapping.shape, name => Change("マッピングを変更", () => { mapping.shape = name; mapping.confirmed = true; mapping.excluded = false; mapping.reason = "手動指定"; mapping.confidence = 0; })));
            var edit = Button(buttons, "表情を編集", () => EditMapping(mapping, plan)); edit.name = "editMappedExpression"; edit.SetEnabled(plan != null);
            edit.tooltip = plan != null ? "選択中の表情を引き継いで編集します。元のMeshは変更しません。" : "先に既存の表情を選んでください。";
            if (!string.IsNullOrEmpty(mapping.shape)) Button(buttons, mapping.excluded ? "除外を取り消す" : "この候補を除外", () => { Change("候補の除外を変更", () => mapping.excluded = !mapping.excluded); refresh(); });
            if (!string.IsNullOrEmpty(mapping.shape)) Portrait(parent, mapping.shape, null);
        }
        void EditMapping(TrackingMapping mapping, RendererPlan plan)
        {
            ShapeRecipe recipe = null;
            Change("選択中の表情を編集", () => recipe = RecipeSources.PrepareEditing(profile, mapping, plan));
            if (sceneEditor != null) sceneEditor.enabled = false;
            rendererIndex = profile.renderers.IndexOf(plan); recipeIndex = plan.recipes.IndexOf(recipe);
            selectedKey = targetKey = mapping.key; tab = 1; editingRecipe = editingFromSetup = true; CreateGUI();
        }
        void Guide(VisualElement parent, string key)
        {
            Text(parent, ExpressionGuide.Title(key), "mapping-title");
            var guide = Column(parent, "guide-row"); guide.Add(new ExpressionDiagram(key)); var words = Column(guide, "guide-text");
            Text(words, key ?? "任意の表情", "muted");
            Text(words, "左右はアバター基準", "muted");
            words.tooltip = ExpressionGuide.Description(key);
        }
        void Portrait(VisualElement parent, string shape, RendererPlan singlePlan)
        {
            var row = Column(parent, "portrait-row"); var image = new Image { scaleMode = ScaleMode.ScaleToFit }; image.AddToClassList("portrait"); row.Add(image);
            var controls = Column(row, "portrait-controls"); Text(controls, "アバターの実際の表情", "muted");
            bool before = false; float yaw = 0, zoom = 1;
            var choices = Column(controls, "choice-row");
            Button neutral = null, expression = null; Slider slider = null;
            neutral = Button(choices, "元の顔", () => { before = true; Render(); });
            expression = Button(choices, "選択中の表情", () => { before = false; Render(); });
            slider = new Slider("表示の強さ", 0, 100) { value = 100, showInputField = true }; controls.Add(slider);
            slider.tooltip = "表示の確認用です。生成設定や適用倍率は変わりません。";
            void Render()
            {
                EnsurePreview(); session.ResetWeights();
                if (!before) foreach (var p in profile.renderers.Where(p => singlePlan != null ? p == singlePlan : p.selected)) session.Weight(p.path, shape, slider.value);
                neutral.EnableInClassList("selected-choice", before); expression.EnableInClassList("selected-choice", !before);
                image.image = session.RenderPortrait(yaw, zoom); image.MarkDirtyRepaint();
            }
            slider.RegisterValueChangedCallback(e => Guard(Render));
            var angles = new PopupField<string>("向き", new List<string> { "正面", "右側から", "左側から" }, 0); angles.RegisterValueChangedCallback(e => { yaw = e.newValue == "正面" ? 0 : e.newValue == "右側から" ? 35 : -35; Guard(Render); }); controls.Add(angles);
            var magnification = new Slider("拡大", .5f, 1.8f) { value = zoom }; magnification.RegisterValueChangedCallback(e => { zoom = e.newValue; Guard(Render); }); controls.Add(magnification);
            Guard(Render);
        }
        void PickShape(RendererPlan plan, string current, Action<string> accept)
        {
            RememberScroll(); recipeRefresh?.Pause(); renderRecipePortrait = null;
            if (sceneEditor != null) sceneEditor.enabled = false; content.Clear(); Actions.Clear(); picker?.Dispose();
            picker = new ShapePicker(profile, plan, current, name => Guard(() => { accept(name); session?.Rebuild(); RefreshContent(); }), () => Guard(RefreshContent), plan != null && editingRecipe ? Recipe() : null); content.Add(picker); Actions.Add(picker.ActionBar);
        }
        void Apply()
        {
            Text(content, "保存内容を確認", "section");
            Button(content, "‹ 表情の確認に戻る", () => { setupStep = 0; RefreshContent(); });
            var included = profile.mappings.Where(m => m.Included).ToList();
            Text(content, included.Count + " 件の表情を設定したコピーを保存します。");
            int generated = profile.renderers.Sum(p => p.recipes.Count);
            Text(content, generated > 0 ? "追加のBlendShape: " + generated + " 件" : "既存のBlendShapeのみ使用");
            var settings = Fold(content, "VRChatとの連携設定");
            String(settings, "追加するSet名", profile.setName, v => profile.setName = v);
            Bool(settings, "すべてのTracking Keyを利用可能にする", profile.useAllKeys, v => profile.useAllKeys = v);
            Bool(settings, "既存VisemeをFace Trackingへ連携", profile.routeVisemes, v => profile.routeVisemes = v);
            Bool(settings, "EyelidsをFace Trackingへ連携", profile.routeEyelids, v => profile.routeEyelids = v);
            Bool(settings, "Eye LookをBothへ連携", profile.routeGaze, v => profile.routeGaze = v);
            Bool(settings, "音声と追跡の干渉設定を編集", profile.configureDampening, v => profile.configureDampening = v);
            var modes = new List<string> { "抑制しない", "音声時に追跡表情を弱める", "追跡時に音声Visemeを弱める" };
            var damp = new PopupField<string>("Viseme Dampen", modes, (int)profile.dampenMode); damp.RegisterValueChangedCallback(e => Change("Viseme Dampen", () => profile.dampenMode = (VRC.SDK3.Avatars.Components.VRCAvatarDescriptor.VisemeDampenMode)modes.IndexOf(e.newValue))); settings.Add(damp);
            Float(settings, "抑制の強さ", profile.dampenAmount, v => profile.dampenAmount = v);
            var changes = Fold(content, "変更点・競合を表示", true);
            foreach (string line in NativeSdk.Diff(profile, profile.sourceAvatar)) Text(changes, line);
            var issues = NativeSdk.Validate(profile, profile.sourceAvatar); foreach (string issue in issues) content.Add(new HelpBox(issue, HelpBoxMessageType.Error));
            bool reviewed = reviewedFingerprint == CurrentFingerprint();
            var save = Primary(reviewed ? "確認した内容でコピーを保存" : "変更後の内容を再確認", () =>
            {
                if (reviewedFingerprint != CurrentFingerprint()) { reviewedFingerprint = CurrentFingerprint(); RefreshContent(); return; }
                string path = BuilderStorage.Export(profile); setupStep = 2; Notify("保存しました: " + path); RefreshContent();
            }); save.SetEnabled(!reviewed || (issues.Count == 0 && included.Count > 0));
            Action invalidate = () => { reviewedFingerprint = null; save.text = "変更後の内容を再確認"; save.SetEnabled(true); };
            settings.RegisterCallback<ChangeEvent<bool>>(e => invalidate()); settings.RegisterCallback<ChangeEvent<string>>(e => invalidate()); settings.RegisterCallback<ChangeEvent<float>>(e => invalidate());
        }
        void Saved()
        {
            Text(content, "Face Trackingを設定したコピーを保存しました", "section"); Text(content, profile.lastExport);
            Primary("保存したPrefabを選択", () => Selection.activeObject = AssetDatabase.LoadAssetAtPath<GameObject>(profile.lastExport));
            Text(content, "次に、出力Prefabを専用シーンで開き、VRChat SDKのBuild & Testで動きを確認してください。機器による追跡や表情メニューとの組み合わせは実機で確認します。");
            Button(content, "対応付けを見直す", () => { setupStep = 0; RefreshContent(); });
            var optional = Fold(content, "さらに表情を追加したい場合（任意）"); Text(optional, "左右独立の視線や表情など、必要なものだけ追加できます。");
            Button(optional, "追加できる表情を見る", () => { tab = 1; editingRecipe = false; CreateGUI(); });
        }
    }
}
