using System;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;

namespace FaceTrackingBuilder
{
    public static class RecipeSources
    {
        public static RendererPlan[] MappedPlans(BuilderProfile profile, string shape)
        {
            if (string.IsNullOrEmpty(shape)) return Array.Empty<RendererPlan>();
            return profile.renderers.Where(p => p.selected && p.source && (p.source.GetBlendShapeIndex(shape) >= 0 || p.recipes.Any(r => r.name == shape))).ToArray();
        }
        public static ShapeRecipe PrepareEditing(BuilderProfile profile, TrackingMapping mapping, RendererPlan selected)
        {
            var plans = MappedPlans(profile, mapping.shape);
            if (!plans.Contains(selected)) throw new InvalidOperationException("編集する表情が対象メッシュにありません。候補と対象メッシュを確認してください。");
            var existing = selected.recipes.FirstOrDefault(r => r.name == mapping.shape);
            bool shared = profile.mappings.Any(m => m != mapping && m.shape == mapping.shape);
            if (existing != null && !shared && (string.IsNullOrEmpty(existing.trackingKey) || existing.trackingKey == mapping.key))
            { existing.trackingKey = mapping.key; return existing; }
            string original = mapping.shape, baseName = "FTB_Edit_" + mapping.key, name = baseName; int suffix = 1;
            while (profile.renderers.Any(p => p.source.GetBlendShapeIndex(name) >= 0 || p.recipes.Any(r => r.name == name))) name = baseName + "_" + suffix++;
            var copies = new Dictionary<RendererPlan, ShapeRecipe>();
            foreach (var plan in plans)
            {
                var renderer = AvatarAnalysis.Resolve(profile.sourceAvatar, plan.path);
                var frames = Frames(plan, original).Where(w => w > 0).Distinct().OrderBy(w => w).ToList();
                copies.Add(plan, new ShapeRecipe { name = name, trackingKey = mapping.key, mode = GenerateMode.Combine,
                    sources = new List<ShapeSource> { new ShapeSource { name = original } }, frames = frames.Count > 0 ? frames : new List<float> { 100 },
                    planeRight = renderer.transform.worldToLocalMatrix.MultiplyVector(profile.sourceAvatar.transform.right).normalized,
                    planeOrigin = renderer.transform.InverseTransformPoint(profile.sourceAvatar.transform.position), regionCenter = plan.source.bounds.center });
            }
            foreach (var pair in copies) pair.Key.recipes.Add(pair.Value);
            mapping.shape = name; mapping.confirmed = false; mapping.reason = "選択した表情の編集用コピー";
            return copies[selected];
        }
        public static ShapeRecipe Effective(RendererPlan plan, ShapeRecipe recipe)
        {
            if (string.IsNullOrEmpty(recipe.mirrorOf)) return recipe;
            var source = plan.recipes.SingleOrDefault(r => r.id == recipe.mirrorOf);
            if (source == null) throw new InvalidOperationException("連動元の表情がありません: " + recipe.name);
            if (!string.IsNullOrEmpty(source.mirrorOf)) throw new InvalidOperationException("連動ミラー同士は参照できません: " + recipe.name);
            return new ShapeRecipe { id = recipe.id, name = recipe.name, trackingKey = recipe.trackingKey, mode = GenerateMode.Mirror,
                sources = new List<ShapeSource> { new ShapeSource { name = source.name } }, frames = source.frames,
                planeOrigin = source.planeOrigin, planeRight = source.planeRight, mirrorTolerance = source.mirrorTolerance };
        }
        public static IEnumerable<string> Inputs(RendererPlan plan, ShapeRecipe recipe)
        {
            var effective = Effective(plan, recipe);
            return effective.mode == GenerateMode.Landmarks ? Enumerable.Empty<string>() : effective.sources.Select(s => s.name);
        }
        public static List<ShapeRecipe> Ordered(RendererPlan plan, string target = null)
        {
            var originals = new HashSet<string>(Enumerable.Range(0, plan.source.blendShapeCount).Select(plan.source.GetBlendShapeName));
            var recipes = new Dictionary<string, ShapeRecipe>();
            foreach (var r in plan.recipes)
            {
                if (string.IsNullOrWhiteSpace(r.name) || originals.Contains(r.name) || recipes.ContainsKey(r.name))
                    throw new InvalidOperationException("生成名が空欄、既存BlendShapeと同名、または生成設定内で重複しています: " + r.name);
                recipes.Add(r.name, r);
            }
            var ordered = new List<ShapeRecipe>(); var visiting = new HashSet<string>(); var done = new HashSet<string>();
            void Visit(string name)
            {
                if (name != null && (originals.Contains(name) || done.Contains(name))) return;
                if (name == null || !recipes.TryGetValue(name, out var r)) throw new InvalidOperationException("合成元BlendShapeがありません: " + name);
                if (!visiting.Add(name)) throw new InvalidOperationException("生成設定が循環しています。合成元を変更してください: " + name);
                foreach (string input in Inputs(plan, r)) Visit(input);
                visiting.Remove(name); done.Add(name); ordered.Add(Effective(plan, r));
            }
            if (target != null) Visit(target); else foreach (var r in plan.recipes) Visit(r.name);
            return ordered;
        }
        public static bool DependsOn(RendererPlan plan, ShapeRecipe candidate, ShapeRecipe source)
        {
            var visited = new HashSet<ShapeRecipe>();
            bool Visit(ShapeRecipe r)
            {
                if (r == source) return true;
                if (!visited.Add(r)) return false;
                return Inputs(plan, r).Any(name => plan.recipes.Any(p => p.name == name && Visit(p)));
            }
            return Visit(candidate);
        }
        public static string[] Available(RendererPlan plan, ShapeRecipe editing = null)
        {
            return Enumerable.Range(0, plan.source.blendShapeCount).Select(plan.source.GetBlendShapeName)
                .Concat(plan.recipes.Where(r => editing == null || !DependsOn(plan, r, editing)).Select(r => r.name)).Distinct().ToArray();
        }
        public static IEnumerable<float> Frames(RendererPlan plan, string name)
        {
            var recipe = plan.recipes.FirstOrDefault(r => r.name == name);
            if (recipe != null) return Effective(plan, recipe).frames;
            int index = plan.source.GetBlendShapeIndex(name);
            if (index < 0) throw new InvalidOperationException("合成元BlendShapeがありません: " + name);
            return Enumerable.Range(0, plan.source.GetBlendShapeFrameCount(index)).Select(f => plan.source.GetBlendShapeFrameWeight(index, f));
        }
        public static void Rename(BuilderProfile profile, RendererPlan plan, ShapeRecipe recipe, string name)
        {
            if (name == recipe.name) return;
            if (string.IsNullOrWhiteSpace(name) || plan.source.GetBlendShapeIndex(name) >= 0 || plan.recipes.Any(r => r != recipe && r.name == name))
                throw new InvalidOperationException("空欄や重複した名前は使用できません。");
            string old = recipe.name; recipe.name = name;
            foreach (var r in plan.recipes) foreach (var input in r.sources.Where(s => s.name == old)) input.name = name;
            foreach (var m in profile.mappings.Where(m => m.shape == old)) { m.shape = name; m.confirmed = false; }
        }
        public static void Remove(BuilderProfile profile, RendererPlan plan, ShapeRecipe recipe)
        {
            var users = plan.recipes.Where(r => r != recipe && DependsOn(plan, r, recipe)).Select(r => r.name).ToArray();
            if (users.Length > 0) throw new InvalidOperationException("この表情を合成元に使っています。先に参照先の合成元を変更するか、生成設定を除外してください: " + string.Join(", ", users));
            plan.recipes.Remove(recipe);
            if (!profile.renderers.Any(p => p.recipes.Any(r => r.name == recipe.name) || p.source.GetBlendShapeIndex(recipe.name) >= 0))
                foreach (var m in profile.mappings.Where(m => m.shape == recipe.name)) { m.shape = ""; m.confirmed = false; }
        }
        public static string OppositeKey(string key)
        {
            if (string.IsNullOrEmpty(key)) return null;
            string opposite = key.EndsWith("Right", StringComparison.Ordinal) ? key.Substring(0, key.Length - 5) + "Left"
                : key.EndsWith("Left", StringComparison.Ordinal) ? key.Substring(0, key.Length - 4) + "Right" : null;
            return AvatarAnalysis.Keys.Contains(opposite) ? opposite : null;
        }
        public static string PairBlock(BuilderProfile profile, RendererPlan plan, ShapeRecipe source, string key)
        {
            string opposite = OppositeKey(key);
            if (opposite == null) return "左右の組がない表情です。";
            if (!string.IsNullOrEmpty(source.mirrorOf)) return "連動元の表情から操作してください。";
            if (profile.mappings.Any(m => m.key == opposite && !string.IsNullOrEmpty(m.shape)) ||
                plan.recipes.Any(r => r.trackingKey == opposite) || plan.source.GetBlendShapeIndex(opposite) >= 0)
                return "反対側には既に表情があります。必要なら合成元を手動で変更してください。";
            return null;
        }
        public static ShapeRecipe CreatePair(BuilderProfile profile, RendererPlan plan, ShapeRecipe source, string key)
        {
            var existing = plan.recipes.FirstOrDefault(r => r.mirrorOf == source.id);
            if (existing != null) return existing;
            string blocked = PairBlock(profile, plan, source, key);
            if (blocked != null) throw new InvalidOperationException(blocked);
            string opposite = OppositeKey(key), name = "FTB_" + opposite; int suffix = 1;
            while (plan.source.GetBlendShapeIndex(name) >= 0 || plan.recipes.Any(r => r.name == name)) name = "FTB_" + opposite + "_" + suffix++;
            var pair = new ShapeRecipe { name = name, trackingKey = opposite, mirrorOf = source.id, mode = GenerateMode.Mirror };
            plan.recipes.Add(pair);
            var row = profile.mappings.FirstOrDefault(m => m.key == opposite);
            if (row == null) { row = new TrackingMapping { key = opposite }; profile.mappings.Add(row); }
            row.shape = pair.name; row.confirmed = false; row.excluded = false; row.reason = "反対側の表情からミラー生成";
            return pair;
        }
        public static void Detach(RendererPlan plan, ShapeRecipe recipe)
        {
            var effective = Effective(plan, recipe);
            recipe.mode = effective.mode; recipe.sources = effective.sources; recipe.frames = new List<float>(effective.frames);
            recipe.planeOrigin = effective.planeOrigin; recipe.planeRight = effective.planeRight; recipe.mirrorTolerance = effective.mirrorTolerance;
            recipe.mirrorOf = null;
        }
    }
}
