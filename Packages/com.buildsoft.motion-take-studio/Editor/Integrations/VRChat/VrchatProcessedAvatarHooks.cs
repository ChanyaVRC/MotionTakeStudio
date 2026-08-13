#if MOTION_TAKE_STUDIO_VRCSDK
using UnityEngine;
using VRC.SDKBase.Editor.BuildPipeline;

namespace BuildSoft.MotionTakeStudio.Editor.Integrations.VRChat
{
    /// <summary>
    /// Participates at the beginning of the VRChat callback chain without reporting completion. Only the late hook
    /// may confirm optional processing; otherwise the raw root could be accepted while processors are still running.
    /// </summary>
    public sealed class VrchatCaptureRootEarlyHook : IVRCSDKPreprocessAvatarCallback
    {
        public int callbackOrder => int.MinValue;

        public bool OnPreprocessAvatar(GameObject avatarGameObject)
        {
            ProcessedAvatarHooks.NotifyProcessingRootDiscovered(
                avatarGameObject,
                "VRChat preprocess (early root discovery)");
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
