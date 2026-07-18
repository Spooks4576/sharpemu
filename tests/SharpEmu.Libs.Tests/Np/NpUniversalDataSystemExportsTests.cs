// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using SharpEmu.HLE;
using SharpEmu.Libs.Np;
using Xunit;

namespace SharpEmu.Libs.Tests.Np;

[CollectionDefinition("NpUniversalDataSystemState", DisableParallelization = true)]
public sealed class NpUniversalDataSystemStateCollection
{
    public const string Name = "NpUniversalDataSystemState";
}

[Collection(NpUniversalDataSystemStateCollection.Name)]
public sealed class NpUniversalDataSystemExportsTests : IDisposable
{
    private const int InvalidArgument = unchecked((int)0x80553102);
    private const ulong MemoryBase = 0x1_0000_0000;
    private const ulong ArrayOutputAddress = MemoryBase + 0x100;
    private readonly FakeCpuMemory _memory = new(MemoryBase, 0x1000);
    private readonly CpuContext _ctx;

    public NpUniversalDataSystemExportsTests()
    {
        NpUniversalDataSystemExports.ResetForTests();
        _ctx = new CpuContext(_memory, Generation.Gen5);
    }

    [Fact]
    public void EventPropertyArray_CreateSetFloatAndDestroyMaintainsOpaqueHandle()
    {
        _ctx[CpuRegister.Rdi] = ArrayOutputAddress;
        Assert.Equal(0, NpUniversalDataSystemExports.NpUniversalDataSystemCreateEventPropertyArray(_ctx));
        Assert.True(_ctx.TryReadUInt64(ArrayOutputAddress, out var handle));
        Assert.NotEqual(0ul, handle);

        _ctx[CpuRegister.Rdi] = handle;
        Assert.Equal(0, NpUniversalDataSystemExports.NpUniversalDataSystemEventPropertyArraySetFloat32(_ctx));
        Assert.Equal(0, NpUniversalDataSystemExports.NpUniversalDataSystemDestroyEventPropertyArray(_ctx));
        Assert.Equal(InvalidArgument, NpUniversalDataSystemExports.NpUniversalDataSystemEventPropertyArraySetFloat32(_ctx));
    }

    [Fact]
    public void CreateEvent_WritesEventAndCompanionObjectForArrayProperty()
    {
        var eventNameAddress = _memory.WriteCString(MemoryBase + 0x200, "frame_rate");
        var keyAddress = _memory.WriteCString(MemoryBase + 0x240, "samples");
        var eventOutputAddress = MemoryBase + 0x280;
        var objectOutputAddress = MemoryBase + 0x288;
        _ctx[CpuRegister.Rdi] = eventNameAddress;
        _ctx[CpuRegister.Rsi] = 0;
        _ctx[CpuRegister.Rdx] = eventOutputAddress;
        _ctx[CpuRegister.Rcx] = objectOutputAddress;

        Assert.Equal(0, NpUniversalDataSystemExports.NpUniversalDataSystemCreateEvent(_ctx));
        Assert.True(_ctx.TryReadUInt64(eventOutputAddress, out var eventHandle));
        Assert.True(_ctx.TryReadUInt64(objectOutputAddress, out var objectHandle));
        Assert.NotEqual(0ul, eventHandle);
        Assert.NotEqual(0ul, objectHandle);

        _ctx[CpuRegister.Rdi] = ArrayOutputAddress;
        Assert.Equal(0, NpUniversalDataSystemExports.NpUniversalDataSystemCreateEventPropertyArray(_ctx));
        Assert.True(_ctx.TryReadUInt64(ArrayOutputAddress, out var arrayHandle));
        _ctx[CpuRegister.Rdi] = objectHandle;
        _ctx[CpuRegister.Rsi] = keyAddress;
        _ctx[CpuRegister.Rdx] = arrayHandle;
        _ctx[CpuRegister.Rcx] = 0;
        Assert.Equal(0, NpUniversalDataSystemExports.NpUniversalDataSystemEventPropertyObjectSetArray(_ctx));

        _ctx[CpuRegister.Rdi] = objectHandle;
        Assert.Equal(0, NpUniversalDataSystemExports.NpUniversalDataSystemDestroyEventPropertyObject(_ctx));
        _ctx[CpuRegister.Rdi] = eventHandle;
        Assert.Equal(0, NpUniversalDataSystemExports.NpUniversalDataSystemDestroyEvent(_ctx));
    }

    [Fact]
    public void EventPropertyArrayExports_RegisterForBothGenerations()
    {
        foreach (var generation in new[] { Generation.Gen4, Generation.Gen5 })
        {
            var manager = new ModuleManager();
            manager.RegisterExports(SharpEmu.Generated.SysAbiExportRegistry.CreateExports(generation));

            Assert.True(manager.TryGetExport("Hm7qubT3b70", out var create));
            Assert.Equal("sceNpUniversalDataSystemCreateEventPropertyArray", create.Name);
            Assert.True(manager.TryGetExport("JmgwKm96Lq4", out var setFloat));
            Assert.Equal("sceNpUniversalDataSystemEventPropertyArraySetFloat32", setFloat.Name);
            Assert.True(manager.TryGetExport("W-0xwY0ZMjw", out var destroy));
            Assert.Equal("sceNpUniversalDataSystemDestroyEventPropertyArray", destroy.Name);
            Assert.True(manager.TryGetExport("s6W4Zl4Slgk", out var createObject));
            Assert.Equal("sceNpUniversalDataSystemCreateEventPropertyObject", createObject.Name);
            Assert.True(manager.TryGetExport("kKUH0Viib3c", out var destroyObject));
            Assert.Equal("sceNpUniversalDataSystemDestroyEventPropertyObject", destroyObject.Name);
        }
    }

    public void Dispose()
    {
        NpUniversalDataSystemExports.ResetForTests();
    }
}
