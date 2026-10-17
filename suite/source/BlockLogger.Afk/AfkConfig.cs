using System;

namespace BlockLogger.Afk;

public class AfkConfig
{
	public const int CurrentSchemaVersion = 2;

	public int SchemaVersion { get; set; } = 2;

	public bool BroadcastMessages { get; set; } = true;

	public bool UseSpectatorMode { get; set; } = true;

	public int LockIntervalMs { get; set; } = 100;

	public int DriftCorrectionIntervalMs { get; set; } = 500;

	public bool ZeroControlsEveryLockTick { get; set; } = false;

	public bool RewritePositionWhenWithinTolerance { get; set; } = false;

	public double LockDistanceTolerance { get; set; } = 0.25;

	public double CombatCooldownSeconds { get; set; } = 120.0;

	public bool DisableAfkWhenDamaged { get; set; } = true;

	public string CombatBlockMessage { get; set; } = "You cannot go AFK for {seconds} more seconds because you were recently damaged.";

	public double CameraDistance { get; set; } = 18.0;

	public double CameraHeight { get; set; } = 7.0;

	public double CameraPitchDegrees { get; set; } = 45.0;

	public double CameraOrbitSpeedDegreesPerSecond { get; set; } = 10.0;

	public bool EnableServerEntitySpin { get; set; } = true;

	public bool TryForceThirdPerson { get; set; } = true;

	public bool DebugClientCameraReflection { get; set; } = false;

	public bool EnableClientCamera { get; set; } = true;

	public bool UseCameraOffset { get; set; } = false;

	public bool ForceThirdPersonEveryTick { get; set; } = false;

	public int ThirdPersonForceIntervalMs { get; set; } = 500;

	public bool OnlyOrbitWhenThirdPersonIsActive { get; set; } = false;

	public bool ReforceThirdPersonIfLost { get; set; } = false;

	public int ClientCameraTickMs { get; set; } = 16;

	public bool UseRenderFrameCamera { get; set; } = true;

	public bool StaticCameraOffset { get; set; } = true;

	public bool UseVanillaThirdPersonZoom { get; set; } = true;

	public bool OrbitByEntityRotation { get; set; } = false;

	public bool WriteCameraYawOnly { get; set; } = true;

	public bool WriteCameraYawPitch { get; set; } = false;

	public bool SmoothOrbitYaw { get; set; } = true;

	public double MaxYawStepDegreesPerTick { get; set; } = 4.0;

	public bool PreventCameraGroundClip { get; set; } = true;

	public double MinimumCameraHeightAbovePlayer { get; set; } = 3.0;

	public bool ClampCameraOffsetToLineOfSight { get; set; } = true;

	public double CameraGroundClearance { get; set; } = 1.0;

	public double CameraCollisionSampleStep { get; set; } = 0.5;

	public double MinimumCameraDistance { get; set; } = 6.0;

	public bool RequireGroundedToEnable { get; set; } = true;

	public double MaxVerticalMotionForGroundedAfk { get; set; } = 0.05;

	public string NotGroundedBlockMessage { get; set; } = "You must be standing on solid ground to use /afk.";

	public bool RestoreGameModeOnDisconnect { get; set; } = true;

	public bool RestoreOriginalGameMode { get; set; } = false;

	public string ReturnGameMode { get; set; } = "Survival";

	public bool DisableXSkillsSurvivalXpWhileAfk { get; set; } = true;

	public bool StopMobSpawnsAroundAfkPlayers { get; set; } = true;

	public bool PauseHungerWhileAfk { get; set; } = true;

	public bool PauseHydrateOrDiedrateThirstWhileAfk { get; set; } = true;

	public bool AutoKickAfkEnabled { get; set; } = false;

	public double AutoKickAfterMinutes { get; set; } = 120.0;

	public int AutoKickWarningSeconds { get; set; } = 60;

	public string AutoKickWarningMessage { get; set; } = "You will be kicked in {seconds} seconds for being AFK too long.";

	public string AutoKickReason { get; set; } = "Kicked for being AFK too long.";

	public bool DebugLogging { get; set; } = false;

	public void Clamp()
	{
		SchemaVersion = Math.Max(2, SchemaVersion);
		LockIntervalMs = Math.Clamp(LockIntervalMs, 20, 5000);
		DriftCorrectionIntervalMs = Math.Clamp(DriftCorrectionIntervalMs, 50, 30000);
		LockDistanceTolerance = Math.Clamp(LockDistanceTolerance, 0.01, 10.0);
		CombatCooldownSeconds = Math.Clamp(CombatCooldownSeconds, 0.0, 86400.0);
		CameraDistance = Math.Clamp(CameraDistance, 1.0, 100.0);
		CameraHeight = Math.Clamp(CameraHeight, -25.0, 100.0);
		CameraPitchDegrees = Math.Clamp(CameraPitchDegrees, -89.0, 89.0);
		CameraOrbitSpeedDegreesPerSecond = Math.Clamp(CameraOrbitSpeedDegreesPerSecond, -180.0, 180.0);
		ThirdPersonForceIntervalMs = Math.Clamp(ThirdPersonForceIntervalMs, 50, 10000);
		ClientCameraTickMs = Math.Clamp(ClientCameraTickMs, 8, 1000);
		MaxYawStepDegreesPerTick = Math.Clamp(MaxYawStepDegreesPerTick, 0.1, 45.0);
		MinimumCameraHeightAbovePlayer = Math.Clamp(MinimumCameraHeightAbovePlayer, 0.0, 50.0);
		CameraGroundClearance = Math.Clamp(CameraGroundClearance, 0.0, 10.0);
		CameraCollisionSampleStep = Math.Clamp(CameraCollisionSampleStep, 0.1, 5.0);
		MinimumCameraDistance = Math.Clamp(MinimumCameraDistance, 1.0, 50.0);
		MaxVerticalMotionForGroundedAfk = Math.Clamp(MaxVerticalMotionForGroundedAfk, 0.0, 5.0);
		AutoKickAfterMinutes = Math.Clamp(AutoKickAfterMinutes, 1.0, 10080.0);
		AutoKickWarningSeconds = Math.Clamp(AutoKickWarningSeconds, 0, 3600);
		CombatBlockMessage = (string.IsNullOrWhiteSpace(CombatBlockMessage) ? "You cannot go AFK for {seconds} more seconds because you were recently damaged." : CombatBlockMessage.Trim());
		NotGroundedBlockMessage = (string.IsNullOrWhiteSpace(NotGroundedBlockMessage) ? "You must be standing on solid ground to use /afk." : NotGroundedBlockMessage.Trim());
		ReturnGameMode = (string.IsNullOrWhiteSpace(ReturnGameMode) ? "Survival" : ReturnGameMode.Trim());
		AutoKickWarningMessage = (string.IsNullOrWhiteSpace(AutoKickWarningMessage) ? "You will be kicked in {seconds} seconds for being AFK too long." : AutoKickWarningMessage.Trim());
		AutoKickReason = (string.IsNullOrWhiteSpace(AutoKickReason) ? "Kicked for being AFK too long." : AutoKickReason.Trim());
	}
}
