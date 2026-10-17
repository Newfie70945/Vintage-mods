using ProtoBuf;

namespace BlockLogger.Afk;

[ProtoContract]
public class AfkCameraPacket
{
    [ProtoMember(1)] public bool Enabled { get; set; }
    [ProtoMember(2)] public double CameraDistance { get; set; }
    [ProtoMember(3)] public double CameraHeight { get; set; }
    [ProtoMember(4)] public double CameraPitchDegrees { get; set; }
    [ProtoMember(5)] public double CameraOrbitSpeedDegreesPerSecond { get; set; }
    [ProtoMember(6)] public bool TryForceThirdPerson { get; set; }
    [ProtoMember(7)] public bool DebugClientCameraReflection { get; set; }
    [ProtoMember(8)] public bool EnableClientCamera { get; set; }
    [ProtoMember(9)] public bool UseCameraOffset { get; set; }
    [ProtoMember(10)] public bool ForceThirdPersonEveryTick { get; set; }
    [ProtoMember(11)] public bool PreventCameraGroundClip { get; set; }
    [ProtoMember(12)] public double MinimumCameraHeightAbovePlayer { get; set; }
    [ProtoMember(13)] public int ThirdPersonForceIntervalMs { get; set; }
    [ProtoMember(14)] public bool OnlyOrbitWhenThirdPersonIsActive { get; set; }
    [ProtoMember(15)] public bool ReforceThirdPersonIfLost { get; set; }
    [ProtoMember(16)] public int ClientCameraTickMs { get; set; }
    [ProtoMember(17)] public bool OrbitByEntityRotation { get; set; }
    [ProtoMember(18)] public bool WriteCameraYawPitch { get; set; }
    [ProtoMember(19)] public bool WriteCameraYawOnly { get; set; }
    [ProtoMember(20)] public bool SmoothOrbitYaw { get; set; }
    [ProtoMember(21)] public double MaxYawStepDegreesPerTick { get; set; }
    [ProtoMember(22)] public bool UseRenderFrameCamera { get; set; }
    [ProtoMember(23)] public bool StaticCameraOffset { get; set; }
    [ProtoMember(24)] public bool ClampCameraOffsetToLineOfSight { get; set; }
    [ProtoMember(25)] public double CameraGroundClearance { get; set; }
    [ProtoMember(26)] public double CameraCollisionSampleStep { get; set; }
    [ProtoMember(27)] public double MinimumCameraDistance { get; set; }
    [ProtoMember(28)] public bool UseVanillaThirdPersonZoom { get; set; }
}
