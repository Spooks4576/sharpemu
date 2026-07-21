// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using SharpEmu.Libs.AvPlayer;
using Xunit;

namespace SharpEmu.Libs.Tests.AvPlayer;

public sealed class AvPlayerTimingTests
{
    [Theory]
    [InlineData(0, 60.0, 0)]
    [InlineData(16, 60.0, 0)]
    [InlineData(17, 60.0, 1)]
    [InlineData(150, 60.0, 9)]
    [InlineData(5767, 60.0, 346)]
    public void PlaybackFrameIndexTracksWallClock(
        int elapsedMilliseconds,
        double framesPerSecond,
        long expectedFrame)
    {
        Assert.Equal(
            expectedFrame,
            AvPlayerExports.PlaybackFrameIndex(
                TimeSpan.FromMilliseconds(elapsedMilliseconds),
                framesPerSecond));
    }

    [Theory]
    [InlineData(0.0)]
    [InlineData(-1.0)]
    [InlineData(double.NaN)]
    [InlineData(double.PositiveInfinity)]
    public void PlaybackFrameIndexRejectsInvalidFrameRates(double framesPerSecond)
    {
        Assert.Equal(
            0,
            AvPlayerExports.PlaybackFrameIndex(TimeSpan.FromSeconds(10), framesPerSecond));
    }

    [Theory]
    [InlineData(10_517UL, 60.0, 631L)]
    [InlineData(3_317UL, 60.0, 199L)]
    [InlineData(5_000UL, 60.0, 300L)]
    public void PlaybackFrameCountIdentifiesTheFinalFrameWithoutAnExtraEofRead(
        ulong durationMilliseconds,
        double framesPerSecond,
        long expectedFrameCount)
    {
        Assert.Equal(
            expectedFrameCount,
            AvPlayerExports.PlaybackFrameCount(durationMilliseconds, framesPerSecond));
    }

    [Theory]
    [InlineData(0UL, 60.0)]
    [InlineData(1000UL, 0.0)]
    [InlineData(1000UL, double.NaN)]
    public void PlaybackFrameCountUsesEofWhenTimingMetadataIsUnavailable(
        ulong durationMilliseconds,
        double framesPerSecond)
    {
        Assert.Equal(
            long.MaxValue,
            AvPlayerExports.PlaybackFrameCount(durationMilliseconds, framesPerSecond));
    }
}
