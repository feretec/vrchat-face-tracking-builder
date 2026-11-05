using System;
using System.Collections.Generic;
using System.Linq;
using UnityEditor;
using UnityEngine;
using UnityEngine.Rendering;

namespace FaceTrackingBuilder
{
    public sealed partial class SceneExpressionEditor
    {
        public bool selectionMode, selectThrough, proportional, joinCoincident;
        public bool connectedOnly = true, avatarAxes = true;
        public float selectionRadius = 24, proportionalRadius = .02f;
        public VertexSelectTool selectionTool;
        public VertexTransformTool transformTool;
        public VertexFalloff falloff;
        VertexSelectionState selection;
        VertexGraph graph;
        VertexLoopTopology loopTopology;
        bool loopJoined, altLoopPending, altLoopDragged, altLoopShift;
        Vector2 altLoopStart;
        public string SelectionNotice { get; private set; } = "";
        bool graphJoined, geometryDirty = true, projectionDirty = true;
        Vector3[] selectionWorld;
        Vector2[] screenPoints;
        bool[] eligible;
        float[] screenDepth;
        VertexPickSurface targetSurface;
        readonly List<VertexPickSurface> surfaces = new List<VertexPickSurface>();
        readonly Dictionary<int, VertexPickSurface> surfaceCache = new Dictionary<int, VertexPickSurface>();
        Matrix4x4 lastCamera, lastProjection;
        Rect lastPixelRect;
        float lastPixelsPerPoint;
        bool lastThrough;
        float[] selectionWeights;
        bool lastProportional, lastConnected;
        float lastRadius;
        VertexFalloff lastFalloff;
        int selectionControl, selectionUndo = -1, handleControl;
        bool selecting, boxDragging, vertexPointerHeld;
        Vector2 selectionStart, selectionEnd;
        VertexSelectOperation gestureOperation;
        readonly HashSet<int> gestureVisited = new HashSet<int>();
        TransformGesture transformGesture;
        public int SelectionRevision { get; private set; }
        public int SelectedCount => selection ? selection.selected.Count(value => value) : 0;
        public bool TransformInProgress => transformGesture != null;
        public Quaternion SelectionAxes => avatarAxes ? session.Clone.transform.rotation : Quaternion.identity;
        public Vector3 SelectionCenter { get { EnsureSelectionGeometry(); return VertexEditGeometry.Center(selectionWorld, selection.selected); } }
        public Vector3 SelectionCoordinates => avatarAxes ? session.Clone.transform.InverseTransformPoint(SelectionCenter) : SelectionCenter;
        public bool[] SelectedVertices { get { EnsureSelectionTarget(); return (bool[])selection.selected.Clone(); } }

