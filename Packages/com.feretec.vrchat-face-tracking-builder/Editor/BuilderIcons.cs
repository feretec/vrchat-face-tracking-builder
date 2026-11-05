using UnityEditor;
using UnityEngine;
using UnityEngine.UIElements;

namespace FaceTrackingBuilder
{
    public enum BuilderIcon { Brush, BrushSelect, LinkSelect, LoopSelect }

    public sealed class BuilderIconElement : VisualElement
    {
        const string IconDirectory = "Packages/com.feretec.vrchat-face-tracking-builder/Editor/Icons/Tabler/";
        static readonly string[] IconNames = { "brush", "circle-dashed", "topology-star-3", "polygon" };

        public BuilderIconElement(BuilderIcon icon)
        {
            pickingMode = PickingMode.Ignore;
            AddToClassList("builder-icon");
            style.backgroundImage = new StyleBackground(AssetDatabase.LoadAssetAtPath<Texture2D>(IconDirectory + IconNames[(int)icon] + ".png"));
        }
    }
}
