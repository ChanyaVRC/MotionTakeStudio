using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using UnityEngine;

namespace BuildSoft.MotionTakeStudio.Editor
{
    /// <summary>
    /// Reads Valve.OpenVR through reflection. The package therefore compiles and remains usable when the
    /// SteamVR Unity Plugin is not installed.
    /// </summary>
    public sealed class ValveOpenVrTrackerProvider : ITrackerPoseProvider
    {
        public const int MinimumTrackerCount = 3;
        public const int MaximumTrackerCount = 11;

        private static readonly TrackerRole[] AutomaticBodyRoles =
        {
            TrackerRole.Waist,
            TrackerRole.LeftFoot,
            TrackerRole.RightFoot,
            TrackerRole.Chest,
            TrackerRole.LeftKnee,
            TrackerRole.RightKnee,
            TrackerRole.LeftElbow,
            TrackerRole.RightElbow
        };

        private readonly Dictionary<string, TrackerRole> _assignedRoles =
            new Dictionary<string, TrackerRole>(StringComparer.Ordinal);
        private readonly Dictionary<string, TrackerRole> _automaticRoles =
            new Dictionary<string, TrackerRole>(StringComparer.Ordinal);
        private readonly List<TrackedDeviceInfo> _devices = new List<TrackedDeviceInfo>();

        private Type _openVrType;
        private Type _poseType;
        private object _trackingOrigin;
        private object _serialNumberProperty;
        private PropertyInfo _systemProperty;
        private MethodInfo _getPosesMethod;
        private MethodInfo _getDeviceClassMethod;
        private MethodInfo _getControllerRoleMethod;
        private MethodInfo _getStringPropertyMethod;
        private Array _poseArray;
        private object _system;
        private string _diagnostic;
        private bool _ownsOpenVrInitialization;

        public ValveOpenVrTrackerProvider()
        {
            BindTypes();
        }

        public string DisplayName => "Valve OpenVR (reflection)";

        public bool IsAvailable
        {
            get
            {
                if (_openVrType == null)
                {
                    BindTypes();
                }

                return ResolveSystem();
            }
        }

        public string Diagnostic => _diagnostic;
        public IReadOnlyList<TrackedDeviceInfo> Devices => _devices;

        public void AssignRole(string deviceId, TrackerRole role)
        {
            if (string.IsNullOrWhiteSpace(deviceId))
            {
                throw new ArgumentException("A tracked-device identifier is required.", nameof(deviceId));
            }

            foreach (var existing in _assignedRoles
                         .Where(pair => pair.Value == role && pair.Key != deviceId)
                         .Select(pair => pair.Key)
                         .ToArray())
            {
                _assignedRoles.Remove(existing);
            }

            foreach (var existing in _automaticRoles
                         .Where(pair => pair.Value == role && pair.Key != deviceId)
                         .Select(pair => pair.Key)
                         .ToArray())
            {
                _automaticRoles.Remove(existing);
            }

            if (role == TrackerRole.Unassigned)
            {
                _assignedRoles.Remove(deviceId);
            }
            else
            {
                _assignedRoles[deviceId] = role;
            }

            _automaticRoles.Remove(deviceId);
        }

