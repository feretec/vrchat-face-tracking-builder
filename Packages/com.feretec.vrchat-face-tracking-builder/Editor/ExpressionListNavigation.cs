using UnityEngine;
using UnityEngine.UIElements;

namespace FaceTrackingBuilder
{
    static class ExpressionListNavigation
    {
        public static void EnableArrowKeys(ListView list)
        {
            list.focusable = true;
            list.RegisterCallback<KeyDownEvent>(e =>
            {
                if (e.altKey || e.ctrlKey || e.commandKey || e.shiftKey) return;
                if (e.keyCode != KeyCode.UpArrow && e.keyCode != KeyCode.DownArrow) return;
                Move(list, e.keyCode == KeyCode.DownArrow ? 1 : -1);
                e.StopImmediatePropagation(); e.PreventDefault();
            }, TrickleDown.TrickleDown);
        }
        public static void Move(ListView list, int direction)
        {
            if (list.itemsSource == null || list.itemsSource.Count == 0) return;
            list.selectedIndex = Mathf.Clamp(list.selectedIndex < 0 ? 0 : list.selectedIndex + direction, 0, list.itemsSource.Count - 1);
            RevealSelection(list);
        }
        public static void RevealSelection(ListView list)
        {
            int index = list.selectedIndex;
            if (index < 0) return;
            int attempts = 0;
            IVisualElementScheduledItem pending = null;
            pending = list.schedule.Execute(() =>
            {
                if (list.selectedIndex != index || list.itemsSource == null || index >= list.itemsSource.Count || ++attempts > 120) { pending.Pause(); return; }
                var viewport = list.Q<ScrollView>()?.contentViewport;
                if (viewport == null || viewport.layout.height <= 0) return;
                var item = list.GetRootElementForIndex(index);
                if (item != null && item.layout.height > 0 && item.worldBound.yMin >= viewport.worldBound.yMin - 1 && item.worldBound.yMax <= viewport.worldBound.yMax + 1)
                { pending.Pause(); return; }
                list.ScrollToItem(index);
            }).Every(16);
        }
    }
}
