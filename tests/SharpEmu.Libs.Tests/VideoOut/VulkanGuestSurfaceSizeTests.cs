// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using SharpEmu.Libs.VideoOut;
using Xunit;

namespace SharpEmu.Libs.Tests.VideoOut;

public sealed class VulkanGuestSurfaceSizeTests
{
    [Theory]
    [InlineData(10u, 3360u, 1892u, 25_428_480UL)]
    [InlineData(12u, 3360u, 1892u, 50_856_960UL)]
    [InlineData(169u, 5u, 5u, 32UL)]
    public void GuestSurfaceByteCountUsesFormatFootprint(
        uint format,
        uint width,
        uint height,
        ulong expected)
    {
        Assert.Equal(
            expected,
            VulkanVideoPresenter.GetGuestSurfaceByteCount(format, width, height));
    }
}
