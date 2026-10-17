using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Reflection;
using BlockLogger.Services;
using Newfies.ChatProtocol;
using NewfiesAntiCheat.Shared;
using Vintagestory.API.Common;
using Vintagestory.API.Common.Entities;
using Vintagestory.API.Datastructures;
using Vintagestory.API.MathTools;
using Vintagestory.API.Server;
using Vintagestory.Server;

namespace BlockLogger.AntiCheat;

internal sealed class PacketGuardService : IDisposable
{
	internal sealed class TrustedTeleportEventSnapshot
	{
		public long Utc { get; set; }

		public string PlayerName { get; set; } = "";

		public string PlayerUid { get; set; } = "";

		public double FromX { get; set; }

		public double FromY { get; set; }

		public double FromZ { get; set; }

		public int FromDimension { get; set; }

		public double ToX { get; set; }

		public double ToY { get; set; }

		public double ToZ { get; set; }

		public int ToDimension { get; set; }

		public string Reason { get; set; } = "";

		public string Verification { get; set; } = "";
	}

	internal sealed class TrustedTeleportSourceSnapshot
	{
		public string Code { get; set; } = "";

		public string Name { get; set; } = "";

		public string Status { get; set; } = "Active";

		public string TrustScope { get; set; } = "Movement + Reach";

		public string Verification { get; set; } = "";

		public long ObservedCount { get; set; }

		public long LastSeenUtc { get; set; }

		public string LastPlayerName { get; set; } = "";

		public string LastReason { get; set; } = "";

		public List<TrustedTeleportEventSnapshot> RecentEvents { get; set; } = new List<TrustedTeleportEventSnapshot>();
	}

	internal sealed class PacketSuppressionSnapshot
	{
		public string Code { get; set; } = "";

		public string Name { get; set; } = "";

		public string Category { get; set; } = "Safety";

		public string Description { get; set; } = "";

		public long Count { get; set; }

		public long LastSeenUtc { get; set; }

		public string LastPlayerName { get; set; } = "";

		public string LastDetail { get; set; } = "";
	}

	private readonly record struct MountProfile(string Code, string MountCode, double MaxHorizontalSpeed, double MaxVerticalSpeed, double MaxAcceleration, double MaxRiderDistance, double MaxRiderRelativeSpeed);

	[Flags]
	private enum MovementRiskSignal
	{
		None = 0,
		HardMove = 1,
		Speed = 2,
		Acceleration = 4,
		Noclip = 8,
		Mount = 0x10,
		RollbackResistance = 0x20
	}

	private sealed class MovementState
	{
		public double X;

		public double Y;

		public double Z;

		public double SafeX;

		public double SafeY;

		public double SafeZ;

		public int Dimension;

		public long EntityId;

		public long LastAcceptedUtc;

		public long GraceUntilUtc;

		public long LastViolationUtc;

		public int ViolationCount;

		public bool WasMounted;

		public int MountedSuspiciousSamples;

		public long MountedSuspiciousSinceUtc;

		public double LastMountedHorizontalSpeed;

		public long LastMountedSpeedUtc;

		public int SuspiciousCount;

		public long SuspiciousSinceUtc;

		public long SuspiciousLastUtc;

		public long SpeedWindowStartUtc;

		public double SpeedWindowDistance;

		public int SpeedSuspiciousWindows;

		public double LastClosedWindowSpeed;

		public double LastInstantHorizontalSpeed;

		public double SmoothedHorizontalSpeed;

		public long LastInstantSpeedUtc;

		public int AccelerationSuspiciousSamples;

		public long AccelerationSuspiciousSinceUtc;

		public int EndpointCollisionSamples;

		public double EndpointCollisionStartX;

		public double EndpointCollisionStartZ;

		public int EndpointCollisionDimension;

		public double MovementRiskScore;

		public long MovementRiskUpdatedUtc;

		public MovementRiskSignal MovementRiskSignalMask;

		public long MovementRiskSignalWindowStartUtc;

		public double RiskHardMove;

		public double RiskSpeed;

		public double RiskAcceleration;

		public double RiskNoclip;

		public double RiskMount;

		public double RiskRollbackResistance;

		public double RiskCorrelation;

		public long LastRollbackUtc;

		public int RollbackLoopCount;

		public bool RollbackResistanceCounted;

		public string LastFindingCode = "";

		public long LastFindingUtc;

		public int UnverifiedTeleportCount;

		public long UnverifiedTeleportWindowStartUtc;

		public long TrackedMountEntityId;

		public double MountX;

		public double MountY;

		public double MountZ;

		public long MountPosUtc;

		public readonly List<SafePositionSample> SafePositions = new List<SafePositionSample>();

		public readonly Queue<DiagnosticSpeedSample> DiagnosticSpeeds = new Queue<DiagnosticSpeedSample>();

		public static MovementState From(EntityPos pos, long now, long entityId)
		{
			MovementState movementState = new MovementState
			{
				EntityId = entityId
			};
			movementState.SafeX = pos.X;
			movementState.SafeY = pos.Y;
			movementState.SafeZ = pos.Z;
			movementState.SpeedWindowStartUtc = now;
			movementState.Accept(pos, now);
			return movementState;
		}

		public void Accept(EntityPos pos, long now, bool markSafe = true)
		{
			X = pos.X;
			Y = pos.Y;
			Z = pos.Z;
			Dimension = pos.Dimension;
			LastAcceptedUtc = now;
			if (markSafe)
			{
				SafeX = pos.X;
				SafeY = pos.Y;
				SafeZ = pos.Z;
				AddSafePosition(pos, now);
			}
			SuspiciousCount = 0;
			SuspiciousSinceUtc = 0L;
			SuspiciousLastUtc = 0L;
		}

		public void AcceptRollback(SafePositionSample sample, long now)
		{
			X = sample.X;
			Y = sample.Y;
			Z = sample.Z;
			Dimension = sample.Dimension;
			SafeX = sample.X;
			SafeY = sample.Y;
			SafeZ = sample.Z;
			LastAcceptedUtc = now;
			SuspiciousCount = 0;
			SuspiciousSinceUtc = 0L;
			SuspiciousLastUtc = 0L;
		}

		private void AddSafePosition(EntityPos pos, long now)
		{
			if (SafePositions.Count > 0)
			{
				SafePositionSample safePositionSample = SafePositions[SafePositions.Count - 1];
				double num = pos.X - safePositionSample.X;
				double num2 = pos.Y - safePositionSample.Y;
				double num3 = pos.Z - safePositionSample.Z;
				if (safePositionSample.Dimension == pos.Dimension && num * num + num2 * num2 + num3 * num3 < 0.0025)
				{
					return;
				}
			}
			SafePositions.Add(new SafePositionSample(pos.X, pos.Y, pos.Z, pos.Dimension, now));
			if (SafePositions.Count > 64)
			{
				SafePositions.RemoveRange(0, SafePositions.Count - 64);
			}
		}

		public SafePositionSample GetRollbackPosition(long now, int safetyLagMs, int maxAgeMs, int historySize)
		{
			long num = now - Math.Max(0, safetyLagMs);
			long num2 = now - Math.Max(1000, maxAgeMs);
			int num3 = Math.Max(0, SafePositions.Count - Math.Max(1, historySize));
			for (int num4 = SafePositions.Count - 1; num4 >= num3; num4--)
			{
				SafePositionSample result = SafePositions[num4];
				if (result.Utc <= num)
				{
					if (result.Utc < num2)
					{
						break;
					}
					if (result.Dimension == Dimension)
					{
						return result;
					}
				}
			}
			return new SafePositionSample(SafeX, SafeY, SafeZ, Dimension, LastAcceptedUtc);
		}

		public IEnumerable<SafePositionSample> GetRollbackCandidates(long now, int safetyLagMs, int maxAgeMs, int historySize, int maxCandidates)
		{
			long latestAllowed = now - Math.Max(0, safetyLagMs);
			long oldestAllowed = now - Math.Max(1000, maxAgeMs);
			int oldestIndex = Math.Max(0, SafePositions.Count - Math.Max(1, historySize));
			int yielded = 0;
			int i = SafePositions.Count - 1;
			while (i >= oldestIndex && yielded < Math.Max(1, maxCandidates))
			{
				SafePositionSample sample = SafePositions[i];
				if (sample.Utc <= latestAllowed)
				{
					if (sample.Utc < oldestAllowed)
					{
						break;
					}
					if (sample.Dimension == Dimension)
					{
						yielded++;
						yield return sample;
					}
				}
				i--;
			}
		}

		public void NoteRollback(long now)
		{
			if (LastRollbackUtc > 0 && now - LastRollbackUtc <= 5000)
			{
				RollbackLoopCount++;
			}
			else
			{
				RollbackLoopCount = 1;
			}
			LastRollbackUtc = now;
			RollbackResistanceCounted = false;
		}

		public int RegisterUnverifiedTeleport(long now, int windowMs)
		{
			if (UnverifiedTeleportWindowStartUtc <= 0 || now - UnverifiedTeleportWindowStartUtc > Math.Max(1000, windowMs))
			{
				UnverifiedTeleportWindowStartUtc = now;
				UnverifiedTeleportCount = 1;
			}
			else
			{
				UnverifiedTeleportCount++;
			}
			return UnverifiedTeleportCount;
		}

		public bool ShouldReportFinding(string code, long now, int cooldownMs)
		{
			if (cooldownMs <= 0 || !string.Equals(code, LastFindingCode, StringComparison.Ordinal) || now - LastFindingUtc >= cooldownMs)
			{
				LastFindingCode = code;
				LastFindingUtc = now;
				return true;
			}
			return false;
		}

		public bool ObserveMountDisplacement(long mountEntityId, EntityPos mountPos, long now, out double horizontalDistance, out long elapsedMs)
		{
			horizontalDistance = 0.0;
			elapsedMs = 0L;
			if (TrackedMountEntityId != mountEntityId || MountPosUtc <= 0 || now <= MountPosUtc)
			{
				TrackedMountEntityId = mountEntityId;
				MountX = mountPos.X;
				MountY = mountPos.Y;
				MountZ = mountPos.Z;
				MountPosUtc = now;
				return false;
			}
			double num = mountPos.X - MountX;
			double num2 = mountPos.Z - MountZ;
			horizontalDistance = Math.Sqrt(num * num + num2 * num2);
			elapsedMs = now - MountPosUtc;
			MountX = mountPos.X;
			MountY = mountPos.Y;
			MountZ = mountPos.Z;
			MountPosUtc = now;
			return elapsedMs > 0;
		}

		public double ObserveAcceleration(double instantaneousSpeed, long now, double minSpeed, double accelerationLimit, int confirmWindowMs, int sampleMinMs, double smoothingFactor, out int suspiciousSamples, out double smoothedSpeed)
		{
			double num = Math.Clamp(smoothingFactor, 0.05, 1.0);
			if (SmoothedHorizontalSpeed <= 0.0)
			{
				SmoothedHorizontalSpeed = instantaneousSpeed;
			}
			else
			{
				SmoothedHorizontalSpeed += (instantaneousSpeed - SmoothedHorizontalSpeed) * num;
			}
			smoothedSpeed = SmoothedHorizontalSpeed;
			if (LastInstantSpeedUtc <= 0 || now <= LastInstantSpeedUtc)
			{
				LastInstantHorizontalSpeed = SmoothedHorizontalSpeed;
				LastInstantSpeedUtc = now;
				suspiciousSamples = AccelerationSuspiciousSamples;
				return 0.0;
			}
			long num2 = now - LastInstantSpeedUtc;
			if (num2 < Math.Max(1, sampleMinMs))
			{
				suspiciousSamples = AccelerationSuspiciousSamples;
				return 0.0;
			}
			double num3 = (double)num2 / 1000.0;
			double num4 = Math.Max(0.0, SmoothedHorizontalSpeed - LastInstantHorizontalSpeed) / num3;
			if (SmoothedHorizontalSpeed >= minSpeed && num4 > accelerationLimit)
			{
				if (AccelerationSuspiciousSamples == 0 || now - AccelerationSuspiciousSinceUtc > confirmWindowMs)
				{
					AccelerationSuspiciousSamples = 1;
					AccelerationSuspiciousSinceUtc = now;
				}
				else
				{
					AccelerationSuspiciousSamples++;
				}
			}
			else
			{
				AccelerationSuspiciousSamples = 0;
				AccelerationSuspiciousSinceUtc = 0L;
			}
			LastInstantHorizontalSpeed = SmoothedHorizontalSpeed;
			LastInstantSpeedUtc = now;
			suspiciousSamples = AccelerationSuspiciousSamples;
			return num4;
		}

		public double ObserveEndpointPenetration(double previousX, double previousZ, double currentX, double currentZ, int dimension)
		{
			if (EndpointCollisionSamples <= 0 || EndpointCollisionDimension != dimension)
			{
				EndpointCollisionStartX = previousX;
				EndpointCollisionStartZ = previousZ;
				EndpointCollisionDimension = dimension;
				EndpointCollisionSamples = 1;
			}
			else
			{
				EndpointCollisionSamples++;
			}
			double num = currentX - EndpointCollisionStartX;
			double num2 = currentZ - EndpointCollisionStartZ;
			return Math.Sqrt(num * num + num2 * num2);
		}

		public void ResetEndpointPenetration()
		{
			EndpointCollisionSamples = 0;
			EndpointCollisionStartX = 0.0;
			EndpointCollisionStartZ = 0.0;
			EndpointCollisionDimension = 0;
		}

		public double ObserveMountedAcceleration(double horizontalSpeed, long now, double accelerationLimit)
		{
			double result = 0.0;
			if (LastMountedSpeedUtc > 0 && now > LastMountedSpeedUtc)
			{
				double num = (double)(now - LastMountedSpeedUtc) / 1000.0;
				result = Math.Max(0.0, horizontalSpeed - LastMountedHorizontalSpeed) / num;
			}
			LastMountedHorizontalSpeed = horizontalSpeed;
			LastMountedSpeedUtc = now;
			return result;
		}

		public void ObserveMountedViolation(long now, int confirmWindowMs)
		{
			if (MountedSuspiciousSamples == 0 || now - MountedSuspiciousSinceUtc > confirmWindowMs)
			{
				MountedSuspiciousSamples = 1;
				MountedSuspiciousSinceUtc = now;
			}
			else
			{
				MountedSuspiciousSamples++;
			}
		}

		public void ResetMountedViolation()
		{
			MountedSuspiciousSamples = 0;
			MountedSuspiciousSinceUtc = 0L;
		}

		public void DecayMovementRisk(long now, bool enabled, double decayPerSecond)
		{
			if (!enabled)
			{
				MovementRiskScore = 0.0;
				RiskHardMove = (RiskSpeed = (RiskAcceleration = (RiskNoclip = (RiskMount = (RiskRollbackResistance = (RiskCorrelation = 0.0))))));
				MovementRiskUpdatedUtc = now;
				return;
			}
			if (MovementRiskUpdatedUtc <= 0)
			{
				MovementRiskUpdatedUtc = now;
				return;
			}
			long num = Math.Max(0L, now - MovementRiskUpdatedUtc);
			if (num > 0)
			{
				double num2 = (double)num / 1000.0;
				RiskHardMove = Math.Max(0.0, RiskHardMove - decayPerSecond * 1.0 * num2);
				RiskSpeed = Math.Max(0.0, RiskSpeed - decayPerSecond * 1.2 * num2);
				RiskAcceleration = Math.Max(0.0, RiskAcceleration - decayPerSecond * 1.6 * num2);
				RiskNoclip = Math.Max(0.0, RiskNoclip - decayPerSecond * 0.5 * num2);
				RiskMount = Math.Max(0.0, RiskMount - decayPerSecond * 0.9 * num2);
				RiskRollbackResistance = Math.Max(0.0, RiskRollbackResistance - decayPerSecond * 0.6 * num2);
				RiskCorrelation = Math.Max(0.0, RiskCorrelation - decayPerSecond * 1.0 * num2);
				MovementRiskScore = Math.Clamp(RiskHardMove + RiskSpeed + RiskAcceleration + RiskNoclip + RiskMount + RiskRollbackResistance + RiskCorrelation, 0.0, 100.0);
			}
			MovementRiskUpdatedUtc = now;
			if (MovementRiskScore <= 0.001)
			{
				MovementRiskSignalMask = MovementRiskSignal.None;
				MovementRiskSignalWindowStartUtc = 0L;
				RollbackLoopCount = 0;
			}
		}

		public double AddMovementRisk(bool enabled, MovementRiskSignal signal, double amount, long now, int correlationWindowMs, double correlationBonus)
		{
			if (!enabled)
			{
				return 0.0;
			}
			if (MovementRiskUpdatedUtc <= 0)
			{
				MovementRiskUpdatedUtc = now;
			}
			if (MovementRiskSignalWindowStartUtc <= 0 || now - MovementRiskSignalWindowStartUtc > Math.Max(250, correlationWindowMs))
			{
				MovementRiskSignalWindowStartUtc = now;
				MovementRiskSignalMask = MovementRiskSignal.None;
			}
			double num = Math.Max(0.0, amount);
			if (signal != MovementRiskSignal.None && (MovementRiskSignalMask & signal) == 0 && MovementRiskSignalMask != MovementRiskSignal.None)
			{
				RiskCorrelation += Math.Max(0.0, correlationBonus);
			}
			switch (signal)
			{
			case MovementRiskSignal.HardMove:
				RiskHardMove += num;
				break;
			case MovementRiskSignal.Speed:
				RiskSpeed += num;
				break;
			case MovementRiskSignal.Acceleration:
				RiskAcceleration += num;
				break;
			case MovementRiskSignal.Noclip:
				RiskNoclip += num;
				break;
			case MovementRiskSignal.Mount:
				RiskMount += num;
				break;
			case MovementRiskSignal.RollbackResistance:
				RiskRollbackResistance += num;
				break;
			}
			MovementRiskSignalMask |= signal;
			MovementRiskScore = Math.Clamp(RiskHardMove + RiskSpeed + RiskAcceleration + RiskNoclip + RiskMount + RiskRollbackResistance + RiskCorrelation, 0.0, 100.0);
			MovementRiskUpdatedUtc = now;
			return MovementRiskScore;
		}

		public string GetMovementConfidenceBand(double guarded, double high, double critical)
		{
			if (MovementRiskScore >= critical)
			{
				return "critical";
			}
			if (MovementRiskScore >= high)
			{
				return "high";
			}
			if (MovementRiskScore >= guarded)
			{
				return "guarded";
			}
			return "low";
		}

		public string GetMovementConfidenceSignals()
		{
			if (MovementRiskSignalMask == MovementRiskSignal.None)
			{
				return "none";
			}
			List<string> list = new List<string>(5);
			if ((MovementRiskSignalMask & MovementRiskSignal.HardMove) != 0)
			{
				list.Add("hardmove");
			}
			if ((MovementRiskSignalMask & MovementRiskSignal.Speed) != 0)
			{
				list.Add("speed");
			}
			if ((MovementRiskSignalMask & MovementRiskSignal.Acceleration) != 0)
			{
				list.Add("acceleration");
			}
			if ((MovementRiskSignalMask & MovementRiskSignal.Noclip) != 0)
			{
				list.Add("noclip");
			}
			if ((MovementRiskSignalMask & MovementRiskSignal.Mount) != 0)
			{
				list.Add("mount");
			}
			if ((MovementRiskSignalMask & MovementRiskSignal.RollbackResistance) != 0)
			{
				list.Add("rollback-resistance");
			}
			return string.Join(",", list);
		}

		public void AddDiagnosticSpeedSample(double speed, long now, int maxSamples)
		{
			DiagnosticSpeeds.Enqueue(new DiagnosticSpeedSample(now, Math.Max(0.0, speed)));
			int num = Math.Clamp(maxSamples, 3, 16);
			while (DiagnosticSpeeds.Count > num)
			{
				DiagnosticSpeeds.Dequeue();
			}
		}

		public string GetDiagnosticSpeedHistory()
		{
			if (DiagnosticSpeeds.Count == 0)
			{
				return "none";
			}
			return string.Join(",", DiagnosticSpeeds.Select((DiagnosticSpeedSample x) => x.Speed.ToString("0.00", CultureInfo.InvariantCulture)));
		}

		public void AddSpeedSample(double horizontalDistance, long now)
		{
			if (SpeedWindowStartUtc <= 0)
			{
				SpeedWindowStartUtc = now;
			}
			SpeedWindowDistance += Math.Max(0.0, horizontalDistance);
		}

		public bool TryCloseSpeedWindow(long now, int windowMs, double speedLimit, int requiredWindows, out double observedSpeed, out int confirmedWindows)
		{
			observedSpeed = 0.0;
			confirmedWindows = SpeedSuspiciousWindows;
			long num = now - SpeedWindowStartUtc;
			if (num < windowMs)
			{
				return false;
			}
			observedSpeed = ((num > 0) ? (SpeedWindowDistance * 1000.0 / (double)num) : 0.0);
			LastClosedWindowSpeed = observedSpeed;
			if (observedSpeed > speedLimit)
			{
				SpeedSuspiciousWindows++;
			}
			else
			{
				SpeedSuspiciousWindows = 0;
			}
			confirmedWindows = SpeedSuspiciousWindows;
			SpeedWindowStartUtc = now;
			SpeedWindowDistance = 0.0;
			return SpeedSuspiciousWindows >= requiredWindows;
		}

		public void ResetAnalysis(long now)
		{
			SuspiciousCount = 0;
			SuspiciousSinceUtc = 0L;
			SuspiciousLastUtc = 0L;
			SpeedWindowStartUtc = now;
			SpeedWindowDistance = 0.0;
			SpeedSuspiciousWindows = 0;
			LastClosedWindowSpeed = 0.0;
			LastInstantHorizontalSpeed = 0.0;
			LastInstantSpeedUtc = 0L;
			AccelerationSuspiciousSamples = 0;
			AccelerationSuspiciousSinceUtc = 0L;
			EndpointCollisionSamples = 0;
		}
	}

