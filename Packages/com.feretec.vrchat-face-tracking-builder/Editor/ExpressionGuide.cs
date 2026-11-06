using System;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;
using UnityEngine.UIElements;

namespace FaceTrackingBuilder
{
    public static class ExpressionGuide
    {
        static readonly Dictionary<string, string> Names = new Dictionary<string, string>
        {
            { "EyeLookOut", "目を外側へ向ける" }, { "EyeLookIn", "目を内側へ向ける" },
            { "EyeLookUp", "目を上へ向ける" }, { "EyeLookDown", "目を下へ向ける" },
            { "EyeClosed", "目を閉じる" }, { "EyeSquint", "目を細める" }, { "EyeWide", "目を大きく開く" },
            { "EyeDilation", "瞳孔を広げる" }, { "EyeConstrict", "瞳孔を縮める" },
            { "BrowPinch", "眉を内側へ寄せる" }, { "BrowLowerer", "眉の外側を下げる" },
            { "BrowInnerUp", "眉の内側を上げる" }, { "BrowOuterUp", "眉の外側を上げる" },
            { "NasalDilation", "鼻の穴を広げる" }, { "NasalConstrict", "鼻の穴を狭める" }, { "NoseSneer", "鼻の脇を引き上げる" },
            { "CheekSquint", "頬を上げる" }, { "CheekPuff", "頬を膨らませる" }, { "CheekSuck", "頬をすぼめる" },
            { "JawOpen", "顎を開く" }, { "JawRight", "顎を右へ動かす" }, { "JawLeft", "顎を左へ動かす" },
            { "JawForward", "顎を前へ出す" }, { "JawBackward", "顎を後ろへ引く" }, { "JawClench", "奥歯を噛みしめる" },
            { "JawMandibleRaise", "下顎を上げる" }, { "MouthClosed", "顎を開いたまま唇を閉じる" },
            { "LipSuckUpper", "上唇を口の中に巻き込む" }, { "LipSuckLower", "下唇を口の中に巻き込む" }, { "LipSuckCorner", "口角を口の中に巻き込む" },
            { "LipFunnelUpper", "上唇を筒状に開く" }, { "LipFunnelLower", "下唇を筒状に開く" },
            { "LipPuckerUpper", "上唇をすぼめて前へ出す" }, { "LipPuckerLower", "下唇をすぼめて前へ出す" },
            { "MouthUpperUp", "上唇を上げる" }, { "MouthUpperDeepen", "上唇を頬側へ押し込む" }, { "MouthLowerDown", "下唇を下げる" },
            { "MouthUpperRight", "上唇を右へ動かす" }, { "MouthUpperLeft", "上唇を左へ動かす" },
            { "MouthLowerRight", "下唇を右へ動かす" }, { "MouthLowerLeft", "下唇を左へ動かす" },
            { "MouthCornerPull", "口角を斜め上へ引く" }, { "MouthCornerSlant", "口角を上げる" },
            { "MouthFrown", "口角を下げる" }, { "MouthStretch", "口角を斜め下へ引く" }, { "MouthDimple", "口角を後ろへ引く" },
            { "MouthRaiserUpper", "上唇の周囲を持ち上げる" }, { "MouthRaiserLower", "下唇の周囲を持ち上げる" },
            { "MouthPress", "上下の唇を押し合わせる" }, { "MouthTightener", "唇を横方向に締める" },
            { "TongueOut", "舌を出す" }, { "TongueUp", "舌を上へ向ける" }, { "TongueDown", "舌を下へ向ける" },
            { "TongueRight", "舌を右へ向ける" }, { "TongueLeft", "舌を左へ向ける" }, { "TongueRoll", "舌の両端を丸める" },
            { "TongueBendDown", "舌先を下へ曲げる" }, { "TongueCurlUp", "舌先を上へ丸める" },
            { "TongueSquish", "舌を細くする" }, { "TongueFlat", "舌を平たく広げる" },
            { "TongueTwistRight", "舌を右へひねる" }, { "TongueTwistLeft", "舌を左へひねる" },
            { "SoftPalateClose", "口の奥の軟口蓋を閉じる" }, { "ThroatSwallow", "喉を飲み込むように動かす" }, { "NeckFlex", "首の筋肉に力を入れる" }
        };
        public static string Stem(string key) => key != null && !Names.ContainsKey(key) ? key.Replace("Right", "").Replace("Left", "") : key;
        public static string Title(string key)
        {
            if (string.IsNullOrEmpty(key)) return "自由な表情";
            if (Names.TryGetValue(key, out var title)) return title;
            return Names.TryGetValue(Stem(key), out title) ? title + (key.EndsWith("Right") ? "（右）" : key.EndsWith("Left") ? "（左）" : "") : key;
        }
        public static string Description(string key)
        {
            if (string.IsNullOrEmpty(key)) return "";
            if (key.StartsWith("EyeLook")) return "片目の視線を動かします。";
            if (key == "MouthClosed") return "JawOpenと組み合わせ、顎を開いたまま唇を閉じます。";
            return "";
        }
        public static bool Matches(string key, string query) => string.IsNullOrWhiteSpace(query) || (key + " " + Title(key) + " " + AvatarAnalysis.Category(key)).IndexOf(query.Trim(), StringComparison.OrdinalIgnoreCase) >= 0;
    }