        public bool TryGetFrame(double time, TrackerFrame destination, out string warning)
        {
            if (destination == null)
            {
                throw new ArgumentNullException(nameof(destination));
            }

            destination.time = time;
            destination.poses.Clear();
            warning = null;

            if (!ResolveSystem())
            {
                warning = _diagnostic;
                return false;
            }

            try
            {
                _getPosesMethod.Invoke(_system, new[] { _trackingOrigin, (object)0f, _poseArray });
                var candidates = ReadCandidates();
                AssignAutomaticRoles(candidates);

                if (candidates.Any(candidate =>
                        candidate.role != TrackerRole.Head &&
                        candidate.role != TrackerRole.LeftHand &&
                        candidate.role != TrackerRole.RightHand &&
                        !_assignedRoles.ContainsKey(candidate.deviceId)))
                {
                    warning = AppendWarning(
                        warning,
                        "Generic trackers received provisional body roles in device order. Confirm each serial-to-role " +
                        "mapping with AssignRole before recording.");
                }

                candidates.Sort((left, right) => RolePriority(left).CompareTo(RolePriority(right)));
                if (candidates.Count > MaximumTrackerCount)
                {
                    warning = $"OpenVR reported {candidates.Count} tracked devices; only the first " +
                              $"{MaximumTrackerCount} role-mapped devices were captured.";
                }

                _devices.Clear();
                var outputCount = Math.Min(candidates.Count, MaximumTrackerCount);
                for (var index = 0; index < outputCount; index++)
                {
                    var candidate = candidates[index];
                    _devices.Add(new TrackedDeviceInfo(
                        candidate.deviceIndex,
                        candidate.deviceId,
                        candidate.deviceClass,
                        candidate.role,
                        candidate.connected));
                    destination.poses.Add(candidate);
                }

                if (outputCount < MinimumTrackerCount)
                {
                    warning = AppendWarning(
                        warning,
                        $"Only {outputCount} OpenVR devices are available; at least head and two hands are recommended.");
                }

                _diagnostic = warning;
                return outputCount > 0;
            }
            catch (TargetInvocationException exception)
            {
                _diagnostic = "OpenVR pose polling failed: " + (exception.InnerException?.Message ?? exception.Message);
                warning = _diagnostic;
                return false;
            }
            catch (Exception exception)
            {
                _diagnostic = "OpenVR pose polling failed: " + exception.Message;
                warning = _diagnostic;
                return false;
            }
        }

        public void Dispose()
        {
            if (_ownsOpenVrInitialization && _openVrType != null)
            {
                try
                {
                    _openVrType.GetMethod("Shutdown", BindingFlags.Static | BindingFlags.Public)
                        ?.Invoke(null, null);
                }
                catch (Exception exception)
                {
                    Debug.LogWarning("Motion Take Studio could not shut down its OpenVR session: " + exception.Message);
                }
            }

            _ownsOpenVrInitialization = false;
            _system = null;
            _poseArray = null;
            _devices.Clear();
        }

        private void BindTypes()
        {
            _openVrType = FindType("Valve.VR.OpenVR");
            _poseType = FindType("Valve.VR.TrackedDevicePose_t");
            var originType = FindType("Valve.VR.ETrackingUniverseOrigin");
            if (_openVrType == null || _poseType == null || originType == null)
            {
                _diagnostic = "Valve.OpenVR types were not found. Install/enable the SteamVR Unity Plugin.";
                return;
            }

            _systemProperty = _openVrType.GetProperty(
                "System",
                BindingFlags.Static | BindingFlags.Public);
            _trackingOrigin = Enum.Parse(originType, "TrackingUniverseStanding");

            var maxDevices = 64;
            var maximumField = _openVrType.GetField(
                "k_unMaxTrackedDeviceCount",
                BindingFlags.Static | BindingFlags.Public);
            if (maximumField != null)
            {
                maxDevices = Convert.ToInt32(maximumField.GetValue(null));
            }

            _poseArray = Array.CreateInstance(_poseType, maxDevices);

            var propertyType = FindType("Valve.VR.ETrackedDeviceProperty");
            if (propertyType != null)
            {
                _serialNumberProperty = Enum.Parse(propertyType, "Prop_SerialNumber_String");
            }

            _diagnostic = "OpenVR types found; the background session will initialize on first use.";
        }

        private bool ResolveSystem()
        {
            if (_systemProperty == null)
            {
                _diagnostic = "Valve.OpenVR is not present.";
                return false;
            }

            _system = _systemProperty.GetValue(null, null);
            if (_system == null)
            {
                if (!TryInitializeBackgroundApplication(out _system, out _diagnostic))
                {
                    return false;
                }

                _ownsOpenVrInitialization = true;
            }

            var systemType = _system.GetType();
            _getPosesMethod ??= FindMethod(systemType, "GetDeviceToAbsoluteTrackingPose", 3);
            _getDeviceClassMethod ??= FindMethod(systemType, "GetTrackedDeviceClass", 1);
            _getControllerRoleMethod ??= FindMethod(systemType, "GetControllerRoleForTrackedDeviceIndex", 1);
            _getStringPropertyMethod ??= FindMethod(systemType, "GetStringTrackedDeviceProperty", 3);

            if (_getPosesMethod == null || _getDeviceClassMethod == null)
            {
                _diagnostic = "The installed OpenVR API does not expose tracked-device pose methods.";
                return false;
            }

            return true;
        }

