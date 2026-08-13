using System;
using System.Linq.Expressions;
using System.Reflection;
using UnityEngine;

namespace BuildSoft.MotionTakeStudio.Editor
{
    /// <summary>Optional processor hooks with no compile-time dependency on NDMF.</summary>
    public static class ProcessedAvatarHooks
    {
        private static EventInfo _ndmfEvent;
        private static Delegate _ndmfDelegate;

        public static void Install()
        {
            Uninstall();
            var processorType = FindType("nadena.dev.ndmf.AvatarProcessor");
            _ndmfEvent = processorType?.GetEvent(
                "OnManualProcessAvatar",
                BindingFlags.Public | BindingFlags.Static);
            if (_ndmfEvent?.EventHandlerType == null)
            {
                return;
            }

            try
            {
                var invoke = _ndmfEvent.EventHandlerType.GetMethod("Invoke");
                var parameters = invoke?.GetParameters();
                if (parameters == null || parameters.Length < 1 || parameters[0].ParameterType != typeof(GameObject))
                {
                    _ndmfEvent = null;
                    return;
                }

                var lambdaParameters = new ParameterExpression[parameters.Length];
                for (var index = 0; index < parameters.Length; index++)
                {
                    lambdaParameters[index] = Expression.Parameter(parameters[index].ParameterType, parameters[index].Name);
                }

                var callback = typeof(ProcessedAvatarHooks).GetMethod(
                    nameof(OnNdmfProcessed),
                    BindingFlags.NonPublic | BindingFlags.Static);
                var body = Expression.Call(callback, lambdaParameters[0]);
                _ndmfDelegate = Expression.Lambda(_ndmfEvent.EventHandlerType, body, lambdaParameters).Compile();
                _ndmfEvent.AddEventHandler(null, _ndmfDelegate);
            }
            catch (Exception exception)
            {
                Debug.LogWarning("Motion Take Studio could not subscribe to NDMF: " + exception.Message);
                _ndmfEvent = null;
                _ndmfDelegate = null;
            }
        }

        public static void Uninstall()
        {
            if (_ndmfEvent != null && _ndmfDelegate != null)
            {
                try
                {
                    _ndmfEvent.RemoveEventHandler(null, _ndmfDelegate);
                }
                catch
                {
                    // Assemblies can be in the middle of a reload; static reset below is sufficient.
                }
            }

            _ndmfEvent = null;
            _ndmfDelegate = null;
        }

        public static void NotifyDirectProcessedRoot(GameObject root, string source)
        {
            MotionCaptureCoordinator.NotifyProcessedRoot(root, source);
        }

        public static void NotifyProcessingRootDiscovered(GameObject root, string source)
        {
            MotionCaptureCoordinator.NotifyProcessingRoot(root, source);
        }

        internal static bool CanArmOptionalProcessing(
            bool activatorAvailable,
            bool applyOnPlayEnabled,
            bool completionNotifierAvailable,
            bool rootIsEligibleForCompletionCallback)
        {
            return activatorAvailable && applyOnPlayEnabled && completionNotifierAvailable &&
                   rootIsEligibleForCompletionCallback;
        }

        public static bool TryInstallNdmfApplyOnPlayActivator(GameObject root, out string warning)
        {
            warning = null;
            if (root == null)
            {
                return false;
            }

            var activatorType = FindType("nadena.dev.ndmf.runtime.AvatarActivator");
            if (activatorType == null || !typeof(MonoBehaviour).IsAssignableFrom(activatorType))
            {
                return false;
            }

            Component addedActivator = null;
            try
            {
                var configType = FindType("nadena.dev.ndmf.config.Config");
                var applyOnPlay = configType?.GetProperty(
                    "ApplyOnPlay",
                    BindingFlags.Public | BindingFlags.Static);
                if (applyOnPlay == null || applyOnPlay.PropertyType != typeof(bool))
                {
                    warning = "NDMF is installed, but its Apply on Play setting could not be verified.";
                    return false;
                }

                if (!(bool)applyOnPlay.GetValue(null, null))
                {
                    warning =
                        "NDMF is installed, but Apply on Play is disabled; " +
                        "optional processing will be skipped.";
                    return false;
                }

                var completionNotifierAvailable = FindType(
                    "BuildSoft.MotionTakeStudio.Editor.Integrations.VRChat.VrchatCaptureRootLateHook") != null;
                var descriptorType = FindType(
                    "VRC.SDK3.Avatars.Components.VRCAvatarDescriptor");
                var rootIsEligibleForCompletionCallback = descriptorType != null &&
                                                          typeof(Component).IsAssignableFrom(descriptorType) &&
                                                          root.GetComponent(descriptorType) != null;
                if (!CanArmOptionalProcessing(
                        true,
                        true,
                        completionNotifierAvailable,
                        rootIsEligibleForCompletionCallback))
                {
                    warning =
                        "NDMF Apply on Play is enabled, but this root cannot provide a compatible " +
                        "VRChat completion callback; " +
                        "the standard Humanoid clone will be captured.";
                    return false;
                }

                if (root.GetComponent(activatorType) == null)
                {
                    addedActivator = root.AddComponent(activatorType);
                    addedActivator.hideFlags = HideFlags.HideInInspector;
                }

                return true;
            }
            catch (TargetInvocationException exception)
            {
                RemoveAddedActivator(addedActivator);
                warning = "NDMF Apply on Play could not activate the capture clone: " +
                          (exception.InnerException?.Message ?? exception.Message);
                return false;
            }
            catch (Exception exception)
            {
                RemoveAddedActivator(addedActivator);
                warning = "NDMF Apply on Play could not activate the capture clone: " + exception.Message;
                return false;
            }
        }

        private static void RemoveAddedActivator(Component activator)
        {
            if (activator == null)
            {
                return;
            }

            if (Application.isPlaying)
            {
                UnityEngine.Object.Destroy(activator);
            }
            else
            {
                UnityEngine.Object.DestroyImmediate(activator);
            }
        }

        private static void OnNdmfProcessed(GameObject root)
        {
            NotifyDirectProcessedRoot(root, "NDMF OnManualProcessAvatar");
        }

        private static Type FindType(string fullName)
        {
            foreach (var assembly in AppDomain.CurrentDomain.GetAssemblies())
            {
                var type = assembly.GetType(fullName, false);
                if (type != null)
                {
                    return type;
                }
            }

            return null;
        }
    }
}
