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
    public partial class FaceTrackingBuilderWindow
    {
        IVisualElementScheduledItem recipeRefresh;
        Action renderRecipePortrait;
        HelpBox recipeError;
        Label recipeDiagnostics;
        Button useRecipe;
        void QueueRecipePreview()
        {
            if (tab != 1 || !editingRecipe || renderRecipePortrait == null || content == null) return;
            recipeRefresh?.Pause();
            recipeRefresh = content.schedule.Execute(() => BuildRecipePreview()); recipeRefresh.ExecuteLater(80);
        }
        bool BuildRecipePreview()
        {
            if (!profile || tab != 1 || !editingRecipe) return false;
            try
            {
                var r = Recipe(); if (r == null) return false;
                bool before = session != null && session.Comparing;
                EnsurePreview();
                if (recipePreviewed != CurrentFingerprint()) { session.Rebuild(); sceneEditor?.Invalidate(); }
                session.ResetWeights(); session.Weight(Plan().path, r.name, 100); session.Compare(before);
                if (sceneEditor != null)
                {
                    if (sceneEditor.recipe != r) { sceneEditor.enabled = false; sceneEditor.Invalidate(); }
                    sceneEditor.plan = Plan(); sceneEditor.recipe = r;
                    if (!string.IsNullOrEmpty(r.mirrorOf)) sceneEditor.enabled = false;
                }
                recipePreviewed = CurrentFingerprint();
                recipeError?.AddToClassList("hidden"); useRecipe?.SetEnabled(true);
                if (recipeDiagnostics != null) recipeDiagnostics.text = session.Diagnostics;
                renderRecipePortrait?.Invoke(); return true;
            }
            catch (Exception ex)
            {
                recipePreviewed = null; useRecipe?.SetEnabled(false);
                if (sceneEditor != null) sceneEditor.enabled = false;
                if (recipeError != null) { recipeError.text = "表示できません: " + ex.Message; recipeError.RemoveFromClassList("hidden"); }
                return false;
            }
        }
        void Generation()
        {
            var p = Plan(); var r = Recipe(); if (r == null) return;
            Button(content, editingFromSetup ? "‹ セットアップへ戻る" : "‹ 追加する表情の一覧へ", () =>
            {
                editingRecipe = false; if (sceneEditor != null) sceneEditor.enabled = false;
                if (editingFromSetup) { setupStep = tab = 0; CreateGUI(); } else RefreshContent();
            });
            string key = RecipeKey(r); Guide(content, key);
            bool linked = !string.IsNullOrEmpty(r.mirrorOf);
            var editing = Column(content, "editing-tools"); editing.name = "editingTools";
            var editingActions = Column(editing, "editing-actions"); editingActions.name = "editingActions";
            var choices = Column(editingActions, "choice-row"); choices.name = "editingMethods";
            var brush = Button(editingActions, "形状を編集", StartEditing); brush.name = "openBrushEditor"; brush.AddToClassList("brush-entry"); brush.SetEnabled(!linked);
            brush.text = "";
            var editTitle = new Label("形状を編集") { pickingMode = PickingMode.Ignore }; brush.Add(editTitle);
            var editIcon = new BuilderIconElement(BuilderIcon.Brush) { name = "shapeEditIcon" }; brush.Add(editIcon);
            brush.tooltip = "編集ウィンドウでブラシや頂点選択を使って形を変更します。";
            if (linked) brush.tooltip = "元の表情を編集するか、ミラー設定を個別に編集してください。";
            if (linked)
            {
                var original = p.recipes.FirstOrDefault(x => x.id == r.mirrorOf);
                Text(editing, "連動ミラー: " + original?.name);
                if (original != null) Button(choices, "元の表情を編集", () => { if (sceneEditor != null) sceneEditor.enabled = false; recipeIndex = p.recipes.IndexOf(original); RefreshContent(); });
                Button(choices, "個別に編集", () => { Change("ミラー設定を個別化", () => RecipeSources.Detach(p, r)); RefreshContent(); }).tooltip = "元の表情は入力として残ります。ブラシで追加補正できます。";
            }
            else
            {
                bool fromShapes = r.mode != GenerateMode.Landmarks;
                var from = Button(choices, "BlendShapeから編集", () => { if (r.mode == GenerateMode.Landmarks) Change("BlendShapeから編集", () => r.mode = GenerateMode.Combine); RefreshContent(); });
                from.name = "editFromBlendShapes"; from.EnableInClassList("selected-choice", fromShapes);
                var blank = Button(choices, "元の顔から作る", () => { Change("元の顔から作成", () => r.mode = GenerateMode.Landmarks); RefreshContent(); });
                blank.name = "editFromNeutral"; blank.EnableInClassList("selected-choice", !fromShapes);
                blank.tooltip = "既存のBlendShapeを使わず、元の顔をベースに編集します。形状の編集内容は保持します。";
                if (fromShapes)
                {
                    Text(editing, "選んだBlendShapeと強さが生成結果に反映されます。", "muted");
                    foreach (var input in r.sources.ToArray()) SourceWeight(editing, p, input);
                    Button(editing, "BlendShapeを選んで追加", () => PickShape(p, null, value => Change("BlendShapeを追加", () => r.sources.Add(new ShapeSource { name = value }))));
                    var operations = new List<string> { "なし", "左側だけ使う", "右側だけ使う", "範囲を絞る", "ミラー" };
                    var modes = new[] { GenerateMode.Combine, GenerateMode.SplitLeft, GenerateMode.SplitRight, GenerateMode.Extract, GenerateMode.Mirror };
                    var operation = new PopupField<string>("追加操作", operations, Math.Max(0, Array.IndexOf(modes, r.mode))) { name = "sourceOperation" };
                    operation.RegisterValueChangedCallback(e => { Change("追加操作", () => r.mode = modes[operation.index]); RefreshContent(); }); editing.Add(operation);
                }
                if (r.landmarks.Count > 0) Text(editing, "以前の特徴点による変形を保持しています。", "muted");
                MirrorPairOption(editing, p, r, key);
            }
            RecipePortrait(p, r);
            var geometry = Fold(content, "生成の詳細（中心面・範囲・フレーム）");
            if (!linked) RecipeDetails(geometry, p, r);
            recipeDiagnostics = new Label(); recipeDiagnostics.AddToClassList("muted"); geometry.Add(recipeDiagnostics);
            RemoveRecipeButton(geometry, p, r);
            recipeError = new HelpBox("", HelpBoxMessageType.Error) { name = "recipeError" }; recipeError.AddToClassList("hidden"); content.Add(recipeError);
            useRecipe = Primary("この表情を使用してセットアップへ", () =>
            {
                if (!BuildRecipePreview()) return;
                Change("生成した表情を使用", () =>
                {
                    if (!string.IsNullOrEmpty(key)) { var m = profile.mappings.First(x => x.key == key); m.shape = r.name; m.confirmed = true; m.excluded = false; m.reason = "生成結果を確認済み"; }
                });
                BuilderStorage.SaveProfile(profile); selectedKey = key; setupStep = tab = 0; if (sceneEditor != null) sceneEditor.enabled = false; CreateGUI();
            });
            BuildRecipePreview();
        }
        void SourceWeight(VisualElement parent, RendererPlan plan, ShapeSource input)
        {
            var card = Card(parent); var row = Row(card);
            Button(row, input.name + "  › 選び直す", () => PickShape(plan, input.name, value => Change("BlendShapeを変更", () => input.name = value))).AddToClassList("grow");
            Button(row, "外す", () => { Change("BlendShapeを除外", () => Recipe().sources.Remove(input)); RefreshContent(); });
            var weight = Row(card); float value = input.coefficient * 100;
            var slider = new Slider("強さ (%)", Mathf.Min(0, value), Mathf.Max(100, value)) { name = "sourceWeightSlider", value = value }; slider.AddToClassList("source-slider"); weight.Add(slider);
            var number = new FloatField { name = "sourceWeightNumber", value = value, isDelayed = true }; number.AddToClassList("source-percent"); weight.Add(number);
            bool syncing = false;
            void Set(float percent)
            {
                if (syncing) return;
                if (float.IsNaN(percent) || float.IsInfinity(percent)) { number.SetValueWithoutNotify(input.coefficient * 100); return; }
                syncing = true;
                try
                {
                    Change("BlendShapeの強さ", () => input.coefficient = percent / 100);
                    slider.SetValueWithoutNotify(Mathf.Clamp(percent, slider.lowValue, slider.highValue));
                    slider.lowValue = Mathf.Min(0, percent); slider.highValue = Mathf.Max(100, percent);
                    slider.SetValueWithoutNotify(percent); number.SetValueWithoutNotify(percent);
                }
                finally { syncing = false; }
            }
            slider.RegisterValueChangedCallback(e => Set(e.newValue)); number.RegisterValueChangedCallback(e => Set(e.newValue));
        }
        void RecipePortrait(RendererPlan plan, ShapeRecipe recipe)
        {
            var row = Column(content, "portrait-row"); var image = new Image { name = "recipePortrait", scaleMode = ScaleMode.ScaleToFit }; image.AddToClassList("portrait"); row.Add(image);
            var controls = Column(row, "portrait-controls"); Text(controls, "生成結果", "section");
            var choices = Column(controls, "choice-row");
            var neutral = Button(choices, "元の顔", () => { EnsurePreview(); sceneEditor.Compare(true); }); neutral.name = "recipeNeutral";
            var expression = Button(choices, "作成中の表情", () => { EnsurePreview(); sceneEditor.Compare(false); }); expression.name = "recipeExpression";
            var state = new Label { name = "recipeDisplayState" }; state.AddToClassList("wrap"); controls.Add(state);
            renderRecipePortrait = () =>
            {
                if (session?.Clone == null || image.panel == null && rootVisualElement.panel != null) return;
                neutral.EnableInClassList("selected-choice", session.Comparing); expression.EnableInClassList("selected-choice", !session.Comparing);
                state.text = session.Comparing ? "元の顔を表示中" : "作成中の表情を表示中";
                image.image = session.RenderPortrait(); image.MarkDirtyRepaint(); sceneEditor?.Controls?.Refresh();
            };
        }
        void RecipeDetails(VisualElement geometry, RendererPlan p, ShapeRecipe r)
        {
            String(geometry, "保存するBlendShape名", r.name, v => RecipeSources.Rename(profile, p, r, v));
            String(geometry, "フレーム（例: 50,100）", string.Join(",", r.frames.Select(v => v.ToString(CultureInfo.InvariantCulture))), v => r.frames = v.Split(',').Select(s => float.Parse(s, CultureInfo.InvariantCulture)).ToList());
            Button(geometry, "入力の全フレームを使用", () => { Change("全フレームを使用", () => r.frames = r.sources.SelectMany(s => RecipeSources.Frames(p, s.name)).Where(w => w > 0).Distinct().OrderBy(w => w).ToList()); RefreshContent(); }).SetEnabled(r.mode != GenerateMode.Landmarks && r.sources.Count > 0);
            Vec(geometry, "中心面上の点", r.planeOrigin, v => r.planeOrigin = v); Vec(geometry, "顔の右方向", r.planeRight, v => r.planeRight = v);
            Float(geometry, "中心のぼかし幅", r.feather, v => r.feather = Mathf.Max(0, v));
            if (r.mode == GenerateMode.Extract) { Vec(geometry, "抽出領域の中心", r.regionCenter, v => r.regionCenter = v); Float(geometry, "抽出半径", r.regionRadius, v => r.regionRadius = Mathf.Max(.00001f, v)); }
            if (r.mode == GenerateMode.Mirror || p.recipes.Any(x => x.mirrorOf == r.id)) Float(geometry, "対応頂点の許容距離", r.mirrorTolerance, v => { r.mirrorTolerance = Mathf.Max(.000001f, v); sceneEditor?.Invalidate(); });
            Bool(geometry, "法線・接線の差分を再計算", r.recalculateSurface, v => r.recalculateSurface = v);
        }
    }
}