        private bool TryInitializeBackgroundApplication(out object system, out string diagnostic)
        {
            system = null;
            diagnostic = null;
            try
            {
                var init = _openVrType.GetMethods(BindingFlags.Static | BindingFlags.Public)
                    .Where(method => method.Name == "Init")
                    .FirstOrDefault(method =>
                    {
                        var parameters = method.GetParameters();
                        return parameters.Length >= 2 && parameters.Length <= 3 &&
                               parameters[0].ParameterType.IsByRef &&
                               parameters[1].ParameterType.IsEnum;
                    });
                if (init == null)
                {
                    diagnostic = "OpenVR is present but exposes no compatible background initialization API.";
                    return false;
                }

                var parameters = init.GetParameters();
                var errorType = parameters[0].ParameterType.GetElementType();
                var applicationType = parameters[1].ParameterType;
                var arguments = new object[parameters.Length];
                arguments[0] = Activator.CreateInstance(errorType);
                arguments[1] = Enum.Parse(applicationType, "VRApplication_Background");
                if (parameters.Length == 3)
                {
                    arguments[2] = string.Empty;
                }

                system = init.Invoke(null, arguments);
                var error = arguments[0]?.ToString();
                if (system == null || (!string.IsNullOrEmpty(error) &&
                                       !string.Equals(error, "None", StringComparison.OrdinalIgnoreCase)))
                {
                    diagnostic = "OpenVR background initialization failed: " + (error ?? "unknown error") +
                                 ". Start SteamVR and verify that no runtime dialog is waiting.";
                    system = null;
                    return false;
                }

                diagnostic = "Motion Take Studio initialized an OpenVR background session.";
                return true;
            }
            catch (TargetInvocationException exception)
            {
                diagnostic = "OpenVR background initialization failed: " +
                             (exception.InnerException?.Message ?? exception.Message);
                return false;
            }
            catch (Exception exception)
            {
                diagnostic = "OpenVR background initialization failed: " + exception.Message;
                return false;
            }
        }

        private List<TrackerPoseSample> ReadCandidates()
        {
            var candidates = new List<TrackerPoseSample>();
            for (var index = 0; index < _poseArray.Length; index++)
            {
                var boxedPose = _poseArray.GetValue(index);
                var connected = ReadBool(boxedPose, "bDeviceIsConnected");
                if (!connected)
                {
                    continue;
                }

                var deviceClassObject = _getDeviceClassMethod.Invoke(_system, new object[] { (uint)index });
                var deviceClass = deviceClassObject?.ToString() ?? "Unknown";
                if (deviceClass.IndexOf("Invalid", StringComparison.OrdinalIgnoreCase) >= 0)
                {
                    continue;
                }

                var id = TryReadSerial((uint)index) ?? $"openvr-device-{index}";
                var role = ResolveBuiltInRole((uint)index, id, deviceClass);
                if (role == TrackerRole.Unassigned &&
                    deviceClass.IndexOf("GenericTracker", StringComparison.OrdinalIgnoreCase) < 0)
                {
                    continue;
                }

                var sample = new TrackerPoseSample
                {
                    deviceIndex = index,
                    deviceId = id,
                    deviceClass = deviceClass,
                    role = role,
                    connected = true,
                    valid = ReadBool(boxedPose, "bPoseIsValid")
                };
                ReadTransform(boxedPose, sample);
                candidates.Add(sample);
            }

            return candidates;
        }

        private TrackerRole ResolveBuiltInRole(uint index, string id, string deviceClass)
        {
            if (_assignedRoles.TryGetValue(id, out var assigned))
            {
                return assigned;
            }

            if (deviceClass.IndexOf("HMD", StringComparison.OrdinalIgnoreCase) >= 0)
            {
                return TrackerRole.Head;
            }

            if (deviceClass.IndexOf("Controller", StringComparison.OrdinalIgnoreCase) >= 0 &&
                _getControllerRoleMethod != null)
            {
                var controllerRole = _getControllerRoleMethod.Invoke(_system, new object[] { index })?.ToString();
                if (controllerRole?.IndexOf("Left", StringComparison.OrdinalIgnoreCase) >= 0)
                {
                    return TrackerRole.LeftHand;
                }

                if (controllerRole?.IndexOf("Right", StringComparison.OrdinalIgnoreCase) >= 0)
                {
                    return TrackerRole.RightHand;
                }
            }

            return _automaticRoles.TryGetValue(id, out var automatic)
                ? automatic
                : TrackerRole.Unassigned;
        }

