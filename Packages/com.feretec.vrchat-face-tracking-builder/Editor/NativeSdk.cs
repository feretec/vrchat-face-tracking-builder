using System;
using System.Collections.Generic;
using System.Linq;
using UnityEditor;
using UnityEngine;
using VRC.SDK3.Avatars.Components;
using VRC.SDKBase;
using VRC.SDKBase.Validation.Performance;
using VRC.SDKBase.Validation.Performance.Stats;

namespace FaceTrackingBuilder
{
    public static class NativeSdk
    {
        public static string Performance(GameObject avatar)
        {
            var stats = new AvatarPerformanceStats(false);
            AvatarPerformance.CalculatePerformanceStats(avatar.name, avatar, stats, false);
            return "SDK計測（PC） Face Tracking Operations: " + stats.faceTrackingOperations +
                   " / Forwarded Parameters: " + stats.forwardedParametersCount +
                   " / FT Rank: " + stats.GetPerformanceRatingForCategory(AvatarPerformanceCategory.FaceTrackingOperations) +
                   " / Overall Rank: " + stats.GetPerformanceRatingForCategory(AvatarPerformanceCategory.Overall);
        }
        public static List<string> Validate(BuilderProfile p, GameObject target)
        {
            var issues = new List<string>();
            var selected = p.renderers.Where(r => r.selected).ToArray();
            if (selected.Length == 0) issues.Add("対象メッシュがありません。");
            var d = target.GetComponent<VRCAvatarDescriptor>();
            var existingMeshes = d?.faceTrackingMeshes ?? new List<SkinnedMeshRenderer>();
            int meshes = existingMeshes.Count + selected.Select(r => AvatarAnalysis.Resolve(target, r.path)).Where(r => !existingMeshes.Contains(r)).Distinct().Count();
            if (meshes > 16) issues.Add("Face Tracking Meshesの上限16を超えています。");
            if (string.IsNullOrWhiteSpace(p.setName)) issues.Add("Set名を指定してください。");
            // Never overwrite somebody else's set, even if it happens to use our default name.
            if (d && d.faceTrackingBlendshapesSetList != null && d.faceTrackingBlendshapesSetList.Any(s => s.setName == p.setName)) issues.Add("元アバターに同じSet名があります。別のSet名を指定してください。");
            var names = new HashSet<string>(AvatarAnalysis.Shapes(p));
            foreach (var row in p.mappings.Where(m => m.Included))
            {
                if (!names.Contains(row.shape ?? "")) issues.Add(row.key + ": 割り当てたBlendShapeが対象メッシュにありません。");
                if (!AvatarAnalysis.Keys.Contains(row.key)) issues.Add("SDKに存在しないTracking Key: " + row.key);
                if (float.IsNaN(row.multiplier) || float.IsInfinity(row.multiplier) || row.multiplier < 0 || row.multiplier > 10) issues.Add(row.key + ": 倍率は0〜10にしてください。");
                if (!p.useAllKeys && !row.key.StartsWith("EyeLook") && !row.key.StartsWith("EyeClosed")) issues.Add(row.key + ": Use All Tracking Keysが必要です。");
            }
            Func<string, bool> assigned = k => p.mappings.Any(m => m.key == k && m.Included && names.Contains(m.shape ?? ""));
            if (p.routeEyelids && (!assigned("EyeClosedLeft") || !assigned("EyeClosedRight"))) issues.Add("Eyelids連携には左右のEyeClosedの割り当てが必要です。");
            if (p.routeGaze && AvatarAnalysis.Keys.Where(k => k.StartsWith("EyeLook")).Any(k => !assigned(k))) issues.Add("Eye Look連携には8つのEyeLookキーの割り当てが必要です。");
            if (p.routeVisemes && (!d || (d.lipSync != VRC_AvatarDescriptor.LipSyncStyle.VisemeBlendShape && d.lipSync != VRC_AvatarDescriptor.LipSyncStyle.VisemeBlendShapeFaceTracking))) issues.Add("LipSync連携は既存Viseme Blend Shape設定がある場合に利用できます。");
            if (p.routeVisemes && d && d.lipSync == VRC_AvatarDescriptor.LipSyncStyle.VisemeBlendShape && (d.VisemeBlendShapes == null || d.VisemeBlendShapes.Any(n => !string.IsNullOrEmpty(n) && !names.Contains(n)))) issues.Add("既存Visemeの参照先を対象メッシュに含めてください。");
            if (p.configureDampening && (!p.useAllKeys || (!p.routeVisemes && (!d || d.lipSync != VRC_AvatarDescriptor.LipSyncStyle.VisemeBlendShapeFaceTracking)))) issues.Add("音声との干渉設定にはUse All Tracking KeysとFace Tracking LipSyncが必要です。");
            if (p.configureDampening && (float.IsNaN(p.dampenAmount) || p.dampenAmount < 0 || p.dampenAmount > 1)) issues.Add("抑制の強さは0〜1にしてください。");
            return issues;
        }
        public static List<string> Diff(BuilderProfile p, GameObject root)
        {
            var d = root.GetComponent<VRCAvatarDescriptor>();
            var result = new List<string> { "保存先: 専用フォルダ内の新しいPrefabとMesh。元アバターは変更しません。", "Face Tracking: " + (d && d.enableFaceTracking) + " → 有効", "Use All Tracking Keys: " + (d && d.useAllTrackingKeys) + " → " + p.useAllKeys, "追加Set: " + p.setName + " / 既存Set・Parameter Behaviors・Animator・ExMenuを保持" };
            result.AddRange(p.renderers.Where(r => r.selected).Select(r => "登録Mesh: " + r.path + " / 追加BlendShape " + r.recipes.Count));
            result.AddRange(p.mappings.Where(m => m.Included).Select(m => m.key + " → " + m.shape + " × " + m.multiplier));
            if (p.routeVisemes) result.Add("LipSync → Viseme Blend Shape Face Tracking（既存15音素をコピー）");
            if (p.routeEyelids) result.Add("Eyelids → Face Tracking / Eye Look有効化");
            if (p.routeGaze) result.Add("Eye Gaze → Both（既存の眼球ボーン設定を保持） / Eye Look有効化");
            if (p.configureDampening) result.Add("Viseme Dampen: " + (d ? d.visemeDampenMode.ToString() : "なし") + " → " + p.dampenMode + " / 強さ " + p.dampenAmount);
            foreach (var group in p.mappings.Where(m => m.Included).GroupBy(m => m.shape).Where(g => g.Count() > 1)) result.Add("要確認: " + group.Key + " を共有: " + string.Join(", ", group.Select(m => m.key)) + "。SDKでは最大値が適用されます。");
            foreach (string name in p.mappings.Where(m => m.Included).Select(m => m.shape).Distinct())
            {
                var paths = p.renderers.Where(r => r.selected && (r.source.GetBlendShapeIndex(name) >= 0 || r.recipes.Any(x => x.name == name))).Select(r => r.path).ToArray();
                if (paths.Length > 1) result.Add("同名を全Meshで駆動: " + name + " → " + string.Join(", ", paths));
                var selectedPaths = new HashSet<string>(p.renderers.Where(r => r.selected).Select(r => r.path));
                if (d?.faceTrackingMeshes != null) foreach (var existing in d.faceTrackingMeshes.Where(r => r && r.sharedMesh && r.sharedMesh.GetBlendShapeIndex(name) >= 0))
                {
                    string path = AnimationUtility.CalculateTransformPath(existing.transform, root.transform);
                    if (!selectedPaths.Contains(path)) result.Add("既存登録Meshも駆動されます: " + path + " / " + name);
                }
            }
            var driving = new HashSet<string>(p.mappings.Where(m => m.Included).Select(m => m.shape));
            foreach (var recipe in p.renderers.SelectMany(r => r.recipes).Where(r => driving.Contains(r.name))) foreach (var source in recipe.sources) driving.Add(source.name);
            var conflicts = AvatarAnalysis.AnimatorConflicts(root, driving);
            result.AddRange(conflicts.Select(c => "Animator競合候補: " + c));
            if (d && !p.routeEyelids && d.customEyeLookSettings.eyelidType != VRCAvatarDescriptor.EyelidType.None && p.mappings.Any(m => m.Included && m.key.StartsWith("EyeClosed"))) result.Add("Eyelidsの既存制御と瞬きマッピングを併用します。重複する場合はEyelids連携を有効にしてください。");
            if (d && !p.routeVisemes && d.lipSync != VRC_AvatarDescriptor.LipSyncStyle.VisemeBlendShapeFaceTracking && p.mappings.Any(m => m.Included && (m.key.StartsWith("Mouth") || m.key.StartsWith("Jaw") || m.key.StartsWith("Lip")))) result.Add("口の既存LipSyncと追跡変形を併用します。発話時の過剰変形をプレビュー・実機で確認してください。");
            int operations = p.mappings.Where(m => m.Included).Sum(m => p.renderers.Count(r => r.selected && (r.source.GetBlendShapeIndex(m.shape) >= 0 || r.recipes.Any(x => x.name == m.shape))));
            result.Add("追加のTrackingマッピング操作数（概算）: " + operations + "。全体のPerformance RankはSDK Builderで確認してください。");
            return result;
        }
        public static void Apply(BuilderProfile p, GameObject clone)
        {
            var issues = Validate(p, clone); if (issues.Count != 0) throw new InvalidOperationException(string.Join("\n", issues));
            var d = clone.GetComponent<VRCAvatarDescriptor>() ?? clone.AddComponent<VRCAvatarDescriptor>();
            d.enableFaceTracking = true; d.useAllTrackingKeys = p.useAllKeys;
            if (d.faceTrackingMeshes == null) d.faceTrackingMeshes = new List<SkinnedMeshRenderer>();
            foreach (var r in p.renderers.Where(r => r.selected)) { var mesh = AvatarAnalysis.Resolve(clone, r.path); if (!d.faceTrackingMeshes.Contains(mesh)) d.faceTrackingMeshes.Add(mesh); }
            var set = new VRCAvatarDescriptor.FaceTrackingBlendshapesSet
            {
                setName = p.setName, setWeight = 1,
                faceTrackingBlendshapeNames = new string[(int)VRCAvatarDescriptor.FaceTrackingKey.Max],
                faceTrackingBlendshapeMultipliers = Enumerable.Repeat(1f, (int)VRCAvatarDescriptor.FaceTrackingKey.Max).ToArray(),
                visemeBlendshapeNames = new string[15], visemeBlendshapeMultipliers = Enumerable.Repeat(1f, 15).ToArray()
            };
            foreach (var row in p.mappings.Where(m => m.Included)) { int k = (int)Enum.Parse(typeof(VRCAvatarDescriptor.FaceTrackingKey), row.key); set.faceTrackingBlendshapeNames[k] = row.shape; set.faceTrackingBlendshapeMultipliers[k] = row.multiplier; }
            if (p.routeVisemes && d.lipSync == VRC_AvatarDescriptor.LipSyncStyle.VisemeBlendShape)
            {
                Array.Copy(d.VisemeBlendShapes, set.visemeBlendshapeNames, Math.Min(15, d.VisemeBlendShapes.Length));
                d.lipSync = VRC_AvatarDescriptor.LipSyncStyle.VisemeBlendShapeFaceTracking;
            }
            if (p.routeEyelids) { d.enableEyeLook = true; d.customEyeLookSettings.eyelidType = VRCAvatarDescriptor.EyelidType.FaceTracking; }
            if (p.routeGaze) { d.enableEyeLook = true; d.customEyeLookSettings.eyeGazeMode = VRCAvatarDescriptor.CustomEyeLookSettings.EyeGazeMode.Both; }
            if (p.configureDampening) { d.visemeDampenMode = p.dampenMode; d.visemeDampenAmount = p.dampenAmount; }
            if (d.faceTrackingBlendshapesSetList == null) d.faceTrackingBlendshapesSetList = new List<VRCAvatarDescriptor.FaceTrackingBlendshapesSet>();
            d.faceTrackingBlendshapesSetList.Add(set);
            var sdkIssues = FaceTrackingMappingValidator.CollectIssues(d);
            if (sdkIssues.HasMappingsButNoMeshes || sdkIssues.UnresolvedBlendshapes.Count > 0 || sdkIssues.AmbiguousMeshPaths.Count > 0)
                throw new InvalidOperationException("SDKのマッピング検証に失敗しました: " + string.Join("\n", sdkIssues.UnresolvedBlendshapes.Concat(sdkIssues.AmbiguousMeshPaths)));
            EditorUtility.SetDirty(d);
        }
    }
}