	private readonly record struct DiagnosticSpeedSample(long Utc, double Speed);

	private readonly record struct SafePositionSample(double X, double Y, double Z, int Dimension, long Utc);

	private sealed class ViolationCounter
	{
		public int Count;

		public long LastUtc;

		public string LastSignature = "";

		private readonly Dictionary<string, long> signatures = new Dictionary<string, long>(StringComparer.Ordinal);

		public int DistinctSignatureCount => signatures.Count;

		public void RecordSignature(string signature, long now)
		{
			if (!string.IsNullOrEmpty(signature))
			{
				signatures[signature] = now;
			}
		}

		public void PruneSignatures(long cutoffUtc)
		{
			if (signatures.Count != 0)
			{
				string[] array = (from x in signatures
					where x.Value < cutoffUtc
					select x.Key).ToArray();
				foreach (string key in array)
				{
					signatures.Remove(key);
				}
				if (Count == 0 && signatures.Count > 0)
				{
					signatures.Clear();
				}
			}
		}
	}

	private readonly record struct ReachPositionSample(long Utc, double EyeX, double EyeY, double EyeZ, int Dimension, long EntityId);

	private sealed class PendingTranslocatorTransition
	{
		public int SourceX;

		public int SourceY;

		public int SourceZ;

		public int SourceDimension;

		public long FirstSeenUtc;

		public long LastSeenUtc;

		public bool Armed;

		public bool HasLinkedTarget;

		public int TargetX;

		public int TargetY;

		public int TargetZ;

		public int TargetDimension;
	}

	private readonly record struct TrustedTransitionSample(double X, double Y, double Z, int Dimension, bool Mounted)
	{
		public static TrustedTransitionSample From(EntityPos pos, bool mounted)
		{
			return new TrustedTransitionSample(pos.X, pos.Y, pos.Z, pos.Dimension, mounted);
		}
	}

	private readonly record struct ReachCheckResult(double Distance, double Allowed, double LatencyAllowance, double ActionAllowance, double CurrentDistance, double ExactHitDistance, bool UsedHistory, long HistorySampleAgeMs, int HistoryRewindMs, bool TeleportGrace, bool PositionUnavailable)
	{
		public static ReachCheckResult AllowedByTeleportGrace => new ReachCheckResult(0.0, double.MaxValue, 0.0, 0.0, 0.0, -1.0, UsedHistory: false, 0L, 0, TeleportGrace: true, PositionUnavailable: false);

		public static ReachCheckResult Unavailable => new ReachCheckResult(0.0, double.MaxValue, 0.0, 0.0, 0.0, -1.0, UsedHistory: false, 0L, 0, TeleportGrace: false, PositionUnavailable: true);
	}

	private readonly record struct BreakSample(long Utc, int X, int Y, int Z, int Dimension, int BlockId, double ExpectedMs, float Resistance, float MiningSpeed, float BlockModifier, int RequiredTier, int ToolTier);

	private enum MiningExpectationReliability
	{
		Authoritative,
		Compatible,
		CustomUnknown
	}

	private readonly record struct MiningExpectation(bool Available, double ExpectedMs, float Resistance, float MiningSpeed, float BlockModifier, int RequiredTier, int ToolTier, bool TierAdequate, bool MaterialCapable, bool CanMineNormally, EnumBlockMaterial Material, MiningExpectationReliability Reliability, string ToolCode)
	{
		public static MiningExpectation Unavailable => new MiningExpectation(Available: false, 0.0, 0f, 0f, 0f, 0, 0, TierAdequate: true, MaterialCapable: true, CanMineNormally: true, EnumBlockMaterial.Other, MiningExpectationReliability.CustomUnknown, "unknown");
	}

	private readonly ICoreServerAPI api;

	private readonly AntiCheatConfig config;

	private readonly Action<IServerPlayer, string, AntiCheatSeverity, string, string, string> reportFinding;

	private readonly ServerTickHealthMonitor tickHealth;

	private readonly PacketIngressGuard ingressGuard;

	private readonly Dictionary<string, MovementState> movement = new Dictionary<string, MovementState>(StringComparer.OrdinalIgnoreCase);

	private readonly Dictionary<string, PacketFlightReviewWindow> flightReviews = new Dictionary<string, PacketFlightReviewWindow>(StringComparer.OrdinalIgnoreCase);

	private readonly Dictionary<string, Queue<BreakSample>> breakHistory = new Dictionary<string, Queue<BreakSample>>(StringComparer.OrdinalIgnoreCase);

	private readonly Dictionary<string, ViolationCounter> reachViolations = new Dictionary<string, ViolationCounter>(StringComparer.OrdinalIgnoreCase);

	private readonly Dictionary<string, Queue<ReachPositionSample>> reachPositionHistory = new Dictionary<string, Queue<ReachPositionSample>>(StringComparer.OrdinalIgnoreCase);

	private readonly Dictionary<string, ViolationCounter> miningViolations = new Dictionary<string, ViolationCounter>(StringComparer.OrdinalIgnoreCase);

	private readonly Dictionary<string, Queue<long>> miningCadenceWindows = new Dictionary<string, Queue<long>>(StringComparer.OrdinalIgnoreCase);

	private readonly Dictionary<string, TrustedTransitionSample> trustedTransitionSamples = new Dictionary<string, TrustedTransitionSample>(StringComparer.OrdinalIgnoreCase);

	private readonly Dictionary<string, PendingTranslocatorTransition> pendingTranslocatorTransitions = new Dictionary<string, PendingTranslocatorTransition>(StringComparer.OrdinalIgnoreCase);

