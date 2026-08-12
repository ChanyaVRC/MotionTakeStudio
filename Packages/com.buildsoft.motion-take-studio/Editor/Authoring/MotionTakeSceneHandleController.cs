using System;
using System.Collections.Generic;
using UnityEditor;
using UnityEngine;

namespace BuildSoft.MotionTakeStudio.Editor
{
    internal sealed class MotionTakeSceneHandleController : IDisposable
    {
        private readonly Action _changed;
        private readonly HashSet<PoseTarget> _rotationTargets;
        private MotionEditRecipe _recipe;
        private IMotionTakeTargetPoseSource _poseSource;
        private IMotionTakeOverlayPoseSource _overlayPoseSource;
        private IMotionTakeRawPoseSource _rawPoseSource;
        private PoseTarget _selectedTarget;
        private int _frame;
        private int _influenceFrames = 12;
        private MotionTakeOverlayFlags _overlays;
        private bool _enabled;

        public MotionTakeSceneHandleController(Action changed)
        {
            _changed = changed;
            _rotationTargets = new HashSet<PoseTarget>();
            foreach (PoseTarget target in Enum.GetValues(typeof(PoseTarget)))
            {
                if (MotionTakeCorrectionAuthoring.SupportsRotation(target))
                {
                    _rotationTargets.Add(target);
                }
            }
        }

        public void Bind(
            MotionEditRecipe recipe,
            IMotionTakeTargetPoseSource poseSource,
            IMotionTakeOverlayPoseSource overlayPoseSource,
            IMotionTakeRawPoseSource rawPoseSource,
            PoseTarget selectedTarget,
            int frame,
            int influenceFrames,
            MotionTakeOverlayFlags overlays,
            bool enabled)
        {
            _recipe = recipe;
            _poseSource = poseSource;
            _overlayPoseSource = overlayPoseSource;
            _rawPoseSource = rawPoseSource;
            _selectedTarget = selectedTarget;
            _frame = Mathf.Max(0, frame);
            _influenceFrames = Mathf.Clamp(influenceFrames, 1, 60);
            _overlays = overlays;

            if (_enabled == enabled)
            {
                return;
            }

            _enabled = enabled;
            if (_enabled)
            {
                SceneView.duringSceneGui += OnSceneGui;
            }
            else
            {
                SceneView.duringSceneGui -= OnSceneGui;
            }
        }

        public void Dispose()
        {
            SceneView.duringSceneGui -= OnSceneGui;
            _enabled = false;
            _recipe = null;
            _poseSource = null;
            _overlayPoseSource = null;
            _rawPoseSource = null;
        }

        private void OnSceneGui(SceneView sceneView)
        {
            if (!_enabled || _recipe == null || _poseSource == null ||
                !MotionTakeCorrectionAuthoring.TryGetEvaluatedTargetPose(
                    _recipe,
                    _poseSource,
                    _selectedTarget,
                    _frame,
                    out var basePose,
                    out var worldPosition,
                    out var worldRotation))
            {
                return;
            }

            DrawPoseOverlays();

            Handles.BeginGUI();
            GUILayout.BeginArea(new Rect(12f, 12f, 320f, 62f), EditorStyles.helpBox);
            GUILayout.Label($"Motion Take: {_selectedTarget} · frame {_frame}");
            GUILayout.Label("Raw ■   IK ●   Auto ○   Manual ◆", EditorStyles.miniLabel);
            GUILayout.EndArea();
            Handles.EndGUI();

            EditorGUI.BeginChangeCheck();
            var newPosition = Handles.PositionHandle(worldPosition, worldRotation);
            if (EditorGUI.EndChangeCheck())
            {
                MotionTakeCorrectionAuthoring.SetPosition(
                    _recipe,
                    _selectedTarget,
                    _frame,
                    _influenceFrames,
                    basePose,
                    newPosition);
                _changed?.Invoke();
            }

            if (!_rotationTargets.Contains(_selectedTarget))
            {
                return;
            }

            EditorGUI.BeginChangeCheck();
            var newRotation = Handles.RotationHandle(worldRotation, newPosition);
            if (EditorGUI.EndChangeCheck())
            {
                MotionTakeCorrectionAuthoring.SetRotation(
                    _recipe,
                    _selectedTarget,
                    _frame,
                    _influenceFrames,
                    basePose,
                    newRotation);
                _changed?.Invoke();
            }
        }

