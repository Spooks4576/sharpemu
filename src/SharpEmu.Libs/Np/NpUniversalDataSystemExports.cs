// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using SharpEmu.HLE;
using System.Buffers.Binary;
using System.Collections.Concurrent;

namespace SharpEmu.Libs.Np;

public static class NpUniversalDataSystemExports
{
    private const int NpUniversalDataSystemErrorInvalidArgument = unchecked((int)0x80553102);
    private static readonly object _eventGate = new();
    private static readonly HashSet<int> _createdEvents = [];
    private static readonly ConcurrentDictionary<ulong, byte> _eventPropertyObjects = new();
    private static readonly ConcurrentDictionary<ulong, byte> _eventPropertyArrays = new();
    private static int _nextHandle = 1;
    private static int _nextEvent = 1;
    private static long _nextEventPropertyObject;
    private static long _nextEventPropertyArray;

    [SysAbiExport(
        Nid = "sjaobBgqeB4",
        ExportName = "sceNpUniversalDataSystemInitialize",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libSceNpUniversalDataSystem")]
    public static int NpUniversalDataSystemInitialize(CpuContext ctx)
    {
        var parameterAddress = ctx[CpuRegister.Rdi];
        if (parameterAddress == 0)
        {
            return ctx.SetReturn(NpUniversalDataSystemErrorInvalidArgument, typeof(long));
        }

        Span<byte> parameters = stackalloc byte[16];
        return ctx.Memory.TryRead(parameterAddress, parameters)
            ? ctx.SetReturn(0, typeof(long))
            : ctx.SetReturn((int)OrbisGen2Result.ORBIS_GEN2_ERROR_MEMORY_FAULT, typeof(long));
    }

    [SysAbiExport(
        Nid = "5zBnau1uIEo",
        ExportName = "sceNpUniversalDataSystemCreateContext",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libSceNpUniversalDataSystem")]
    public static int NpUniversalDataSystemCreateContext(CpuContext ctx)
    {
        var contextAddress = ctx[CpuRegister.Rdi];
        if (contextAddress == 0)
        {
            return ctx.SetReturn(0, typeof(long));
        }

        Span<byte> context = stackalloc byte[sizeof(int)];
        BinaryPrimitives.WriteInt32LittleEndian(context, 1);
        return ctx.Memory.TryWrite(contextAddress, context)
            ? ctx.SetReturn(0, typeof(long))
            : ctx.SetReturn((int)OrbisGen2Result.ORBIS_GEN2_ERROR_MEMORY_FAULT, typeof(long));
    }

    [SysAbiExport(
        Nid = "hT0IAEvN+M0",
        ExportName = "sceNpUniversalDataSystemCreateHandle",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libSceNpUniversalDataSystem")]
    public static int NpUniversalDataSystemCreateHandle(CpuContext ctx)
    {
        var handle = Interlocked.Increment(ref _nextHandle);
        if (ctx.TryWriteInt32(ctx[CpuRegister.Rdi], handle, checkNil: true) ||
            ctx.TryWriteInt32(ctx[CpuRegister.Rsi], handle, checkNil: true))
        {
            return ctx.SetReturn(0, typeof(long));
        }

        return ctx.SetReturn((int)OrbisGen2Result.ORBIS_GEN2_ERROR_MEMORY_FAULT, typeof(long));
    }

    [SysAbiExport(
        Nid = "p+GcLqwpL9M",
        ExportName = "sceNpUniversalDataSystemCreateEvent",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libSceNpUniversalDataSystem")]
    public static int NpUniversalDataSystemCreateEvent(CpuContext ctx)
    {
        var eventNameAddress = ctx[CpuRegister.Rdi];
        var propertyObjectHandle = ctx[CpuRegister.Rsi];
        var eventOutputAddress = ctx[CpuRegister.Rdx];
        var propertyObjectOutputAddress = ctx[CpuRegister.Rcx];
        Span<byte> probe = stackalloc byte[1];
        if (eventNameAddress == 0 || eventOutputAddress == 0)
        {
            return ctx.SetReturn(NpUniversalDataSystemErrorInvalidArgument, typeof(long));
        }

        if (!ctx.Memory.TryRead(eventNameAddress, probe))
        {
            return ctx.SetReturn((int)OrbisGen2Result.ORBIS_GEN2_ERROR_MEMORY_FAULT, typeof(long));
        }

        if (propertyObjectHandle != 0 && !_eventPropertyObjects.ContainsKey(propertyObjectHandle))
        {
            return ctx.SetReturn(NpUniversalDataSystemErrorInvalidArgument, typeof(long));
        }

        var eventId = Interlocked.Increment(ref _nextEvent);
        lock (_eventGate)
        {
            _createdEvents.Add(eventId);
        }

        if (!ctx.TryWriteUInt64(eventOutputAddress, unchecked((ulong)eventId)))
        {
            lock (_eventGate)
            {
                _createdEvents.Remove(eventId);
            }

            return ctx.SetReturn((int)OrbisGen2Result.ORBIS_GEN2_ERROR_MEMORY_FAULT, typeof(long));
        }

        ulong createdPropertyObject = 0;
        if (propertyObjectOutputAddress != 0)
        {
            if (propertyObjectHandle == 0)
            {
                createdPropertyObject = CreateOpaqueHandle(
                    ref _nextEventPropertyObject,
                    _eventPropertyObjects);
                propertyObjectHandle = createdPropertyObject;
            }

            if (!ctx.TryWriteUInt64(propertyObjectOutputAddress, propertyObjectHandle))
            {
                lock (_eventGate)
                {
                    _createdEvents.Remove(eventId);
                }

                if (createdPropertyObject != 0)
                {
                    _eventPropertyObjects.TryRemove(createdPropertyObject, out _);
                }

                return ctx.SetReturn((int)OrbisGen2Result.ORBIS_GEN2_ERROR_MEMORY_FAULT, typeof(long));
            }
        }

        return ctx.SetReturn(0, typeof(long));
    }

    [SysAbiExport(
        Nid = "wG+84pnNIuo",
        ExportName = "sceNpUniversalDataSystemDestroyEvent",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libSceNpUniversalDataSystem")]
    public static int NpUniversalDataSystemDestroyEvent(CpuContext ctx)
    {
        var eventId = unchecked((int)ctx[CpuRegister.Rdi]);
        lock (_eventGate)
        {
            _createdEvents.Remove(eventId);
        }

        return ctx.SetReturn(0, typeof(long));
    }

    [SysAbiExport(
        Nid = "s6W4Zl4Slgk",
        ExportName = "sceNpUniversalDataSystemCreateEventPropertyObject",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libSceNpUniversalDataSystem")]
    public static int NpUniversalDataSystemCreateEventPropertyObject(CpuContext ctx)
    {
        var outputAddress = ctx[CpuRegister.Rdi];
        if (outputAddress == 0)
        {
            return ctx.SetReturn(NpUniversalDataSystemErrorInvalidArgument, typeof(long));
        }

        var objectHandle = CreateOpaqueHandle(
            ref _nextEventPropertyObject,
            _eventPropertyObjects);
        if (!ctx.TryWriteUInt64(outputAddress, objectHandle))
        {
            _eventPropertyObjects.TryRemove(objectHandle, out _);
            return ctx.SetReturn((int)OrbisGen2Result.ORBIS_GEN2_ERROR_MEMORY_FAULT, typeof(long));
        }

        return ctx.SetReturn(0, typeof(long));
    }

    [SysAbiExport(
        Nid = "kKUH0Viib3c",
        ExportName = "sceNpUniversalDataSystemDestroyEventPropertyObject",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libSceNpUniversalDataSystem")]
    public static int NpUniversalDataSystemDestroyEventPropertyObject(CpuContext ctx)
    {
        var objectHandle = ctx[CpuRegister.Rdi];
        if (objectHandle != 0)
        {
            _eventPropertyObjects.TryRemove(objectHandle, out _);
        }

        return ctx.SetReturn(0, typeof(long));
    }

    [SysAbiExport(
        Nid = "Hm7qubT3b70",
        ExportName = "sceNpUniversalDataSystemCreateEventPropertyArray",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libSceNpUniversalDataSystem")]
    public static int NpUniversalDataSystemCreateEventPropertyArray(CpuContext ctx)
    {
        var outputAddress = ctx[CpuRegister.Rdi];
        if (outputAddress == 0)
        {
            return ctx.SetReturn(NpUniversalDataSystemErrorInvalidArgument, typeof(long));
        }

        var arrayHandle = CreateOpaqueHandle(
            ref _nextEventPropertyArray,
            _eventPropertyArrays);

        if (!ctx.TryWriteUInt64(outputAddress, arrayHandle))
        {
            _eventPropertyArrays.TryRemove(arrayHandle, out _);
            return ctx.SetReturn((int)OrbisGen2Result.ORBIS_GEN2_ERROR_MEMORY_FAULT, typeof(long));
        }

        return ctx.SetReturn(0, typeof(long));
    }

    [SysAbiExport(
        Nid = "JmgwKm96Lq4",
        ExportName = "sceNpUniversalDataSystemEventPropertyArraySetFloat32",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libSceNpUniversalDataSystem")]
    public static int NpUniversalDataSystemEventPropertyArraySetFloat32(CpuContext ctx)
    {
        var arrayHandle = ctx[CpuRegister.Rdi];
        return ctx.SetReturn(
            arrayHandle != 0 && _eventPropertyArrays.ContainsKey(arrayHandle)
                ? 0
                : NpUniversalDataSystemErrorInvalidArgument,
            typeof(long));
    }

    [SysAbiExport(
        Nid = "W-0xwY0ZMjw",
        ExportName = "sceNpUniversalDataSystemDestroyEventPropertyArray",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libSceNpUniversalDataSystem")]
    public static int NpUniversalDataSystemDestroyEventPropertyArray(CpuContext ctx)
    {
        var arrayHandle = ctx[CpuRegister.Rdi];
        if (arrayHandle != 0)
        {
            _eventPropertyArrays.TryRemove(arrayHandle, out _);
        }

        return ctx.SetReturn(0, typeof(long));
    }

    [SysAbiExport(
        Nid = "MfDb+4Nln64",
        ExportName = "sceNpUniversalDataSystemEventPropertyObjectSetString",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libSceNpUniversalDataSystem")]
    public static int NpUniversalDataSystemEventPropertyObjectSetString(CpuContext ctx)
    {
        var propertyObjectHandle = ctx[CpuRegister.Rdi];
        var keyAddress = ctx[CpuRegister.Rsi];
        var valueAddress = ctx[CpuRegister.Rdx];
        if (propertyObjectHandle == 0 ||
            !_eventPropertyObjects.ContainsKey(propertyObjectHandle) ||
            keyAddress == 0 ||
            valueAddress == 0)
        {
            return ctx.SetReturn(NpUniversalDataSystemErrorInvalidArgument, typeof(long));
        }

        Span<byte> probe = stackalloc byte[1];
        return ctx.Memory.TryRead(keyAddress, probe) &&
               ctx.Memory.TryRead(valueAddress, probe)
            ? ctx.SetReturn(0, typeof(long))
            : ctx.SetReturn((int)OrbisGen2Result.ORBIS_GEN2_ERROR_MEMORY_FAULT, typeof(long));
    }

    [SysAbiExport(
        Nid = "Wxbg5x3pTXA",
        ExportName = "sceNpUniversalDataSystemEventPropertyObjectSetArray",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libSceNpUniversalDataSystem")]
    public static int NpUniversalDataSystemEventPropertyObjectSetArray(CpuContext ctx)
    {
        var propertyObjectHandle = ctx[CpuRegister.Rdi];
        var keyAddress = ctx[CpuRegister.Rsi];
        var valueHandle = ctx[CpuRegister.Rdx];
        var valueOutputAddress = ctx[CpuRegister.Rcx];
        if (propertyObjectHandle == 0 ||
            !_eventPropertyObjects.ContainsKey(propertyObjectHandle) ||
            keyAddress == 0 ||
            valueHandle != 0 && !_eventPropertyArrays.ContainsKey(valueHandle))
        {
            return ctx.SetReturn(NpUniversalDataSystemErrorInvalidArgument, typeof(long));
        }

        Span<byte> probe = stackalloc byte[1];
        if (!ctx.Memory.TryRead(keyAddress, probe))
        {
            return ctx.SetReturn((int)OrbisGen2Result.ORBIS_GEN2_ERROR_MEMORY_FAULT, typeof(long));
        }

        if (valueOutputAddress != 0)
        {
            var outputHandle = valueHandle;
            if (outputHandle == 0)
            {
                outputHandle = CreateOpaqueHandle(
                    ref _nextEventPropertyArray,
                    _eventPropertyArrays);
            }

            if (!ctx.TryWriteUInt64(valueOutputAddress, outputHandle))
            {
                if (valueHandle == 0)
                {
                    _eventPropertyArrays.TryRemove(outputHandle, out _);
                }

                return ctx.SetReturn((int)OrbisGen2Result.ORBIS_GEN2_ERROR_MEMORY_FAULT, typeof(long));
            }
        }

        return ctx.SetReturn(0, typeof(long));
    }

    [SysAbiExport(
        Nid = "CzkKf7ahIyU",
        ExportName = "sceNpUniversalDataSystemPostEvent",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libSceNpUniversalDataSystem")]
    public static int NpUniversalDataSystemPostEvent(CpuContext ctx)
    {
        return ctx.SetReturn(0, typeof(long));
    }

    [SysAbiExport(
        Nid = "tpFJ8LIKvPw",
        ExportName = "sceNpUniversalDataSystemRegisterContext",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libSceNpUniversalDataSystem")]
    public static int NpUniversalDataSystemRegisterContext(CpuContext ctx)
    {
        return ctx.SetReturn(0, typeof(long));
    }

    [SysAbiExport(
        Nid = "AUIHb7jUX3I",
        ExportName = "sceNpUniversalDataSystemDestroyHandle",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libSceNpUniversalDataSystem")]
    public static int NpUniversalDataSystemDestroyHandle(CpuContext ctx)
    {
        return ctx.SetReturn(0, typeof(long));
    }

    internal static void ResetForTests()
    {
        lock (_eventGate)
        {
            _createdEvents.Clear();
        }

        _eventPropertyObjects.Clear();
        _eventPropertyArrays.Clear();
        Interlocked.Exchange(ref _nextHandle, 1);
        Interlocked.Exchange(ref _nextEvent, 1);
        Interlocked.Exchange(ref _nextEventPropertyObject, 0);
        Interlocked.Exchange(ref _nextEventPropertyArray, 0);
    }

    private static ulong CreateOpaqueHandle(
        ref long nextHandle,
        ConcurrentDictionary<ulong, byte> handles)
    {
        ulong handle;
        do
        {
            handle = unchecked((ulong)Interlocked.Increment(ref nextHandle));
        }
        while (handle == 0 || !handles.TryAdd(handle, 0));

        return handle;
    }
}
