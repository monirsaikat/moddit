using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using Modit.Core.Settings;

namespace Modit.Update;

/// <summary>Pure decision logic of the updater, kept free of I/O so it is unit-tested exhaustively.</summary>
public static class UpdateRules
{
    /// <summary>Velopack channel name, one per architecture and release track (docs/plan/15).</summary>
    public static string ChannelName(Architecture architecture, UpdateChannel channel)
    {
        string arch = architecture switch
        {
            Architecture.Arm64 => "arm64",
            Architecture.X64 => "x64",
            _ => throw new PlatformNotSupportedException($"Unsupported architecture: {architecture}"),
        };
        return $"win-{arch}-{(channel == UpdateChannel.Beta ? "beta" : "stable")}";
    }

    /// <summary>The policy value wins over the user's setting; unknown policy text falls back to stable.</summary>
    public static UpdateChannel EffectiveChannel(string? policyValue, UpdateChannel userSetting)
    {
        if (string.IsNullOrWhiteSpace(policyValue))
        {
            return userSetting;
        }

        return string.Equals(policyValue.Trim(), "beta", StringComparison.OrdinalIgnoreCase)
            ? UpdateChannel.Beta
            : UpdateChannel.Stable;
    }

    public static bool IsSkipped(string version, IEnumerable<string> skippedVersions) =>
        skippedVersions.Contains(version, StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Staged rollout: a machine is eligible for <paramref name="version"/> when its stable bucket
    /// (0-99, derived from machine id and version) is below <paramref name="percent"/>. The bucket
    /// changes per version, so the same machines are not always first.
    /// </summary>
    public static bool IsInRollout(string machineId, string version, int percent)
    {
        if (percent >= 100)
        {
            return true;
        }

        if (percent <= 0)
        {
            return false;
        }

        byte[] hash = SHA256.HashData(Encoding.UTF8.GetBytes($"{machineId}|{version}"));
        uint bucket = BitConverter.ToUInt32(hash, 0) % 100;
        return bucket < (uint)percent;
    }

    /// <summary>
    /// Administrators can hold back new releases for some days. A release with an unknown publish
    /// date is offered, because blocking updates forever on missing metadata is the worse failure.
    /// </summary>
    public static bool IsPastDeferral(DateTimeOffset? publishedAt, int deferDays, DateTimeOffset now) =>
        deferDays <= 0 || publishedAt is null || now - publishedAt.Value >= TimeSpan.FromDays(deferDays);
}
