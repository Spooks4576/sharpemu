// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Buffers.Binary;
using System.Text;
using SharpEmu.HLE;
using SharpEmu.Libs.SaveData;
using Xunit;

namespace SharpEmu.Libs.Tests.SaveData;

[Collection("SaveDataMemoryState")]
public sealed class SaveDataMetadataExportsTests : IDisposable
{
    private const ulong Base = 0x1_0000_0000;
    private const ulong MountRequestAddress = Base + 0x100;
    private const ulong MountResultAddress = Base + 0x200;
    private const ulong DirectoryNameAddress = Base + 0x300;
    private const ulong IconAddress = Base + 0x400;
    private const ulong IconBytesAddress = Base + 0x500;
    private const ulong TitleAddress = Base + 0x600;
    private const int UserId = 0x1001;
    private const string TitleId = "METATEST01";
    private const string DirectoryName = "slot0";

    private readonly FakeCpuMemory _memory = new(Base, 0x4000);
    private readonly CpuContext _ctx;
    private readonly string _root;
    private readonly string? _previousRoot;

    public SaveDataMetadataExportsTests()
    {
        _ctx = new CpuContext(_memory, Generation.Gen5);
        _root = Path.Combine(Path.GetTempPath(), $"sharpemu-save-metadata-{Guid.NewGuid():N}");
        _previousRoot = Environment.GetEnvironmentVariable("SHARPEMU_SAVEDATA_DIR");
        Environment.SetEnvironmentVariable("SHARPEMU_SAVEDATA_DIR", _root);
        SaveDataExports.ConfigureApplicationInfo(TitleId);
        MountSave();
    }

    private string SystemPath =>
        Path.Combine(_root, UserId.ToString(), TitleId, DirectoryName, "sce_sys");

    [Fact]
    public void SaveIcon_PersistsGuestPayloadInMountedSave()
    {
        byte[] icon = [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A];
        Assert.True(_memory.TryWrite(IconBytesAddress, icon));
        WriteUInt64(IconAddress, IconBytesAddress);
        WriteUInt64(IconAddress + 0x08, (ulong)icon.Length);
        WriteUInt64(IconAddress + 0x10, (ulong)icon.Length);
        _ctx[CpuRegister.Rdi] = MountResultAddress;
        _ctx[CpuRegister.Rsi] = IconAddress;

        Assert.Equal(0, SaveDataExports.SaveDataSaveIcon(_ctx));
        Assert.Equal(icon, File.ReadAllBytes(Path.Combine(SystemPath, "icon0.png")));
    }

    [Fact]
    public void SetParam_UpdatesRequestedMetadataField()
    {
        var title = new byte[128];
        Encoding.ASCII.GetBytes("San Andreas").CopyTo(title, 0);
        Assert.True(_memory.TryWrite(TitleAddress, title));
        _ctx[CpuRegister.Rdi] = MountResultAddress;
        _ctx[CpuRegister.Rsi] = 1;
        _ctx[CpuRegister.Rdx] = TitleAddress;
        _ctx[CpuRegister.Rcx] = (ulong)title.Length;

        Assert.Equal(0, SaveDataExports.SaveDataSetParam(_ctx));

        var metadata = File.ReadAllBytes(Path.Combine(SystemPath, "sharpemu-param.bin"));
        Assert.Equal(0x530, metadata.Length);
        Assert.Equal(title, metadata[..title.Length]);
    }

    public void Dispose()
    {
        SaveDataExports.ConfigureApplicationInfo(null);
        Environment.SetEnvironmentVariable("SHARPEMU_SAVEDATA_DIR", _previousRoot);
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }

    private void MountSave()
    {
        var directoryName = new byte[32];
        Encoding.ASCII.GetBytes(DirectoryName).CopyTo(directoryName, 0);
        Assert.True(_memory.TryWrite(DirectoryNameAddress, directoryName));
        WriteUInt32(MountRequestAddress, UserId);
        WriteUInt64(MountRequestAddress + 0x08, DirectoryNameAddress);
        WriteUInt32(MountRequestAddress + 0x20, 1u << 2);
        _ctx[CpuRegister.Rdi] = MountRequestAddress;
        _ctx[CpuRegister.Rsi] = MountResultAddress;
        Assert.Equal(0, SaveDataExports.SaveDataMount3(_ctx));
    }

    private void WriteUInt32(ulong address, int value) =>
        WriteUInt32(address, unchecked((uint)value));

    private void WriteUInt32(ulong address, uint value)
    {
        Span<byte> bytes = stackalloc byte[sizeof(uint)];
        BinaryPrimitives.WriteUInt32LittleEndian(bytes, value);
        Assert.True(_memory.TryWrite(address, bytes));
    }

    private void WriteUInt64(ulong address, ulong value)
    {
        Span<byte> bytes = stackalloc byte[sizeof(ulong)];
        BinaryPrimitives.WriteUInt64LittleEndian(bytes, value);
        Assert.True(_memory.TryWrite(address, bytes));
    }
}
