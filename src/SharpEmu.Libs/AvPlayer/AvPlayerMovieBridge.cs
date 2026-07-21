// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

namespace SharpEmu.Libs.AvPlayer;

/// <summary>
/// Carries host-decoded AvPlayer frames to VideoOut. The guest still receives
/// its NV12 frame normally; this copy makes full-screen movies use the same
/// swapchain-sized presentation path as other host-decoded video.
/// </summary>
internal static class AvPlayerMovieBridge
{
    private const uint MaximumOverlayWidth = 1280;
    private static readonly object Gate = new();
    private static readonly object PublishGate = new();
    private static ulong _owner;
    private static byte[]? _writeBuffer;
    private static byte[]? _pendingBuffer;
    private static byte[]? _displayBuffer;
    private static uint _width;
    private static uint _height;
    private static uint _sourceWidth;
    private static uint _sourceHeight;
    private static uint _sourcePitch;
    private static int[]? _sourceColumns;
    private static int[]? _lumaRows;
    private static int[]? _chromaRows;
    private static long _generation;
    private static bool _hasPendingFrame;
    private static bool _active;

    internal static void PublishNv12(
        ulong owner,
        ReadOnlySpan<byte> nv12,
        uint width,
        uint height,
        uint pitch)
    {
        if (owner == 0 || width == 0 || height == 0 || pitch < width ||
            (ulong)pitch * height * 3 / 2 > (ulong)nv12.Length)
        {
            return;
        }

        lock (PublishGate)
        {
            var outputWidth = Math.Min(width, MaximumOverlayWidth);
            var outputHeight = Math.Max(2u, (uint)((ulong)height * outputWidth / width));
            outputHeight &= ~1u;
            var outputLength = checked((int)((ulong)outputWidth * outputHeight * 4));
            byte[] writeBuffer;
            int[] sourceColumns;
            int[] lumaRows;
            int[] chromaRows;
            long generation;

            lock (Gate)
            {
                if (_owner != owner ||
                    _width != outputWidth || _height != outputHeight ||
                    _sourceWidth != width || _sourceHeight != height || _sourcePitch != pitch)
                {
                    _owner = owner;
                    _width = outputWidth;
                    _height = outputHeight;
                    _sourceWidth = width;
                    _sourceHeight = height;
                    _sourcePitch = pitch;
                    _writeBuffer = new byte[outputLength];
                    _pendingBuffer = new byte[outputLength];
                    _displayBuffer = new byte[outputLength];
                    _sourceColumns = BuildSourceColumns(width, outputWidth);
                    (_lumaRows, _chromaRows) = BuildSourceRows(height, pitch, outputHeight);
                    _hasPendingFrame = false;
                    _generation++;
                }
                writeBuffer = _writeBuffer!;
                sourceColumns = _sourceColumns!;
                lumaRows = _lumaRows!;
                chromaRows = _chromaRows!;
                generation = _generation;
            }

            ConvertNv12ToBgra(
                nv12,
                writeBuffer,
                outputWidth,
                outputHeight,
                sourceColumns,
                lumaRows,
                chromaRows);

            // Keep conversion outside Gate so the presenter can read the previous frame.
            lock (Gate)
            {
                if (_generation != generation ||
                    _owner != owner || _width != outputWidth || _height != outputHeight)
                {
                    return;
                }

                (_writeBuffer, _pendingBuffer) = (_pendingBuffer, writeBuffer);
                _hasPendingFrame = true;
                _active = true;
            }
        }
    }

    internal static bool TryGetFrame(out byte[] pixels, out uint width, out uint height)
    {
        lock (Gate)
        {
            if (!_active || _displayBuffer is null || _pendingBuffer is null)
            {
                pixels = [];
                width = 0;
                height = 0;
                return false;
            }

            if (_hasPendingFrame)
            {
                (_pendingBuffer, _displayBuffer) = (_displayBuffer, _pendingBuffer);
                _hasPendingFrame = false;
            }

            pixels = _displayBuffer;
            width = _width;
            height = _height;
            return true;
        }
    }

    internal static void Clear(ulong owner)
    {
        lock (Gate)
        {
            if (_owner == owner)
            {
                _active = false;
                _hasPendingFrame = false;
                _generation++;
            }
        }
    }

    private static void ConvertNv12ToBgra(
        ReadOnlySpan<byte> source,
        Span<byte> destination,
        uint destinationWidth,
        uint destinationHeight,
        ReadOnlySpan<int> sourceColumns,
        ReadOnlySpan<int> lumaRows,
        ReadOnlySpan<int> chromaRows)
    {
        var width = checked((int)destinationWidth);
        var height = checked((int)destinationHeight);
        for (var y = 0; y < height; y++)
        {
            var lumaRow = lumaRows[y];
            var chromaRow = chromaRows[y];
            var destinationRow = y * width * 4;
            for (var x = 0; x < width; x++)
            {
                var sourceX = sourceColumns[x];
                var yValue = source[lumaRow + sourceX];
                var chromaColumn = sourceX & ~1;
                var uValue = source[chromaRow + chromaColumn];
                var vValue = source[chromaRow + chromaColumn + 1];

                // BT.709 limited-range YUV, the normal range for H.264 movies.
                var c = Math.Max(0, yValue - 16);
                var d = uValue - 128;
                var e = vValue - 128;
                var offset = destinationRow + (x * 4);
                destination[offset] = ClampByte((298 * c + 541 * d + 128) >> 8);
                destination[offset + 1] = ClampByte((298 * c - 55 * d - 136 * e + 128) >> 8);
                destination[offset + 2] = ClampByte((298 * c + 459 * e + 128) >> 8);
                destination[offset + 3] = 0xFF;
            }
        }
    }

    private static int[] BuildSourceColumns(uint sourceWidth, uint destinationWidth)
    {
        var columns = new int[checked((int)destinationWidth)];
        for (var x = 0; x < columns.Length; x++)
        {
            columns[x] = checked((int)((ulong)x * sourceWidth / destinationWidth));
        }
        return columns;
    }

    private static (int[] Luma, int[] Chroma) BuildSourceRows(
        uint sourceHeight,
        uint sourcePitch,
        uint destinationHeight)
    {
        var luma = new int[checked((int)destinationHeight)];
        var chroma = new int[luma.Length];
        var chromaOffset = checked((int)((ulong)sourcePitch * sourceHeight));
        for (var y = 0; y < luma.Length; y++)
        {
            var sourceY = (uint)((ulong)y * sourceHeight / destinationHeight);
            luma[y] = checked((int)((ulong)sourceY * sourcePitch));
            chroma[y] = chromaOffset + checked((int)((ulong)(sourceY / 2) * sourcePitch));
        }
        return (luma, chroma);
    }

    private static byte ClampByte(int value) =>
        value <= 0 ? (byte)0 : value >= 255 ? byte.MaxValue : (byte)value;
}
