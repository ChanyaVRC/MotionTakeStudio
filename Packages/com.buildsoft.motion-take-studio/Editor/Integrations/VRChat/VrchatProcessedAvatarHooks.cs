#if MOTION_TAKE_STUDIO_VRCSDK
using UnityEngine;
using VRC.SDKBase.Editor.BuildPipeline;

namespace BuildSoft.MotionTakeStudio.Editor.Integrations.VRChat
{
    /// <summary>
    /// Captures the exact root reference before VRChat processors can strip the inert marker. Animator and bone
    /// references are deliberately acquired later by the stable-frame queue.
    /// </summary>
    public sealed class VrchatCaptureRootEarlyHook : IVRCSDKPreprocessAvatarCallback
    {
        public int callbackOrder => int.MinValue;

        public bool OnPreprocessAvatar(GameObject avatarGameObject)
        {
            ProcessedAvatarHooks.NotifyDirectProcessedRoot(
                avatarGameObject,
                "VRChat preprocess (early direct root)");
            return true;
        }
    }

    /// <summary>Signals the same direct root after the VRChat callback chain has completed.</summary>
    public sealed class VrchatCaptureRootLateHook : IVRCSDKPreprocessAvatarCallback
    {
        public int callbackOrder => int.MaxValue;

        public bool OnPreprocessAvatar(GameObject avatarGameObject)
        {
            ProcessedAvatarHooks.NotifyDirectProcessedRoot(
                avatarGameObject,
                "VRChat preprocess (late direct root)");
            return true;
        }
    }
}
#endif
