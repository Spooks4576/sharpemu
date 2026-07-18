// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using SharpEmu.Libs.AvPlayer;
using Xunit;

namespace SharpEmu.Libs.Tests.AvPlayer;

public sealed class AvPlayerFrameTimingTests
{
    [Theory]
    [InlineData(0, 0, true)]
    [InlineData(1, 0, false)]
    [InlineData(30, 29, false)]
    [InlineData(30, 30, true)]
    [InlineData(29, 30, true)]
    public void FrameIsReturnedOnlyWhenPlaybackClockHasReachedIt(
        long nextFrame,
        long expectedFrame,
        bool due)
    {
        Assert.Equal(due, AvPlayerExports.IsVideoFrameDue(nextFrame, expectedFrame));
    }
}