    public sealed class ExpressionDiagram : VisualElement
    {
        readonly string key;
        public ExpressionDiagram(string key) { this.key = key ?? ""; AddToClassList("expression-diagram"); generateVisualContent += Draw; }
        internal static Vector2 BrowPoint(string key, int side, bool inner)
        {
            float x = .5f + side * .14f + side * (inner ? -.075f : .075f);
            float y = inner ? .275f : .29f;
            if (key.StartsWith("Brow") && key.EndsWith(side < 0 ? "Right" : "Left"))
            {
                if (key.StartsWith("BrowPinch")) x -= side * .025f;
                if (key.StartsWith("BrowLowerer") && !inner) y += .035f;
                if ((key.StartsWith("BrowInnerUp") && inner) || (key.StartsWith("BrowOuterUp") && !inner)) y -= .04f;
            }
            return new Vector2(x, y);
        }
        void Draw(MeshGenerationContext context)
        {
            if (contentRect.width < 1 || contentRect.height < 1) return;
            var p = context.painter2D; float size = Mathf.Min(contentRect.width, contentRect.height);
            Vector2 offset = new Vector2((contentRect.width - size) / 2, (contentRect.height - size) / 2);
            Func<float, float, Vector2> point = (x, y) => offset + new Vector2(x, y) * size;
            Color neutral = new Color(.63f, .68f, .74f), accent = new Color(.25f, .78f, .97f);
            Action<float, float, float, float, Color> ellipse = (x, y, rx, ry, color) =>
            {
                p.strokeColor = color; p.lineWidth = 2; p.BeginPath();
                for (int i = 0; i <= 48; i++) { float a = i * Mathf.PI / 24; var v = point(x + Mathf.Cos(a) * rx, y + Mathf.Sin(a) * ry); if (i == 0) p.MoveTo(v); else p.LineTo(v); }
                p.ClosePath(); p.Stroke();
            };
            Action<float, float, float, float, Color> line = (x, y, ex, ey, color) => { p.strokeColor = color; p.lineWidth = 2.5f; p.BeginPath(); p.MoveTo(point(x, y)); p.LineTo(point(ex, ey)); p.Stroke(); };
            Action<float, float, float, float> arrow = (x, y, dx, dy) =>
            {
                line(x, y, x + dx, y + dy, accent); var d = new Vector2(dx, dy).normalized * .035f;
                line(x + dx, y + dy, x + dx - d.x + d.y * .6f, y + dy - d.y - d.x * .6f, accent);
                line(x + dx, y + dy, x + dx - d.x - d.y * .6f, y + dy - d.y + d.x * .6f, accent);
            };
            ellipse(.5f, .47f, .29f, .39f, neutral);
            line(.39f, .83f, .38f, .96f, neutral); line(.61f, .83f, .62f, .96f, neutral);
            line(.49f, .44f, .46f, .58f, neutral); line(.46f, .58f, .54f, .58f, neutral);
            string category = AvatarAnalysis.Category(key);
            for (int side = -1; side <= 1; side += 2)
            {
                float x = .5f + side * .14f;
                bool active = key.EndsWith(side < 0 ? "Right" : "Left");
                Color eye = active && category == "目" ? accent : neutral;
                float h = active && key.StartsWith("EyeClosed") ? .003f : active && key.StartsWith("EyeSquint") ? .018f : active && key.StartsWith("EyeWide") ? .06f : .038f;
                ellipse(x, .40f, .085f, h, eye);
                float dx = active && key.StartsWith("EyeLookOut") ? side * .035f : active && key.StartsWith("EyeLookIn") ? -side * .035f : 0;
                float dy = active && key.StartsWith("EyeLookUp") ? -.02f : active && key.StartsWith("EyeLookDown") ? .02f : 0;
                if (h > .005f) ellipse(x + dx, .40f + dy, active && key.Contains("Dilation") ? .026f : active && key.Contains("Constrict") ? .009f : .018f, Mathf.Min(h * .7f, .022f), eye);
                if (dx != 0 || dy != 0) arrow(x, .5f, dx * 2, dy * 3);
                var inner = BrowPoint(key, side, true); var outer = BrowPoint(key, side, false);
                line(outer.x, outer.y, inner.x, inner.y, active && category == "眉" ? accent : neutral);
                if (active && category == "眉") arrow(x, .2f, key.Contains("Pinch") ? -side * .09f : 0, key.Contains("Lowerer") ? .07f : key.Contains("Up") ? -.07f : 0);
            }
            bool jaw = category == "顎";
            ellipse(.5f, jaw && key == "JawOpen" ? .73f : .69f, .12f, jaw && key == "JawOpen" ? .07f : .018f, category == "口" || jaw || category == "舌" ? accent : neutral);
            float sx = key.EndsWith("Right") ? -.15f : key.EndsWith("Left") ? .15f : 0;
            if (category == "口")
            {
                float y = key.Contains("Upper") ? .66f : key.Contains("Lower") ? .72f : .69f;
                ellipse(.5f + sx * .65f, y, .025f, .025f, accent);
                if (key.Contains("Pull") || key.Contains("Slant") || key.Contains("Up") || key.Contains("Raiser")) arrow(.5f + sx, y, key.Contains("Pull") ? sx * .45f : 0, -.09f);
                else if (key.Contains("Frown") || key.Contains("Down") || key.Contains("Stretch")) arrow(.5f + sx, y, key.Contains("Stretch") ? sx * .45f : 0, .09f);
                else if (key.StartsWith("MouthUpper") || key.StartsWith("MouthLower")) arrow(.5f, y, sx * .7f, 0);
            }
            if (category == "頬") { ellipse(.5f + sx * 1.3f, .58f, .05f, .06f, accent); if (key.Contains("Squint")) arrow(.5f + sx * 1.3f, .58f, 0, -.08f); }
            if (category == "鼻") ellipse(.5f + sx * .35f, .56f, .035f, .025f, accent);
            if (jaw) { ellipse(.5f, .83f, .08f, .025f, accent); if (key == "JawOpen") arrow(.5f, .83f, 0, .08f); else if (sx != 0) arrow(.5f, .83f, sx, 0); }
            if (category == "舌") { ellipse(.5f, .735f, .035f, .045f, accent); if (key == "TongueRight" || key == "TongueLeft") arrow(.5f, .75f, sx, 0); else if (key == "TongueUp" || key == "TongueDown") arrow(.5f, .75f, 0, key == "TongueUp" ? -.1f : .1f); }
            if (category == "喉・首") ellipse(.5f + sx * .45f, .89f, .04f, .055f, accent);
        }
    }