        private void AssignAutomaticRoles(IList<TrackerPoseSample> candidates)
        {
            var used = new HashSet<TrackerRole>(
                candidates.Where(candidate => candidate.role != TrackerRole.Unassigned)
                    .Select(candidate => candidate.role));

            foreach (var candidate in candidates.Where(candidate => candidate.role == TrackerRole.Unassigned))
            {
                if (_automaticRoles.TryGetValue(candidate.deviceId, out var remembered) && !used.Contains(remembered))
                {
                    candidate.role = remembered;
                    used.Add(remembered);
                    continue;
                }

                foreach (var bodyRole in AutomaticBodyRoles)
                {
                    if (used.Contains(bodyRole))
                    {
                        continue;
                    }

                    candidate.role = bodyRole;
                    _automaticRoles[candidate.deviceId] = bodyRole;
                    used.Add(bodyRole);
                    break;
                }
            }
        }

        private string TryReadSerial(uint deviceIndex)
        {
            if (_getStringPropertyMethod == null || _serialNumberProperty == null)
            {
                return null;
            }

            try
            {
                var parameters = _getStringPropertyMethod.GetParameters();
                var errorType = parameters[2].ParameterType.GetElementType() ?? parameters[2].ParameterType;
                var error = Enum.ToObject(errorType, 0);
                var args = new[] { (object)deviceIndex, _serialNumberProperty, error };
                return _getStringPropertyMethod.Invoke(_system, args) as string;
            }
            catch
            {
                return null;
            }
        }

        private static void ReadTransform(object boxedPose, TrackerPoseSample destination)
        {
            var matrix = ReadField(boxedPose, "mDeviceToAbsoluteTracking");
            if (matrix != null)
            {
                destination.position = new Vector3(
                    ReadFloat(matrix, "m3"),
                    ReadFloat(matrix, "m7"),
                    -ReadFloat(matrix, "m11"));
                var forward = new Vector3(
                    -ReadFloat(matrix, "m2"),
                    -ReadFloat(matrix, "m6"),
                    ReadFloat(matrix, "m10"));
                var up = new Vector3(
                    ReadFloat(matrix, "m1"),
                    ReadFloat(matrix, "m5"),
                    -ReadFloat(matrix, "m9"));
                if (forward.sqrMagnitude > 1e-8f && up.sqrMagnitude > 1e-8f)
                {
                    destination.rotation = Quaternion.LookRotation(forward, up);
                }
            }

            destination.velocity = ReadVector(boxedPose, "vVelocity");
            destination.angularVelocity = ReadVector(boxedPose, "vAngularVelocity");
        }

        private static Vector3 ReadVector(object owner, string fieldName)
        {
            var vector = ReadField(owner, fieldName);
            return vector == null
                ? Vector3.zero
                : new Vector3(ReadFloat(vector, "v0"), ReadFloat(vector, "v1"), -ReadFloat(vector, "v2"));
        }

        private static object ReadField(object owner, string fieldName)
        {
            return owner?.GetType().GetField(fieldName, BindingFlags.Instance | BindingFlags.Public)?.GetValue(owner);
        }

        private static bool ReadBool(object owner, string fieldName)
        {
            var value = ReadField(owner, fieldName);
            return value != null && Convert.ToBoolean(value);
        }

        private static float ReadFloat(object owner, string fieldName)
        {
            var value = ReadField(owner, fieldName);
            return value == null ? 0f : Convert.ToSingle(value);
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

        private static MethodInfo FindMethod(Type type, string name, int parameterCount)
        {
            return type.GetMethods(BindingFlags.Instance | BindingFlags.Public)
                .FirstOrDefault(method => method.Name == name && method.GetParameters().Length == parameterCount);
        }

        private static int RolePriority(TrackerPoseSample sample)
        {
            return sample.role == TrackerRole.Unassigned ? int.MaxValue : (int)sample.role;
        }

        private static string AppendWarning(string current, string addition)
        {
            return string.IsNullOrEmpty(current) ? addition : current + " " + addition;
        }
    }
}