        private void DrawPoseOverlays()
        {
            if (Event.current.type != EventType.Repaint)
            {
                return;
            }

            foreach (PoseTarget target in Enum.GetValues(typeof(PoseTarget)))
            {
                if ((_overlays & MotionTakeOverlayFlags.Raw) != 0 &&
                    _rawPoseSource != null &&
                    _rawPoseSource.TryGetRawTargetPose(
                        target,
                        _frame,
                        out var rawPosition,
                        out var rawRotation))
                {
                    DrawPoseMarker(target, rawPosition, rawRotation, new Color(1f, 0.45f, 0.08f, 0.9f), MarkerKind.Cube);
                }

                if ((_overlays & MotionTakeOverlayFlags.Ik) != 0 &&
                    TryGetOverlayPose(MotionTakeOverlayFlags.Ik, target, out var ikPose))
                {
                    DrawPoseMarker(
                        target,
                        ikPose.WorldPosition,
                        ikPose.WorldRotation,
                        new Color(0.1f, 0.8f, 1f, 0.9f),
                        MarkerKind.Sphere);
                }

                if ((_overlays & MotionTakeOverlayFlags.Automatic) != 0 &&
                    TryGetOverlayPose(MotionTakeOverlayFlags.Automatic, target, out var automaticPose))
                {
                    DrawPoseMarker(
                        target,
                        automaticPose.WorldPosition,
                        automaticPose.WorldRotation,
                        new Color(1f, 0.82f, 0.12f, 0.9f),
                        MarkerKind.Ring);
                }

                if ((_overlays & MotionTakeOverlayFlags.Manual) != 0 &&
                    TryGetOverlayPose(MotionTakeOverlayFlags.Manual, target, out var manualPose))
                {
                    DrawPoseMarker(
                        target,
                        manualPose.WorldPosition,
                        manualPose.WorldRotation,
                        new Color(1f, 0.15f, 0.75f, 0.95f),
                        MarkerKind.Diamond);
                }
            }

            Handles.color = Color.white;
        }

        private bool TryGetOverlayPose(
            MotionTakeOverlayFlags stage,
            PoseTarget target,
            out MotionTakeTargetPose pose)
        {
            pose = default(MotionTakeTargetPose);
            return _overlayPoseSource != null &&
                   _overlayPoseSource.TryGetSolvedTargetPose(stage, target, _frame, out pose);
        }

        private static void DrawPoseMarker(
            PoseTarget target,
            Vector3 position,
            Quaternion rotation,
            Color color,
            MarkerKind markerKind)
        {
            var size = HandleUtility.GetHandleSize(position);
            Handles.color = color;
            switch (markerKind)
            {
                case MarkerKind.Cube:
                    Handles.CubeHandleCap(0, position, rotation, size * 0.035f, EventType.Repaint);
                    break;
                case MarkerKind.Ring:
                    Handles.CircleHandleCap(0, position, rotation, size * 0.05f, EventType.Repaint);
                    break;
                case MarkerKind.Diamond:
                    Handles.RectangleHandleCap(
                        0,
                        position,
                        rotation * Quaternion.Euler(0f, 0f, 45f),
                        size * 0.045f,
                        EventType.Repaint);
                    break;
                default:
                    Handles.SphereHandleCap(0, position, rotation, size * 0.03f, EventType.Repaint);
                    break;
            }

            if (MotionTakeCorrectionAuthoring.SupportsRotation(target))
            {
                // The marker's short forward tick makes rotational offsets visible without adding another handle.
                Handles.DrawLine(position, position + rotation * Vector3.forward * size * 0.09f);
            }
        }

        private enum MarkerKind
        {
            Cube,
            Sphere,
            Ring,
            Diamond
        }
    }
}