    public sealed class RecipeSuggestion
    {
        public string source, explanation;
        public GenerateMode mode;
    }
    public static class ExpressionRecipes
    {
        public static RecipeSuggestion Suggest(RendererPlan plan, string key)
        {
            if (plan?.source == null || string.IsNullOrEmpty(key)) return null;
            string[] aliases = null;
            if (key == "EyeLookOutRight" || key == "EyeLookInLeft") aliases = new[] { "LookRight", "EyeLookRight", "右を見る" };
            if (key == "EyeLookOutLeft" || key == "EyeLookInRight") aliases = new[] { "LookLeft", "EyeLookLeft", "左を見る" };
            if (key.StartsWith("EyeLookUp")) aliases = new[] { "LookUp", "EyeLookUp", "上を見る" };
            if (key.StartsWith("EyeLookDown")) aliases = new[] { "LookDown", "EyeLookDown", "下を見る" };
            if (key.StartsWith("EyeClosed")) aliases = new[] { "EyeClosed", "eyeBlink", "blink", "まばたき" };
            if (key.StartsWith("EyeWide")) aliases = new[] { "びっくり" };
            if (key.StartsWith("BrowInnerUp")) aliases = new[] { "困る" };
            if (key.StartsWith("MouthCornerPull")) aliases = new[] { "MouthCornerPull", "mouthSmile", "kuchi_smile_1", "口角上げ", "にやり" };
            if (aliases == null) return null;
            var names = Enumerable.Range(0, plan.source.blendShapeCount).Select(plan.source.GetBlendShapeName).ToArray();
            string source = aliases.Select(a => names.FirstOrDefault(n => string.Equals(n, a, StringComparison.OrdinalIgnoreCase))).FirstOrDefault(n => n != null);
            if (source == null) return null;
            bool right = key.EndsWith("Right");
            return new RecipeSuggestion { source = source, mode = right ? GenerateMode.SplitRight : GenerateMode.SplitLeft,
                explanation = source + " → " + (right ? "右側" : "左側") + "を抽出" };
        }
    }
}
