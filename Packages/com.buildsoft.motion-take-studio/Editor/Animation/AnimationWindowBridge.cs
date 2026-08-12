using System;
using UnityEditor;
using UnityEngine;

namespace BuildSoft.MotionTakeStudio.Editor
{
    /// <summary>Opens a clip in Unity's public AnimationWindow API.</summary>
    internal static class AnimationWindowBridge
    {
        [MenuItem("Assets/BuildSoft/Open Clip in Animation Window", true)]
        private static bool ValidateOpenSelected()
        {
            return Selection.activeObject is AnimationClip;
        }

        [MenuItem("Assets/BuildSoft/Open Clip in Animation Window")]
        private static void OpenSelected()
        {
            if (!(Selection.activeObject is AnimationClip clip))
            {
                return;
            }

            if (!Open(clip, out var error))
            {
                EditorUtility.DisplayDialog("Motion Take Studio", error, "OK");
            }
        }

        public static bool Open(AnimationClip clip, out string error)
        {
            error = null;
            if (clip == null)
            {
                error = "No AnimationClip was supplied.";
                return false;
            }

            try
            {
                var window = EditorWindow.GetWindow<AnimationWindow>(false, "Animation", true);
                window.animationClip = clip;
                window.Show();
                window.Focus();
                return true;
            }
            catch (Exception exception)
            {
                error = exception.Message;
                Debug.LogException(exception);
                return false;
            }
        }
    }
}