        sealed class TransformGesture
        {
            public int undo, control;
            public Vector3[] world, sculpt;
            public Matrix4x4[] inverseSkin;
            public float[] weights;
            public bool[] selected;
            public Vector3 pivot, position, scale = Vector3.one;
            public Quaternion axes, rotation;
            public ShapeRecipe recipe;
        }
        void EnsureSelectionTarget()
        {
            if (session?.Clone == null || plan?.source == null || recipe == null) throw new InvalidOperationException("編集する表情を開いてください。");
            if (!selection) selection = ScriptableObject.CreateInstance<VertexSelectionState>();
            selection.hideFlags = HideFlags.HideAndDontSave;
            string target = session.Profile.GetInstanceID() + ":" + plan.path + ":" + plan.source.GetInstanceID() + ":" + recipe.id;
            if (selection.target == target && selection.selected.Length == plan.source.vertexCount) return;
            EndSelectionInteraction(false); Undo.ClearUndo(selection);
            selection.target = target; selection.selected = new bool[plan.source.vertexCount]; graph = null; loopTopology = null;
            SelectionNotice = ""; altLoopPending = false;
            surfaceCache.Clear();
            skinMatrices = null; mirrorPairs = null; InvalidateSelectionGeometry();
        }
        void EnsureSelectionGeometry()
        {
            EnsureSelectionTarget();
            if (graph == null || graphJoined != joinCoincident)
            {
                graph = new VertexGraph(plan.source.vertices, plan.source.triangles, joinCoincident); graphJoined = joinCoincident;
                selectionWeights = null;
            }
            if (!geometryDirty) return;
            var renderer = session.Renderer(plan); skinMatrices = null; Prepare(renderer);
            if (!baked) baked = new Mesh(); renderer.BakeMesh(baked);
            selectionWorld = baked.vertices.Select(renderer.transform.TransformPoint).ToArray();
            targetSurface = CaptureSurface(renderer.GetInstanceID(), selectionWorld, baked); surfaces.Clear(); surfaces.Add(targetSurface);
            var temporary = new Mesh();
            try
            {
                foreach (var other in session.Clone.GetComponentsInChildren<SkinnedMeshRenderer>())
                {
                    if (other == renderer || !other.enabled || !other.sharedMesh || !other.gameObject.activeInHierarchy) continue;
                    other.BakeMesh(temporary);
                    surfaces.Add(CaptureSurface(other.GetInstanceID(), temporary.vertices.Select(other.transform.TransformPoint).ToArray(), temporary));
                }
                foreach (var other in session.Clone.GetComponentsInChildren<MeshRenderer>())
                {
                    var filter = other.GetComponent<MeshFilter>();
                    if (!other.enabled || !other.gameObject.activeInHierarchy || !filter || !filter.sharedMesh) continue;
                    surfaces.Add(CaptureSurface(other.GetInstanceID(), filter.sharedMesh.vertices.Select(other.transform.TransformPoint).ToArray(), filter.sharedMesh));
                }
            }
            finally { UnityEngine.Object.DestroyImmediate(temporary); }
            geometryDirty = false; projectionDirty = true; selectionWeights = null;
        }
        VertexPickSurface CaptureSurface(int rendererId, Vector3[] world, Mesh mesh)
        {
            if (surfaceCache.TryGetValue(rendererId, out var surface) && surface.VertexCount == world.Length) surface.Refit(world);
            else surfaceCache[rendererId] = surface = new VertexPickSurface(world, mesh.triangles);
            return surface;
        }
        void EnsureSelectionProjection(SceneView view)
        {
            var camera = view.camera;
            if (!projectionDirty && lastCamera == camera.worldToCameraMatrix && lastProjection == camera.projectionMatrix &&
                lastPixelRect == camera.pixelRect && lastPixelsPerPoint == EditorGUIUtility.pixelsPerPoint && lastThrough == selectThrough) return;
            screenPoints = new Vector2[selectionWorld.Length]; eligible = new bool[selectionWorld.Length]; screenDepth = new float[selectionWorld.Length];
            for (int i = 0; i < selectionWorld.Length; i++)
            {
                var viewport = camera.WorldToViewportPoint(selectionWorld[i]);
                screenDepth[i] = viewport.z;
                screenPoints[i] = HandleUtility.WorldToGUIPoint(selectionWorld[i]);
                if (viewport.z <= camera.nearClipPlane || viewport.z >= camera.farClipPlane || viewport.x < 0 || viewport.x > 1 || viewport.y < 0 || viewport.y > 1) continue;
                eligible[i] = selectThrough || VertexPickSurface.Visible(HandleUtility.GUIPointToWorldRay(screenPoints[i]), selectionWorld[i], surfaces);
            }
            lastCamera = camera.worldToCameraMatrix; lastProjection = camera.projectionMatrix; lastPixelRect = camera.pixelRect;
            lastPixelsPerPoint = EditorGUIUtility.pixelsPerPoint; lastThrough = selectThrough; projectionDirty = false;
        }
        float[] SelectionWeights()
        {
            if (transformGesture != null) return transformGesture.weights;
            if (selectionWeights == null || lastProportional != proportional || lastConnected != connectedOnly || lastRadius != proportionalRadius || lastFalloff != falloff)
            {
                selectionWeights = VertexEditGeometry.Weights(selectionWorld, selection.selected, graph, proportional, proportionalRadius, falloff, connectedOnly);
                lastProportional = proportional; lastConnected = connectedOnly; lastRadius = proportionalRadius; lastFalloff = falloff;
            }
            return selectionWeights;
        }
        void InvalidateSelectionGeometry() { geometryDirty = projectionDirty = true; selectionWeights = null; SelectionRevision++; session?.View?.Repaint(); }
        void SelectionUndoRedo()
        {
            EndSelectionInteraction(false); skinMatrices = null; mirrorPairs = null; InvalidateSelectionGeometry();
            if (session?.Clone == null || plan == null || recipe == null) return;
            string path = plan.path, id = recipe.id;
            plan = session.Profile.renderers.FirstOrDefault(p => p.path == path); recipe = plan?.recipes.FirstOrDefault(r => r.id == id);
            if (recipe == null) { enabled = false; return; }
            try { session.Rebuild(); if (!session.Comparing) session.Weight(plan.path, recipe.name, 100); }
            catch (Exception ex) { enabled = false; failed?.Invoke(ex); }
        }
        public void SelectionSettingsChanged(bool coordinatesChanged = false)
        { EndSelectionInteraction(false); selectionWeights = null; projectionDirty = true; if (coordinatesChanged) SelectionRevision++; session?.View?.Repaint(); controls?.Refresh(); }
        public void SetCoincidentSelection(bool value)
        {
            joinCoincident = value; graph = null;
            if (value && selection && CanEdit)
                SelectVertices(Enumerable.Range(0, selection.selected.Length).Where(i => selection.selected[i]).ToArray(), VertexSelectOperation.Add);
            SelectionSettingsChanged();
        }
        public void SetSelectionMode(bool value)
        {
            EndSelectionInteraction(false); selectionMode = value;
            if (value) EnsureSelectionTarget();
            SelectionSettingsChanged();
        }
        public void SelectVertices(IEnumerable<int> vertices, VertexSelectOperation operation)
        {
            if (!CanEdit) return; EndSelectionInteraction(false); EnsureSelectionGeometry();
            var next = (bool[])selection.selected.Clone(); VertexEditGeometry.Select(next, graph.ExpandCoincident(vertices), operation);
            if (next.SequenceEqual(selection.selected)) return;
            Undo.IncrementCurrentGroup(); Undo.RegisterCompleteObjectUndo(selection, "頂点の選択"); selection.selected = next; SelectionChanged(); Undo.IncrementCurrentGroup();
        }
        void SelectionChanged() { EditorUtility.SetDirty(selection); SelectionNotice = ""; selectionWeights = null; SelectionRevision++; session.View?.Repaint(); }
        public void SelectAllVertices() { if (CanEdit) { EnsureSelectionTarget(); SelectVertices(Enumerable.Range(0, selection.selected.Length), VertexSelectOperation.Replace); } }
        public void ClearVertexSelection() => SelectVertices(Array.Empty<int>(), VertexSelectOperation.Replace);
        public void ApplyNumericTransform(Vector3 value)
        {
            if (!CanEdit || SelectedCount == 0) return;
            if (!VertexEditGeometry.Finite(value)) throw new InvalidOperationException("座標には有限の数値を指定してください。");
            Vector3 pivot = SelectionCenter; Quaternion axes = SelectionAxes;
            var move = transformTool == VertexTransformTool.Move ? (avatarAxes ? session.Clone.transform.TransformPoint(value) : value) - pivot : Vector3.zero;
            var rotation = transformTool == VertexTransformTool.Rotate ? axes * Quaternion.Euler(value) * Quaternion.Inverse(axes) : Quaternion.identity;
            var scale = transformTool == VertexTransformTool.Scale ? value : Vector3.one;
            BeginVertexTransform();
            try { PreviewVertexTransform(move, rotation, scale); EndSelectionInteraction(false); }
            catch { EndSelectionInteraction(true); throw; }
        }
        public void BeginVertexTransform()
        {
            if (transformGesture != null || !CanEdit) return; EnsureSelectionGeometry(); if (SelectedCount == 0) return;
            if (skinMatrices.Any(m => Mathf.Abs(m.determinant) < 1e-10f)) throw new InvalidOperationException("メッシュのスケールが0に近いため編集できません。");
            var weights = SelectionWeights();
            Undo.IncrementCurrentGroup(); int undo = Undo.GetCurrentGroup(); Undo.RegisterCompleteObjectUndo(session.Profile, "選択した頂点を編集");
            var pivot = VertexEditGeometry.Center(selectionWorld, selection.selected); var axes = SelectionAxes;
            transformGesture = new TransformGesture { undo = undo, control = GUIUtility.hotControl, world = (Vector3[])selectionWorld.Clone(),
                sculpt = recipe.sculpt.Length == selectionWorld.Length ? (Vector3[])recipe.sculpt.Clone() : new Vector3[selectionWorld.Length],
                inverseSkin = skinMatrices.Select(m => m.inverse).ToArray(), weights = (float[])weights.Clone(), selected = (bool[])selection.selected.Clone(),
                pivot = pivot, position = pivot, axes = axes, rotation = axes, recipe = recipe };
        }
        public void PreviewVertexTransform(Vector3 translation, Quaternion rotation, Vector3 scale)
        {
            var gesture = transformGesture; if (gesture == null) return;
            if (gesture.recipe != recipe) { EndSelectionInteraction(false); return; }
            if (symmetric && mirrorPairs == null) mirrorPairs = MeshEngine.MirrorPairs(baseVertices, recipe.planeOrigin, recipe.planeRight, recipe.mirrorTolerance);
            recipe.sculpt = VertexEditGeometry.Transform(gesture.world, gesture.inverseSkin, gesture.sculpt, gesture.weights, gesture.pivot, gesture.axes,
                translation, rotation, scale, symmetric ? mirrorPairs : null, gesture.selected, recipe.planeRight);
            gesture.position = gesture.pivot + translation; gesture.rotation = rotation * gesture.axes; gesture.scale = scale;
            Commit();
        }
        public void EndSelectionInteraction(bool cancel)
        {
            if (handleControl != 0 && GUIUtility.hotControl == handleControl) GUIUtility.hotControl = 0;
            if (handleControl != 0 && GUIUtility.keyboardControl == handleControl) GUIUtility.keyboardControl = 0;
            handleControl = 0;
            if (transformGesture != null)
            {
                var gesture = transformGesture; transformGesture = null;
                if (gesture.control != 0 && GUIUtility.hotControl == gesture.control) GUIUtility.hotControl = 0;
                if (gesture.control != 0 && GUIUtility.keyboardControl == gesture.control) GUIUtility.keyboardControl = 0;
                if (cancel) Undo.RevertAllDownToGroup(gesture.undo); else Undo.CollapseUndoOperations(gesture.undo);
                Undo.IncrementCurrentGroup();
                if (cancel && session?.Clone != null) { session.Rebuild(); session.Weight(plan.path, recipe.name, 100); changed?.Invoke(); }
                InvalidateSelectionGeometry();
            }
            if (selecting)
            {
                selecting = false;
                boxDragging = false;
                gestureVisited.Clear();
                if (GUIUtility.hotControl == selectionControl) GUIUtility.hotControl = 0;
                if (selectionUndo >= 0)
                {
                    int undo = selectionUndo; selectionUndo = -1;
                    if (cancel) Undo.RevertAllDownToGroup(undo); else Undo.CollapseUndoOperations(undo);
                    Undo.IncrementCurrentGroup();
                }
                SelectionChanged();
            }
        }
        void DisposeSelection() { if (selection) { Undo.ClearUndo(selection); UnityEngine.Object.DestroyImmediate(selection); } selection = null; surfaces.Clear(); surfaceCache.Clear(); }
        public void ToggleSelectionTool(VertexSelectTool tool)
        {
            selectionTool = selectionTool == tool ? VertexSelectTool.Normal : tool;
            SelectionNotice = "";
            SelectionSettingsChanged();
        }
        internal bool ResetSelectionTool()
        {
            if (!selectionMode || !CanEdit || selectionTool == VertexSelectTool.Normal) return false;
            selectionTool = VertexSelectTool.Normal; SelectionNotice = ""; SelectionSettingsChanged(); return true;
        }
        internal void BeginAltLoop(Vector2 point, bool shift)
        { altLoopPending = true; altLoopDragged = false; altLoopStart = point; altLoopShift = shift; }
        internal void MoveAltLoop(Vector2 point)
        { if (altLoopPending) altLoopDragged |= VertexEditGeometry.IsBoxDrag(altLoopStart, point); }
        internal bool EndAltLoop(Vector2 point, out bool shift)
        {
            MoveAltLoop(point); shift = altLoopShift;
            bool click = altLoopPending && !altLoopDragged; altLoopPending = false; return click;
        }
        void SelectLoopAt(SceneView view, Vector2 point, bool shift)
        {
            EnsureSelectionGeometry(); EnsureSelectionProjection(view);
            if (loopTopology == null || loopJoined != joinCoincident)
            { loopTopology = new VertexLoopTopology(plan.source.vertices, plan.source.triangles, joinCoincident); loopJoined = joinCoincident; }
            int edge = loopTopology.PickEdge(screenPoints, eligible, point, 9, screenDepth);
            if (edge < 0) { SelectionNotice = "辺の中央付近をクリックしてください。"; controls?.Refresh(); return; }
            var result = loopTopology.Trace(edge, selectionWorld);
            SelectVertices(result.vertices, shift ? VertexSelectOperation.Toggle : VertexSelectOperation.Replace);
            SelectionNotice = result.ambiguous ? "曖昧な分岐で停止しました。" : result.closed ? "閉じたループを選択しました。" : "連続する頂点列を選択しました。";
            controls?.Refresh();
        }
        internal void ObserveVertexPointer(bool held)
        {
            vertexPointerHeld = held;
            if (!held && transformGesture != null) EndSelectionInteraction(false);
        }
        internal void CompleteReleasedHandle(int activeControl)
        {
            int owned = transformGesture?.control ?? handleControl;
            if (owned != 0 && activeControl != owned) EndSelectionInteraction(false);
        }
        internal bool HandleSelectionShortcut(SceneView view, KeyCode key, EventModifiers modifiers, bool textInput, bool navigating)
        {
            if (view != session?.View || EditorWindow.focusedWindow != view || !CanEdit || !selectionMode || plan == null ||
                EditorApplication.isPlayingOrWillChangePlaymode || textInput || navigating ||
                (modifiers & (EventModifiers.Alt | EventModifiers.Control | EventModifiers.Command)) != 0) return false;
            if (key == KeyCode.Escape) { EndSelectionInteraction(true); return true; }
            if (transformGesture != null && !vertexPointerHeld) EndSelectionInteraction(false);
            if (transformGesture != null || selecting) return false;
            if ((modifiers & EventModifiers.Shift) != 0 && key != KeyCode.A) return false;
            switch (key)
            {
                case KeyCode.B: selectionTool = VertexSelectTool.Normal; break;
                case KeyCode.C: selectionTool = VertexSelectTool.Brush; break;
                case KeyCode.L: selectionTool = VertexSelectTool.Linked; break;
                case KeyCode.G: transformTool = VertexTransformTool.Move; break;
                case KeyCode.R: transformTool = VertexTransformTool.Rotate; break;
                case KeyCode.S: transformTool = VertexTransformTool.Scale; break;
                case KeyCode.A: if ((modifiers & EventModifiers.Shift) != 0) ClearVertexSelection(); else SelectAllVertices(); break;
                default: return false;
            }
            SelectionSettingsChanged(key == KeyCode.G || key == KeyCode.R || key == KeyCode.S);
            return true;
        }
        void BeforeSelectionGUI(SceneView view)
        {
            if (view != session?.View || controls == null) return;
            var e = Event.current;
            try
            {
                if (e.isMouse) controls.UpdateViewportPointer(GUIUtility.GUIToScreenPoint(e.mousePosition), e.type == EventType.MouseDown);
                if (selectionMode && CanEdit && !pointerOverControls && e.type == EventType.MouseDown && e.button == 0 && e.alt && !e.control && !e.command)
                    BeginAltLoop(e.mousePosition, e.shift);
                if (altLoopPending && e.type == EventType.MouseDrag) MoveAltLoop(e.mousePosition);
                if (altLoopPending && e.rawType == EventType.MouseUp && e.button == 0)
                {
                    bool click = EndAltLoop(e.mousePosition, out bool shift);
                    if (click && selectionMode && CanEdit && !pointerOverControls) SelectLoopAt(view, e.mousePosition, shift);
                }
                if (e.type == EventType.MouseLeaveWindow || e.type == EventType.Ignore || e.type == EventType.KeyDown && e.keyCode == KeyCode.Escape) altLoopPending = false;
                if (e.type == EventType.MouseDown && e.button == 0 && !pointerOverControls) ObserveVertexPointer(true);
                if (e.rawType == EventType.MouseUp && e.button == 0) ObserveVertexPointer(false);
                if (e.type == EventType.MouseLeaveWindow || e.type == EventType.Ignore) ObserveVertexPointer(false);
                if (e.type == EventType.MouseDown && e.button == 1 && !e.alt && !pointerOverControls && ResetSelectionTool()) { e.Use(); return; }
                if (e.type == EventType.KeyDown && HandleSelectionShortcut(view, e.keyCode, e.modifiers, controls.HasTextFocus, controls.Navigating)) e.Use();
            }
            catch (Exception ex) { EndSelectionInteraction(true); enabled = false; failed?.Invoke(ex); }
        }
        void DrawSelection(SceneView view)
        {
            Event e = Event.current;
            EventType rawType = e.rawType;
            EnsureSelectionGeometry(); EnsureSelectionProjection(view);
            CompleteReleasedHandle(GUIUtility.hotControl);
            if (pointerOverControls || e.alt || (e.isMouse && (e.button > 0 || e.control || e.command)))
            { DrawSelectionPoints(); return; }
            selectionControl = GUIUtility.GetControlID("FTBVertexSelection".GetHashCode(), FocusType.Passive);
            if (e.type == EventType.Layout) HandleUtility.AddDefaultControl(selectionControl);
            if (!selecting && (transformGesture != null || (selectionTool == VertexSelectTool.Normal && !e.shift && !e.control && !e.command))) DrawTransformHandles();
            CompleteReleasedHandle(GUIUtility.hotControl);
            if (transformGesture != null)
            {
                if (proportional && e.type == EventType.ScrollWheel)
                {
                    ResizeProportionalRadius(proportionalRadius * Mathf.Pow(1.12f, -e.delta.y)); e.Use();
                }
                if (rawType == EventType.MouseUp || rawType == EventType.Ignore || rawType == EventType.MouseLeaveWindow) EndSelectionInteraction(false);
                DrawSelectionPoints(); return;
            }
            if (e.type == EventType.MouseDown && e.button == 0 && HandleUtility.nearestControl == selectionControl && GUIUtility.hotControl == 0)
            {
                if (selectionTool == VertexSelectTool.Loop) { SelectLoopAt(view, e.mousePosition, e.shift); e.Use(); return; }
                selecting = true; GUIUtility.hotControl = selectionControl; selectionStart = selectionEnd = e.mousePosition;
                boxDragging = false;
                gestureOperation = e.shift ? VertexSelectOperation.Toggle : VertexSelectOperation.Replace;
                gestureVisited.Clear();
                Undo.IncrementCurrentGroup(); selectionUndo = Undo.GetCurrentGroup(); Undo.RegisterCompleteObjectUndo(selection, "頂点の選択");
                if (selectionTool == VertexSelectTool.Linked)
                {
                    int index = VertexEditGeometry.Click(screenPoints, eligible, e.mousePosition, 10);
                    var ray = HandleUtility.GUIPointToWorldRay(e.mousePosition);
                    if (targetSurface.Hit(ray, float.MaxValue, out float distance, out int hit) &&
                        (selectThrough || VertexPickSurface.Visible(ray, ray.GetPoint(distance), surfaces))) index = hit;
                    ChangeGestureSelection(index < 0 ? Array.Empty<int>() : graph.Linked(index), gestureOperation);
                    EndSelectionInteraction(false);
                }
                else if (selectionTool == VertexSelectTool.Brush)
                    ChangeGestureSelection(VertexEditGeometry.Brush(screenPoints, eligible, selectionStart, selectionEnd, selectionRadius), gestureOperation);
                e.Use();
            }
            else if (selecting && e.type == EventType.MouseDrag && GUIUtility.hotControl == selectionControl)
            {
                Vector2 previous = selectionEnd; selectionEnd = e.mousePosition;
                boxDragging |= VertexEditGeometry.IsBoxDrag(selectionStart, selectionEnd);
                if (selectionTool == VertexSelectTool.Brush)
                    ChangeGestureSelection(VertexEditGeometry.Brush(screenPoints, eligible, previous, selectionEnd, selectionRadius),
                        gestureOperation == VertexSelectOperation.Replace ? VertexSelectOperation.Add : gestureOperation);
                view.Repaint(); e.Use();
            }
            else if (selecting && e.rawType == EventType.MouseUp)
            {
                selectionEnd = e.mousePosition;
                if (selectionTool == VertexSelectTool.Normal)
                    ChangeGestureSelection(VertexEditGeometry.NormalSelection(screenPoints, eligible, selectionStart, selectionEnd,
                        boxDragging || VertexEditGeometry.IsBoxDrag(selectionStart, selectionEnd)), gestureOperation);
                EndSelectionInteraction(false); e.Use();
            }
            if (e.type == EventType.Ignore || e.type == EventType.MouseLeaveWindow) EndSelectionInteraction(false);
            DrawSelectionPoints(); DrawSelectionOverlay();
            if (e.type == EventType.MouseMove) view.Repaint();
        }
        void ChangeGestureSelection(IEnumerable<int> indices, VertexSelectOperation operation)
        {
            var expanded = graph.ExpandCoincident(indices);
            if (operation == VertexSelectOperation.Toggle) expanded = expanded.Where(gestureVisited.Add);
            VertexEditGeometry.Select(selection.selected, expanded, operation); SelectionChanged();
        }
        Rect SelectionRect() => Rect.MinMaxRect(Mathf.Min(selectionStart.x, selectionEnd.x), Mathf.Min(selectionStart.y, selectionEnd.y),
            Mathf.Max(selectionStart.x, selectionEnd.x), Mathf.Max(selectionStart.y, selectionEnd.y));
        void DrawTransformHandles()
        {
            if (SelectedCount == 0) return;
            Vector3 pivot = transformGesture?.pivot ?? VertexEditGeometry.Center(selectionWorld, selection.selected);
            Quaternion axes = transformGesture?.axes ?? SelectionAxes;
            int previousControl = GUIUtility.hotControl;
            EditorGUI.BeginChangeCheck();
            Vector3 position = transformGesture?.position ?? pivot, scale = transformGesture?.scale ?? Vector3.one;
            Quaternion rotation = transformGesture?.rotation ?? axes;
            switch (transformTool)
            {
                case VertexTransformTool.Move: position = Handles.PositionHandle(position, axes); break;
                case VertexTransformTool.Rotate: rotation = Handles.RotationHandle(rotation, pivot); break;
                case VertexTransformTool.Scale: scale = Handles.ScaleHandle(scale, pivot, axes, HandleUtility.GetHandleSize(pivot)); break;
            }
            bool changedHandle = EditorGUI.EndChangeCheck();
            if (previousControl == 0 && GUIUtility.hotControl != 0) handleControl = GUIUtility.hotControl;
            if (GUIUtility.hotControl == 0 && transformGesture == null) handleControl = 0;
            if (!changedHandle) return;
            BeginVertexTransform(); if (transformGesture == null) return;
            transformGesture.position = position; transformGesture.rotation = rotation; transformGesture.scale = scale;
            PreviewVertexTransform(position - transformGesture.pivot, rotation * Quaternion.Inverse(transformGesture.axes), scale);
        }
        public void ResizeProportionalRadius(float value)
        {
            if (!VertexEditGeometry.Finite(value)) return;
            proportionalRadius = Mathf.Clamp(value, .001f, .15f); selectionWeights = null;
            if (transformGesture != null)
            {
                var gesture = transformGesture;
                gesture.weights = VertexEditGeometry.Weights(gesture.world, gesture.selected, graph, proportional, proportionalRadius, falloff, connectedOnly);
                PreviewVertexTransform(gesture.position - gesture.pivot, gesture.rotation * Quaternion.Inverse(gesture.axes), gesture.scale);
            }
            session.View?.Repaint();
        }
        void DrawSelectionPoints()
        {
            if (Event.current.type != EventType.Repaint) return;
            var weights = SelectionWeights(); var oldColor = Handles.color; var oldDepth = Handles.zTest; Handles.zTest = CompareFunction.Always;
            try
            {
                Handles.BeginGUI();
                try
                {
                    for (int pass = 0; pass < 2; pass++)
                        for (int i = 0; i < selectionWorld.Length; i++)
                        {
                            bool selected = selection.selected[i];
                            if (!eligible[i] || selected != (pass == 1)) continue;
                            Rect rect = VertexEditGeometry.MarkerRect(screenPoints[i], selected);
                            EditorGUI.DrawRect(rect, new Color(.06f, .07f, .08f, 1));
                            rect = new Rect(rect.x + 1, rect.y + 1, rect.width - 2, rect.height - 2);
                            EditorGUI.DrawRect(rect, VertexEditGeometry.MarkerColor(selected, weights[i]));
                        }
                }
                finally { Handles.EndGUI(); }
                if (proportional && SelectedCount == 1)
                {
                    Handles.color = new Color(1, .7f, .1f, .7f);
                    Handles.DrawWireDisc(VertexEditGeometry.Center(selectionWorld, selection.selected), session.View.camera.transform.forward, proportionalRadius);
                }
            }
            finally { Handles.color = oldColor; Handles.zTest = oldDepth; }
        }
        void DrawSelectionOverlay()
        {
            if (Event.current.type != EventType.Repaint) return;
            Handles.BeginGUI();
            try
            {
                if (selecting && selectionTool == VertexSelectTool.Normal && boxDragging)
                {
                    Rect rect = SelectionRect(); EditorGUI.DrawRect(rect, new Color(.1f, .65f, 1, .12f));
                    var color = new Color(.2f, .8f, 1, .9f);
                    EditorGUI.DrawRect(new Rect(rect.x, rect.y, rect.width, 1), color); EditorGUI.DrawRect(new Rect(rect.x, rect.yMax, rect.width, 1), color);
                    EditorGUI.DrawRect(new Rect(rect.x, rect.y, 1, rect.height), color); EditorGUI.DrawRect(new Rect(rect.xMax, rect.y, 1, rect.height), color);
                }
                else if (selectionTool == VertexSelectTool.Brush)
                {
                    var points = new Vector3[49]; var mouse = Event.current.mousePosition;
                    for (int i = 0; i < points.Length; i++) { float angle = i * Mathf.PI * 2 / (points.Length - 1); points[i] = mouse + new Vector2(Mathf.Cos(angle), Mathf.Sin(angle)) * selectionRadius; }
                    var color = Handles.color; Handles.color = Color.cyan; Handles.DrawAAPolyLine(1.5f, points); Handles.color = color;
                }
            }
            finally { Handles.EndGUI(); }
        }
    }
}
