using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Text.RegularExpressions;
using UnityEditor;
using UnityEngine;
using VRC.SDK3.Avatars.Components;

namespace FaceTrackingBuilder
{
    public static class AvatarAnalysis
    {
        public sealed class ShapeCandidate
        {
            public string name;
            public float similarity;
        }

        static readonly Dictionary<string, string[]> Aliases = new Dictionary<string, string[]>
        {
            { "EyeClosedLeft", new[] { "ウィンク2", "vrc.blink_left", "blink_L", "wink_L", "まばたき左", "ウィンク", "Eye_Blink_L", "EyeBlinkLeft", "eye_close_L", "eye_まばたき_L" } },
            { "EyeClosedRight", new[] { "ウィンク2右", "vrc.blink_right", "blink_R", "wink_R", "まばたき右", "ウィンク右", "Eye_Blink_R", "EyeBlinkRight", "eye_close_R", "eye_まばたき_R" } },
            { "EyeWideLeft", new[] { "surprise_L", "びっくり左" } }, { "EyeWideRight", new[] { "surprise_R", "びっくり右" } },
            { "EyeSquintLeft", new[] { "jitome_L", "じと目左" } }, { "EyeSquintRight", new[] { "jitome_R", "じと目右" } },
            { "BrowLowererLeft", new[] { "mayu_down_L", "眉下げ左" } }, { "BrowLowererRight", new[] { "mayu_down_R", "眉下げ右" } },
            { "BrowInnerUpLeft", new[] { "mayu_up_L", "眉上げ左" } }, { "BrowInnerUpRight", new[] { "mayu_up_R", "眉上げ右" } },
            { "MouthCornerPullLeft", new[] { "kuchi_smile_1_L", "口角上げ左" } }, { "MouthCornerPullRight", new[] { "kuchi_smile_1_R", "口角上げ右" } },
            { "MouthFrownLeft", new[] { "kuchi_angry_1_L", "口角下げ左" } }, { "MouthFrownRight", new[] { "kuchi_angry_1_R", "口角下げ右" } },
            { "JawOpen", new[] { "jaw_open", "顎開け", "口開け", "kuchi_A", "あ" } },
            { "TongueOut", new[] { "bero_be-", "ぺろっ", "舌出し" } }, { "TongueUp", new[] { "bero_up", "舌上" } }, { "TongueDown", new[] { "bero_down", "舌下" } }
        };
        public static string[] Keys => Enum.GetNames(typeof(VRCAvatarDescriptor.FaceTrackingKey)).Where(n => n != "Max").ToArray();
        static MethodInfo SdkFind => AppDomain.CurrentDomain.GetAssemblies().Select(a => a.GetType("VRC.SDK3A.Editor.FaceTrackingAutoFill")).FirstOrDefault(t => t != null)?.GetMethod("FindFaceTrackingBlendshape", BindingFlags.NonPublic | BindingFlags.Static);
        public static bool SdkAutoFillAvailable => SdkFind != null;
        public static string Category(string name)
        {
            string n = name.ToLowerInvariant();
            if (Regex.IsMatch(n, "brow|mayu|眉")) return "眉";
            if (Regex.IsMatch(n, "eye|blink|wink|lowerlid|upperlid|look(right|left|up|down)|jitome|瞳|目|まばたき|ウィンク")) return "目";
            if (Regex.IsMatch(n, "cheek|頬|hoppe")) return "頬";
            if (Regex.IsMatch(n, "jaw|顎")) return "顎";
            if (Regex.IsMatch(n, "nose|nasal|鼻")) return "鼻";
            if (Regex.IsMatch(n, "tongue|bero|舌")) return "舌";
            if (Regex.IsMatch(n, "mouth|lip|kuchi|vrc.v_|口|唇")) return "口";
            if (Regex.IsMatch(n, "throat|neck|palate")) return "喉・首";
            return "その他";
        }
        public static SkinnedMeshRenderer Resolve(GameObject root, string path)
        {
            var matches = root.GetComponentsInChildren<SkinnedMeshRenderer>(true).Where(r => AnimationUtility.CalculateTransformPath(r.transform, root.transform) == path).ToArray();
            if (matches.Length != 1) throw new InvalidOperationException("Rendererのパスが欠落または重複しています。同名の階層を整理してください: " + path);
            return matches[0];
        }
        public static BuilderProfile Analyze(GameObject root)
        {
            if (!root) throw new InvalidOperationException("アバターのルートを指定してください。");
            var p = ScriptableObject.CreateInstance<BuilderProfile>();
            p.sourceAvatar = root; p.sourceName = root.name;
            var d = root.GetComponent<VRCAvatarDescriptor>();
            var report = new StringBuilder();
            report.AppendLine(d ? "LipSync: " + d.lipSync + " / Eye Look: " + d.enableEyeLook + " / Eyelids: " + d.customEyeLookSettings.eyelidType + " / Face Tracking: " + d.enableFaceTracking : "VRC Avatar Descriptorなし。適用時に複製へ追加します。");
            foreach (var r in root.GetComponentsInChildren<SkinnedMeshRenderer>(true))
            {
                if (!r.sharedMesh) continue;
                var mesh = r.sharedMesh; var path = AnimationUtility.CalculateTransformPath(r.transform, root.transform);
                Resolve(root, path);
                var names = Enumerable.Range(0, mesh.blendShapeCount).Select(mesh.GetBlendShapeName).ToArray();
                int faceNames = names.Count(n => Category(n) != "その他");
                bool descriptor = d && (d.VisemeSkinnedMesh == r || d.customEyeLookSettings.eyelidsSkinnedMesh == r || (d.faceTrackingMeshes != null && d.faceTrackingMeshes.Contains(r)));
                var plan = new RendererPlan { path = path, source = mesh, selected = descriptor || faceNames >= 3, evidence = descriptor ? "Descriptor参照" : faceNames + "個の顔関連名（推定）" };
                try { plan.fingerprint = MeshEngine.Fingerprint(mesh); }
                catch (Exception) { plan.selected = false; plan.evidence = "読取不可。複製FBXのRead/Writeを有効にしてください。"; }
                p.renderers.Add(plan);
                report.AppendLine(path + ": " + mesh.name + " / " + mesh.vertexCount + "頂点 / " + mesh.blendShapeCount + "表情 / " + plan.evidence);
            }
            foreach (var a in root.GetComponentsInChildren<Animator>(true)) report.AppendLine("Animator: " + a.name + " / " + (a.runtimeAnimatorController ? a.runtimeAnimatorController.name : "Controllerなし"));
            if (d)
            {
                report.AppendLine("Eye Gaze: " + d.customEyeLookSettings.eyeGazeMode + " / Viseme Mesh: " + (d.VisemeSkinnedMesh ? d.VisemeSkinnedMesh.name : "なし"));
                if (d.VisemeBlendShapes != null) report.AppendLine("Visemes: " + string.Join(", ", d.VisemeBlendShapes));
                foreach (var layers in new[] { d.baseAnimationLayers, d.specialAnimationLayers }.Where(a => a != null))
                    foreach (var layer in layers) if (layer.animatorController) report.AppendLine("Playable Layer: " + layer.type + " / " + layer.animatorController.name);
                report.AppendLine("ExMenu: " + (d.expressionsMenu ? d.expressionsMenu.name : "なし") + " / 既存Set: " + (d.faceTrackingBlendshapesSetList?.Count ?? 0));
                if (d.faceTrackingBlendshapesSetList != null) foreach (var set in d.faceTrackingBlendshapesSetList.Where(s => s != null))
                {
                    report.AppendLine("既存Set: " + set.setName + " / 初期ウェイト: " + set.setWeight);
                    if (set.faceTrackingBlendshapeNames != null) for (int i = 0; i < Math.Min(Keys.Length, set.faceTrackingBlendshapeNames.Length); i++)
                        if (!string.IsNullOrEmpty(set.faceTrackingBlendshapeNames[i])) report.AppendLine("  " + ((VRCAvatarDescriptor.FaceTrackingKey)i) + " → " + set.faceTrackingBlendshapeNames[i]);
                }
                report.AppendLine("Parameter Behaviors: " + (d.faceTrackingParameterBehaviors?.Count ?? 0));
            }
            p.analysisSummary = report.ToString(); Suggest(p); return p;
        }
        public static string[] Shapes(BuilderProfile p) => p.renderers.Where(r => r.selected).SelectMany(r => Enumerable.Range(0, r.source.blendShapeCount).Select(r.source.GetBlendShapeName).Concat(r.recipes.Select(x => x.name))).Distinct().OrderBy(x => x).ToArray();
        static string Normalize(string s)
        {
            if (string.IsNullOrEmpty(s)) return "";
            s = s.Normalize(NormalizationForm.FormKC);
            s = s.ToLowerInvariant().Replace("左", "left").Replace("右", "right");
            s = Regex.Replace(s, "([_. -])l($|[_. -])", "$1left$2");
            s = Regex.Replace(s, "([_. -])r($|[_. -])", "$1right$2");
            return Regex.Replace(s, "[^a-z0-9ぁ-んァ-ヶ一-龠]", "");
        }
        static int Side(string s) { s = Normalize(s); return s.Contains("left") ? -1 : s.Contains("right") ? 1 : 0; }
        static float Similarity(string a, string b)
        {
            a = Normalize(a); b = Normalize(b); if (a.Length == 0 || b.Length == 0) return 0;
            var row = Enumerable.Range(0, b.Length + 1).ToArray();
            for (int i = 1; i <= a.Length; i++) { int previous = row[0]; row[0] = i; for (int j = 1; j <= b.Length; j++) { int old = row[j]; row[j] = Math.Min(Math.Min(row[j] + 1, row[j - 1] + 1), previous + (a[i - 1] == b[j - 1] ? 0 : 1)); previous = old; } }
            return 1f - (float)row[b.Length] / Math.Max(a.Length, b.Length);
        }
        static string PreferredMmdBlink(IEnumerable<string> names, string key)
        {
            // Use the MMD naming convention consistently, including avatars with reversed geometry.
            string alias = key == "EyeClosedLeft" ? "ウィンク2" : key == "EyeClosedRight" ? "ウィンク2右" : null;
            return alias == null ? null : names
                .Where(n => string.Equals(n.Normalize(NormalizationForm.FormKC), alias, StringComparison.Ordinal))
                .OrderBy(n => n, StringComparer.Ordinal).FirstOrDefault();
        }
        public static ShapeCandidate[] RankShapes(IEnumerable<string> names, string key, string current)
        {
            var source = (names ?? Enumerable.Empty<string>()).Where(n => !string.IsNullOrEmpty(n)).Distinct(StringComparer.Ordinal).ToArray();
            string preferred = PreferredMmdBlink(source, key);
            var references = new List<string> { key ?? "" };
            if (Aliases.TryGetValue(key ?? "", out var aliases)) references.AddRange(aliases);
            if (!string.IsNullOrEmpty(current)) references.Add(current);
            var normalizedReferences = references.Select(Normalize).Where(n => n.Length > 0).Distinct(StringComparer.Ordinal).ToArray();
            return source
                .Select(n => new ShapeCandidate
                {
                    name = n,
                    similarity = normalizedReferences.Length == 0 ? 0 : normalizedReferences.Max(reference => Similarity(n, reference))
                })
                .OrderByDescending(c => c.name == preferred)
                .ThenByDescending(c => c.similarity)
                .ThenBy(c => c.name, StringComparer.Ordinal)
                .ToArray();
        }
        public static void Suggest(BuilderProfile p)
        {
            var names = Shapes(p); var sdk = SdkFind;
            foreach (string key in Keys)
            {
                var row = p.mappings.FirstOrDefault(m => m.key == key);
                if (row != null && (row.confirmed || row.excluded)) continue;
                if (row == null) { row = new TrackingMapping { key = key }; p.mappings.Add(row); }
                row.shape = ""; row.confidence = 0; row.reason = "候補なし";
                string mmdBlink = PreferredMmdBlink(names, key);
                if (mmdBlink != null)
                {
                    row.shape = mmdBlink; row.confidence = .75f;
                    row.reason = "MMDの通常目閉じ（ウィンク2）候補。プレビューで確認"; continue;
                }
                int index = -1;
                if (sdk != null) index = (int)sdk.Invoke(null, new object[] { names, Enum.Parse(typeof(VRCAvatarDescriptor.FaceTrackingKey), key), "" });
                if (index >= 0) { row.shape = names[index]; row.confidence = names[index] == key ? 1 : .9f; row.reason = "SDK Auto-Fill候補"; continue; }
                if (names.Contains(key)) { row.shape = key; row.confidence = 1; row.reason = "完全一致"; continue; }
                if (Aliases.TryGetValue(key, out var aliases))
                {
                    var alias = aliases.Select(a => names.FirstOrDefault(n => string.Equals(n.Normalize(NormalizationForm.FormKC), a.Normalize(NormalizationForm.FormKC), StringComparison.OrdinalIgnoreCase))).FirstOrDefault(n => n != null);
                    if (alias != null) { row.shape = alias; row.confidence = .75f; row.reason = "別名辞書による推定。プレビューで確認"; continue; }
                }
                var candidate = names.Where(n => Side(n) == Side(key)).Select(n => new { Name = n, Score = Similarity(n, key) }).OrderByDescending(n => n.Score).FirstOrDefault();
                if (candidate != null && candidate.Score >= .78f) { row.shape = candidate.Name; row.confidence = candidate.Score * .7f; row.reason = "名前の類似度による推定"; }
            }
        }
        public static string Recommendation(TrackingMapping row, string[] names)
        {
            if (row.confirmed && !string.IsNullOrEmpty(row.shape)) return "対応済み（確認済み）";
            if (!string.IsNullOrEmpty(row.shape)) return "自動マッピング候補あり";
            if (row.key.StartsWith("EyeClosed") && names.Any(n => new[] { "blink", "まばたき", "EyeClosed" }.Contains(n))) return "既存BlendShapeから生成可能：両目閉じを左右分割";
            if (row.key.StartsWith("MouthCornerPull") && names.Any(n => new[] { "口角上げ", "kuchi_smile_1", "mouthSmile" }.Contains(n))) return "既存BlendShapeから生成可能：口角を部分抽出・左右分割";
            string other = row.key.EndsWith("Left") ? row.key.Replace("Left", "Right") : row.key.EndsWith("Right") ? row.key.Replace("Right", "Left") : "";
            if (names.Contains(other)) return "既存BlendShapeから生成可能：反対側をミラー（対応頂点の検証が必要）";
            if (Category(row.key) == "喉・首" || row.key.Contains("Dilation") || row.key.Contains("Constrict")) return "未対応：構造の確認と手動編集が必要";
            if (Category(row.key) == "舌") return "手動編集が必要：舌メッシュの構造を確認";
            return "特徴点指定によって生成可能（位置と変位を指定）";
        }
        public static List<string> AnimatorConflicts(GameObject root, IEnumerable<string> shapeNames)
        {
            var wanted = new HashSet<string>(shapeNames); var clips = new HashSet<AnimationClip>();
            var controllers = root.GetComponentsInChildren<Animator>(true).Select(a => a.runtimeAnimatorController).Where(c => c).ToList();
            var d = root.GetComponent<VRCAvatarDescriptor>();
            if (d)
                foreach (var layers in new[] { d.baseAnimationLayers, d.specialAnimationLayers }.Where(a => a != null))
                    controllers.AddRange(layers.Select(l => l.animatorController).Where(c => c));
            foreach (var c in controllers) foreach (var clip in c.animationClips) clips.Add(clip);
            var report = new List<string>();
            foreach (var clip in clips) foreach (var binding in AnimationUtility.GetCurveBindings(clip))
                if (binding.propertyName.StartsWith("blendShape.") && wanted.Contains(binding.propertyName.Substring(11))) report.Add(clip.name + " : " + binding.path + " / " + binding.propertyName.Substring(11));
            return report.Distinct().ToList();
        }
    }
}
