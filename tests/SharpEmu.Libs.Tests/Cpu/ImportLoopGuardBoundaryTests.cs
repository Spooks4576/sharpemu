// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using SharpEmu.Core.Cpu.Native;
using Xunit;

namespace SharpEmu.Libs.Tests.Cpu;

public sealed class ImportLoopGuardBoundaryTests
{
    [Theory]
    [InlineData("ejekcaNQNq0")] // sceKernelGettimeofday
    [InlineData("n88vx3C5nW8")] // gettimeofday
    [InlineData("lLMT9vJAck0")] // clock_gettime
    public void AdvancingClockImportsResetStaticLoopHistory(string nid)
    {
        Assert.True(DirectExecutionBackend.IsImportLoopGuardBoundary(nid));
    }

    [Fact]
    public void OrdinaryPureImportDoesNotResetLoopHistory()
    {
        Assert.False(DirectExecutionBackend.IsImportLoopGuardBoundary("EI-5-jlq2dE"));
    }
}
