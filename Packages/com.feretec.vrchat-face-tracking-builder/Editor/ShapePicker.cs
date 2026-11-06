using System;
using System.Collections.Generic;
using System.Linq;
using UnityEditor.UIElements;
using UnityEngine;
using UnityEngine.UIElements;

namespace FaceTrackingBuilder
{
    // Selection is temporary until the user presses Use; cancelling only disposes the preview copy.
    public sealed class ShapePicker : VisualElement, IDisposable
    {
        readonly BuilderProfile profile;
        readonly RendererPlan plan;
        readonly string[] names;
        readonly ListView list;
        readonly Image portrait;
        readonly Label selectedLabel, status;
        readonly Button use;
        BuilderSession session;
        string previewed;
        string selected, query = "", category = "すべて";
        float weight = 100;
        public string Selected => selected;
        public VisualElement ActionBar { get; }
        public ShapePicker(BuilderProfile profile, RendererPlan plan, string current, Action<string> accept, Action cancel, ShapeRecipe editing = null)
        {
            this.profile = profile; this.plan = plan;
            names = plan == null ? AvatarAnalysis.Shapes(profile) : RecipeSources.Available(plan, editing);
            AddToClassList("shape-picker");
            Add(new Label("BlendShapeを選ぶ") { name = "pickerTitle" });
            var columns = new VisualElement(); columns.AddToClassList("columns"); Add(columns);
            var left = new VisualElement(); left.AddToClassList("browser-column"); columns.Add(left);
            var search = new ToolbarSearchField { name = "shapeSearch" }; search.RegisterValueChangedCallback(e => { query = e.newValue; Filter(); }); left.Add(search);
            var categories = new PopupField<string>("部位", new List<string> { "すべて", "目", "眉", "口", "顎", "頬", "鼻", "舌", "喉・首", "その他" }, 0);
            categories.RegisterValueChangedCallback(e => { category = e.newValue; Filter(); }); left.Add(categories);
            list = new ListView { name = "shapeList", selectionType = SelectionType.Single };
            list.AddToClassList("shape-list");
            list.makeItem = () => { var item = new VisualElement(); item.AddToClassList("shape-item"); item.Add(new Label { name = "shapeName" }); var caption = new Label { name = "shapeCaption" }; caption.AddToClassList("muted"); item.Add(caption); return item; };
            list.bindItem = (element, index) => { string name = (string)list.itemsSource[index]; element.Q<Label>("shapeName").text = name; element.Q<Label>("shapeCaption").text = Caption(name); };
            list.selectionChanged += values => { selected = values.OfType<string>().FirstOrDefault(); selectedLabel.text = selected ?? "表情を選んでください"; use.SetEnabled(selected != null); Render(); };
            left.Add(list);
            ExpressionListNavigation.EnableArrowKeys(list);
            var right = new VisualElement(); right.AddToClassList("preview-column"); columns.Add(right);
            selectedLabel = new Label("表情を選んでください"); selectedLabel.AddToClassList("wrap"); right.Add(selectedLabel);
            portrait = new Image { scaleMode = ScaleMode.ScaleToFit }; portrait.AddToClassList("portrait"); right.Add(portrait);
            var slider = new Slider("表情の強さ", 0, 100) { value = weight, showInputField = true }; slider.RegisterValueChangedCallback(e => { weight = e.newValue; Render(); }); right.Add(slider);
            status = new Label(); status.AddToClassList("wrap"); right.Add(status);
            var buttons = new VisualElement(); buttons.AddToClassList("row"); Add(buttons);
            ActionBar = buttons;
            buttons.Add(new Button(cancel) { text = "キャンセルして戻る" });
            use = new Button(() => { if (selected != null) accept(selected); }) { text = "この表情を使う", name = "useShape" }; use.AddToClassList("primary"); use.SetEnabled(false); buttons.Add(use);
            use.AddToClassList("row-end");
            Filter(); int initial = Array.IndexOf(names, current);
            if (initial >= 0) { list.selectedIndex = initial; ExpressionListNavigation.RevealSelection(list); }
        }
        string Caption(string name)
        {
            var mapping = profile.mappings.FirstOrDefault(m => m.shape == name);
            return mapping != null ? ExpressionGuide.Title(mapping.key) + " の候補" : AvatarAnalysis.Category(name);
        }
        void Filter()
        {
            list.itemsSource = names.Where(n => (category == "すべて" || AvatarAnalysis.Category(n) == category || profile.mappings.Any(m => m.shape == n && AvatarAnalysis.Category(m.key) == category)) &&
                (string.IsNullOrWhiteSpace(query) || (n + " " + Caption(n)).IndexOf(query.Trim(), StringComparison.OrdinalIgnoreCase) >= 0)).ToList();
            list.ClearSelection(); list.Rebuild();
            selected = null; if (use != null) use.SetEnabled(false); if (portrait != null) portrait.image = null;
        }
        void Render()
        {
            if (string.IsNullOrEmpty(selected)) return;
            try
            {
                if (session?.Clone == null || previewed != selected)
                { session?.Dispose(); session = new BuilderSession(profile, true, selected, plan?.path); previewed = selected; }
                session.ResetWeights();
                foreach (var p in profile.renderers.Where(p => plan != null ? p == plan : p.selected)) session.Weight(p.path, selected, weight);
                portrait.image = session.RenderPortrait(); portrait.MarkDirtyRepaint();
                status.text = session.MissingShaderCount > 0 ? "一部のShaderが未導入のため、代替表示です。" : "元のアバターは変更しません。";
            }
            catch (Exception ex) { Dispose(); status.text = "表示できません: " + ex.Message; }
        }
        public void Dispose() { portrait.image = null; session?.Dispose(); session = null; }
    }
}
