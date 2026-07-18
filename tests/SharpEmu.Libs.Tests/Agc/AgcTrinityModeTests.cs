// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using SharpEmu.HLE;
using Xunit;

namespace SharpEmu.Libs.Tests.Agc;

public sealed class AgcTrinityModeTests
{
    private const string TrinityModeNid = "BfBDZGbti7A";

    [Fact]
    public void Gen5ReportsBasePs5GpuProfile()
    {
        var manager = new ModuleManager();
        manager.RegisterExports(SharpEmu.Generated.SysAbiExportRegistry.CreateExports(Generation.Gen5));

        Assert.True(manager.TryGetExport(TrinityModeNid, out var export));
        Assert.Equal("sceAgcGetIsTrinityMode", export.Name);
        Assert.Equal("libSceAgc", export.LibraryName);

        var context = new CpuContext(new FakeCpuMemory(0x1000, 0x1000), Generation.Gen5);
        context[CpuRegister.Rax] = ulong.MaxValue;

        Assert.True(manager.TryDispatch(TrinityModeNid, context, out _));
        Assert.Equal(0UL, context[CpuRegister.Rax]);
    }
}
