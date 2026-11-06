using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using UnityEngine;
using UnityEngine.UIElements;
using UnityEditor.UIElements;

namespace FaceTrackingBuilder
{
    public partial class FaceTrackingBuilderWindow
    {
        string recipePreviewed;
        void Advanced()
        {
            Text(content, "表情の追加・編集（任意）", "section");
            SelectRenderer(); var plan = Plan();
            if (plan.recipes.Count > 0)
            {
                var saved = Fold(content, "作成済みの表情（" + plan.recipes.Count + "）");
                for (int i = 0; i < plan.recipes.Count; i++)
                {
                    int index = i; var r = plan.recipes[i]; string key = RecipeKey(r);
                    Button(saved, (string.IsNullOrEmpty(key) ? r.name : ExpressionGuide.Title(key)) + "  › 編集", () => { recipeIndex = index; editingRecipe = true; editingFromSetup = false; RefreshContent(); });
                }
            }
            var columns = Column(content, "columns"); var left = Column(columns, "browser-column"); var right = Column(columns, "detail-column");
            var query = new ToolbarSearchField(); left.Add(query); var categoryField = new PopupField<string>("部位", Categories.ToList(), 0); left.Add(categoryField);
            var list = new ListView { selectionType = SelectionType.Single }; list.AddToClassList("key-list"); left.Add(list);
            list.makeItem = () => new Label(); list.bindItem = (item, i) => ((Label)item).text = ExpressionGuide.Title((string)list.itemsSource[i]);
            void Detail()
            {
                right.Clear(); Actions.Clear(); if (string.IsNullOrEmpty(targetKey)) { Text(right, "一致する表情がありません。"); return; }
                Guide(right, targetKey); var suggestion = ExpressionRecipes.Suggest(plan, targetKey);
                var mapped = profile.mappings.FirstOrDefault(m => m.key == targetKey);
                if (!string.IsNullOrEmpty(mapped?.shape)) Text(right, "割り当て: " + mapped.shape);
                Text(right, suggestion != null ? suggestion.explanation : "BlendShapeやブラシから作成");
                if (mapped != null && !string.IsNullOrEmpty(mapped.shape))
                {
                    var strength = Fold(right, "適用時の強さ");
                    Float(strength, "動きの倍率（通常は1）", mapped.multiplier, v => mapped.multiplier = v);
                }
                Primary(suggestion != null ? "この方法で作成・編集" : "この表情を手動で作成・編集", () => { AddRecipe(targetKey); editingRecipe = true; RefreshContent(); });
            }
            list.selectionChanged += values => Guard(() => { targetKey = values.OfType<string>().FirstOrDefault(); Detail(); });
            void Populate()
            {
                var keys = AvatarAnalysis.Keys.Where(k => ExpressionGuide.Matches(k, query.value) && (categoryField.value == "すべて" || AvatarAnalysis.Category(k) == categoryField.value)).ToList();
                string previous = targetKey; list.itemsSource = keys; list.Rebuild();
                if (keys.Count > 0) { int index = Math.Max(0, keys.IndexOf(previous)); list.SetSelectionWithoutNotify(new[] { index }); targetKey = keys[index]; }
                else targetKey = null;
                Detail();
            }
            query.RegisterValueChangedCallback(e => Populate()); categoryField.RegisterValueChangedCallback(e => Populate()); Populate();
            var free = Fold(content, "Tracking Keyを決めずに作成"); Button(free, "自由な表情の作成を始める", () => { AddRecipe(); editingRecipe = true; RefreshContent(); });
        }
        string RecipeKey(ShapeRecipe r) => !string.IsNullOrEmpty(r.trackingKey) ? r.trackingKey : profile.mappings.FirstOrDefault(m => m.shape == r.name)?.key
            ?? AvatarAnalysis.Keys.FirstOrDefault(k => r.name == "FTB_" + k || r.name.StartsWith("FTB_" + k + "_", StringComparison.Ordinal));
        void AddRecipe(string key = null)
        {
            editingFromSetup = false;
            var p = Plan(); string mappedName = profile.mappings.FirstOrDefault(m => m.key == key)?.shape;
            var existing = key == null ? null : p.recipes.FirstOrDefault(r => r.name == mappedName) ?? p.recipes.FirstOrDefault(r => RecipeKey(r) == key);
            if (existing != null) { recipeIndex = p.recipes.IndexOf(existing); return; }
            string name = key == null ? "FTB_Expression" : "FTB_" + key, baseName = name; int suffix = 1;
            while (p.source.GetBlendShapeIndex(name) >= 0 || p.recipes.Any(r => r.name == name)) name = baseName + "_" + suffix++;
            var sourceRenderer = AvatarAnalysis.Resolve(profile.sourceAvatar, p.path);
            var recipe = new ShapeRecipe { name = name, trackingKey = key,
                planeRight = sourceRenderer.transform.worldToLocalMatrix.MultiplyVector(profile.sourceAvatar.transform.right).normalized,
                planeOrigin = sourceRenderer.transform.InverseTransformPoint(profile.sourceAvatar.transform.position), regionCenter = p.source.bounds.center };
            var suggestion = ExpressionRecipes.Suggest(p, key);
            if (suggestion != null) { recipe.mode = suggestion.mode; recipe.sources.Add(new ShapeSource { name = suggestion.source }); }
            Change("生成設定を追加", () => { p.selected = true; p.recipes.Add(recipe); }); recipeIndex = p.recipes.Count - 1;
            if (key != null) Change("生成先を割り当て", () => { var row = profile.mappings.First(m => m.key == key); row.shape = name; row.confirmed = false; row.excluded = false; row.reason = "生成した表情。動きを確認してください"; });
        }
        void RemoveRecipeButton(VisualElement parent, RendererPlan plan, ShapeRecipe recipe)
        {
            Button(parent, "この生成設定を除外（Undo可能）", () =>
            {
                Change("生成設定を除外", () => RecipeSources.Remove(profile, plan, recipe));
                if (sceneEditor != null) sceneEditor.enabled = false; session?.Rebuild(); editingRecipe = false; RefreshContent();
            });
        }
        void MirrorPairOption(VisualElement parent, RendererPlan plan, ShapeRecipe recipe, string key)
        {
            string opposite = RecipeSources.OppositeKey(key); if (opposite == null) return;
            var pair = plan.recipes.FirstOrDefault(r => r.mirrorOf == recipe.id);
            var toggle = new Toggle("反対側も作る（" + ExpressionGuide.Title(opposite) + "）") { name = "createMirrorPair", value = pair != null };
            string blocked = pair == null ? RecipeSources.PairBlock(profile, plan, recipe, key) : null;
            toggle.SetEnabled(blocked == null); toggle.tooltip = blocked ?? "片側の変形をミラーし、別のBlendShapeとして作ります。"; parent.Add(toggle);
            if (blocked != null) Text(parent, blocked, "muted");
            toggle.RegisterValueChangedCallback(e => Guard(() =>
            {
                try
                {
                    Change("反対側の表情", () => { if (e.newValue) RecipeSources.CreatePair(profile, plan, recipe, key); else RecipeSources.Remove(profile, plan, pair); });
                    session?.Rebuild();
                }
                finally { RefreshContent(); }
            }));
            if (pair != null)
            {
                Text(parent, "元の表情を直すと、反対側も更新されます。", "muted");
                Button(parent, "反対側を確認", () => { if (sceneEditor != null) sceneEditor.enabled = false; recipeIndex = plan.recipes.IndexOf(pair); EnsurePreview(); session.Rebuild(); recipePreviewed = CurrentFingerprint(); RefreshContent(); });
            }
        }
    }
}
