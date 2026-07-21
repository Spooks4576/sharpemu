// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using SharpEmu.Libs.AvPlayer;
using Xunit;

namespace SharpEmu.Libs.Tests.AvPlayer;

public sealed class AvPlayerMovieBridgeTests
{
    [Fact]
    public void PublishesNv12FrameAsVisibleBgraFrame()
    {
        const ulong owner = 0x1234;
        // 2x2 NV12: limited-range white with neutral chroma.
        byte[] nv12 = [235, 235, 235, 235, 128, 128];

        AvPlayerMovieBridge.PublishNv12(owner, nv12, 2, 2, 2);

        Assert.True(AvPlayerMovieBridge.TryGetFrame(out var pixels, out var width, out var height));
        Assert.Equal(2u, width);
        Assert.Equal(2u, height);
        Assert.Equal(16, pixels.Length);
        Assert.All(pixels.Chunk(4), pixel =>
        {
            Assert.InRange(pixel[0], (byte)250, byte.MaxValue);
            Assert.InRange(pixel[1], (byte)250, byte.MaxValue);
            Assert.InRange(pixel[2], (byte)250, byte.MaxValue);
            Assert.Equal(byte.MaxValue, pixel[3]);
        });

        AvPlayerMovieBridge.Clear(owner);
        Assert.False(AvPlayerMovieBridge.TryGetFrame(out _, out _, out _));
    }

    [Fact]
    public void IgnoresClearFromDifferentPlayer()
    {
        const ulong owner = 0x5678;
        byte[] nv12 = [16, 16, 16, 16, 128, 128];
        AvPlayerMovieBridge.PublishNv12(owner, nv12, 2, 2, 2);

        AvPlayerMovieBridge.Clear(owner + 1);

        Assert.True(AvPlayerMovieBridge.TryGetFrame(out _, out _, out _));
        AvPlayerMovieBridge.Clear(owner);
    }
}
