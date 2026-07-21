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
    private static ulong _owner;
    private static byte[]? _writeBuffer;
    private static byte[]? _pendingBuffer;
    private static byte[]? _displayBuffer;
    private static uint _width;
    private static uint _height;
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

        var outputWidth = Math.Min(width, MaximumOverlayWidth);
        var outputHeight = Math.Max(2u, (uint)((ulong)height * outputWidth / width));
        outputHeight &= ~1u;
        var outputLength = checked((int)((ulong)outputWidth * outputHeight * 4));

        lock (Gate)
        {
            if (_owner != owner || _width != outputWidth || _height != outputHeight)
            {
                _owner = owner;
                _width = outputWidth;
                _height = outputHeight;
                _writeBuffer = new byte[outputLength];
                _pendingBuffer = new byte[outputLength];
                _displayBuffer = new byte[outputLength];
                _hasPendingFrame = false;
            }

            ConvertNv12ToBgra(
                nv12,
                width,
                height,
                pitch,
                _writeBuffer!,
                outputWidth,
                outputHeight);
            (_writeBuffer, _pendingBuffer) = (_pendingBuffer, _writeBuffer);
            _hasPendingFrame = true;
            _active = true;
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
            }
        }
    }

    private static void ConvertNv12ToBgra(
        ReadOnlySpan<byte> source,
        uint sourceWidth,
        uint sourceHeight,
        uint sourcePitch,
        Span<byte> destination,
        uint destinationWidth,
        uint destinationHeight)
    {
        var chromaOffset = checked((int)((ulong)sourcePitch * sourceHeight));
        for (var y = 0u; y < destinationHeight; y++)
        {
            var sourceY = (uint)((ulong)y * sourceHeight / destinationHeight);
            var lumaRow = checked((int)((ulong)sourceY * sourcePitch));
            var chromaRow = chromaOffset + checked((int)((ulong)(sourceY / 2) * sourcePitch));
            var destinationRow = checked((int)((ulong)y * destinationWidth * 4));
            for (var x = 0u; x < destinationWidth; x++)
            {
                var sourceX = (uint)((ulong)x * sourceWidth / destinationWidth);
                var yValue = source[lumaRow + checked((int)sourceX)];
                var chromaColumn = checked((int)(sourceX & ~1u));
                var uValue = source[chromaRow + chromaColumn];
                var vValue = source[chromaRow + chromaColumn + 1];

                // BT.709 limited-range YUV, the normal range for H.264 movies.
                var c = Math.Max(0, yValue - 16);
                var d = uValue - 128;
                var e = vValue - 128;
                var offset = destinationRow + checked((int)x * 4);
                destination[offset] = ClampByte((298 * c + 541 * d + 128) >> 8);
                destination[offset + 1] = ClampByte((298 * c - 55 * d - 136 * e + 128) >> 8);
                destination[offset + 2] = ClampByte((298 * c + 459 * e + 128) >> 8);
                destination[offset + 3] = 0xFF;
            }
        }
    }

    private static byte ClampByte(int value) => (byte)Math.Clamp(value, 0, 255);
}
