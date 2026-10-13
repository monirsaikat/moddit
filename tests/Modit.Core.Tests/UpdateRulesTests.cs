using System.Runtime.InteropServices;
using Modit.Core.Settings;
using Modit.Update;

namespace Modit.Core.Tests;

public sealed class UpdateRulesTests
{
    [Theory]
    [InlineData(Architecture.X64, UpdateChannel.Stable, "win-x64-stable")]
    [InlineData(Architecture.X64, UpdateChannel.Beta, "win-x64-beta")]
    [InlineData(Architecture.Arm64, UpdateChannel.Stable, "win-arm64-stable")]
    public void Channel_names_match_what_the_build_script_publishes(Architecture arch, UpdateChannel channel, string expected)
    {
        Assert.Equal(expected, UpdateRules.ChannelName(arch, channel));
    }

    [Theory]
    [InlineData(null, UpdateChannel.Beta, UpdateChannel.Beta)]
    [InlineData("", UpdateChannel.Stable, UpdateChannel.Stable)]
    [InlineData("beta", UpdateChannel.Stable, UpdateChannel.Beta)]
    [InlineData("STABLE", UpdateChannel.Beta, UpdateChannel.Stable)]
    [InlineData("nonsense", UpdateChannel.Beta, UpdateChannel.Stable)]
    public void Policy_overrides_the_user_channel(string? policy, UpdateChannel user, UpdateChannel expected)
    {
        Assert.Equal(expected, UpdateRules.EffectiveChannel(policy, user));
    }

    [Fact]
    public void Rollout_is_stable_per_machine_and_roughly_proportional()
    {
        Assert.True(UpdateRules.IsInRollout("m", "1.0.0", 100));
        Assert.False(UpdateRules.IsInRollout("m", "1.0.0", 0));
        Assert.Equal(UpdateRules.IsInRollout("machine-7", "1.2.3", 40), UpdateRules.IsInRollout("machine-7", "1.2.3", 40));

        int eligible = Enumerable.Range(0, 5000).Count(i => UpdateRules.IsInRollout($"machine-{i}", "1.2.3", 25));
        Assert.InRange(eligible, 1100, 1400);

        // Raising the percentage never removes a machine that was already eligible.
        Assert.All(
            Enumerable.Range(0, 500).Where(i => UpdateRules.IsInRollout($"machine-{i}", "1.2.3", 25)),
            i => Assert.True(UpdateRules.IsInRollout($"machine-{i}", "1.2.3", 50)));
    }

    [Fact]
    public void Deferral_holds_back_recent_releases_only()
    {
        DateTimeOffset now = new(2026, 10, 13, 0, 0, 0, TimeSpan.Zero);

        Assert.True(UpdateRules.IsPastDeferral(now.AddDays(-1), deferDays: 0, now));
        Assert.False(UpdateRules.IsPastDeferral(now.AddDays(-3), deferDays: 7, now));
        Assert.True(UpdateRules.IsPastDeferral(now.AddDays(-7), deferDays: 7, now));
        Assert.True(UpdateRules.IsPastDeferral(null, deferDays: 7, now));
    }

    [Fact]
    public void Skipped_versions_are_matched_case_insensitively()
    {
        Assert.True(UpdateRules.IsSkipped("1.2.0-BETA.1", ["1.2.0-beta.1"]));
        Assert.False(UpdateRules.IsSkipped("1.2.1", ["1.2.0"]));
    }
}