	private readonly Dictionary<string, long> th3EssentialsSpawnIntents = new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase);

	private const string ServerTeleportHarmonyId = "newfies.serveressentials.packetguard.teleporttrust";

	private static WeakReference<PacketGuardService>? activeServerTeleportHook;

	private object? serverTeleportHarmony;

	private bool serverTeleportHookInstalled;

	private const int TranslocatorArmDelayMs = 750;

	private const int TranslocatorDepartureWindowMs = 3000;

	private readonly bool tprunesEnabled;

	private readonly bool th3EssentialsEnabled;

	private long sharedSamplerTickId;

	private long lastMovementSampleUtc;

	private long lastFlightReviewSampleUtc;

	private bool flightReviewRuntimeDisabled;

	private long lastReachHistorySampleUtc;

	private long lastTrustedTransitionSampleUtc;

	private readonly List<IServerPlayer> sampledPlayers = new List<IServerPlayer>();

	private readonly Dictionary<string, EntityPos> sampledPlayerPositions = new Dictionary<string, EntityPos>(StringComparer.OrdinalIgnoreCase);

	private bool started;

	private bool movementRuntimeDisabled;

	private long lastMovementRuntimeErrorUtc;

	internal bool IsIngressActive => ingressGuard.IsActive;

	internal string IngressStatus => ingressGuard.Status;

	public PacketGuardService(ICoreServerAPI api, AntiCheatConfig config, Action<IServerPlayer, string, AntiCheatSeverity, string, string, string> reportFinding)
	{
		this.api = api;
		this.config = config;
		this.reportFinding = reportFinding;
		tickHealth = new ServerTickHealthMonitor(api, config);
		ingressGuard = new PacketIngressGuard(api, config, reportFinding);
		try
		{
			tprunesEnabled = api.ModLoader.IsModEnabled("tprunes");
		}
		catch
		{
			tprunesEnabled = false;
		}
		try
		{
			th3EssentialsEnabled = api.ModLoader.IsModEnabled("th3essentials");
		}
		catch
		{
			th3EssentialsEnabled = false;
		}
	}

	public void Start()
	{
		if (started || !config.PacketGuardEnabled)
		{
			return;
		}
		started = true;
		tickHealth.Start();
		ingressGuard.Start();
		TryInstallServerTeleportTrustHook();
		api.Event.PlayerNowPlaying += ResetPlayer;
		api.Event.PlayerRespawn += ResetPlayer;
		api.Event.PlayerDisconnect += RemovePlayer;
		api.Event.PlayerChat += OnPlayerChat;
		api.Event.BreakBlock += OnBreakBlock;
		api.Event.CanUseBlock += OnCanUseBlock;
		api.Event.PlayerDimensionChanged += OnPlayerDimensionChanged;
		if (config.PacketGuardMovementEnabled && !EntityRuntimeCompat.CanResolvePosition)
		{
			movementRuntimeDisabled = true;
			api.Logger.Error("[ServerEssentials/AntiCheat] PacketGuard movement protection was not started because this Vintage Story runtime exposes no compatible entity position member. Reach/mining protection will continue.");
		}
		if ((config.PacketGuardMovementEnabled || config.PacketGuardReachEnabled || config.PacketGuardFlightReviewEnabled) && EntityRuntimeCompat.CanResolvePosition)
		{
			int num = (config.PacketGuardMovementEnabled ? Math.Clamp(config.PacketGuardMovementSampleMs, 75, 500) : 100);
			sharedSamplerTickId = api.Event.RegisterGameTickListener((float _) =>
			{
				SamplePlayersShared();
			}, num, num);
			api.Logger.Notification("[ServerEssentials/AntiCheat] PacketGuard shared player sampler active: base={0}ms, reach/trusted=100ms, adaptive degraded=100ms, critical=125ms.", num);
			if (config.PacketGuardMovementEnabled && !movementRuntimeDisabled)
			{
				api.Logger.Notification("[ServerEssentials/AntiCheat] PacketGuard movement ABI-safe position accessor active.");
			}
		}
		api.Logger.Notification("[ServerEssentials/AntiCheat] PacketGuard enabled: movement={0}, reach={1}, mining={2}; hardMove={3:0.#} blocks, connectedBreakLimit={4}/{5}ms; adminsExempt={6}, mountsExempt={7}, engineTeleportGrace={8}ms, tprunes={9}; th3spawn={10}; reachHistory={11}; movementConfirm={12}/{13}ms; reachRewindCap={14}ms; miningDedupe={15}ms; speedModes={16}; endpointNoclip={17}; movementRisk={18}.", config.PacketGuardMovementEnabled, config.PacketGuardReachEnabled, config.PacketGuardMiningEnabled, config.PacketGuardHardMoveDistance, config.PacketGuardConnectedBreakLimit, config.PacketGuardVeinWindowMs, config.PacketGuardExemptAdmins, config.PacketGuardExemptMountedPlayers, config.PacketGuardEngineTeleportGraceMs, (tprunesEnabled && config.PacketGuardTPRunesCompatibility) ? "compatible" : (tprunesEnabled ? "detected/compat-off" : "not-loaded"), (th3EssentialsEnabled && config.PacketGuardTh3EssentialsSpawnCompatibility) ? "compatible" : (th3EssentialsEnabled ? "detected/compat-off" : "not-loaded"), config.PacketGuardReachPositionHistoryEnabled ? $"{config.PacketGuardReachPositionHistoryMs}ms" : "off", config.PacketGuardMovementConfirmSamples, config.PacketGuardMovementConfirmWindowMs, config.PacketGuardReachMaxRewindMs, config.PacketGuardMiningDuplicateTargetMs, config.PacketGuardMovementModeLimitsEnabled ? $"ground {config.PacketGuardGroundMaxHorizontalSpeed:0.#}/swim {config.PacketGuardSwimmingMaxHorizontalSpeed:0.#}/air {config.PacketGuardAirborneMaxHorizontalSpeed:0.#} bps" : $"off ({config.PacketGuardMaxHorizontalSpeed:0.#} bps)", config.PacketGuardNoclipEndpointEnabled ? $"on/{config.PacketGuardNoclipEndpointConfirmSamples}x" : "off", config.PacketGuardMovementRiskEnabled ? $"on decay {config.PacketGuardMovementRiskDecayPerSecond:0.#}/s" : "off");
		api.Logger.Notification("[ServerEssentials/AntiCheat] Server teleport trust hook: {0}. Legitimate server/mod TeleportToDouble calls are {1}.", serverTeleportHookInstalled ? "active" : "unavailable", serverTeleportHookInstalled ? "trusted before movement sampling" : "using engine flags and compatibility fallbacks only");
		api.Logger.Notification("[ServerEssentials/AntiCheat] Unsupported flight review: enabled={0}, confirm={1}s, sample={2}ms; advisory history only, no risk/correlation/punishment. Loaded support, liquids, climbing, mounts, trusted transitions, fresh positions and healthy TPS are required.", config.PacketGuardFlightReviewEnabled, config.PacketGuardFlightReviewConfirmSeconds, config.PacketGuardFlightReviewSampleMs);
		api.Logger.Notification("[ServerEssentials/AntiCheat] Vanilla translocator compatibility: linked-target verification active; warm-up authorization retained as fallback ({0}ms departure window).", 3000);
		api.Logger.Notification("[ServerEssentials/AntiCheat] Mining Validation v2: enabled={0}, suspiciousRatio={1:0.00}, confirmations={2}/{3}ms, reject={4}; incidentCorrelation={5}, window={6}s, categories={7}, maxBonus={8}.", config.PacketGuardMiningCadenceValidationEnabled, config.PacketGuardMiningCadenceSuspiciousRatio, config.PacketGuardMiningCadenceConfirmations, config.PacketGuardMiningCadenceConfirmationWindowMs, config.PacketGuardRejectImpossibleMiningCadence, config.IncidentCorrelationEnabled, config.IncidentCorrelationWindowSeconds, config.IncidentCorrelationRequiredCategories, config.IncidentCorrelationMaxRiskBonus);
		PacketGuardTpsSnapshot snapshot = tickHealth.GetSnapshot();
		api.Logger.Notification("[ServerEssentials/AntiCheat] TPS-aware timing: enabled={0}, target={1:0.0} TPS, degraded<{2:0.#}%, critical<{3:0.#}%, maxMultiplier={4:0.00}x, suppressCriticalTimingRejects={5}.", config.PacketGuardTpsAwarenessEnabled, snapshot.TargetTps, config.PacketGuardTpsDegradedPercent, config.PacketGuardTpsCriticalPercent, config.PacketGuardTpsMaxTimingMultiplier, config.PacketGuardTpsSuppressTimingRejectsWhenCritical);
	}

	public PacketGuardTpsSnapshot GetTpsSnapshot()
	{
		return tickHealth.GetSnapshot();
	}

	public IReadOnlyList<TrustedTeleportSourceSnapshot> GetTrustedTeleportSources()
	{
		Dictionary<string, PacketGuardTrustedState.TeleportSourceTelemetry> telemetry = PacketGuardTrustedState.GetTeleportSourceTelemetry().ToDictionary((PacketGuardTrustedState.TeleportSourceTelemetry x) => x.Code, (PacketGuardTrustedState.TeleportSourceTelemetry x) => x, StringComparer.OrdinalIgnoreCase);
		string status = (serverTeleportHookInstalled ? "Active" : "Fallback only");
		string status2;
		if (tprunesEnabled)
		{
			status2 = (config.PacketGuardTPRunesCompatibility ? "Active" : "Disabled");
		}
		else
		{
			status2 = "Not loaded";
		}
		string status3;
		if (th3EssentialsEnabled)
		{
			status3 = (config.PacketGuardTh3EssentialsSpawnCompatibility ? "Active" : "Disabled");
		}
		else
		{
			status3 = "Not loaded";
		}
		return new List<TrustedTeleportSourceSnapshot>
		{
			Build("server-engine-teleport", "Server / mod teleport API", status, serverTeleportHookInstalled ? "EntityPlayer.TeleportToDouble is trusted before movement sampling; engine teleport flags remain a secondary signal." : "Harmony hook unavailable; PacketGuard falls back to engine teleport flags and destination-specific compatibility checks."),
			Build("vanilla-translocator", "Vanilla static translocator", "Active", "Linked-target verification is preferred; warm-up authorization and endpoint validation are bounded fallbacks."),
			Build("suite-admin-teleport", "Server Essentials admin / map teleport", "Active", "Suite-owned teleport actions explicitly mark a trusted transition before moving the player."),
			Build("dashboard-teleport", "Web dashboard teleport", "Active", "Dashboard-admin teleport explicitly marks PacketGuard trust before the server moves the player."),
			Build("admin-teleport-command", "Admin /tp and /teleport commands", "Active", "Command intent is captured for authorized admins and paired with the subsequent server-side destination change."),
			Build("spawn-arrival", "Server spawn arrival", "Active", "An impossible jump is trusted only when the destination matches the server's spawn area."),
			Build("th3essentials-spawn", "Th3Essentials /spawn", status3, "Compatibility arms only from an actual /spawn command and accepts only a matching spawn destination."),
			Build("tp-runes", "TP Runes", status2, "Compatibility accepts only the bounded rune-arrival path when the mod is detected and compatibility is enabled."),
			Build("dimension-change", "Dimension change", "Active", "Vintage Story's server-side dimension-change event resets movement/reach baselines and grants a short transition window.")
		};
		TrustedTeleportSourceSnapshot Build(string code, string name, string status4, string verification)
		{
			telemetry.TryGetValue(code, out var value);
			return new TrustedTeleportSourceSnapshot
			{
				Code = code,
				Name = name,
				Status = status4,
				TrustScope = "Movement + Reach",
				Verification = verification,
				ObservedCount = value.Count,
				LastSeenUtc = value.LastSeenUtc,
				LastPlayerName = (value.LastPlayerName ?? ""),
				LastReason = (value.LastReason ?? ""),
				RecentEvents = (from e in PacketGuardTrustedState.GetRecentTeleportEvents(code)
					select new TrustedTeleportEventSnapshot
					{
						Utc = e.Utc,
						PlayerName = e.PlayerName,
						PlayerUid = e.PlayerUid,
						FromX = e.FromX,
						FromY = e.FromY,
						FromZ = e.FromZ,
						FromDimension = e.FromDimension,
						ToX = e.ToX,
						ToY = e.ToY,
						ToZ = e.ToZ,
						ToDimension = e.ToDimension,
						Reason = e.Reason,
						Verification = e.Verification
					}).ToList()
			};
		}
	}

	public IReadOnlyList<PacketSuppressionSnapshot> GetPacketSuppressions()
	{
		Dictionary<string, PacketGuardTrustedState.SuppressionTelemetry> telemetry = PacketGuardTrustedState.GetSuppressionTelemetry().ToDictionary((PacketGuardTrustedState.SuppressionTelemetry x) => x.Code, (PacketGuardTrustedState.SuppressionTelemetry x) => x, StringComparer.OrdinalIgnoreCase);
		return new List<PacketSuppressionSnapshot>
		{
			Build("ingress-unloaded-target", "Unloaded block target discarded", "Safety", "An action targeted terrain the server had not loaded. It was discarded without loading chunks or scoring cheating evidence."),
			Build("ingress-stale-transition-target", "Stale dimension target discarded", "Teleport", "A packet from the prior dimension arrived during a verified server transition and was discarded without a cheating finding."),
			Build("trusted-translocator", "Translocator trusted", "Teleport", "A large movement was accepted because PacketGuard verified a vanilla translocator path or linked target."),
			Build("trusted-server-teleport", "Server teleport trusted", "Teleport", "A server/mod TeleportToDouble call or engine teleport signal established a trusted transition before packet enforcement."),
			Build("invalid-block-origin", "Invalid 0,0,0 target ignored", "Reach", "A default all-zero block selection was discarded because the player was clearly nowhere near world origin."),
			Build("entity-recreated", "Respawn/entity baseline reset", "Movement", "The player's EntityId changed, so movement/reach history was reset instead of comparing against the previous entity."),
			Build("movement-sampler-gap", "Sampler gap re-baselined", "Movement", "A long server sampling gap was treated as incomplete evidence and re-baselined instead of scored as a speed/teleport violation."),
			Build("movement-reconciliation-spike", "Transient movement spike discarded", "Movement", "An impossible position appeared for too few consecutive samples and recovered before the confirmation threshold, so it was retained only as suppression telemetry."),
			Build("dimension-change", "Dimension change baseline reset", "Teleport", "A server dimension transition reset movement/reach baselines and opened only the normal short trusted-transition window."),
			Build("reach-teleport-grace", "Reach action covered by teleport grace", "Reach", "A block action arrived while a previously verified teleport was still settling, so the reach detector did not double-report it."),
			Build("mounted-movement", "Mounted/dismount movement re-baselined", "Movement", "Seat or mount repositioning was recognized as non-locomotion and excluded from movement scoring."),
			Build("mining-cadence-collapsed-timestamp", "Lag-collapsed mining timestamp ignored", "Mining", "Adjacent mining callbacks separated by 0-5 ms during critical TPS degradation were treated as scheduler timing artifacts instead of player evidence.")
		};
		PacketSuppressionSnapshot Build(string code, string name, string category, string description)
		{
			telemetry.TryGetValue(code, out var value);
			return new PacketSuppressionSnapshot
			{
				Code = code,
				Name = name,
				Category = category,
				Description = description,
				Count = value.Count,
				LastSeenUtc = value.LastSeenUtc,
				LastPlayerName = (value.LastPlayerName ?? ""),
				LastDetail = (value.LastDetail ?? "")
			};
		}
	}

	private void ResetPlayer(IServerPlayer player)
	{
		if (player != null && !string.IsNullOrWhiteSpace(player.PlayerUID))
		{
			movement.Remove(player.PlayerUID);
			flightReviews.Remove(player.PlayerUID);
			breakHistory.Remove(player.PlayerUID);
			reachViolations.Remove(player.PlayerUID);
			reachPositionHistory.Remove(player.PlayerUID);
			miningViolations.Remove(player.PlayerUID);
			miningViolations.Remove(player.PlayerUID + ":vein");
			miningCadenceWindows.Remove(player.PlayerUID + ":authoritative");
			miningCadenceWindows.Remove(player.PlayerUID + ":uncertain");
			trustedTransitionSamples.Remove(player.PlayerUID);
			pendingTranslocatorTransitions.Remove(player.PlayerUID);
			th3EssentialsSpawnIntents.Remove(player.PlayerUID);
			PacketGuardTrustedState.Clear(player.PlayerUID);
		}
	}

	private void RemovePlayer(IServerPlayer player)
	{
		ResetPlayer(player);
	}

	private bool ShouldGuard(IServerPlayer? player)
	{
		if (player?.Entity == null || player.WorldData == null)
		{
			return false;
		}
		if (player.WorldData.CurrentGameMode != EnumGameMode.Survival)
		{
			return false;
		}
		if (config.PacketGuardExemptAdmins && IsAdmin(player))
		{
			return false;
		}
		return true;
	}

	private static bool IsAdmin(IServerPlayer player)
	{
		try
		{
			return player.HasPrivilege(Privilege.root) || player.HasPrivilege(Privilege.controlserver);
		}
		catch
		{
			return false;
		}
	}

	private void TryInstallServerTeleportTrustHook()
	{
		if (serverTeleportHookInstalled)
		{
			return;
		}
		try
		{
			Assembly assembly = AppDomain.CurrentDomain.GetAssemblies().FirstOrDefault((Assembly a) => string.Equals(a.GetName().Name, "0Harmony", StringComparison.OrdinalIgnoreCase));
			if (assembly == null)
			{
				try
				{
					assembly = Assembly.Load("0Harmony");
				}
				catch
				{
				}
			}
			Type type = assembly?.GetType("HarmonyLib.Harmony");
			Type harmonyMethodType = assembly?.GetType("HarmonyLib.HarmonyMethod");
			if (type == null || harmonyMethodType == null)
			{
				return;
			}
			MethodInfo methodInfo = typeof(EntityPlayer).GetMethods(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic).FirstOrDefault((MethodInfo m) =>
			{
				if (!string.Equals(m.Name, "TeleportToDouble", StringComparison.Ordinal))
				{
					return false;
				}
				ParameterInfo[] parameters2 = m.GetParameters();
				return parameters2.Length >= 3 && parameters2[0].ParameterType == typeof(double) && parameters2[1].ParameterType == typeof(double) && parameters2[2].ParameterType == typeof(double);
			});
			MethodInfo method = typeof(PacketGuardService).GetMethod("BeforeServerPlayerTeleport", BindingFlags.Static | BindingFlags.NonPublic);
			if (methodInfo == null || method == null)
			{
				return;
			}
			object obj2 = Activator.CreateInstance(type, "newfies.serveressentials.packetguard.teleporttrust");
			object obj3 = Activator.CreateInstance(harmonyMethodType, method);
			if (obj2 != null && obj3 != null)
			{
				MethodInfo methodInfo2 = (from m in type.GetMethods(BindingFlags.Instance | BindingFlags.Public)
					where string.Equals(m.Name, "Patch", StringComparison.Ordinal)
					select m).FirstOrDefault((MethodInfo m) =>
				{
					ParameterInfo[] parameters2 = m.GetParameters();
					return parameters2.Length >= 2 && typeof(MethodBase).IsAssignableFrom(parameters2[0].ParameterType) && parameters2[1].ParameterType == harmonyMethodType;
				});
				if (!(methodInfo2 == null))
				{
					ParameterInfo[] parameters = methodInfo2.GetParameters();
					object[] array = new object[parameters.Length];
					array[0] = methodInfo;
					array[1] = obj3;
					methodInfo2.Invoke(obj2, array);
					serverTeleportHarmony = obj2;
					activeServerTeleportHook = new WeakReference<PacketGuardService>(this);
					serverTeleportHookInstalled = true;
				}
			}
		}
		catch (Exception ex)
		{
			api.Logger.Warning("[ServerEssentials/AntiCheat] Could not install optional server TeleportToDouble trust hook; compatibility fallbacks remain active. {0}", ex.Message);
			serverTeleportHarmony = null;
			serverTeleportHookInstalled = false;
		}
	}

	private static void BeforeServerPlayerTeleport(EntityPlayer __instance, object[] __args)
	{
		try
		{
			if (__instance != null && activeServerTeleportHook != null && activeServerTeleportHook.TryGetTarget(out PacketGuardService target))
			{
				double? targetX = TryReadTeleportArgument(__args, 0);
				double? targetY = TryReadTeleportArgument(__args, 1);
				double? targetZ = TryReadTeleportArgument(__args, 2);
				target.MarkServerInitiatedTeleport(__instance, targetX, targetY, targetZ);
			}
		}
		catch
		{
		}
	}

	private static double? TryReadTeleportArgument(object[]? args, int index)
	{
		if (args == null || index < 0 || index >= args.Length || args[index] == null)
		{
			return null;
		}
		try
		{
			return Convert.ToDouble(args[index], CultureInfo.InvariantCulture);
		}
		catch
		{
			return null;
		}
	}

	private void MarkServerInitiatedTeleport(Entity entity, double? targetX = null, double? targetY = null, double? targetZ = null)
	{
		try
		{
			IServerPlayer serverPlayer = api.World.AllOnlinePlayers.OfType<IServerPlayer>().FirstOrDefault((IServerPlayer p) => p?.Entity != null && (p.Entity == entity || p.Entity.EntityId == entity.EntityId));
			if (serverPlayer == null || string.IsNullOrWhiteSpace(serverPlayer.PlayerUID))
			{
				return;
			}
			long num = NowMs();
			string text = "server-teleport-call";
			string reason;
			if (pendingTranslocatorTransitions.TryGetValue(serverPlayer.PlayerUID, out PendingTranslocatorTransition value) && value.Armed && num - value.LastSeenUtc <= 3000)
			{
				text = (value.HasLinkedTarget ? "vanilla-translocator-linked-target" : "vanilla-translocator-preauthorized");
			}
			else if (PacketGuardTrustedState.TryGetActiveReason(serverPlayer.PlayerUID, num, PacketGuardTrustScope.Movement, out reason) && IsSpecificTeleportTrustReason(reason))
			{
				text = reason;
			}
			EntityPos pos = EntityRuntimeCompat.GetPos(entity);
			if (string.Equals(text, "server-teleport-call", StringComparison.OrdinalIgnoreCase) && pos != null && targetX.HasValue && targetY.HasValue && targetZ.HasValue)
			{
				double num2 = targetX.Value - pos.X;
				double num3 = targetY.Value - pos.Y;
				double num4 = targetZ.Value - pos.Z;
				double num5 = Math.Max(Math.Abs(num2), Math.Max(Math.Abs(num3), Math.Abs(num4)));
				double num6 = Math.Sqrt(num2 * num2 + num3 * num3 + num4 * num4);
				if (num5 <= config.PacketGuardHardMoveDistance && num6 <= config.PacketGuardHardMoveDistance * 1.45)
				{
					return;
				}
			}
			PacketGuardTrustedState.Mark(serverPlayer, text, config.PacketGuardEngineTeleportGraceMs);
			if (pos != null && targetX.HasValue && targetY.HasValue && targetZ.HasValue)
			{
				string verification = TeleportVerificationForReason(text, serverHookObserved: true);
				PacketGuardTrustedState.RecordTeleportEvent(serverPlayer, text, pos.X, pos.Y, pos.Z, pos.Dimension, targetX.Value, targetY.Value, targetZ.Value, pos.Dimension, verification, num);
				PacketGuardTrustedState.RecordSuppression(serverPlayer, IsTranslocatorTrustReason(text) ? "trusted-translocator" : "trusted-server-teleport", $"{text}: {pos.X:0.0},{pos.Y:0.0},{pos.Z:0.0} -> {targetX.Value:0.0},{targetY.Value:0.0},{targetZ.Value:0.0}", 1750, num);
			}
			if (movement.TryGetValue(serverPlayer.PlayerUID, out MovementState value2))
			{
				value2.GraceUntilUtc = Math.Max(value2.GraceUntilUtc, NowMs() + config.PacketGuardEngineTeleportGraceMs);
			}
			reachPositionHistory.Remove(serverPlayer.PlayerUID);
			reachViolations.Remove(serverPlayer.PlayerUID);
			pendingTranslocatorTransitions.Remove(serverPlayer.PlayerUID);
		}
		catch
		{
		}
	}

	private static bool IsSpecificTeleportTrustReason(string? reason)
	{
		string text = (reason ?? "").ToLowerInvariant();
		if (1 == 0)
		{
		}
		bool result;
		switch (text)
		{
		case "vanilla-translocator-linked-direct":
		case "vanilla-translocator-linked-target":
		case "vanilla-translocator-preauthorized":
		case "vanilla-translocator":
		case "suite-map-teleport":
		case "suite-admin-teleport":
		case "dashboard-teleport":
		case "admin-teleport-command":
		case "th3essentials-spawn":
		case "tp-runes":
		case "spawn":
		case "dimension-change":
			result = true;
			break;
		default:
			result = false;
			break;
		}
		if (1 == 0)
		{
		}
		return result;
	}

	private static bool IsTranslocatorTrustReason(string? reason)
	{
		return (reason ?? "").StartsWith("vanilla-translocator", StringComparison.OrdinalIgnoreCase);
	}

	private static string TeleportVerificationForReason(string? reason, bool serverHookObserved = false)
	{
		string text = (reason ?? "").ToLowerInvariant();
		string text2 = text;
		if (1 == 0)
		{
		}
		string result;
		switch (text2)
		{
		case "vanilla-translocator-linked-direct":
			result = "Source-side static translocator link matched the observed destination.";
			break;
		case "vanilla-translocator-linked-target":
			result = "Warm-up authorization was armed at a static translocator and its linked target matched the destination.";
			break;
		case "vanilla-translocator-preauthorized":
			result = "Vanilla teleporter warm-up state authorized the transition; endpoint validation remained bounded.";
			break;
		case "vanilla-translocator":
			result = "Both source and destination were validated beside compatible translocator endpoints.";
			break;
		case "th3essentials-spawn":
			result = "Recent /spawn intent plus a matching saved spawn destination.";
			break;
		case "tp-runes":
			result = "TP Runes was detected and the arrival matched the rune's bounded beacon destination.";
			break;
		case "spawn":
			result = "Arrival matched the server/player spawn area.";
			break;
		case "suite-map-teleport":
		case "suite-admin-teleport":
			result = "Server Essentials explicitly marked this administrator teleport before movement.";
			break;
		case "dashboard-teleport":
			result = "Authenticated dashboard teleport explicitly marked trust before movement.";
			break;
		case "admin-teleport-command":
			result = "Authorized /tp or /teleport command intent was paired with the server-side move.";
			break;
		case "dimension-change":
			result = "Server dimension lifecycle reset baselines and granted a bounded transition window.";
			break;
		case "engine-teleport":
			result = "Vintage Story reported an engine-authorized teleport flag.";
			break;
		case "server-teleport-call":
			result = ((!serverHookObserved) ? "Server-side teleport API trust was active." : "EntityPlayer.TeleportToDouble was intercepted server-side before the destination was applied.");
			break;
		default:
			result = (serverHookObserved ? "Server-side teleport API call was observed before movement." : "Recognized trusted transition.");
			break;
		}
		if (1 == 0)
		{
		}
		return result;
	}

	private void RecordTrustedMovementTransition(IServerPlayer player, string reason, MovementState state, EntityPos destination, string verification, string suppressionCode)
	{
		long nowUtc = NowMs();
		PacketGuardTrustedState.RecordTeleportEvent(player, reason, state.X, state.Y, state.Z, state.Dimension, destination.X, destination.Y, destination.Z, destination.Dimension, verification, nowUtc);
		PacketGuardTrustedState.RecordSuppression(player, suppressionCode, $"{reason}: {state.X:0.0},{state.Y:0.0},{state.Z:0.0} d{state.Dimension} -> {destination.X:0.0},{destination.Y:0.0},{destination.Z:0.0} d{destination.Dimension}", 1750, nowUtc);
	}

	private void RecordTrustedTransitionSample(IServerPlayer player, string reason, TrustedTransitionSample source, EntityPos destination, string verification, string suppressionCode, long now)
	{
		PacketGuardTrustedState.RecordTeleportEvent(player, reason, source.X, source.Y, source.Z, source.Dimension, destination.X, destination.Y, destination.Z, destination.Dimension, verification, now);
		PacketGuardTrustedState.RecordSuppression(player, suppressionCode, $"{reason}: {source.X:0.0},{source.Y:0.0},{source.Z:0.0} d{source.Dimension} -> {destination.X:0.0},{destination.Y:0.0},{destination.Z:0.0} d{destination.Dimension}", 1750, now);
	}

	private void RemoveServerTeleportTrustHook()
	{
		try
		{
			if (serverTeleportHarmony != null)
			{
				serverTeleportHarmony.GetType().GetMethod("UnpatchSelf", BindingFlags.Instance | BindingFlags.Public)?.Invoke(serverTeleportHarmony, null);
			}
		}
		catch
		{
		}
		finally
		{
			serverTeleportHarmony = null;
			serverTeleportHookInstalled = false;
			if (activeServerTeleportHook != null && activeServerTeleportHook.TryGetTarget(out PacketGuardService target) && target == this)
			{
				activeServerTeleportHook = null;
			}
		}
	}

	private void SamplePlayersShared()
	{
		if (!started)
		{
			return;
		}
		long num = NowMs();
		sampledPlayers.Clear();
		sampledPlayerPositions.Clear();
		IPlayer[] allOnlinePlayers = api.World.AllOnlinePlayers;
		foreach (IPlayer player in allOnlinePlayers)
		{
			if (player is IServerPlayer { Entity: not null } serverPlayer && !string.IsNullOrWhiteSpace(serverPlayer.PlayerUID))
			{
				sampledPlayers.Add(serverPlayer);
				EntityPos pos = EntityRuntimeCompat.GetPos(serverPlayer.Entity);
				if (pos != null)
				{
					sampledPlayerPositions[serverPlayer.PlayerUID] = pos;
				}
			}
		}
		if (num - lastTrustedTransitionSampleUtc >= 100)
		{
			SampleTrustedTransitions(num);
			lastTrustedTransitionSampleUtc = num;
		}
		if (config.PacketGuardReachEnabled && config.PacketGuardReachPositionHistoryEnabled && num - lastReachHistorySampleUtc >= 100)
		{
			SampleReachPositions(num);
			lastReachHistorySampleUtc = num;
		}
		if (config.PacketGuardMovementEnabled && !movementRuntimeDisabled)
		{
			PacketGuardTpsSnapshot snapshot = tickHealth.GetSnapshot();
			int num2 = Math.Clamp(config.PacketGuardMovementSampleMs, 75, 500);
			if (snapshot.IsCritical)
			{
				num2 = Math.Max(num2, 125);
			}
			else if (snapshot.IsDegraded)
			{
				num2 = Math.Max(num2, 100);
			}
			if (num - lastMovementSampleUtc >= num2)
			{
				SafeCheckMovement(num, snapshot);
				lastMovementSampleUtc = num;
			}
		}
		if (!config.PacketGuardFlightReviewEnabled || flightReviewRuntimeDisabled || num - lastFlightReviewSampleUtc < config.PacketGuardFlightReviewSampleMs)
		{
			return;
		}
		lastFlightReviewSampleUtc = num;
		try
		{
			CheckFlightReviews(num, tickHealth.GetSnapshot());
		}
		catch (Exception ex)
		{
			flightReviewRuntimeDisabled = true;
			flightReviews.Clear();
			api.Logger.Warning("[ServerEssentials/AntiCheat] Flight review disabled after a contained runtime error; other PacketGuard checks continue. {0}", ex.Message);
		}
	}

	private void CheckFlightReviews(long now, PacketGuardTpsSnapshot tps)
	{
		foreach (IServerPlayer sampledPlayer in sampledPlayers)
		{
			if (!flightReviews.TryGetValue(sampledPlayer.PlayerUID, out PacketFlightReviewWindow value))
			{
				value = (flightReviews[sampledPlayer.PlayerUID] = new PacketFlightReviewWindow());
			}
			bool flag = ShouldGuard(sampledPlayer) && sampledPlayer.Entity.Alive && sampledPlayer.Entity.MountedOn == null && !tps.IsDegraded && !tps.IsCritical && (!tps.Enabled || tps.Ready) && !PacketGuardTrustedState.IsActive(sampledPlayer.PlayerUID, now, PacketGuardTrustScope.Movement);
			if (!(sampledPlayer is ServerPlayer serverPlayer) || api.World.ElapsedMilliseconds - serverPlayer.LastReceivedClientPosition > 1500)
			{
				flag = false;
			}
			if (sampledPlayer.WorldData is ServerWorldPlayerData serverWorldPlayerData && (serverWorldPlayerData.FreeMove || serverWorldPlayerData.NoClip))
			{
				flag = false;
			}
			if (!sampledPlayerPositions.TryGetValue(sampledPlayer.PlayerUID, out EntityPos value2) || !flag)
			{
				value.Reset();
				continue;
			}
			bool eligible = HasLoadedUnsupportedFlightVolume(sampledPlayer.Entity, value2);
			if (value.Observe(now, value2.Y, eligible, config.PacketGuardFlightReviewConfirmSeconds * 1000, Math.Max(1000, config.PacketGuardFlightReviewSampleMs * 3)))
			{
				string text = $"decision=review-only;durationMs={value.DurationMs};rise={value.Rise:0.00};position={value2.X:0.00},{value2.Y:0.00},{value2.Z:0.00};dimension={value2.Dimension};supportProbe=loaded-collision-liquid-climb;groundClearance=2;freshClientPositionMs=1500;{tps.Evidence}";
				reportFinding(sampledPlayer, "PACKET_FLIGHT_REVIEW", AntiCheatSeverity.Moderate, "The server observed prolonged non-falling survival movement without nearby loaded ground support, liquid or climbable terrain. Review possible flight abuse or legitimate modded physics; this advisory observation does not score risk or trigger automatic punishment.", sampledPlayer.PlayerUID, text);
				api.Logger.Audit("[PacketGuard/FlightReview] playerUid={0};player={1};{2}", sampledPlayer.PlayerUID, PacketGuardTrustedState.CleanDiagnosticText(sampledPlayer.PlayerName, 80), text);
			}
		}
	}

	private bool HasLoadedUnsupportedFlightVolume(Entity entity, EntityPos position)
	{
		Cuboidf originCollisionBox = entity.OriginCollisionBox;
		if (originCollisionBox == null)
		{
			return false;
		}
		double num = position.X + (double)originCollisionBox.X1 - 0.3;
		double num2 = position.X + (double)originCollisionBox.X2 + 0.3;
		double num3 = position.Z + (double)originCollisionBox.Z1 - 0.3;
		double num4 = position.Z + (double)originCollisionBox.Z2 + 0.3;
		double num5 = position.Y + (double)originCollisionBox.Y1;
		int num6 = (int)Math.Floor(num);
		int num7 = (int)Math.Floor(num2);
		int num8 = (int)Math.Floor(num3);
		int num9 = (int)Math.Floor(num4);
		int num10 = (int)Math.Floor(num5 - 2.0);
		int num11 = (int)Math.Floor(position.Y + (double)originCollisionBox.Y2 + 0.3);
		if ((long)(num7 - num6 + 1) * (long)(num11 - num10 + 1) * (num9 - num8 + 1) > 72)
		{
			return false;
		}
		IBlockAccessor blockAccessor = api.World.BlockAccessor;
		BlockPos blockPos = new BlockPos(position.Dimension);
		for (int i = num6; i <= num7; i++)
		{
			for (int j = num10; j <= num11; j++)
			{
				for (int k = num8; k <= num9; k++)
				{
					if (i < 0 || k < 0 || j < 0 || i >= blockAccessor.MapSizeX || k >= blockAccessor.MapSizeZ || j >= blockAccessor.MapSizeY)
					{
						return false;
					}
					blockPos.Set(i, j, k);
					if (blockAccessor.GetChunkAtBlockPos(blockPos) == null)
					{
						return false;
					}
					Block block = blockAccessor.GetBlock(blockPos, 2);
					if (block.IsLiquid())
					{
						return false;
					}
					Block block2 = blockAccessor.GetBlock(blockPos, 1);
					if (block2.Climbable)
					{
						return false;
					}
					if (block2.Id == 0)
					{
						continue;
					}
					Cuboidf[] collisionBoxes;
					try
					{
						collisionBoxes = block2.GetCollisionBoxes(blockAccessor, blockPos);
					}
					catch
					{
						return false;
					}
					if (collisionBoxes == null)
					{
						continue;
					}
					Cuboidf[] array = collisionBoxes;
					foreach (Cuboidf cuboidf in array)
					{
						if ((double)((float)i + cuboidf.X2) >= num && (double)((float)i + cuboidf.X1) <= num2 && (double)((float)k + cuboidf.Z2) >= num3 && (double)((float)k + cuboidf.Z1) <= num4 && (double)((float)j + cuboidf.Y2) >= num5 - 2.0 && (double)((float)j + cuboidf.Y1) <= position.Y + (double)originCollisionBox.Y2 + 0.3)
						{
							return false;
						}
					}
				}
			}
		}
		return true;
	}

	private void SampleTrustedTransitions(long now)
	{
		foreach (IServerPlayer sampledPlayer in sampledPlayers)
		{
			if (!sampledPlayerPositions.TryGetValue(sampledPlayer.PlayerUID, out EntityPos value))
			{
				continue;
			}
			bool flag = sampledPlayer.Entity.MountedOn != null;
			if (!trustedTransitionSamples.TryGetValue(sampledPlayer.PlayerUID, out var value2))
			{
				UpdatePendingTranslocatorTransition(sampledPlayer, value, now);
				trustedTransitionSamples[sampledPlayer.PlayerUID] = TrustedTransitionSample.From(value, flag);
				continue;
			}
			if (config.PacketGuardExemptMountedPlayers && value2.Mounted != flag)
			{
				PacketGuardTrustedState.Mark(sampledPlayer, flag ? "mounted-reposition" : "dismount-reposition", config.PacketGuardMountGraceMs, PacketGuardTrustScope.Movement);
				PacketGuardTrustedState.RecordSuppression(sampledPlayer, "mounted-movement", flag ? "Mount transition re-baselined movement." : "Dismount transition re-baselined movement.", 1500, now);
				if (movement.TryGetValue(sampledPlayer.PlayerUID, out MovementState value3))
				{
					value3.WasMounted = flag;
				}
			}
			if (sampledPlayer.Entity.IsTeleport || sampledPlayer.Entity.Teleporting)
			{
				string reason = "engine-teleport";
				if (PacketGuardTrustedState.TryGetActiveReason(sampledPlayer.PlayerUID, now, PacketGuardTrustScope.Movement, out string reason2) && IsSpecificTeleportTrustReason(reason2))
				{
					reason = reason2;
				}
				RecordTrustedTransitionSample(sampledPlayer, reason, value2, value, TeleportVerificationForReason(reason), IsTranslocatorTrustReason(reason) ? "trusted-translocator" : "trusted-server-teleport", now);
				PacketGuardTrustedState.Mark(sampledPlayer, reason, config.PacketGuardEngineTeleportGraceMs);
				ClearPendingTranslocatorIfDeparted(sampledPlayer.PlayerUID, value);
				trustedTransitionSamples[sampledPlayer.PlayerUID] = TrustedTransitionSample.From(value, flag);
				continue;
			}
			if (value2.Dimension != value.Dimension)
			{
				RecordTrustedTransitionSample(sampledPlayer, "dimension-change", value2, value, TeleportVerificationForReason("dimension-change"), "dimension-change", now);
				PacketGuardTrustedState.Mark(sampledPlayer, "dimension-change", config.PacketGuardEngineTeleportGraceMs);
				pendingTranslocatorTransitions.Remove(sampledPlayer.PlayerUID);
				reachPositionHistory.Remove(sampledPlayer.PlayerUID);
				trustedTransitionSamples[sampledPlayer.PlayerUID] = TrustedTransitionSample.From(value, flag);
				continue;
			}
			double num = value.X - value2.X;
			double num2 = value.Y - value2.Y;
			double num3 = value.Z - value2.Z;
			double num4 = Math.Max(Math.Abs(num), Math.Max(Math.Abs(num2), Math.Abs(num3)));
			double num5 = Math.Sqrt(num * num + num2 * num2 + num3 * num3);
			if (num4 > config.PacketGuardHardMoveDistance || num5 > config.PacketGuardHardMoveDistance * 1.45)
			{
				bool linkedTargetVerified;
				if (TryConsumeTh3EssentialsSpawnArrival(sampledPlayer, value, now))
				{
					RecordTrustedTransitionSample(sampledPlayer, "th3essentials-spawn", value2, value, TeleportVerificationForReason("th3essentials-spawn"), "trusted-server-teleport", now);
					PacketGuardTrustedState.Mark(sampledPlayer, "th3essentials-spawn", config.PacketGuardTh3EssentialsSpawnGraceMs);
				}
				else if (IsTrustedTPRunesArrival(sampledPlayer, value))
				{
					RecordTrustedTransitionSample(sampledPlayer, "tp-runes", value2, value, TeleportVerificationForReason("tp-runes"), "trusted-server-teleport", now);
					PacketGuardTrustedState.Mark(sampledPlayer, "tp-runes", config.PacketGuardTPRunesGraceMs);
				}
				else if (IsTrustedSpawnArrival(value))
				{
					RecordTrustedTransitionSample(sampledPlayer, "spawn", value2, value, TeleportVerificationForReason("spawn"), "trusted-server-teleport", now);
					PacketGuardTrustedState.Mark(sampledPlayer, "spawn", config.PacketGuardEngineTeleportGraceMs);
				}
				else if (IsTrustedLinkedTranslocatorJump(sampledPlayer, value2.X, value2.Y, value2.Z, value2.Dimension, value))
				{
					RecordTrustedTransitionSample(sampledPlayer, "vanilla-translocator-linked-direct", value2, value, TeleportVerificationForReason("vanilla-translocator-linked-direct"), "trusted-translocator", now);
					PacketGuardTrustedState.Mark(sampledPlayer, "vanilla-translocator-linked-direct", config.PacketGuardEngineTeleportGraceMs);
				}
				else if (TryConsumePendingTranslocatorTransition(sampledPlayer, value2.X, value2.Y, value2.Z, value2.Dimension, value, now, out linkedTargetVerified))
				{
					string reason3 = (linkedTargetVerified ? "vanilla-translocator-linked-target" : "vanilla-translocator-preauthorized");
					RecordTrustedTransitionSample(sampledPlayer, reason3, value2, value, TeleportVerificationForReason(reason3), "trusted-translocator", now);
					PacketGuardTrustedState.Mark(sampledPlayer, reason3, config.PacketGuardEngineTeleportGraceMs);
				}
				else if (IsTrustedVanillaTranslocatorJump(value2, value))
				{
					RecordTrustedTransitionSample(sampledPlayer, "vanilla-translocator", value2, value, TeleportVerificationForReason("vanilla-translocator"), "trusted-translocator", now);
					PacketGuardTrustedState.Mark(sampledPlayer, "vanilla-translocator", config.PacketGuardEngineTeleportGraceMs);
				}
			}
			UpdatePendingTranslocatorTransition(sampledPlayer, value, now);
			trustedTransitionSamples[sampledPlayer.PlayerUID] = TrustedTransitionSample.From(value, flag);
		}
	}

	private void OnPlayerDimensionChanged(IPlayer byPlayer)
	{
		if (byPlayer is IServerPlayer serverPlayer && !string.IsNullOrWhiteSpace(serverPlayer.PlayerUID))
		{
			long num = NowMs();
			EntityPos pos = EntityRuntimeCompat.GetPos(serverPlayer.Entity);
			if (pos != null && trustedTransitionSamples.TryGetValue(serverPlayer.PlayerUID, out var value))
			{
				RecordTrustedTransitionSample(serverPlayer, "dimension-change", value, pos, TeleportVerificationForReason("dimension-change"), "dimension-change", num);
			}
			else
			{
				PacketGuardTrustedState.RecordSuppression(serverPlayer, "dimension-change", "Dimension lifecycle event reset PacketGuard baselines.", 1500, num);
			}
			PacketGuardTrustedState.Mark(serverPlayer, "dimension-change", config.PacketGuardEngineTeleportGraceMs);
			pendingTranslocatorTransitions.Remove(serverPlayer.PlayerUID);
			reachPositionHistory.Remove(serverPlayer.PlayerUID);
			if (pos != null)
			{
				trustedTransitionSamples[serverPlayer.PlayerUID] = TrustedTransitionSample.From(pos, serverPlayer.Entity.MountedOn != null);
			}
		}
	}

	private void SafeCheckMovement(long now, PacketGuardTpsSnapshot tps)
	{
		if (movementRuntimeDisabled)
		{
			return;
		}
		try
		{
			CheckMovement(now, tps);
		}
		catch (Exception ex)
		{
			movementRuntimeDisabled = true;
			long num = NowMs();
			if (num - lastMovementRuntimeErrorUtc >= 5000)
			{
				lastMovementRuntimeErrorUtc = num;
				api.Logger.Error("[ServerEssentials/AntiCheat] PacketGuard movement protection disabled for this server session after a contained runtime error. Server will continue running. Error: {0}", ex);
			}
		}
	}

	private void CheckMovement(long now, PacketGuardTpsSnapshot tps)
	{
		if (!config.PacketGuardMovementEnabled || movementRuntimeDisabled)
		{
			return;
		}
		double num = (tps.Enabled ? tps.TimingMultiplier : 1.0);
		foreach (IServerPlayer sampledPlayer in sampledPlayers)
		{
			if (!ShouldGuard(sampledPlayer))
			{
				movement.Remove(sampledPlayer.PlayerUID);
				continue;
			}
			EntityPlayer entity = sampledPlayer.Entity;
			if (!sampledPlayerPositions.TryGetValue(sampledPlayer.PlayerUID, out EntityPos value))
			{
				continue;
			}
			bool flag = entity.MountedOn != null;
			if (!movement.TryGetValue(sampledPlayer.PlayerUID, out MovementState value2))
			{
				value2 = MovementState.From(value, now, entity.EntityId);
				value2.WasMounted = flag;
				movement[sampledPlayer.PlayerUID] = value2;
				continue;
			}
			value2.DecayMovementRisk(now, config.PacketGuardMovementRiskEnabled, config.PacketGuardMovementRiskDecayPerSecond);
			if (value2.EntityId != entity.EntityId)
			{
				PacketGuardTrustedState.Mark(sampledPlayer, "player-entity-recreated", config.PacketGuardEngineTeleportGraceMs);
				PacketGuardTrustedState.RecordSuppression(sampledPlayer, "entity-recreated", $"EntityId {value2.EntityId} -> {entity.EntityId}; movement/reach baseline reset.", 1500, now);
				reachPositionHistory.Remove(sampledPlayer.PlayerUID);
				reachViolations.Remove(sampledPlayer.PlayerUID);
				value2.EntityId = entity.EntityId;
				value2.GraceUntilUtc = Math.Max(value2.GraceUntilUtc, now + config.PacketGuardEngineTeleportGraceMs);
				value2.Accept(value, now);
				continue;
			}
			long num2 = Math.Max(0L, now - value2.LastAcceptedUtc);
			int num3 = Math.Max(1500, config.PacketGuardMovementSampleMs * 20);
			if (num2 > num3)
			{
				PacketGuardTrustedState.Mark(sampledPlayer, "movement-sampler-gap", Math.Min(config.PacketGuardEngineTeleportGraceMs, 3000));
				PacketGuardTrustedState.RecordSuppression(sampledPlayer, "movement-sampler-gap", $"Sampling gap {num2}ms exceeded {num3}ms; baseline reset.", 1500, now);
				value2.Accept(value, now);
				continue;
			}
			if (flag)
			{
				if (!value2.WasMounted)
				{
					value2.WasMounted = true;
					value2.ResetMountedViolation();
					PacketGuardTrustedState.Mark(sampledPlayer, "mount-transition", config.PacketGuardMountGraceMs, PacketGuardTrustScope.Movement);
					PacketGuardTrustedState.RecordSuppression(sampledPlayer, "mount-transition", "decision=rebaseline;reason=first-mounted-sample;pre-mount movement baseline discarded.", 1500, now);
					value2.GraceUntilUtc = Math.Max(value2.GraceUntilUtc, now + config.PacketGuardMountGraceMs);
					value2.Accept(value, now);
					continue;
				}
				if (config.PacketGuardMountProfilesEnabled && TryResolveMountProfile(entity, out MountProfile profile, out Entity mountEntity))
				{
					long num4 = Math.Max(1L, now - value2.LastAcceptedUtc);
					double num5 = value.X - value2.X;
					double value3 = value.Y - value2.Y;
					double num6 = value.Z - value2.Z;
					double num7 = Math.Sqrt(num5 * num5 + num6 * num6);
					double num8 = num7 * 1000.0 / (double)num4;
					value2.AddDiagnosticSpeedSample(num8, now, config.PacketGuardDiagnosticSpeedHistorySamples);
					double num9 = Math.Abs(value3) * 1000.0 / (double)num4;
					double num10 = value2.ObserveMountedAcceleration(num8, now, profile.MaxAcceleration);
					double num11 = 0.0;
					bool flag2 = false;
					double value4 = 0.0;
					double num12 = 0.0;
					bool flag3 = false;
					if (mountEntity != null)
					{
						EntityPos pos = EntityRuntimeCompat.GetPos(mountEntity);
						if (pos != null && pos.Dimension == value.Dimension)
						{
							double num13 = value.X - pos.X;
							double num14 = value.Y - pos.Y;
							double num15 = value.Z - pos.Z;
							num11 = Math.Sqrt(num13 * num13 + num14 * num14 + num15 * num15);
							flag2 = num11 > profile.MaxRiderDistance;
							if (config.PacketGuardMountRiderCorrelationEnabled && value2.ObserveMountDisplacement(mountEntity.EntityId, pos, now, out var horizontalDistance, out var elapsedMs))
							{
								value4 = horizontalDistance * 1000.0 / (double)Math.Max(1L, elapsedMs);
								double num16 = Math.Abs(num7 - horizontalDistance);
								num12 = num16 * 1000.0 / (double)Math.Max(1L, num4);
								flag3 = num12 > profile.MaxRiderRelativeSpeed;
							}
						}
					}
					bool flag4 = num10 > profile.MaxAcceleration && num8 >= profile.MaxHorizontalSpeed * config.PacketGuardMountAccelerationMinSpeedRatio;
					bool flag5 = num8 > profile.MaxHorizontalSpeed || num9 > profile.MaxVerticalSpeed;
					bool flag6 = string.Equals(profile.Code, "unknown-modded-mount", StringComparison.Ordinal) && !config.PacketGuardUnknownMountEnforcementEnabled;
					if (flag5 & flag6)
					{
						value2.ResetMountedViolation();
						if (config.PacketGuardDiagnosticSuppressionEnabled)
						{
							PacketGuardTrustedState.RecordSuppression(sampledPlayer, "unknown-mount-envelope", $"decision=ignored;reason=unknown-mount-telemetry-only;mount={profile.MountCode};horizontalSpeed={num8:0.00}/{profile.MaxHorizontalSpeed:0.00};verticalSpeed={num9:0.00}/{profile.MaxVerticalSpeed:0.00};sampleMs={num4};history={value2.GetDiagnosticSpeedHistory()}", 1500, now);
						}
						PacketGuardTrustedState.RecordSuppression(sampledPlayer, "mounted-profile", profile.Code + ": " + profile.MountCode, 2500, now);
						value2.GraceUntilUtc = Math.Max(value2.GraceUntilUtc, now + Math.Min(config.PacketGuardMountGraceMs, 750));
						value2.Accept(value, now);
						continue;
					}
					if (flag5)
					{
						value2.ObserveMountedViolation(now, config.PacketGuardMountProfileConfirmWindowMs);
						if (value2.MountedSuspiciousSamples >= config.PacketGuardMountProfileConfirmSamples)
						{
							double value5 = AddMovementRisk(value2, MovementRiskSignal.Mount, config.PacketGuardMovementRiskMount, now);
							AntiCheatSeverity arg = ApplyMovementConfidenceSeverity(AntiCheatSeverity.High, value2);
							string text = $"profile={profile.Code};mount={profile.MountCode};horizontalSpeed={num8:0.00};hLimit={profile.MaxHorizontalSpeed:0.00};verticalSpeed={num9:0.00};vLimit={profile.MaxVerticalSpeed:0.00};acceleration={num10:0.00};aLimit={profile.MaxAcceleration:0.00};accelElevated={flag4};accelMinSpeedRatio={config.PacketGuardMountAccelerationMinSpeedRatio:0.00};riderDistance={num11:0.00};riderLimit={profile.MaxRiderDistance:0.00};mountSpeed={value4:0.00};riderRelativeSpeed={num12:0.00};relativeLimit={profile.MaxRiderRelativeSpeed:0.00};confirm={value2.MountedSuspiciousSamples}/{config.PacketGuardMountProfileConfirmSamples};sampleMs={num4};movementRisk={value5:0.0};decision=confirmed;reason=mount-envelope-violation;{MovementDiagnosticEvidence(sampledPlayer, value2, "mounted", value, mounted: true)};{MovementConfidenceEvidence(value2)};{tps.Evidence}";
							if (ShouldReportMovementFinding(value2, "PACKET_MOUNT_MOVEMENT_HACK", now))
							{
								reportFinding(sampledPlayer, "PACKET_MOUNT_MOVEMENT_HACK", arg, "The server observed mounted-player movement outside the verified mount profile and corrected the rider to a previously verified safe position.", sampledPlayer.PlayerUID, text);
							}
							api.Logger.Warning("[ServerEssentials/AntiCheat] PacketGuard mount movement anomaly for {0}: {1}", sampledPlayer.PlayerName, text);
							if (config.PacketGuardMountProfileEnforcementEnabled && !tps.SuppressTimingRejections)
							{
								RollbackMovement(sampledPlayer, value2, entity, now, "packetguard-mount-correction");
								continue;
							}
						}
					}
					else
					{
						value2.ResetMountedViolation();
						if (flag4 && config.PacketGuardDiagnosticSuppressionEnabled)
						{
							PacketGuardTrustedState.RecordSuppression(sampledPlayer, "mount-acceleration-only", $"decision=ignored;reason=legal-mount-envelope;profile={profile.Code};speed={num8:0.00}/{profile.MaxHorizontalSpeed:0.00};vertical={num9:0.00}/{profile.MaxVerticalSpeed:0.00};acceleration={num10:0.00}/{profile.MaxAcceleration:0.00};history={value2.GetDiagnosticSpeedHistory()}", 1500, now);
						}
						if ((flag2 | flag3) && config.PacketGuardDiagnosticSuppressionEnabled)
						{
							PacketGuardTrustedState.RecordSuppression(sampledPlayer, "mount-correlation-only", $"decision=ignored;reason=mount-correlation-telemetry-only;profile={profile.Code};riderDistance={num11:0.00}/{profile.MaxRiderDistance:0.00};riderRelativeSpeed={num12:0.00}/{profile.MaxRiderRelativeSpeed:0.00};mountSpeed={value4:0.00}", 1500, now);
						}
					}
					PacketGuardTrustedState.RecordSuppression(sampledPlayer, "mounted-profile", profile.Code + ": " + profile.MountCode, 2500, now);
					value2.GraceUntilUtc = Math.Max(value2.GraceUntilUtc, now + Math.Min(config.PacketGuardMountGraceMs, 750));
					value2.Accept(value, now);
					continue;
				}
				if (config.PacketGuardExemptMountedPlayers)
				{
					PacketGuardTrustedState.Mark(sampledPlayer, "mounted-reposition", config.PacketGuardMountGraceMs, PacketGuardTrustScope.Movement);
					PacketGuardTrustedState.RecordSuppression(sampledPlayer, "mounted-movement", "Mounted player movement re-baselined using legacy exemption because no mount profile could be resolved.", 1500, now);
					value2.GraceUntilUtc = Math.Max(value2.GraceUntilUtc, now + config.PacketGuardMountGraceMs);
					value2.Accept(value, now, markSafe: false);
					continue;
				}
			}
			if (config.PacketGuardExemptMountedPlayers && value2.WasMounted)
			{
				value2.WasMounted = false;
				PacketGuardTrustedState.Mark(sampledPlayer, "dismount-reposition", config.PacketGuardMountGraceMs, PacketGuardTrustScope.Movement);
				PacketGuardTrustedState.RecordSuppression(sampledPlayer, "mounted-movement", "Dismount reposition grace re-baselined movement.", 1500, now);
				value2.GraceUntilUtc = Math.Max(value2.GraceUntilUtc, now + config.PacketGuardMountGraceMs);
				value2.Accept(value, now);
				continue;
			}
			if (entity.IsTeleport || entity.Teleporting)
			{
				string reason = "engine-teleport";
				if (PacketGuardTrustedState.TryGetActiveReason(sampledPlayer.PlayerUID, now, PacketGuardTrustScope.Movement, out string reason2) && IsSpecificTeleportTrustReason(reason2))
				{
					reason = reason2;
				}
				RecordTrustedMovementTransition(sampledPlayer, reason, value2, value, TeleportVerificationForReason(reason), IsTranslocatorTrustReason(reason) ? "trusted-translocator" : "trusted-server-teleport");
				PacketGuardTrustedState.Mark(sampledPlayer, reason, config.PacketGuardEngineTeleportGraceMs);
				value2.GraceUntilUtc = Math.Max(value2.GraceUntilUtc, now + config.PacketGuardEngineTeleportGraceMs);
				value2.Accept(value, now);
				continue;
			}
			if (PacketGuardTrustedState.IsActive(sampledPlayer.PlayerUID, now, PacketGuardTrustScope.Movement) || value2.Dimension != value.Dimension || now < value2.GraceUntilUtc)
			{
				if (value2.Dimension != value.Dimension)
				{
					RecordTrustedMovementTransition(sampledPlayer, "dimension-change", value2, value, TeleportVerificationForReason("dimension-change"), "dimension-change");
					PacketGuardTrustedState.Mark(sampledPlayer, "dimension-change", config.PacketGuardEngineTeleportGraceMs);
					value2.GraceUntilUtc = Math.Max(value2.GraceUntilUtc, now + config.PacketGuardEngineTeleportGraceMs);
				}
				value2.Accept(value, now);
				continue;
			}
			double num17 = value.X - value2.X;
			double num18 = value.Y - value2.Y;
			double num19 = value.Z - value2.Z;
			double num20 = Math.Max(Math.Abs(num17), Math.Max(Math.Abs(num18), Math.Abs(num19)));
			double num21 = Math.Sqrt(num17 * num17 + num18 * num18 + num19 * num19);
			long num22 = Math.Max(1L, now - value2.LastAcceptedUtc);
			double num23 = Math.Clamp((double)num22 / (double)Math.Max(1, config.PacketGuardMovementSampleMs), 1.0, config.PacketGuardMovementMaxSampleScale);
			double num24 = config.PacketGuardHardMoveDistance * num23 * num;
			bool flag7 = num20 > config.PacketGuardHardMoveDistance * config.PacketGuardMovementExtremeMultiplier || num21 > config.PacketGuardHardMoveDistance * 1.45 * config.PacketGuardMovementExtremeMultiplier;
			bool flag8 = flag7 || num20 > num24 || num21 > num24 * 1.45;
			if (flag8 && TryConsumeTh3EssentialsSpawnArrival(sampledPlayer, value, now))
			{
				RecordTrustedMovementTransition(sampledPlayer, "th3essentials-spawn", value2, value, TeleportVerificationForReason("th3essentials-spawn"), "trusted-server-teleport");
				PacketGuardTrustedState.Mark(sampledPlayer, "th3essentials-spawn", config.PacketGuardTh3EssentialsSpawnGraceMs);
				value2.GraceUntilUtc = Math.Max(value2.GraceUntilUtc, now + config.PacketGuardTh3EssentialsSpawnGraceMs);
				value2.Accept(value, now);
				continue;
			}
			if (flag8 && IsTrustedTPRunesArrival(sampledPlayer, value))
			{
				RecordTrustedMovementTransition(sampledPlayer, "tp-runes", value2, value, TeleportVerificationForReason("tp-runes"), "trusted-server-teleport");
				PacketGuardTrustedState.Mark(sampledPlayer, "tp-runes", config.PacketGuardTPRunesGraceMs);
				value2.GraceUntilUtc = Math.Max(value2.GraceUntilUtc, now + config.PacketGuardTPRunesGraceMs);
				value2.Accept(value, now);
				continue;
			}
			if (flag8 && IsTrustedSpawnArrival(value))
			{
				RecordTrustedMovementTransition(sampledPlayer, "spawn", value2, value, TeleportVerificationForReason("spawn"), "trusted-server-teleport");
				PacketGuardTrustedState.Mark(sampledPlayer, "spawn", config.PacketGuardEngineTeleportGraceMs);
				value2.GraceUntilUtc = Math.Max(value2.GraceUntilUtc, now + config.PacketGuardEngineTeleportGraceMs);
				value2.Accept(value, now);
				continue;
			}
			if (flag8 && IsTrustedLinkedTranslocatorJump(sampledPlayer, value2.X, value2.Y, value2.Z, value2.Dimension, value))
			{
				RecordTrustedMovementTransition(sampledPlayer, "vanilla-translocator-linked-direct", value2, value, TeleportVerificationForReason("vanilla-translocator-linked-direct"), "trusted-translocator");
				PacketGuardTrustedState.Mark(sampledPlayer, "vanilla-translocator-linked-direct", config.PacketGuardEngineTeleportGraceMs);
				value2.GraceUntilUtc = Math.Max(value2.GraceUntilUtc, now + config.PacketGuardEngineTeleportGraceMs);
				value2.Accept(value, now);
				continue;
			}
			if (flag8 && TryConsumePendingTranslocatorTransition(sampledPlayer, value2.X, value2.Y, value2.Z, value2.Dimension, value, now, out var linkedTargetVerified))
			{
				string reason3 = (linkedTargetVerified ? "vanilla-translocator-linked-target" : "vanilla-translocator-preauthorized");
				RecordTrustedMovementTransition(sampledPlayer, reason3, value2, value, TeleportVerificationForReason(reason3), "trusted-translocator");
				PacketGuardTrustedState.Mark(sampledPlayer, reason3, config.PacketGuardEngineTeleportGraceMs);
				value2.GraceUntilUtc = Math.Max(value2.GraceUntilUtc, now + config.PacketGuardEngineTeleportGraceMs);
				value2.Accept(value, now);
				continue;
			}
			if (flag8 && IsTrustedVanillaTranslocatorJump(value2, value))
			{
				RecordTrustedMovementTransition(sampledPlayer, "vanilla-translocator", value2, value, TeleportVerificationForReason("vanilla-translocator"), "trusted-translocator");
				PacketGuardTrustedState.Mark(sampledPlayer, "vanilla-translocator", config.PacketGuardEngineTeleportGraceMs);
				value2.GraceUntilUtc = Math.Max(value2.GraceUntilUtc, now + config.PacketGuardEngineTeleportGraceMs);
				value2.Accept(value, now);
				continue;
			}
			if (flag8 && config.PacketGuardUnverifiedTeleportReconciliationEnabled && num21 >= config.PacketGuardUnverifiedTeleportMinDistance)
			{
				int num25 = value2.RegisterUnverifiedTeleport(now, config.PacketGuardUnverifiedTeleportRepeatWindowMs);
				string text2 = $"dx={num17:0.00};dy={num18:0.00};dz={num19:0.00};distance={num21:0.00};maxAxis={num20:0.00};reconcileMin={config.PacketGuardUnverifiedTeleportMinDistance:0.00};repeat={num25}/{config.PacketGuardUnverifiedTeleportRepeatCount};repeatWindowMs={config.PacketGuardUnverifiedTeleportRepeatWindowMs};elapsedMs={num22};pingMs={Math.Max(0f, sampledPlayer.Ping * 1000f):0};decision=rebaseline;reason=unverified-teleport-sized-transition;{tps.Evidence}";
				PacketGuardTrustedState.RecordSuppression(sampledPlayer, "unverified-teleport-reconcile", text2, 1500, now);
				if (num25 >= config.PacketGuardUnverifiedTeleportRepeatCount && ShouldReportMovementFinding(value2, "PACKET_UNVERIFIED_TELEPORT_PATTERN", now))
				{
					reportFinding(sampledPlayer, "PACKET_UNVERIFIED_TELEPORT_PATTERN", AntiCheatSeverity.Moderate, "The server observed repeated teleport-sized position changes without a matching trusted teleport marker. This is compatibility telemetry only: the transitions were re-baselined and do not contribute to punishment risk because server/mod teleport metadata may be incomplete.", sampledPlayer.PlayerUID, text2);
				}
				value2.Accept(value, now);
				continue;
			}
			if (!flag8)
			{
				if (value2.SuspiciousCount > 0)
				{
					PacketGuardTrustedState.RecordSuppression(sampledPlayer, "movement-reconciliation-spike", $"Discarded {value2.SuspiciousCount} transient impossible movement sample(s); position returned to a valid displacement before confirmation.", 1500, now);
				}
				long num26 = Math.Max(1L, now - value2.LastAcceptedUtc);
				double num27 = Math.Sqrt(num17 * num17 + num19 * num19);
				double num28 = num27 * 1000.0 / (double)num26;
				value2.AddDiagnosticSpeedSample(num28, now, config.PacketGuardDiagnosticSpeedHistorySamples);
				(string Mode, double Limit) tuple = ResolveMovementSpeedLimit(entity, num18);
				string item = tuple.Mode;
				double item2 = tuple.Limit;
				bool flag9 = config.PacketGuardSpeedHackEnabled && num28 > item2;
				bool flag10 = false;
				double penetrationDepth = 0.0;
				double num29 = 0.0;
				string blockCode = "";
				if (config.PacketGuardNoclipEnabled && config.PacketGuardNoclipEndpointEnabled && EntityBodyIntersectsCollision(entity, value.X, value.Y, value.Z, value.Dimension, out blockCode, out penetrationDepth) && penetrationDepth >= config.PacketGuardNoclipEndpointMinPenetrationDepth)
				{
					flag10 = true;
					num29 = value2.ObserveEndpointPenetration(value2.X, value2.Z, value.X, value.Z, value.Dimension);
				}
				else
				{
					value2.ResetEndpointPenetration();
				}
				if (flag10 && value2.EndpointCollisionSamples >= config.PacketGuardNoclipEndpointConfirmSamples && num29 < config.PacketGuardNoclipEndpointMinNetMovement && config.PacketGuardDiagnosticSuppressionEnabled)
				{
					PacketGuardTrustedState.RecordSuppression(sampledPlayer, "noclip-stationary-overlap", $"decision=ignored;reason=insufficient-net-movement;block={blockCode};penetration={penetrationDepth:0.000};samples={value2.EndpointCollisionSamples};netMovement={num29:0.000}/{config.PacketGuardNoclipEndpointMinNetMovement:0.000};history={value2.GetDiagnosticSpeedHistory()}", 1250, now);
				}
				if (flag10 && value2.EndpointCollisionSamples >= config.PacketGuardNoclipEndpointConfirmSamples && num29 >= config.PacketGuardNoclipEndpointMinNetMovement)
				{
					double value6 = AddMovementRisk(value2, MovementRiskSignal.Noclip, config.PacketGuardMovementRiskNoclip, now);
					string text3 = $"mode={item};distance={num21:0.00};horizontal={num27:0.00};sampleMs={num26};endpointPenetration={penetrationDepth:0.000};minPenetration={config.PacketGuardNoclipEndpointMinPenetrationDepth:0.000};endpointConfirm={value2.EndpointCollisionSamples}/{config.PacketGuardNoclipEndpointConfirmSamples};endpointNetMovement={num29:0.000};minNetMovement={config.PacketGuardNoclipEndpointMinNetMovement:0.000};block={blockCode};movementRisk={value6:0.0};decision=confirmed;reason=endpoint-penetration-with-movement;{MovementDiagnosticEvidence(sampledPlayer, value2, item, value, flag)};{MovementConfidenceEvidence(value2)};{tps.Evidence}";
					if (ShouldReportMovementFinding(value2, "PACKET_NOCLIP", now))
					{
						reportFinding(sampledPlayer, "PACKET_NOCLIP", AntiCheatSeverity.Critical, "The server observed repeated untrusted player-body penetration into solid collision geometry and rolled the player back to a verified safe position.", sampledPlayer.PlayerUID, text3);
					}
					api.Logger.Warning("[ServerEssentials/AntiCheat] PacketGuard endpoint noclip anomaly for {0}: {1}", sampledPlayer.PlayerName, text3);
					if (config.PacketGuardRejectNoclip)
					{
						RollbackMovement(sampledPlayer, value2, entity, now, "packetguard-noclip-endpoint-correction");
					}
					else
					{
						value2.Accept(value, now, markSafe: false);
					}
					continue;
				}
				if (config.PacketGuardNoclipEnabled && TryDetectNoclipPath(entity, value2.X, value2.Y, value2.Z, value.X, value.Y, value.Z, value.Dimension, out int collisionSamples, out double maxPenetrationDepth, out string firstBlock))
				{
					double value7 = AddMovementRisk(value2, MovementRiskSignal.Noclip, config.PacketGuardMovementRiskNoclip, now);
					string text4 = $"mode={item};distance={num21:0.00};horizontal={num27:0.00};sampleMs={num26};collisionSamples={collisionSamples};required={config.PacketGuardNoclipRequiredCollisionSamples};maxPenetration={maxPenetrationDepth:0.000};minPenetration={config.PacketGuardNoclipMinPenetrationDepth:0.000};sampleStep={config.PacketGuardNoclipSampleStep:0.00};block={firstBlock};movementRisk={value7:0.0};decision=confirmed;reason=collision-path-crossing;{MovementDiagnosticEvidence(sampledPlayer, value2, item, value, flag)};{MovementConfidenceEvidence(value2)};{tps.Evidence}";
					if (ShouldReportMovementFinding(value2, "PACKET_NOCLIP", now))
					{
						reportFinding(sampledPlayer, "PACKET_NOCLIP", AntiCheatSeverity.Critical, "The server observed untrusted survival movement crossing solid block collision geometry and rolled the player back to the last safe position.", sampledPlayer.PlayerUID, text4);
					}
					api.Logger.Warning("[ServerEssentials/AntiCheat] PacketGuard noclip anomaly for {0}: {1}", sampledPlayer.PlayerName, text4);
					if (config.PacketGuardRejectNoclip)
					{
						RollbackMovement(sampledPlayer, value2, entity, now, "packetguard-noclip-correction");
					}
					else
					{
						value2.Accept(value, now, markSafe: false);
					}
					continue;
				}
				double num30 = value2.ObserveAcceleration(num28, now, config.PacketGuardAccelerationMinSpeed, config.PacketGuardMaxHorizontalAcceleration, config.PacketGuardAccelerationConfirmWindowMs, config.PacketGuardAccelerationSampleMinMs, config.PacketGuardAccelerationSmoothingFactor, out var suspiciousSamples, out var smoothedSpeed);
				if (config.PacketGuardAccelerationEnabled && smoothedSpeed >= config.PacketGuardAccelerationMinSpeed && num30 > config.PacketGuardMaxHorizontalAcceleration && suspiciousSamples > 0 && suspiciousSamples < config.PacketGuardAccelerationConfirmSamples && config.PacketGuardDiagnosticSuppressionEnabled)
				{
					PacketGuardTrustedState.RecordSuppression(sampledPlayer, "acceleration-awaiting-confirmation", $"decision=deferred;reason=confirmation-pending;acceleration={num30:0.00}/{config.PacketGuardMaxHorizontalAcceleration:0.00};smoothedSpeed={smoothedSpeed:0.00};confirm={suspiciousSamples}/{config.PacketGuardAccelerationConfirmSamples};history={value2.GetDiagnosticSpeedHistory()}", 1000, now);
				}
				if (config.PacketGuardAccelerationEnabled && smoothedSpeed >= config.PacketGuardAccelerationMinSpeed && num30 > config.PacketGuardMaxHorizontalAcceleration && suspiciousSamples >= config.PacketGuardAccelerationConfirmSamples)
				{
					bool suppressTimingRejections = tps.SuppressTimingRejections;
					if (suppressTimingRejections && config.PacketGuardDiagnosticSuppressionEnabled)
					{
						PacketGuardTrustedState.RecordSuppression(sampledPlayer, "acceleration-tps-deferred", $"decision=deferred;reason=critical-tps;acceleration={num30:0.00};smoothedSpeed={smoothedSpeed:0.00};history={value2.GetDiagnosticSpeedHistory()};{tps.Evidence}", 1500, now);
					}
					double value8 = AddMovementRisk(value2, MovementRiskSignal.Acceleration, config.PacketGuardMovementRiskAcceleration, now);
					AntiCheatSeverity arg2 = (suppressTimingRejections ? AntiCheatSeverity.High : ApplyMovementConfidenceSeverity(AntiCheatSeverity.Critical, value2));
					string text5 = $"mode={item};horizontalAcceleration={num30:0.00};limit={config.PacketGuardMaxHorizontalAcceleration:0.00};speed={num28:0.00};smoothedSpeed={smoothedSpeed:0.00};minSpeed={config.PacketGuardAccelerationMinSpeed:0.00};confirmSamples={suspiciousSamples}/{config.PacketGuardAccelerationConfirmSamples};confirmWindowMs={config.PacketGuardAccelerationConfirmWindowMs};accelSampleMinMs={config.PacketGuardAccelerationSampleMinMs};sampleMs={num26};pingMs={Math.Max(0f, sampledPlayer.Ping * 1000f):0};movementRisk={value8:0.0};decision={(suppressTimingRejections ? "deferred" : "confirmed")};reason={(suppressTimingRejections ? "critical-tps" : "repeated-smoothed-acceleration")};{MovementDiagnosticEvidence(sampledPlayer, value2, item, value, flag)};{MovementConfidenceEvidence(value2)};{tps.Evidence}";
					if (ShouldReportMovementFinding(value2, "PACKET_ACCELERATION_HACK", now))
					{
						reportFinding(sampledPlayer, "PACKET_ACCELERATION_HACK", arg2, suppressTimingRejections ? "The server observed repeated impossible horizontal acceleration, but rollback was deferred because server TPS was critically degraded." : "The server observed repeated impossible horizontal acceleration and rolled the player back to a previously verified safe position.", sampledPlayer.PlayerUID, text5);
					}
					api.Logger.Warning("[ServerEssentials/AntiCheat] PacketGuard acceleration anomaly for {0}: {1}", sampledPlayer.PlayerName, text5);
					if (config.PacketGuardRejectAccelerationHack && !suppressTimingRejections)
					{
						RollbackMovement(sampledPlayer, value2, entity, now, "packetguard-acceleration-correction");
						continue;
					}
				}
				value2.AddSpeedSample(num27, now);
				if (config.PacketGuardSpeedHackEnabled && value2.TryCloseSpeedWindow(now, config.PacketGuardSpeedWindowMs, item2, config.PacketGuardSpeedConfirmWindows, out var observedSpeed, out var confirmedWindows))
				{
					bool suppressTimingRejections2 = tps.SuppressTimingRejections;
					double value9 = AddMovementRisk(value2, MovementRiskSignal.Speed, config.PacketGuardMovementRiskSpeed, now);
					AntiCheatSeverity arg3 = (suppressTimingRejections2 ? AntiCheatSeverity.High : ApplyMovementConfidenceSeverity(AntiCheatSeverity.Critical, value2));
					string text6 = $"mode={item};horizontalSpeed={observedSpeed:0.00};limit={item2:0.00};windowMs={config.PacketGuardSpeedWindowMs};confirmWindows={confirmedWindows}/{config.PacketGuardSpeedConfirmWindows};instantaneous={num28:0.00};pingMs={Math.Max(0f, sampledPlayer.Ping * 1000f):0};movementRisk={value9:0.0};speedEscalationEligible={!tps.Enabled || (tps.Ready && !tps.IsDegraded)};{MovementConfidenceEvidence(value2)};{tps.Evidence}";
					if (ShouldReportMovementFinding(value2, "PACKET_SPEED_HACK", now))
					{
						reportFinding(sampledPlayer, "PACKET_SPEED_HACK", arg3, suppressTimingRejections2 ? "The server observed sustained impossible horizontal movement speed, but rollback was deferred because server TPS was critically degraded." : "The server observed sustained impossible horizontal movement speed and rolled the player back to the last safe position.", sampledPlayer.PlayerUID, text6);
					}
					api.Logger.Warning("[ServerEssentials/AntiCheat] PacketGuard speed anomaly for {0}: {1}", sampledPlayer.PlayerName, text6);
					if (config.PacketGuardRejectSpeedHack && !suppressTimingRejections2)
					{
						RollbackMovement(sampledPlayer, value2, entity, now, "packetguard-speed-correction");
						continue;
					}
				}
				bool flag11 = suspiciousSamples > 0;
				value2.Accept(value, now, !flag9 && !flag11 && !flag10);
				if (now - value2.LastViolationUtc > 30000)
				{
					value2.ViolationCount = 0;
				}
				continue;
			}
			int num31 = Math.Clamp((int)Math.Round((double)config.PacketGuardMovementConfirmWindowMs * num), config.PacketGuardMovementConfirmWindowMs, 10000);
			int num32 = Math.Clamp(Math.Max(2, config.PacketGuardMovementConfirmSamples) + (tps.SuppressTimingRejections ? 1 : 0), 2, 4);
			if (value2.SuspiciousCount == 0 || now - value2.SuspiciousSinceUtc > num31)
			{
				value2.SuspiciousCount = 1;
				value2.SuspiciousSinceUtc = now;
			}
			else
			{
				value2.SuspiciousCount++;
			}
			value2.SuspiciousLastUtc = now;
			if (value2.SuspiciousCount >= num32)
			{
				if (now - value2.LastViolationUtc > 10000)
				{
					value2.ViolationCount = 0;
				}
				value2.ViolationCount++;
				value2.LastViolationUtc = now;
				bool flag12 = tps.SuppressTimingRejections && !flag7;
				AntiCheatSeverity antiCheatSeverity = ((value2.ViolationCount >= 2) ? AntiCheatSeverity.Critical : AntiCheatSeverity.High);
				if (flag12 && antiCheatSeverity == AntiCheatSeverity.Critical)
				{
					antiCheatSeverity = AntiCheatSeverity.High;
				}
				else if (flag12 && antiCheatSeverity == AntiCheatSeverity.High)
				{
					antiCheatSeverity = AntiCheatSeverity.Moderate;
				}
				double value10 = AddMovementRisk(value2, MovementRiskSignal.HardMove, config.PacketGuardMovementRiskHardMove, now);
				antiCheatSeverity = ApplyMovementConfidenceSeverity(antiCheatSeverity, value2);
				string text7 = $"dx={num17:0.00};dy={num18:0.00};dz={num19:0.00};distance={num21:0.00};maxAxis={num20:0.00};baseLimit={config.PacketGuardHardMoveDistance:0.00};effectiveLimit={num24:0.00};sampleScale={num23:0.00};elapsedMs={num22};confirmSamples={value2.SuspiciousCount}/{num32};baseConfirmSamples={config.PacketGuardMovementConfirmSamples};confirmWindowMs={num31};extreme={flag7};pingMs={Math.Max(0f, sampledPlayer.Ping * 1000f):0};violations={value2.ViolationCount};movementRisk={value10:0.0};{MovementConfidenceEvidence(value2)};{tps.Evidence}";
				if (ShouldReportMovementFinding(value2, "PACKET_UNAUTHORIZED_MOVEMENT", now))
				{
					reportFinding(sampledPlayer, "PACKET_UNAUTHORIZED_MOVEMENT", antiCheatSeverity, flag12 ? "The server observed an impossible survival-mode position change, but automatic rollback was deferred because server TPS was critically degraded." : "The server rejected an impossible survival-mode position change that was not marked as a legitimate server teleport.", sampledPlayer.PlayerUID, text7);
				}
				api.Logger.Warning("[ServerEssentials/AntiCheat] PacketGuard movement anomaly for {0}: {1}", sampledPlayer.PlayerName, text7);
				if (config.PacketGuardRejectUnauthorizedMovement && !flag12)
				{
					RollbackMovement(sampledPlayer, value2, entity, now, "packetguard-correction");
				}
				else
				{
					value2.Accept(value, now);
				}
			}
		}
	}

	private double AddMovementRisk(MovementState state, MovementRiskSignal signal, double amount, long now)
	{
		double result = state.AddMovementRisk(config.PacketGuardMovementRiskEnabled, signal, amount, now, config.PacketGuardMovementConfidenceCorrelationWindowMs, config.PacketGuardMovementConfidenceCorrelationBonus);
		if (config.PacketGuardMovementRiskEnabled && state.LastRollbackUtc > 0 && now - state.LastRollbackUtc <= config.PacketGuardRollbackAntiLoopWindowMs && !state.RollbackResistanceCounted)
		{
			state.RollbackResistanceCounted = true;
			result = state.AddMovementRisk(enabled: true, MovementRiskSignal.RollbackResistance, config.PacketGuardRollbackAntiLoopRisk, now, config.PacketGuardMovementConfidenceCorrelationWindowMs, config.PacketGuardMovementConfidenceCorrelationBonus);
		}
		return result;
	}

	private bool ShouldReportMovementFinding(MovementState state, string code, long now)
	{
		return state.ShouldReportFinding(code, now, config.PacketGuardMovementDuplicateFindingCooldownMs);
	}

	private string MovementConfidenceEvidence(MovementState state)
	{
		string movementConfidenceBand = state.GetMovementConfidenceBand(config.PacketGuardMovementConfidenceGuardedThreshold, config.PacketGuardMovementConfidenceHighThreshold, config.PacketGuardMovementConfidenceCriticalThreshold);
		return $"movementConfidence={state.MovementRiskScore:0.0};confidenceBand={movementConfidenceBand};confidenceSignals={state.GetMovementConfidenceSignals()};rollbackLoop={state.RollbackLoopCount}";
	}

	private string MovementDiagnosticEvidence(IServerPlayer player, MovementState state, string movementMode, EntityPos pos, bool mounted)
	{
		if (!config.PacketGuardDiagnosticContextEnabled)
		{
			return "diagnostics=off";
		}
		return $"diagnostics=on;modeContext={movementMode};mounted={mounted};pingMs={Math.Max(0f, player.Ping * 1000f):0};position={pos.X:0.0},{pos.Y:0.0},{pos.Z:0.0};dimension={pos.Dimension};acceptedAgeMs={Math.Max(0L, DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() - state.LastAcceptedUtc)};speedHistory={state.GetDiagnosticSpeedHistory()}";
	}

	private AntiCheatSeverity ApplyMovementConfidenceSeverity(AntiCheatSeverity severity, MovementState state)
	{
		if (!config.PacketGuardMovementRiskEnabled || !config.PacketGuardMovementConfidenceEscalatesSeverity)
		{
			return severity;
		}
		double movementRiskScore = state.MovementRiskScore;
		if (movementRiskScore >= config.PacketGuardMovementConfidenceCriticalThreshold)
		{
			return AntiCheatSeverity.Critical;
		}
		if (movementRiskScore >= config.PacketGuardMovementConfidenceHighThreshold && severity == AntiCheatSeverity.Moderate)
		{
			return AntiCheatSeverity.High;
		}
		return severity;
	}

	private bool TryResolveMountProfile(EntityPlayer rider, out MountProfile profile, out Entity? mountEntity)
	{
		profile = default;
		object mountedOn = rider.MountedOn;
		mountEntity = ResolveMountedEntity(mountedOn);
		string text = mountEntity?.Code?.ToString() ?? ResolveMountedCode(mountedOn) ?? string.Empty;
		if (text == null)
		{
			text = string.Empty;
		}
		string text2 = text.ToLowerInvariant();
		if (text2.Contains("shipwright:"))
		{
			profile = new MountProfile("shipwright-vessel", text, config.PacketGuardShipwrightMaxHorizontalSpeed, config.PacketGuardShipwrightMaxVerticalSpeed, config.PacketGuardShipwrightMaxAcceleration, config.PacketGuardShipwrightMaxRiderDistance, config.PacketGuardShipwrightMaxRiderRelativeSpeed);
			return true;
		}
		if (text2.Contains("draconis:") || text2.Contains("jaunt:"))
		{
			profile = new MountProfile("draconis-mount", text, config.PacketGuardDraconisMaxHorizontalSpeed, config.PacketGuardDraconisMaxVerticalSpeed, config.PacketGuardDraconisMaxAcceleration, config.PacketGuardDraconisMaxRiderDistance, config.PacketGuardDraconisMaxRiderRelativeSpeed);
			return true;
		}
		if (mountedOn != null)
		{
			profile = new MountProfile("unknown-modded-mount", string.IsNullOrWhiteSpace(text) ? (mountedOn.GetType().FullName ?? "unknown") : text, config.PacketGuardUnknownMountMaxHorizontalSpeed, config.PacketGuardUnknownMountMaxVerticalSpeed, config.PacketGuardUnknownMountMaxAcceleration, config.PacketGuardUnknownMountMaxRiderDistance, config.PacketGuardUnknownMountMaxRiderRelativeSpeed);
			return true;
		}
		return false;
	}

	private static Entity? ResolveMountedEntity(object? mountedOn)
	{
		if (mountedOn == null)
		{
			return null;
		}
		if (mountedOn is Entity result)
		{
			return result;
		}
		Type type = mountedOn.GetType();
		string[] array = new string[5] { "Entity", "MountableEntity", "MountedEntity", "Carrier", "OwnerEntity" };
		foreach (string name in array)
		{
			try
			{
				if (type.GetProperty(name, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)?.GetValue(mountedOn) is Entity result2)
				{
					return result2;
				}
				if (type.GetField(name, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)?.GetValue(mountedOn) is Entity result3)
				{
					return result3;
				}
			}
			catch
			{
			}
		}
		return null;
	}

	private static string ResolveMountedCode(object? mountedOn)
	{
		if (mountedOn == null)
		{
			return "";
		}
		Type type = mountedOn.GetType();
		string[] array = new string[5] { "Entity", "MountableEntity", "MountedEntity", "Carrier", "OwnerEntity" };
		foreach (string name in array)
		{
			try
			{
				object obj = type.GetProperty(name, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)?.GetValue(mountedOn) ?? type.GetField(name, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)?.GetValue(mountedOn);
				if (obj is Entity { Code: var code })
				{
					return code?.ToString() ?? "";
				}
			}
			catch
			{
			}
		}
		return type.FullName ?? type.Name;
	}

	private (string Mode, double Limit) ResolveMovementSpeedLimit(Entity entity, double deltaY)
	{
		if (!config.PacketGuardMovementModeLimitsEnabled)
		{
			return (Mode: "default", Limit: config.PacketGuardMaxHorizontalSpeed);
		}
		try
		{
			if (entity.Swimming || entity.FeetInLiquid)
			{
				return (Mode: "swimming", Limit: config.PacketGuardSwimmingMaxHorizontalSpeed);
			}
		}
		catch
		{
		}
		if (Math.Abs(deltaY) > 0.3)
		{
			return (Mode: "airborne", Limit: config.PacketGuardAirborneMaxHorizontalSpeed);
		}
		return (Mode: "ground", Limit: config.PacketGuardGroundMaxHorizontalSpeed);
	}

	private bool TryDetectNoclipPath(Entity entity, double fromX, double fromY, double fromZ, double toX, double toY, double toZ, int dimension, out int collisionSamples, out double maxPenetrationDepth, out string firstBlock)
	{
		collisionSamples = 0;
		maxPenetrationDepth = 0.0;
		firstBlock = "";
		if (!config.PacketGuardNoclipEnabled)
		{
			return false;
		}
		double num = toX - fromX;
		double num2 = toY - fromY;
		double num3 = toZ - fromZ;
		double num4 = Math.Sqrt(num * num + num2 * num2 + num3 * num3);
		if (num4 < config.PacketGuardNoclipMinPathDistance)
		{
			return false;
		}
		int num5 = Math.Clamp((int)Math.Ceiling(num4 / config.PacketGuardNoclipSampleStep), 2, 96);
		for (int i = 1; i < num5; i++)
		{
			double num6 = (double)i / (double)num5;
			double x = fromX + num * num6;
			double y = fromY + num2 * num6;
			double z = fromZ + num3 * num6;
			if (EntityBodyIntersectsCollision(entity, x, y, z, dimension, out string blockCode, out double penetrationDepth) && !(penetrationDepth < config.PacketGuardNoclipMinPenetrationDepth))
			{
				collisionSamples++;
				maxPenetrationDepth = Math.Max(maxPenetrationDepth, penetrationDepth);
				if (firstBlock.Length == 0)
				{
					firstBlock = blockCode;
				}
				if (collisionSamples >= config.PacketGuardNoclipRequiredCollisionSamples)
				{
					return true;
				}
			}
		}
		return false;
	}

	private bool EntityBodyIntersectsCollision(Entity entity, double x, double y, double z, int dimension, out string blockCode, out double penetrationDepth)
	{
		blockCode = "";
		penetrationDepth = 0.0;
		Cuboidf originCollisionBox = entity.OriginCollisionBox;
		if (originCollisionBox == null)
		{
			return false;
		}
		double num = x + (double)originCollisionBox.X1 + 0.035;
		double num2 = y + (double)originCollisionBox.Y1 + 0.035;
		double num3 = z + (double)originCollisionBox.Z1 + 0.035;
		double num4 = x + (double)originCollisionBox.X2 - 0.035;
		double num5 = y + (double)originCollisionBox.Y2 - 0.035;
		double num6 = z + (double)originCollisionBox.Z2 - 0.035;
		if (num4 <= num || num5 <= num2 || num6 <= num3)
		{
			return false;
		}
		int num7 = (int)Math.Floor(num);
		int num8 = (int)Math.Floor(num2);
		int num9 = (int)Math.Floor(num3);
		int num10 = (int)Math.Floor(num4);
		int num11 = (int)Math.Floor(num5);
		int num12 = (int)Math.Floor(num6);
		IBlockAccessor blockAccessor = api.World.BlockAccessor;
		for (int i = num7; i <= num10; i++)
		{
			for (int j = num8; j <= num11; j++)
			{
				for (int k = num9; k <= num12; k++)
				{
					BlockPos pos = new BlockPos(i, j, k, dimension);
					Block block = blockAccessor.GetBlock(pos);
					if (block == null || block.Id == 0)
					{
						continue;
					}
					Cuboidf[] array;
					try
					{
						array = block.GetCollisionBoxes(blockAccessor, pos) ?? Array.Empty<Cuboidf>();
					}
					catch
					{
						continue;
					}
					Cuboidf[] array2 = array;
					foreach (Cuboidf cuboidf in array2)
					{
						double num13 = (float)i + cuboidf.X1;
						double num14 = (float)j + cuboidf.Y1;
						double num15 = (float)k + cuboidf.Z1;
						double num16 = (float)i + cuboidf.X2;
						double num17 = (float)j + cuboidf.Y2;
						double num18 = (float)k + cuboidf.Z2;
						if (num4 > num13 && num < num16 && num5 > num14 && num2 < num17 && num6 > num15 && num3 < num18)
						{
							double val = Math.Min(num4, num16) - Math.Max(num, num13);
							double val2 = Math.Min(num5, num17) - Math.Max(num2, num14);
							double val3 = Math.Min(num6, num18) - Math.Max(num3, num15);
							double num19 = Math.Max(0.0, Math.Min(val, Math.Min(val2, val3)));
							if (!(num19 <= penetrationDepth))
							{
								penetrationDepth = num19;
								blockCode = block.Code?.ToString() ?? block.Id.ToString();
							}
						}
					}
				}
			}
		}
		return penetrationDepth > 0.0;
	}

	private bool RollbackMovement(IServerPlayer player, MovementState state, Entity entity, long now, string reason)
	{
		state.GraceUntilUtc = now + config.PacketGuardCorrectionGraceMs;
		PacketGuardTrustedState.Mark(player, reason, config.PacketGuardCorrectionGraceMs);
		SafePositionSample sample = state.GetRollbackPosition(now, config.PacketGuardRollbackSafetyLagMs, config.PacketGuardSafePositionMaxAgeMs, config.PacketGuardSafePositionHistorySize);
		if (config.PacketGuardRollbackValidateCollision)
		{
			foreach (SafePositionSample rollbackCandidate in state.GetRollbackCandidates(now, config.PacketGuardRollbackSafetyLagMs, config.PacketGuardSafePositionMaxAgeMs, config.PacketGuardSafePositionHistorySize, config.PacketGuardRollbackValidationCandidates))
			{
				if (!EntityBodyIntersectsCollision(entity, rollbackCandidate.X, rollbackCandidate.Y, rollbackCandidate.Z, rollbackCandidate.Dimension, out string _, out double penetrationDepth) || penetrationDepth < 0.03)
				{
					sample = rollbackCandidate;
					break;
				}
			}
		}
		Vec3d position = new Vec3d(sample.X, sample.Y, sample.Z);
		try
		{
			EntityRuntimeCompat.GetPos(entity)?.Motion?.Set(0.0, 0.0, 0.0);
			entity.TeleportTo(position);
			state.AcceptRollback(sample, now);
			state.NoteRollback(now);
			state.ResetAnalysis(now);
			return true;
		}
		catch (Exception ex)
		{
			api.Logger.Warning("[ServerEssentials/AntiCheat] PacketGuard could not roll {0} back to the last safe position: {1}", player.PlayerName, ex.Message);
			return false;
		}
	}

	private void SampleReachPositions(long now)
	{
		if (!config.PacketGuardReachEnabled || !config.PacketGuardReachPositionHistoryEnabled)
		{
			return;
		}
		foreach (IServerPlayer sampledPlayer in sampledPlayers)
		{
			if (!ShouldGuard(sampledPlayer))
			{
				if (sampledPlayer != null && !string.IsNullOrWhiteSpace(sampledPlayer.PlayerUID))
				{
					reachPositionHistory.Remove(sampledPlayer.PlayerUID);
				}
				continue;
			}
			try
			{
				if (sampledPlayerPositions.TryGetValue(sampledPlayer.PlayerUID, out EntityPos value))
				{
					RecordReachPosition(sampledPlayer, now, value);
				}
			}
			catch
			{
			}
		}
	}

	private void RecordReachPosition(IServerPlayer player, long now)
	{
		if (player?.Entity != null && !string.IsNullOrWhiteSpace(player.PlayerUID))
		{
			EntityPos pos = EntityRuntimeCompat.GetPos(player.Entity);
			if (pos != null)
			{
				RecordReachPosition(player, now, pos);
			}
		}
	}

	private void RecordReachPosition(IServerPlayer player, long now, EntityPos pos)
	{
		if (player?.Entity != null && !string.IsNullOrWhiteSpace(player.PlayerUID))
		{
			Vec3d localEyePos = player.Entity.LocalEyePos;
			ReachPositionSample item = new ReachPositionSample(now, pos.X + localEyePos.X, pos.Y + localEyePos.Y, pos.Z + localEyePos.Z, pos.Dimension, player.Entity.EntityId);
			if (!reachPositionHistory.TryGetValue(player.PlayerUID, out Queue<ReachPositionSample> value))
			{
				value = new Queue<ReachPositionSample>();
				reachPositionHistory[player.PlayerUID] = value;
			}
			value.Enqueue(item);
			long num = now - Math.Max(100, config.PacketGuardReachPositionHistoryMs) - 250;
			while (value.Count > 0 && (value.Peek().Utc < num || value.Count > 24))
			{
				value.Dequeue();
			}
		}
	}

	private bool IsClearlyInvalidBlockSelection(IServerPlayer player, BlockSelection? blockSel)
	{
		BlockPos blockPos = blockSel?.Position;
		if (blockPos == null)
		{
			return true;
		}
		if (blockPos.X == 0 && blockPos.InternalY == 0 && blockPos.Z == 0)
		{
			EntityPos pos = EntityRuntimeCompat.GetPos(player.Entity);
			if (pos == null)
			{
				return true;
			}
			double x = pos.X;
			double y = pos.Y;
			double z = pos.Z;
			double num = x * x + y * y + z * z;
			double num2 = Math.Max(64.0, Math.Max(1.0, ((double?)player.WorldData?.PickingRange) ?? 4.5) * 8.0);
			if (num > num2 * num2)
			{
				PacketGuardTrustedState.RecordSuppression(player, "invalid-block-origin", $"Ignored placeholder block target 0,0,0 while player was at {pos.X:0.0},{pos.Y:0.0},{pos.Z:0.0}.", 750, 0L);
				return true;
			}
		}
		return false;
	}

	private bool OnCanUseBlock(IServerPlayer player, BlockSelection blockSel)
	{
		if (!config.PacketGuardReachEnabled || !ShouldGuard(player) || blockSel?.Position == null)
		{
			return true;
		}
		if (IsClearlyInvalidBlockSelection(player, blockSel))
		{
			reachViolations.Remove(player.PlayerUID);
			return true;
		}
		if (IsBlockInReach(player, blockSel, "use", out var result))
		{
			return true;
		}
		RegisterReachViolation(player, blockSel, result, "use");
		return true;
	}

	private void OnPlayerChat(IServerPlayer byPlayer, int channelId, ref string message, ref string data, BoolRef consumed)
	{
		if (byPlayer == null)
		{
			return;
		}
		string text = ChatMessageBoundary.Parse(message, byPlayer.PlayerName, null, byPlayer.Role?.Name).Body.Trim();
		if (text.Length < 2 || text[0] != '/')
		{
			return;
		}
		string[] array = text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
		if (array.Length == 0)
		{
			return;
		}
		string text2 = array[0].TrimStart('/').ToLowerInvariant();
		if (text2 == "spawn" && array.Length == 1 && th3EssentialsEnabled && config.PacketGuardTh3EssentialsSpawnCompatibility && !string.IsNullOrWhiteSpace(byPlayer.PlayerUID))
		{
			th3EssentialsSpawnIntents[byPlayer.PlayerUID] = NowMs() + config.PacketGuardTh3EssentialsSpawnGraceMs;
		}
		if (!IsAdmin(byPlayer) || (text2 != "tp" && text2 != "teleport"))
		{
			return;
		}
		bool flag = array.Skip(1).Any((string t) => t.Equals("all", StringComparison.OrdinalIgnoreCase) || t == "*");
		foreach (IServerPlayer candidate in api.World.AllOnlinePlayers.OfType<IServerPlayer>())
		{
			if (candidate != null && !(candidate.PlayerUID == byPlayer.PlayerUID) && (flag || array.Skip(1).Any((string t) =>
			{
				string text3 = t.Trim(new char[2] { '"', '\'' });
				return text3.Equals(candidate.PlayerName, StringComparison.OrdinalIgnoreCase) || text3.Equals(candidate.PlayerUID, StringComparison.OrdinalIgnoreCase);
			})))
			{
				PacketGuardTrustedState.Mark(candidate, "admin-teleport-command", config.PacketGuardEngineTeleportGraceMs);
			}
		}
	}

	private bool TryConsumeTh3EssentialsSpawnArrival(IServerPlayer player, EntityPos pos, long now)
	{
		if (!config.PacketGuardTh3EssentialsSpawnCompatibility || !th3EssentialsEnabled || string.IsNullOrWhiteSpace(player.PlayerUID))
		{
			return false;
		}
		if (!th3EssentialsSpawnIntents.TryGetValue(player.PlayerUID, out var value))
		{
			return false;
		}
		if (now > value)
		{
			th3EssentialsSpawnIntents.Remove(player.PlayerUID);
			return false;
		}
		bool flag = false;
		try
		{
			FuzzyEntityPos spawnPosition = player.GetSpawnPosition(consumeSpawnUse: false);
			if (spawnPosition != null)
			{
				flag = IsNearSpawn(pos, spawnPosition, Math.Max(8.0, Math.Min(32.0, (double)spawnPosition.Radius + 6.0)));
			}
		}
		catch
		{
		}
		if (!flag)
		{
			try
			{
				Vec3i defaultSpawnOrWorldCenter = CoordinateDisplayService.GetDefaultSpawnOrWorldCenter(api);
				double num = pos.X - ((double)defaultSpawnOrWorldCenter.X + 0.5);
				double num2 = pos.Z - ((double)defaultSpawnOrWorldCenter.Z + 0.5);
				flag = num * num + num2 * num2 <= 576.0 && (defaultSpawnOrWorldCenter.Y <= 0 || Math.Abs(pos.Y - (double)defaultSpawnOrWorldCenter.Y) <= 32.0);
			}
			catch
			{
			}
		}
		if (!flag)
		{
			return false;
		}
		th3EssentialsSpawnIntents.Remove(player.PlayerUID);
		return true;
	}

	private static bool IsNearSpawn(EntityPos pos, EntityPos spawn, double horizontalAllowance)
	{
		if (pos.Dimension != spawn.Dimension)
		{
			return false;
		}
		double num = pos.X - spawn.X;
		double num2 = pos.Z - spawn.Z;
		if (num * num + num2 * num2 > horizontalAllowance * horizontalAllowance)
		{
			return false;
		}
		return Math.Abs(pos.Y - spawn.Y) <= 32.0;
	}

	private bool IsTrustedSpawnArrival(EntityPos pos)
	{
		try
		{
			Vec3i defaultSpawnOrWorldCenter = CoordinateDisplayService.GetDefaultSpawnOrWorldCenter(api);
			double num = pos.X - ((double)defaultSpawnOrWorldCenter.X + 0.5);
			double num2 = pos.Z - ((double)defaultSpawnOrWorldCenter.Z + 0.5);
			double num3 = num * num + num2 * num2;
			if (num3 > 576.0)
			{
				return false;
			}
			return defaultSpawnOrWorldCenter.Y <= 0 || Math.Abs(pos.Y - (double)defaultSpawnOrWorldCenter.Y) <= 16.0;
		}
		catch
		{
			return false;
		}
	}

	private void UpdatePendingTranslocatorTransition(IServerPlayer player, EntityPos pos, long now)
	{
		if (string.IsNullOrWhiteSpace(player.PlayerUID) || player.Entity == null)
		{
			return;
		}
		if (!TryFindActiveTranslocatorForPlayer(player, pos, out BlockPos source, out BlockPos target))
		{
			if (pendingTranslocatorTransitions.TryGetValue(player.PlayerUID, out PendingTranslocatorTransition value) && now - value.LastSeenUtc > 3000)
			{
				pendingTranslocatorTransitions.Remove(player.PlayerUID);
			}
			return;
		}
		BlockPos blockPos = source;
		if (!pendingTranslocatorTransitions.TryGetValue(player.PlayerUID, out PendingTranslocatorTransition value2) || value2.SourceDimension != blockPos.dimension || value2.SourceX != blockPos.X || value2.SourceY != blockPos.Y || value2.SourceZ != blockPos.Z)
		{
			value2 = new PendingTranslocatorTransition
			{
				SourceX = blockPos.X,
				SourceY = blockPos.Y,
				SourceZ = blockPos.Z,
				SourceDimension = blockPos.dimension,
				FirstSeenUtc = now,
				LastSeenUtc = now
			};
			pendingTranslocatorTransitions[player.PlayerUID] = value2;
		}
		else
		{
			value2.LastSeenUtc = now;
		}
		if (target != null)
		{
			value2.HasLinkedTarget = true;
			value2.TargetX = target.X;
			value2.TargetY = target.Y;
			value2.TargetZ = target.Z;
			value2.TargetDimension = target.dimension;
		}
		if (!value2.Armed && now - value2.FirstSeenUtc >= 750)
		{
			value2.Armed = true;
		}
	}

	private bool TryConsumePendingTranslocatorTransition(IServerPlayer player, double sourceX, double sourceY, double sourceZ, int sourceDimension, EntityPos destination, long now, out bool linkedTargetVerified)
	{
		linkedTargetVerified = false;
		if (string.IsNullOrWhiteSpace(player.PlayerUID) || !pendingTranslocatorTransitions.TryGetValue(player.PlayerUID, out PendingTranslocatorTransition value))
		{
			return false;
		}
		if (!value.Armed || now - value.LastSeenUtc > 3000)
		{
			pendingTranslocatorTransitions.Remove(player.PlayerUID);
			return false;
		}
		double num = sourceX - ((double)value.SourceX + 0.5);
		double num2 = sourceZ - ((double)value.SourceZ + 0.5);
		if (sourceDimension != value.SourceDimension || !(num * num + num2 * num2 <= 36.0) || !(Math.Abs(sourceY - (double)value.SourceY) <= 6.0))
		{
			pendingTranslocatorTransitions.Remove(player.PlayerUID);
			return false;
		}
		if (value.HasLinkedTarget)
		{
			double num3 = destination.X - ((double)value.TargetX + 0.5);
			double num4 = destination.Z - ((double)value.TargetZ + 0.5);
			if ((value.TargetDimension != destination.Dimension && value.TargetDimension != 0 && destination.Dimension != value.SourceDimension) || !(num3 * num3 + num4 * num4 <= 25.0) || !(Math.Abs(destination.Y - ((double)value.TargetY + 1.0)) <= 5.0))
			{
				pendingTranslocatorTransitions.Remove(player.PlayerUID);
				return false;
			}
			linkedTargetVerified = true;
		}
		else if (!IsNearTranslocator(destination.X, destination.Y, destination.Z, destination.Dimension))
		{
			pendingTranslocatorTransitions.Remove(player.PlayerUID);
			return false;
		}
		pendingTranslocatorTransitions.Remove(player.PlayerUID);
		return true;
	}

	private void ClearPendingTranslocatorIfDeparted(string playerUid, EntityPos pos)
	{
		if (pendingTranslocatorTransitions.TryGetValue(playerUid, out PendingTranslocatorTransition value))
		{
			double num = pos.X - ((double)value.SourceX + 0.5);
			double num2 = pos.Z - ((double)value.SourceZ + 0.5);
			if (pos.Dimension != value.SourceDimension || num * num + num2 * num2 > 64.0 || Math.Abs(pos.Y - (double)value.SourceY) > 8.0)
			{
				pendingTranslocatorTransitions.Remove(playerUid);
			}
		}
	}

	private bool TryFindActiveTranslocatorForPlayer(IServerPlayer player, EntityPos pos, out BlockPos? source, out BlockPos? target)
	{
		source = null;
		target = null;
		long entityId = player.Entity.EntityId;
		int num = (int)Math.Floor(pos.X);
		int num2 = (int)Math.Floor(pos.Y);
		int num3 = (int)Math.Floor(pos.Z);
		try
		{
			for (int i = -2; i <= 2; i++)
			{
				for (int j = -2; j <= 2; j++)
				{
					for (int k = -2; k <= 2; k++)
					{
						BlockPos blockPos = new BlockPos(num + j, num2 + i, num3 + k, pos.Dimension);
						string text = api.World.BlockAccessor.GetBlock(blockPos)?.Code?.Path ?? string.Empty;
						if (text.IndexOf("translocator", StringComparison.OrdinalIgnoreCase) >= 0)
						{
							BlockEntity blockEntity = api.World.BlockAccessor.GetBlockEntity(blockPos);
							if (blockEntity != null && BlockEntityTracksTeleportingEntity(blockEntity, entityId))
							{
								source = blockPos.Copy();
								target = TryReadTranslocatorTarget(blockEntity);
								return true;
							}
						}
					}
				}
			}
		}
		catch
		{
		}
		return false;
	}

	private static bool BlockEntityTracksTeleportingEntity(BlockEntity blockEntity, long entityId)
	{
		try
		{
			Type type = blockEntity.GetType();
			while (type != null)
			{
				FieldInfo field = type.GetField("tpingEntities", BindingFlags.DeclaredOnly | BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
				if (!(field == null))
				{
					object value = field.GetValue(blockEntity);
					if (value is IDictionary dictionary)
					{
						return dictionary.Contains(entityId);
					}
					if ((value?.GetType().GetMethod("ContainsKey", new Type[1] { typeof(long) }))?.Invoke(value, new object[1] { entityId }) is bool result)
					{
						return result;
					}
					return false;
				}
				type = type.BaseType;
			}
		}
		catch
		{
		}
		return false;
	}

	private static BlockPos? TryReadTranslocatorTarget(BlockEntity blockEntity)
	{
		try
		{
			Type type = blockEntity.GetType();
			if (type.GetProperty("TargetLocation", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)?.GetValue(blockEntity) is BlockPos blockPos)
			{
				return blockPos.Copy();
			}
			Type type2 = type;
			while (type2 != null)
			{
				if (type2.GetField("tpLocation", BindingFlags.DeclaredOnly | BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)?.GetValue(blockEntity) is BlockPos blockPos2)
				{
					return blockPos2.Copy();
				}
				type2 = type2.BaseType;
			}
		}
		catch
		{
		}
		return null;
	}

	private bool IsTrustedLinkedTranslocatorJump(IServerPlayer player, double sourceX, double sourceY, double sourceZ, int sourceDimension, EntityPos destination)
	{
		if (player?.Entity == null || destination == null)
		{
			return false;
		}
		if (sourceDimension != destination.Dimension)
		{
			return false;
		}
		int num = (int)Math.Floor(sourceX);
		int num2 = (int)Math.Floor(sourceY);
		int num3 = (int)Math.Floor(sourceZ);
		try
		{
			for (int i = -5; i <= 5; i++)
			{
				for (int j = -6; j <= 6; j++)
				{
					for (int k = -6; k <= 6; k++)
					{
						BlockPos blockPos = new BlockPos(num + j, num2 + i, num3 + k, sourceDimension);
						string text = api.World.BlockAccessor.GetBlock(blockPos)?.Code?.Path ?? string.Empty;
						if (text.IndexOf("translocator", StringComparison.OrdinalIgnoreCase) < 0)
						{
							continue;
						}
						BlockEntity blockEntity = api.World.BlockAccessor.GetBlockEntity(blockPos);
						if (blockEntity != null && TryResolveTranslocatorTarget(blockEntity, player.Entity, sourceDimension, out Vec3d target, out int targetDimension) && targetDimension == destination.Dimension)
						{
							double num4 = destination.X - target.X;
							double value = destination.Y - target.Y;
							double num5 = destination.Z - target.Z;
							if (num4 * num4 + num5 * num5 <= 36.0 && Math.Abs(value) <= 6.0)
							{
								return true;
							}
						}
					}
				}
			}
		}
		catch
		{
		}
		return false;
	}

	private static bool TryResolveTranslocatorTarget(BlockEntity blockEntity, Entity forEntity, int sourceDimension, out Vec3d target, out int targetDimension)
	{
		target = new Vec3d();
		targetDimension = sourceDimension;
		try
		{
			if (blockEntity.GetType().GetMethod("GetTarget", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic, null, new Type[1] { typeof(Entity) }, null)?.Invoke(blockEntity, new object[1] { forEntity }) is Vec3d vec3d)
			{
				target = vec3d.Clone();
				BlockPos blockPos = TryReadTranslocatorTarget(blockEntity);
				if (blockPos != null)
				{
					targetDimension = blockPos.dimension;
				}
				return true;
			}
			BlockPos blockPos2 = TryReadTranslocatorTarget(blockEntity);
			if (blockPos2 != null)
			{
				target = new Vec3d((double)blockPos2.X - 0.3, (double)blockPos2.Y + 1.0, (double)blockPos2.Z - 0.3);
				targetDimension = blockPos2.dimension;
				return true;
			}
		}
		catch
		{
		}
		return false;
	}

	private bool IsTrustedVanillaTranslocatorJump(MovementState state, EntityPos destination)
	{
		if (state.Dimension != destination.Dimension)
		{
			return false;
		}
		return IsNearTranslocator(state.X, state.Y, state.Z, state.Dimension) && IsNearTranslocator(destination.X, destination.Y, destination.Z, destination.Dimension);
	}

	private bool IsTrustedVanillaTranslocatorJump(TrustedTransitionSample state, EntityPos destination)
	{
		if (state.Dimension != destination.Dimension)
		{
			return false;
		}
		return IsNearTranslocator(state.X, state.Y, state.Z, state.Dimension) && IsNearTranslocator(destination.X, destination.Y, destination.Z, destination.Dimension);
	}

	private bool IsNearTranslocator(double x, double y, double z, int dimension)
	{
		int num = (int)Math.Floor(x);
		int num2 = (int)Math.Floor(y);
		int num3 = (int)Math.Floor(z);
		try
		{
			for (int i = -4; i <= 4; i++)
			{
				for (int j = -5; j <= 5; j++)
				{
					for (int k = -5; k <= 5; k++)
					{
						string text = api.World.BlockAccessor.GetBlock(new BlockPos(num + j, num2 + i, num3 + k, dimension))?.Code?.Path ?? string.Empty;
						if (text.IndexOf("translocator", StringComparison.OrdinalIgnoreCase) >= 0)
						{
							return true;
						}
					}
				}
			}
		}
		catch
		{
		}
		return false;
	}

	private bool IsTrustedTPRunesArrival(IServerPlayer? player, EntityPos? pos)
	{
		if (!config.PacketGuardTPRunesCompatibility || !tprunesEnabled || player == null || pos == null)
		{
			return false;
		}
		try
		{
			ItemStack itemStack = (player.InventoryManager?.ActiveHotbarSlot)?.Itemstack;
			CollectibleObject collectibleObject = itemStack?.Collectible;
			AssetLocation assetLocation = collectibleObject?.Code;
			if (assetLocation == null || !string.Equals(assetLocation.Domain, "tprunes", StringComparison.OrdinalIgnoreCase))
			{
				return false;
			}
			string text = collectibleObject?.GetType().FullName ?? "";
			if ((!text.EndsWith(".ItemRune", StringComparison.Ordinal) && !assetLocation.Path.Contains("rune", StringComparison.OrdinalIgnoreCase)) || itemStack?.Attributes == null)
			{
				return false;
			}
			if (!itemStack.Attributes.HasAttribute("x") || !itemStack.Attributes.HasAttribute("y") || !itemStack.Attributes.HasAttribute("z"))
			{
				return false;
			}
			int num = itemStack.Attributes.GetInt("x");
			int num2 = itemStack.Attributes.GetInt("y");
			int num3 = itemStack.Attributes.GetInt("z");
			double num4 = pos.X - (double)num;
			double value = pos.Y - (double)num2;
			double num5 = pos.Z - (double)num3;
			double num6 = Math.Sqrt(num4 * num4 + num5 * num5);
			return num6 <= 3.5 && Math.Abs(value) <= 3.0;
		}
		catch
		{
			return false;
		}
	}

	private static string GetSelectionBoxIdCompat(BlockSelection blockSel)
	{
		try
		{
			Type type = blockSel.GetType();
			if (type.GetField("SelectionBoxId")?.GetValue(blockSel) is string result)
			{
				return result;
			}
			if (type.GetProperty("SelectionBoxId")?.GetValue(blockSel) is string result2)
			{
				return result2;
			}
		}
		catch
		{
		}
		return "";
	}

	private void OnBreakBlock(IServerPlayer player, BlockSelection blockSel, ref float dropQuantityMultiplier, ref EnumHandling handling)
	{
		if (ShouldGuard(player) && !(blockSel?.Position == null))
		{
			ReachCheckResult result;
			if (IsClearlyInvalidBlockSelection(player, blockSel))
			{
				reachViolations.Remove(player.PlayerUID);
			}
			else if (config.PacketGuardReachEnabled && !IsBlockInReach(player, blockSel, "break", out result) && RegisterReachViolation(player, blockSel, result, "break"))
			{
				handling = EnumHandling.PreventDefault;
			}
			else if (config.PacketGuardMiningEnabled)
			{
				CheckMiningBurst(player, blockSel, ref handling);
			}
		}
	}

	private bool IsBlockInReach(IServerPlayer player, BlockSelection blockSel, string action, out ReachCheckResult result)
	{
		long num = NowMs();
		PacketGuardTpsSnapshot snapshot = tickHealth.GetSnapshot();
		double num2 = (snapshot.Enabled ? snapshot.TimingMultiplier : 1.0);
		if (PacketGuardTrustedState.TryGetActiveReason(player.PlayerUID, num, PacketGuardTrustScope.Reach, out string reason))
		{
			PacketGuardTrustedState.RecordSuppression(player, "reach-teleport-grace", $"Block {action} accepted during trusted transition: {reason}.", 1000, num);
			result = ReachCheckResult.AllowedByTeleportGrace;
			return true;
		}
		BlockPos position = blockSel.Position;
		Entity entity = player.Entity;
		Vec3d localEyePos = entity.LocalEyePos;
		EntityPos pos = EntityRuntimeCompat.GetPos(entity);
		if (pos == null)
		{
			result = ReachCheckResult.Unavailable;
			return true;
		}
		double eyeX = pos.X + localEyePos.X;
		double eyeY = pos.Y + localEyePos.Y;
		double eyeZ = pos.Z + localEyePos.Z;
		double num3 = DistanceToBlockCube(eyeX, eyeY, eyeZ, position);
		double num4 = num3;
		bool usedHistory = false;
		long historySampleAgeMs = 0L;
		double num5 = Math.Clamp((double)player.Ping * 1000.0, 0.0, 1000.0);
		int val = Math.Min(config.PacketGuardReachPositionHistoryMs, Math.Max(100, (int)Math.Round((double)config.PacketGuardReachMaxRewindMs * num2)));
		int val2 = Math.Max(100, (int)Math.Round((150.0 + num5 * 0.75) * num2));
		val = Math.Min(val, val2);
		if (config.PacketGuardReachPositionHistoryEnabled && reachPositionHistory.TryGetValue(player.PlayerUID, out Queue<ReachPositionSample> value))
		{
			long num6 = num - val;
			foreach (ReachPositionSample item in value)
			{
				if (item.Utc >= num6 && item.Dimension == pos.Dimension && item.EntityId == entity.EntityId)
				{
					double num7 = DistanceToBlockCube(item.EyeX, item.EyeY, item.EyeZ, position);
					if (!(num7 + 0.0001 >= num4))
					{
						num4 = num7;
						usedHistory = true;
						historySampleAgeMs = Math.Max(0L, num - item.Utc);
					}
				}
			}
		}
		if (config.PacketGuardReachPositionHistoryEnabled)
		{
			try
			{
				RecordReachPosition(player, num);
			}
			catch
			{
			}
		}
		double num8 = Math.Max(0.1, player.WorldData.PickingRange);
		double num9 = (string.Equals(action, "use", StringComparison.OrdinalIgnoreCase) ? config.PacketGuardReachUseExtraTolerance : config.PacketGuardReachBreakExtraTolerance);
		double num10 = 0.0;
		if (config.PacketGuardReachLatencyCompensation)
		{
			num10 = Math.Min(config.PacketGuardReachMaxLatencyCompensation, 0.2 + num5 / 1000.0 * 3.0);
		}
		double num11 = num8 + config.PacketGuardReachTolerance + num9 + num10;
		double exactHitDistance = (TryDistanceToExactHit(eyeX, eyeY, eyeZ, blockSel, out var distance) ? distance : (-1.0));
		result = new ReachCheckResult(num4, num11, num10, num9, num3, exactHitDistance, usedHistory, historySampleAgeMs, val, TeleportGrace: false, PositionUnavailable: false);
		return num4 <= num11;
	}

	private static double DistanceToBlockCube(double eyeX, double eyeY, double eyeZ, BlockPos pos)
	{
		double num = pos.Y;
		double num2 = Math.Clamp(eyeX, pos.X, (double)pos.X + 1.0);
		double num3 = Math.Clamp(eyeY, num, num + 1.0);
		double num4 = Math.Clamp(eyeZ, pos.Z, (double)pos.Z + 1.0);
		double num5 = eyeX - num2;
		double num6 = eyeY - num3;
		double num7 = eyeZ - num4;
		return Math.Sqrt(num5 * num5 + num6 * num6 + num7 * num7);
	}

	private static bool TryDistanceToExactHit(double eyeX, double eyeY, double eyeZ, BlockSelection blockSel, out double distance)
	{
		distance = 0.0;
		Vec3d hitPosition = blockSel.HitPosition;
		BlockPos position = blockSel.Position;
		if (hitPosition == null || position == null)
		{
			return false;
		}
		if (!double.IsFinite(hitPosition.X) || !double.IsFinite(hitPosition.Y) || !double.IsFinite(hitPosition.Z))
		{
			return false;
		}
		if (hitPosition.X < -0.25 || hitPosition.X > 1.25 || hitPosition.Y < -0.25 || hitPosition.Y > 1.25 || hitPosition.Z < -0.25 || hitPosition.Z > 1.25)
		{
			return false;
		}
		double num = (double)position.X + hitPosition.X;
		double num2 = (double)position.Y + hitPosition.Y;
		double num3 = (double)position.Z + hitPosition.Z;
		double num4 = eyeX - num;
		double num5 = eyeY - num2;
		double num6 = eyeZ - num3;
		distance = Math.Sqrt(num4 * num4 + num5 * num5 + num6 * num6);
		return true;
	}

	private bool RegisterReachViolation(IServerPlayer player, BlockSelection blockSel, ReachCheckResult reach, string action)
	{
		BlockPos position = blockSel.Position;
		long num = NowMs();
		PacketGuardTpsSnapshot snapshot = tickHealth.GetSnapshot();
		double num2 = (snapshot.Enabled ? snapshot.TimingMultiplier : 1.0);
		int num3 = Math.Clamp((int)Math.Ceiling((double)config.PacketGuardReachNearThresholdStrikes * Math.Min(2.0, num2)), config.PacketGuardReachNearThresholdStrikes, 12);
		int num4 = Math.Clamp((int)Math.Round(300.0 * num2), 300, 1500);
		string text = $"{action}:{position.X}:{position.InternalY}:{position.Z}:{position.dimension}";
		if (!reachViolations.TryGetValue(player.PlayerUID, out ViolationCounter value))
		{
			value = new ViolationCounter();
			reachViolations[player.PlayerUID] = value;
		}
		else
		{
			long num5 = Math.Max(0L, num - value.LastUtc);
			if (value.LastUtc > 0 && num5 > config.PacketGuardReachStrikeWindowMs)
			{
				value.Count = 0;
			}
			else if (value.LastUtc > 0 && num5 >= config.PacketGuardReachStrikeDecayMs && value.Count > 0)
			{
				int num6 = Math.Max(1, (int)(num5 / config.PacketGuardReachStrikeDecayMs));
				value.Count = Math.Max(0, value.Count - num6);
			}
		}
		value.PruneSignatures(num - config.PacketGuardReachStrikeWindowMs);
		bool flag = value.LastSignature == text && num - value.LastUtc <= num4;
		double num7 = Math.Max(0.0, reach.Distance - reach.Allowed);
		bool flag2 = num7 >= config.PacketGuardReachHardExcess;
		if (flag)
		{
			bool flag3 = value.DistinctSignatureCount >= config.PacketGuardReachDistinctTargetsRequired;
			return !action.Equals("use", StringComparison.OrdinalIgnoreCase) && config.PacketGuardRejectOutOfReachActions && (flag2 || ((value.Count >= num3) & flag3)) && (!snapshot.SuppressTimingRejections | flag2);
		}
		value.Count++;
		value.LastUtc = num;
		value.LastSignature = text;
		value.RecordSignature(text, num);
		bool flag4 = value.Count >= num3;
		bool flag5 = value.DistinctSignatureCount >= config.PacketGuardReachDistinctTargetsRequired;
		if (!flag2 && (!flag4 || !flag5))
		{
			return false;
		}
		AntiCheatSeverity antiCheatSeverity = ((flag2 || value.Count >= num3 + 2) ? AntiCheatSeverity.High : AntiCheatSeverity.Moderate);
		string value2 = ((blockSel.HitPosition == null) ? "none" : $"{blockSel.HitPosition.X:0.00},{blockSel.HitPosition.Y:0.00},{blockSel.HitPosition.Z:0.00}");
		string value3 = GetSelectionBoxIdCompat(blockSel).Replace(';', '_').Replace('=', '_');
		string arg = $"action={action};target={position.X},{position.InternalY},{position.Z};distance={reach.Distance:0.00};currentDistance={reach.CurrentDistance:0.00};exactHitDistance={((reach.ExactHitDistance >= 0.0) ? reach.ExactHitDistance.ToString("0.00") : "n/a")};allowed={reach.Allowed:0.00};excess={num7:0.00};hardExcess={config.PacketGuardReachHardExcess:0.00};basePickingRange={player.WorldData.PickingRange:0.00};configuredTolerance={config.PacketGuardReachTolerance:0.00};actionAllowance={reach.ActionAllowance:0.00};latencyAllowance={reach.LatencyAllowance:0.00};pingMs={Math.Max(0f, player.Ping * 1000f):0};historyEnabled={config.PacketGuardReachPositionHistoryEnabled};historyWindowMs={config.PacketGuardReachPositionHistoryMs};historyRewindMs={reach.HistoryRewindMs};historyUsed={reach.UsedHistory};historySampleAgeMs={reach.HistorySampleAgeMs};strikes={value.Count}/{num3};baseStrikes={config.PacketGuardReachNearThresholdStrikes};distinctTargets={value.DistinctSignatureCount}/{config.PacketGuardReachDistinctTargetsRequired};duplicateWindowMs={num4};windowMs={config.PacketGuardReachStrikeWindowMs};decayMs={config.PacketGuardReachStrikeDecayMs};hit={value2};selectionBoxIndex={blockSel.SelectionBoxIndex};selectionBoxId={value3};didOffset={blockSel.DidOffset};zone={(flag2 ? "hard" : "repeated-near")};{snapshot.Evidence}";
		Action<IServerPlayer, string, AntiCheatSeverity, string, string, string> action2 = reportFinding;
		IServerPlayer arg2 = player;
		AntiCheatSeverity arg3 = antiCheatSeverity;
		string arg4;
		if (action.Equals("use", StringComparison.OrdinalIgnoreCase))
		{
			arg4 = "The server observed a block-use action outside the player's server-side interaction range. The action was recorded as evidence but allowed to continue so modded right-click placement flows cannot lose or desync items.";
		}
		else if (flag2)
		{
			arg4 = "The server rejected a block action substantially outside the player's server-side interaction range.";
		}
		else
		{
			arg4 = (snapshot.SuppressTimingRejections ? "The server observed repeated block actions outside the player's TPS-, history-, and latency-adjusted interaction range; automatic rejection was deferred during critical server lag." : "The server observed repeated block actions outside the player's TPS-, history-, and latency-adjusted interaction range.");
		}
		action2(arg2, "PACKET_BLOCK_REACH", arg3, arg4, player.PlayerUID, arg);
		if (action.Equals("use", StringComparison.OrdinalIgnoreCase))
		{
			return false;
		}
		return config.PacketGuardRejectOutOfReachActions && (!snapshot.SuppressTimingRejections | flag2);
	}

	private void CheckMiningBurst(IServerPlayer player, BlockSelection blockSel, ref EnumHandling handling)
	{
		BlockPos pos = blockSel.Position;
		long now = NowMs();
		PacketGuardTpsSnapshot snapshot = tickHealth.GetSnapshot();
		double num = (snapshot.Enabled ? snapshot.TimingMultiplier : 1.0);
		double num2 = Math.Clamp(config.PacketGuardMiningCadenceSuspiciousRatio / num, 0.05, config.PacketGuardMiningCadenceSuspiciousRatio);
		int num3 = Math.Clamp((int)Math.Ceiling((double)config.PacketGuardMiningCadenceConfirmations * Math.Min(2.0, num)), config.PacketGuardMiningCadenceConfirmations, 12);
		int num4 = Math.Clamp((int)Math.Round((double)config.PacketGuardMiningCadenceConfirmationWindowMs * num), config.PacketGuardMiningCadenceConfirmationWindowMs, 60000);
		int effectiveDuplicateTargetMs = Math.Clamp((int)Math.Round((double)config.PacketGuardMiningDuplicateTargetMs * num), config.PacketGuardMiningDuplicateTargetMs, 5000);
		int num5 = Math.Clamp((int)Math.Ceiling((double)config.PacketGuardConnectedBreakLimit * num), config.PacketGuardConnectedBreakLimit, config.PacketGuardConnectedBreakLimit * 4);
		if (!breakHistory.TryGetValue(player.PlayerUID, out Queue<BreakSample> value))
		{
			value = new Queue<BreakSample>();
			breakHistory[player.PlayerUID] = value;
		}
		int num6 = Math.Max(config.PacketGuardVeinWindowMs, num4);
		while (value.Count > 0 && now - value.Peek().Utc > num6)
		{
			value.Dequeue();
		}
		int candidateDimension = EntityRuntimeCompat.GetPos(player.Entity)?.Dimension ?? 0;
		if (value.Any((BreakSample x) => x.Dimension == candidateDimension && x.X == pos.X && x.Y == pos.Y && x.Z == pos.Z && now - x.Utc <= effectiveDuplicateTargetMs))
		{
			return;
		}
		bool flag = blockSel.Block != null;
		Block block = blockSel.Block ?? api.World.BlockAccessor.GetBlock(pos);
		if (block == null || block.Id <= 0)
		{
			value.Enqueue(new BreakSample(now, pos.X, pos.Y, pos.Z, candidateDimension, 0, 0.0, 0f, 0f, 0f, 0, 0));
			return;
		}
		int id = block.Id;
		MiningExpectation miningExpectation = CalculateMiningExpectation(player, blockSel, block);
		BreakSample breakSample = new BreakSample(now, pos.X, pos.Y, pos.Z, candidateDimension, id, miningExpectation.ExpectedMs, miningExpectation.Resistance, miningExpectation.MiningSpeed, miningExpectation.BlockModifier, miningExpectation.RequiredTier, miningExpectation.ToolTier);
		if (config.PacketGuardMiningCadenceValidationEnabled && miningExpectation.Available && miningExpectation.ExpectedMs >= config.PacketGuardMiningCadenceMinExpectedMs)
		{
			BreakSample? breakSample2 = null;
			foreach (BreakSample item in value)
			{
				if (item.Dimension == breakSample.Dimension && item.BlockId > 0 && Adjacent26(item, breakSample) && (!breakSample2.HasValue || item.Utc > breakSample2.Value.Utc))
				{
					breakSample2 = item;
				}
			}
			if (breakSample2.HasValue)
			{
				long num7 = Math.Max(0L, now - breakSample2.Value.Utc);
				double num8 = miningExpectation.ExpectedMs * num2;
				if (snapshot.SuppressTimingRejections && num7 <= 5)
				{
					PacketGuardTrustedState.RecordSuppression(player, "mining-cadence-collapsed-timestamp", $"Ignored {num7}ms adjacent-break interval during critical TPS degradation; expectedMs={miningExpectation.ExpectedMs:0}.", 1000, now);
					value.Enqueue(breakSample);
					return;
				}
				if ((double)num7 < num8)
				{
					string key = player.PlayerUID + ((miningExpectation.Reliability == MiningExpectationReliability.CustomUnknown) ? ":uncertain" : ":authoritative");
					int num9 = RegisterFixedWindow(miningCadenceWindows, key, now, num4);
					if (num9 >= num3)
					{
						string value2 = block.Code?.ToString() ?? id.ToString();
						string value3 = miningExpectation.ToolCode.Replace(';', '_').Replace('=', '_');
						string arg = $"block={value2};blockId={id};target={pos.X},{pos.InternalY},{pos.Z};expectedMs={miningExpectation.ExpectedMs:0};observedAdjacentMs={num7};suspiciousBelowMs={num8:0};ratio={((miningExpectation.ExpectedMs > 0.0) ? ((double)num7 / miningExpectation.ExpectedMs) : 0.0):0.000};baseSuspiciousRatio={config.PacketGuardMiningCadenceSuspiciousRatio:0.000};effectiveSuspiciousRatio={num2:0.000};resistance={miningExpectation.Resistance:0.###};miningSpeed={miningExpectation.MiningSpeed:0.###};blockModifier={miningExpectation.BlockModifier:0.###};requiredTier={miningExpectation.RequiredTier};toolTier={miningExpectation.ToolTier};tool={value3};tierAdequate={miningExpectation.TierAdequate};material={miningExpectation.Material};materialCapable={miningExpectation.MaterialCapable};canMineNormally={miningExpectation.CanMineNormally};reliability={miningExpectation.Reliability};confirmations={num9}/{num3};baseConfirmations={config.PacketGuardMiningCadenceConfirmations};confirmationWindowMs={num4};baseConfirmationWindowMs={config.PacketGuardMiningCadenceConfirmationWindowMs};duplicateTargetMs={effectiveDuplicateTargetMs};pingMs={Math.Max(0f, player.Ping * 1000f):0};{snapshot.Evidence}";
						if (miningExpectation.Reliability == MiningExpectationReliability.CustomUnknown)
						{
							reportFinding(player, "PACKET_MINING_CADENCE_UNCERTAIN", AntiCheatSeverity.Info, "The server observed an unusually fast mining cadence, but the block or held collectible uses custom breaking logic so the normal expected-time model is not punishment-grade evidence.", player.PlayerUID, arg);
							miningCadenceWindows.Remove(key);
						}
						else
						{
							AntiCheatSeverity antiCheatSeverity = ((!miningExpectation.CanMineNormally || (double)num7 <= miningExpectation.ExpectedMs * 0.15 || num9 >= num3 + 2) ? AntiCheatSeverity.High : AntiCheatSeverity.Moderate);
							if (snapshot.SuppressTimingRejections && miningExpectation.CanMineNormally && antiCheatSeverity == AntiCheatSeverity.High)
							{
								antiCheatSeverity = AntiCheatSeverity.Moderate;
							}
							Action<IServerPlayer, string, AntiCheatSeverity, string, string, string> action = reportFinding;
							IServerPlayer arg2 = player;
							AntiCheatSeverity arg3 = antiCheatSeverity;
							string arg4;
							if (snapshot.SuppressTimingRejections)
							{
								arg4 = "The server observed repeated mining cadence evidence outside the TPS-adjusted server expectation; automatic rejection was deferred during critical server lag.";
							}
							else
							{
								arg4 = (miningExpectation.CanMineNormally ? "The server observed repeated adjacent block breaks substantially faster than the TPS-adjusted server-derived expected mining time." : "The server observed repeated break completions using a held item that does not satisfy the normal material/tier mining path for this block.");
							}
							action(arg2, "PACKET_IMPOSSIBLE_MINING_CADENCE", arg3, arg4, player.PlayerUID, arg);
							miningCadenceWindows.Remove(key);
							if (config.PacketGuardRejectImpossibleMiningCadence && !snapshot.SuppressTimingRejections)
							{
								handling = EnumHandling.PreventDefault;
								return;
							}
						}
					}
				}
			}
		}
		int num10 = Math.Max(num5 + 1, (int)Math.Ceiling((double)num5 * config.PacketGuardConnectedBreakExtremeMultiplier));
		int num11 = CountConnectedSameBlock(value, breakSample, num10, now, config.PacketGuardVeinWindowMs);
		if (num11 >= num5)
		{
			ViolationCounter counter = GetCounter(miningViolations, player.PlayerUID + ":vein", now, 15000L);
			bool flag2 = num11 >= num10;
			bool flag3 = counter.Count >= config.PacketGuardConnectedBreakConfirmations;
			AntiCheatSeverity antiCheatSeverity2;
			if (flag2 | flag3)
			{
				antiCheatSeverity2 = ((counter.Count >= config.PacketGuardConnectedBreakConfirmations + 1) ? AntiCheatSeverity.Critical : AntiCheatSeverity.High);
			}
			else
			{
				antiCheatSeverity2 = AntiCheatSeverity.Moderate;
			}
			if (snapshot.SuppressTimingRejections && antiCheatSeverity2 == AntiCheatSeverity.Critical)
			{
				antiCheatSeverity2 = AntiCheatSeverity.High;
			}
			else if (snapshot.SuppressTimingRejections && antiCheatSeverity2 == AntiCheatSeverity.High)
			{
				antiCheatSeverity2 = AntiCheatSeverity.Moderate;
			}
			string value4 = block.Code?.ToString() ?? id.ToString();
			string text = $"block={value4};blockId={id};blockSource={(flag ? "selection" : "world")};connectedDistinct={num11};windowMs={config.PacketGuardVeinWindowMs};baseLimit={config.PacketGuardConnectedBreakLimit};effectiveLimit={num5};extremeLimit={num10};extreme={flag2};confirmations={counter.Count}/{config.PacketGuardConnectedBreakConfirmations};duplicateTargetMs={effectiveDuplicateTargetMs};target={pos.X},{pos.Y},{pos.Z};{snapshot.Evidence}";
			Action<IServerPlayer, string, AntiCheatSeverity, string, string, string> action2 = reportFinding;
			IServerPlayer arg5 = player;
			AntiCheatSeverity arg6 = antiCheatSeverity2;
			string arg7;
			if (snapshot.SuppressTimingRejections)
			{
				arg7 = "The server observed a TPS-adjusted connected block-break burst, but automatic rejection was deferred during critical server lag.";
			}
			else if (flag2)
			{
				arg7 = "The server observed an extreme connected run of identical block breaks consistent with automated vein-mining packets.";
			}
			else
			{
				arg7 = (flag3 ? "The server observed repeated connected block-break bursts consistent with automated vein-mining packets." : "The server observed a connected block-break burst above the normal threshold; enforcement is waiting for confirmation.");
			}
			action2(arg5, "PACKET_CONNECTED_BREAK_BURST", arg6, arg7, player.PlayerUID, text);
			api.Logger.Warning("[ServerEssentials/AntiCheat] PacketGuard connected break burst for {0}: {1}", player.PlayerName, text);
			if (config.PacketGuardRejectConnectedBreakBursts && !snapshot.SuppressTimingRejections && (flag2 | flag3))
			{
				handling = EnumHandling.PreventDefault;
				return;
			}
		}
		value.Enqueue(breakSample);
		while (value.Count > num5 * 6)
		{
			value.Dequeue();
		}
	}

	private MiningExpectation CalculateMiningExpectation(IServerPlayer player, BlockSelection blockSel, Block block)
	{
		try
		{
			BlockPos position = blockSel.Position;
			ItemSlot itemSlot = player.InventoryManager?.ActiveHotbarSlot;
			ItemStack itemStack = itemSlot?.Itemstack;
			CollectibleObject collectibleObject = itemStack?.Collectible;
			float num = Math.Max(0.001f, block.GetResistance(api.World.BlockAccessor, position));
			int num2 = Math.Max(0, block.RequiredMiningTier);
			int num3 = ((collectibleObject != null) ? Math.Max(0, collectibleObject.ToolTier) : 0);
			EnumBlockMaterial blockMaterial = block.GetBlockMaterial(api.World.BlockAccessor, position, itemStack);
			bool flag = collectibleObject != null && itemSlot != null;
			Dictionary<EnumBlockMaterial, float> dictionary = (flag ? collectibleObject.MiningSpeed : null);
			bool flag2 = num2 <= 0 || !flag || (dictionary != null && dictionary.TryGetValue(blockMaterial, out var value) && value > 0f);
			bool flag3 = num2 <= 0 || !flag || num3 >= num2;
			bool flag4 = num2 <= 0 || !flag || (flag3 & flag2);
			float num4 = ((collectibleObject != null && itemStack != null) ? Math.Max(0.001f, collectibleObject.GetMiningSpeed(itemStack, blockSel, block, player)) : 1f);
			float num5 = 1f;
			if (block.BlockBehaviors != null)
			{
				BlockBehavior[] blockBehaviors = block.BlockBehaviors;
				foreach (BlockBehavior blockBehavior in blockBehaviors)
				{
					if (blockBehavior != null)
					{
						float miningSpeedModifier = blockBehavior.GetMiningSpeedModifier(api.World, position, player);
						if (float.IsFinite(miningSpeedModifier) && miningSpeedModifier > 0f)
						{
							num5 *= miningSpeedModifier;
						}
					}
				}
			}
			num5 = Math.Max(0.001f, num5);
			MiningExpectationReliability reliability = ClassifyMiningExpectation(block, collectibleObject);
			double num6 = Math.Max(0.001, num4 * num5);
			double value2 = (double)num / num6 * 1000.0;
			if (!flag4)
			{
				value2 = 120000.0;
			}
			return new MiningExpectation(Available: true, Math.Clamp(value2, 1.0, 120000.0), num, num4, num5, num2, num3, flag3, flag2, flag4, blockMaterial, reliability, collectibleObject?.Code?.ToString() ?? "hand");
		}
		catch
		{
			return MiningExpectation.Unavailable;
		}
	}

	private static MiningExpectationReliability ClassifyMiningExpectation(Block block, CollectibleObject? collectible)
	{
		try
		{
			Type[] types = new Type[6]
			{
				typeof(IPlayer),
				typeof(BlockSelection),
				typeof(ItemSlot),
				typeof(float),
				typeof(float),
				typeof(int)
			};
			MethodInfo method = block.GetType().GetMethod("OnGettingBroken", types);
			if (method != null && method.DeclaringType != typeof(Block))
			{
				return MiningExpectationReliability.CustomUnknown;
			}
			if (HasCustomBreakingBehavior(block.BlockBehaviors))
			{
				return MiningExpectationReliability.CustomUnknown;
			}
			if (collectible != null)
			{
				if (HasCustomBreakingBehavior(collectible.CollectibleBehaviors))
				{
					return MiningExpectationReliability.CustomUnknown;
				}
				MethodInfo method2 = collectible.GetType().GetMethod("OnBlockBreaking", types);
				if (method2 != null && method2.DeclaringType != typeof(CollectibleObject))
				{
					return MiningExpectationReliability.CustomUnknown;
				}
				Type[] types2 = new Type[4]
				{
					typeof(IItemStack),
					typeof(BlockSelection),
					typeof(Block),
					typeof(IPlayer)
				};
				MethodInfo method3 = collectible.GetType().GetMethod("GetMiningSpeed", types2);
				if (method3 != null && method3.DeclaringType != typeof(CollectibleObject))
				{
					return MiningExpectationReliability.Compatible;
				}
			}
			Type[] types3 = new Type[2]
			{
				typeof(IBlockAccessor),
				typeof(BlockPos)
			};
			MethodInfo method4 = block.GetType().GetMethod("GetResistance", types3);
			if (method4 != null && method4.DeclaringType != typeof(Block))
			{
				return MiningExpectationReliability.Compatible;
			}
			return MiningExpectationReliability.Authoritative;
		}
		catch
		{
			return MiningExpectationReliability.CustomUnknown;
		}
	}

	private static bool HasCustomBreakingBehavior(IEnumerable<CollectibleBehavior>? behaviors)
	{
		if (behaviors == null)
		{
			return false;
		}
		Type[] types = new Type[7]
		{
			typeof(IPlayer),
			typeof(BlockSelection),
			typeof(ItemSlot),
			typeof(float),
			typeof(float),
			typeof(int),
			typeof(EnumHandling).MakeByRefType()
		};
		foreach (CollectibleBehavior behavior in behaviors)
		{
			if (behavior != null)
			{
				MethodInfo method = behavior.GetType().GetMethod("OnBlockBreaking", types);
				if (method != null && method.DeclaringType != typeof(CollectibleBehavior))
				{
					return true;
				}
			}
		}
		return false;
	}

	private static int CountConnectedSameBlock(Queue<BreakSample> history, BreakSample current, int stopAt, long now, int windowMs)
	{
		long cutoff = now - Math.Max(1, windowMs);
		List<BreakSample> list = (from x in history
			where x.Utc >= cutoff && x.BlockId == current.BlockId && x.Dimension == current.Dimension
			group x by (X: x.X, Y: x.Y, Z: x.Z, Dimension: x.Dimension) into g
			select g.OrderByDescending((BreakSample x) => x.Utc).First() into x
			where x.X != current.X || x.Y != current.Y || x.Z != current.Z || x.Dimension != current.Dimension
			select x).ToList();
		list.Add(current);
		List<BreakSample> list2 = new List<BreakSample> { current };
		bool[] array = new bool[list.Count];
		array[list.Count - 1] = true;
		bool flag;
		do
		{
			flag = false;
			for (int num = 0; num < list.Count; num++)
			{
				if (array[num])
				{
					continue;
				}
				BreakSample sample = list[num];
				if (list2.Any((BreakSample x) => Adjacent26(x, sample)))
				{
					array[num] = true;
					list2.Add(sample);
					flag = true;
					if (list2.Count >= stopAt)
					{
						return list2.Count;
					}
				}
			}
		}
		while (flag);
		return list2.Count;
	}

	private static bool Adjacent26(BreakSample a, BreakSample b)
	{
		int num = Math.Abs(a.X - b.X);
		int num2 = Math.Abs(a.Y - b.Y);
		int num3 = Math.Abs(a.Z - b.Z);
		return num <= 1 && num2 <= 1 && num3 <= 1 && (num != 0 || num2 != 0 || num3 != 0);
	}

	private static ViolationCounter GetCounter(Dictionary<string, ViolationCounter> counters, string uid, long now, long windowMs)
	{
		if (!counters.TryGetValue(uid, out ViolationCounter value) || now - value.LastUtc > windowMs)
		{
			value = (counters[uid] = new ViolationCounter());
		}
		value.Count++;
		value.LastUtc = now;
		return value;
	}

	private static int RegisterFixedWindow(Dictionary<string, Queue<long>> windows, string key, long now, long windowMs)
	{
		if (!windows.TryGetValue(key, out Queue<long> value))
		{
			value = (windows[key] = new Queue<long>());
		}
		long num = now - Math.Max(1L, windowMs);
		while (value.Count > 0 && value.Peek() < num)
		{
			value.Dequeue();
		}
		value.Enqueue(now);
		while (value.Count > 16)
		{
			value.Dequeue();
		}
		return value.Count;
	}

	private static long NowMs()
	{
		return DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
	}

	public void Dispose()
	{
		if (started)
		{
			started = false;
			ingressGuard.Dispose();
			RemoveServerTeleportTrustHook();
			api.Event.PlayerNowPlaying -= ResetPlayer;
			api.Event.PlayerRespawn -= ResetPlayer;
			api.Event.PlayerDisconnect -= RemovePlayer;
			api.Event.PlayerChat -= OnPlayerChat;
			api.Event.BreakBlock -= OnBreakBlock;
			api.Event.CanUseBlock -= OnCanUseBlock;
			api.Event.PlayerDimensionChanged -= OnPlayerDimensionChanged;
			if (sharedSamplerTickId != 0)
			{
				api.Event.UnregisterGameTickListener(sharedSamplerTickId);
			}
			sharedSamplerTickId = 0L;
			sampledPlayers.Clear();
			sampledPlayerPositions.Clear();
			tickHealth.Dispose();
			movement.Clear();
			flightReviews.Clear();
			breakHistory.Clear();
			reachViolations.Clear();
			reachPositionHistory.Clear();
			miningViolations.Clear();
			miningCadenceWindows.Clear();
			trustedTransitionSamples.Clear();
			pendingTranslocatorTransitions.Clear();
			th3EssentialsSpawnIntents.Clear();
			PacketGuardTrustedState.ClearAll();
		}
	}
}
