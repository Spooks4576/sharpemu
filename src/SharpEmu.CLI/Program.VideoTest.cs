// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using SharpEmu.GUI;
using SharpEmu.HLE;
using SharpEmu.Libs.VideoOut;
using System.Diagnostics;
using System.Globalization;
using System.Numerics;
using System.Text.Json;

namespace SharpEmu.CLI;

internal static partial class Program
{
    private static bool TryGetCubeTestDuration(IReadOnlyList<string> args, out double seconds)
    {
        seconds = 15;
        foreach (var argument in args)
        {
            if (string.Equals(argument, "--vulkan-cube-test", StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }

            const string prefix = "--vulkan-cube-test=";
            if (argument.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            {
                return double.TryParse(
                    argument[prefix.Length..],
                    NumberStyles.Float,
                    CultureInfo.InvariantCulture,
                    out seconds) && seconds > 0;
            }
        }

        return false;
    }

    private static int RunCubeTest(double durationSeconds, VulkanHostSurface? hostSurface)
    {
        const int width = 960;
        const int height = 540;
        const double frameRate = 60;
        if (hostSurface is not null && !VulkanVideoHost.TryAttachSurface(hostSurface))
        {
            Console.Error.WriteLine("[CUBE-TEST][ERROR] The requested GUI host surface is already active.");
            return 3;
        }

        Console.Error.WriteLine(
            $"[CUBE-TEST][INFO] Presenting a {width}x{height} animated RGB cube through Vulkan " +
            $"for {durationSeconds:F1}s at {frameRate:F0} FPS.");
        try
        {
            var clock = Stopwatch.StartNew();
            long frameIndex = 0;
            while (clock.Elapsed.TotalSeconds < durationSeconds)
            {
                var due = TimeSpan.FromSeconds(frameIndex / frameRate);
                var remaining = due - clock.Elapsed;
                if (remaining > TimeSpan.Zero)
                {
                    Thread.Sleep(remaining);
                }

                var pixels = RenderCubeFrame(width, height, (float)clock.Elapsed.TotalSeconds);
                VulkanVideoHost.SubmitBgraFrame(pixels, (uint)width, (uint)height);
                frameIndex++;
            }

            Console.Error.WriteLine(
                $"[CUBE-TEST][INFO] Submitted {frameIndex} frames in {clock.Elapsed.TotalSeconds:F2}s.");
            Thread.Sleep(500);
            return 0;
        }
        finally
        {
            VulkanVideoHost.RequestClose();
            if (hostSurface is not null)
            {
                VulkanVideoHost.DetachSurface(hostSurface);
                hostSurface.Dispose();
            }

            HostSessionControl.SetEmbeddedHostSurface(0);
        }
    }

    private static byte[] RenderCubeFrame(int width, int height, float time)
    {
        var pixels = new byte[width * height * 4];
        var depth = new float[width * height];
        Array.Fill(depth, float.PositiveInfinity);
        for (var y = 0; y < height; y++)
        {
            var shade = (byte)(10 + (24 * y / height));
            for (var x = 0; x < width; x++)
            {
                var offset = ((y * width) + x) * 4;
                pixels[offset] = shade;
                pixels[offset + 1] = (byte)(shade / 2);
                pixels[offset + 2] = (byte)(shade / 3);
                pixels[offset + 3] = 255;
            }
        }

        Vector3[] vertices =
        [
            new(-1, -1, -1), new(1, -1, -1), new(1, 1, -1), new(-1, 1, -1),
            new(-1, -1, 1), new(1, -1, 1), new(1, 1, 1), new(-1, 1, 1),
        ];
        int[] indices =
        [
            0, 2, 1, 0, 3, 2, 4, 5, 6, 4, 6, 7,
            0, 1, 5, 0, 5, 4, 3, 7, 6, 3, 6, 2,
            1, 2, 6, 1, 6, 5, 0, 4, 7, 0, 7, 3,
        ];

        var rotation = Matrix4x4.CreateRotationY(time * 0.9f) *
            Matrix4x4.CreateRotationX(time * 0.63f) *
            Matrix4x4.CreateRotationZ(time * 0.21f);
        var projected = new Vector3[vertices.Length];
        var colors = new Vector3[vertices.Length];
        for (var i = 0; i < vertices.Length; i++)
        {
            var point = Vector3.Transform(vertices[i], rotation);
            var z = point.Z + 4.2f;
            var scale = height * 0.72f / z;
            projected[i] = new Vector3(
                (width * 0.5f) + (point.X * scale),
                (height * 0.5f) - (point.Y * scale),
                z);
            colors[i] = HueToRgb((time * 0.13f) + (i / (float)vertices.Length));
        }

        for (var i = 0; i < indices.Length; i += 3)
        {
            RasterizeTriangle(
                pixels,
                depth,
                width,
                height,
                projected[indices[i]],
                projected[indices[i + 1]],
                projected[indices[i + 2]],
                colors[indices[i]],
                colors[indices[i + 1]],
                colors[indices[i + 2]]);
        }

        return pixels;
    }

    private static void RasterizeTriangle(
        byte[] pixels,
        float[] depth,
        int width,
        int height,
        Vector3 a,
        Vector3 b,
        Vector3 c,
        Vector3 colorA,
        Vector3 colorB,
        Vector3 colorC)
    {
        var area = Edge(a, b, c.X, c.Y);
        if (MathF.Abs(area) < 0.001f)
        {
            return;
        }

        var minX = Math.Clamp((int)MathF.Floor(MathF.Min(a.X, MathF.Min(b.X, c.X))), 0, width - 1);
        var maxX = Math.Clamp((int)MathF.Ceiling(MathF.Max(a.X, MathF.Max(b.X, c.X))), 0, width - 1);
        var minY = Math.Clamp((int)MathF.Floor(MathF.Min(a.Y, MathF.Min(b.Y, c.Y))), 0, height - 1);
        var maxY = Math.Clamp((int)MathF.Ceiling(MathF.Max(a.Y, MathF.Max(b.Y, c.Y))), 0, height - 1);
        for (var y = minY; y <= maxY; y++)
        {
            for (var x = minX; x <= maxX; x++)
            {
                var sampleX = x + 0.5f;
                var sampleY = y + 0.5f;
                var wa = Edge(b, c, sampleX, sampleY) / area;
                var wb = Edge(c, a, sampleX, sampleY) / area;
                var wc = 1 - wa - wb;
                if (wa < 0 || wb < 0 || wc < 0)
                {
                    continue;
                }

                var z = (wa * a.Z) + (wb * b.Z) + (wc * c.Z);
                var pixelIndex = (y * width) + x;
                if (z >= depth[pixelIndex])
                {
                    continue;
                }

                depth[pixelIndex] = z;
                var color = Vector3.Clamp(
                    (wa * colorA) + (wb * colorB) + (wc * colorC),
                    Vector3.Zero,
                    Vector3.One);
                var offset = pixelIndex * 4;
                pixels[offset] = (byte)(color.Z * 255);
                pixels[offset + 1] = (byte)(color.Y * 255);
                pixels[offset + 2] = (byte)(color.X * 255);
                pixels[offset + 3] = 255;
            }
        }

        static float Edge(Vector3 p0, Vector3 p1, float x, float y) =>
            ((x - p0.X) * (p1.Y - p0.Y)) - ((y - p0.Y) * (p1.X - p0.X));
    }

    private static Vector3 HueToRgb(float hue)
    {
        hue -= MathF.Floor(hue);
        var red = MathF.Abs((hue * 6) - 3) - 1;
        var green = 2 - MathF.Abs((hue * 6) - 2);
        var blue = 2 - MathF.Abs((hue * 6) - 4);
        return Vector3.Clamp(new Vector3(red, green, blue), Vector3.Zero, Vector3.One);
    }

    private static bool TryGetVideoTestPath(IReadOnlyList<string> args, out string path)
    {
        for (var i = 0; i < args.Count; i++)
        {
            if (string.Equals(args[i], "--video-test", StringComparison.OrdinalIgnoreCase))
            {
                if (i + 1 < args.Count && !args[i + 1].StartsWith("--", StringComparison.Ordinal))
                {
                    path = args[i + 1];
                    return true;
                }

                path = FindDefaultVideoTestPath() ?? string.Empty;
                return true;
            }

            const string prefix = "--video-test=";
            if (args[i].StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            {
                path = args[i][prefix.Length..];
                return !string.IsNullOrWhiteSpace(path);
            }
        }

        path = string.Empty;
        return false;
    }

    private static string? FindDefaultVideoTestPath()
    {
        var directory = new DirectoryInfo(Directory.GetCurrentDirectory());
        for (var depth = 0; depth < 3 && directory is not null; depth++, directory = directory.Parent)
        {
            var candidate = directory.EnumerateFiles("*.mp4", SearchOption.TopDirectoryOnly)
                .OrderBy(file => file.Name, StringComparer.OrdinalIgnoreCase)
                .FirstOrDefault();
            if (candidate is not null)
            {
                return candidate.FullName;
            }
        }

        return null;
    }

    private static int RunVideoTest(string sourcePath, VulkanHostSurface? hostSurface)
    {
        if (string.IsNullOrWhiteSpace(sourcePath))
        {
            Console.Error.WriteLine(
                "[VIDEO-TEST][ERROR] No MP4 was found in the current folder or either parent folder.");
            return 2;
        }

        sourcePath = Path.GetFullPath(sourcePath);
        if (!File.Exists(sourcePath))
        {
            Console.Error.WriteLine($"[VIDEO-TEST][ERROR] Media file was not found: {sourcePath}");
            return 2;
        }

        var ffmpeg = FindVideoTool("ffmpeg");
        var ffprobe = FindVideoTool("ffprobe");
        if (ffmpeg is null || ffprobe is null)
        {
            Console.Error.WriteLine(
                "[VIDEO-TEST][ERROR] FFmpeg and FFprobe are required. " +
                "Install them or set SHARPEMU_FFMPEG_PATH.");
            return 3;
        }

        if (!TryProbeVideo(ffprobe, sourcePath, out var width, out var height, out var fps, out var error))
        {
            Console.Error.WriteLine($"[VIDEO-TEST][ERROR] {error}");
            return 3;
        }

        if (hostSurface is not null && !VulkanVideoHost.TryAttachSurface(hostSurface))
        {
            Console.Error.WriteLine("[VIDEO-TEST][ERROR] The requested GUI host surface is already active.");
            return 3;
        }

        Console.Error.WriteLine(
            $"[VIDEO-TEST][INFO] Decoding {width}x{height} at {fps:F3} FPS: {sourcePath}");

        try
        {
            using var decoder = StartVideoDecoder(ffmpeg, sourcePath);
            var output = decoder.StandardOutput.BaseStream;
            if ((width & 1) != 0 || (height & 1) != 0)
            {
                Console.Error.WriteLine("[VIDEO-TEST][ERROR] NV12 diagnostics require even dimensions.");
                return 3;
            }

            var frame = new byte[checked(width * height * 3 / 2)];
            var clock = Stopwatch.StartNew();
            long frameIndex = 0;

            while (ReadVideoTestFrame(output, frame))
            {
                var due = TimeSpan.FromSeconds(frameIndex / fps);
                var remaining = due - clock.Elapsed;
                if (remaining > TimeSpan.Zero)
                {
                    Thread.Sleep(remaining);
                }

                // UE receives NV12 from sceAvPlayerGetVideoData*. Convert the
                // same tightly packed Y and interleaved UV planes here before
                // handing the result to the host presenter. This keeps the
                // diagnostic sensitive to plane offsets, pitch and color
                // conversion instead of asking FFmpeg for presentation-ready BGRA.
                VulkanVideoHost.SubmitBgraFrame(
                    ConvertNv12ToBgra(frame, width, height),
                    (uint)width,
                    (uint)height);
                frameIndex++;
            }

            decoder.WaitForExit();
            Console.Error.WriteLine(
                $"[VIDEO-TEST][INFO] Presented {frameIndex} frames in {clock.Elapsed.TotalSeconds:F2}s; " +
                $"decoder exit code {decoder.ExitCode}.");
            Thread.Sleep(500);
            return decoder.ExitCode == 0 ? 0 : 4;
        }
        catch (Exception exception) when (
            exception is IOException or InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            Console.Error.WriteLine($"[VIDEO-TEST][ERROR] {exception.Message}");
            return 4;
        }
        finally
        {
            VulkanVideoHost.RequestClose();
            if (hostSurface is not null)
            {
                VulkanVideoHost.DetachSurface(hostSurface);
                hostSurface.Dispose();
            }

            HostSessionControl.SetEmbeddedHostSurface(0);
        }
    }

    private static Process StartVideoDecoder(string ffmpeg, string sourcePath)
    {
        var startInfo = new ProcessStartInfo(ffmpeg)
        {
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = false,
            CreateNoWindow = true,
        };
        foreach (var argument in new[]
        {
            "-nostdin", "-hide_banner", "-loglevel", "warning", "-i", sourcePath,
            "-map", "0:v:0", "-an", "-vsync", "0", "-pix_fmt", "nv12",
            "-f", "rawvideo", "pipe:1",
        })
        {
            startInfo.ArgumentList.Add(argument);
        }

        return Process.Start(startInfo)
            ?? throw new InvalidOperationException("FFmpeg did not start.");
    }

    private static bool TryProbeVideo(
        string ffprobe,
        string sourcePath,
        out int width,
        out int height,
        out double fps,
        out string error)
    {
        width = 0;
        height = 0;
        fps = 0;
        error = string.Empty;
        try
        {
            var startInfo = new ProcessStartInfo(ffprobe)
            {
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true,
            };
            foreach (var argument in new[]
            {
                "-v", "error", "-select_streams", "v:0", "-show_entries",
                "stream=width,height,avg_frame_rate", "-of", "json", sourcePath,
            })
            {
                startInfo.ArgumentList.Add(argument);
            }

            using var process = Process.Start(startInfo);
            if (process is null)
            {
                error = "FFprobe did not start.";
                return false;
            }

            var json = process.StandardOutput.ReadToEnd();
            var stderr = process.StandardError.ReadToEnd();
            process.WaitForExit();
            if (process.ExitCode != 0)
            {
                error = $"FFprobe failed: {stderr.Trim()}";
                return false;
            }

            using var document = JsonDocument.Parse(json);
            var streams = document.RootElement.GetProperty("streams");
            if (streams.GetArrayLength() == 0)
            {
                error = "The file has no video stream.";
                return false;
            }

            var stream = streams[0];
            width = stream.GetProperty("width").GetInt32();
            height = stream.GetProperty("height").GetInt32();
            fps = ParseFrameRate(stream.GetProperty("avg_frame_rate").GetString());
            if (width <= 0 || height <= 0 || fps <= 0)
            {
                error = "The video has invalid dimensions or frame rate.";
                return false;
            }

            return true;
        }
        catch (Exception exception) when (
            exception is IOException or InvalidOperationException or JsonException or
            System.ComponentModel.Win32Exception)
        {
            error = exception.Message;
            return false;
        }
    }

    private static double ParseFrameRate(string? value)
    {
        var parts = value?.Split('/', 2);
        if (parts is not { Length: 2 } ||
            !double.TryParse(parts[0], NumberStyles.Float, CultureInfo.InvariantCulture, out var numerator) ||
            !double.TryParse(parts[1], NumberStyles.Float, CultureInfo.InvariantCulture, out var denominator) ||
            denominator == 0)
        {
            return 0;
        }

        return numerator / denominator;
    }

    private static string? FindVideoTool(string name)
    {
        var configured = Environment.GetEnvironmentVariable("SHARPEMU_FFMPEG_PATH");
        if (!string.IsNullOrWhiteSpace(configured))
        {
            var directory = Directory.Exists(configured)
                ? configured
                : Path.GetDirectoryName(configured);
            if (!string.IsNullOrWhiteSpace(directory))
            {
                var candidate = Path.Combine(directory, OperatingSystem.IsWindows() ? $"{name}.exe" : name);
                if (File.Exists(candidate))
                {
                    return candidate;
                }
            }
        }

        var path = Environment.GetEnvironmentVariable("PATH") ?? string.Empty;
        foreach (var directory in path.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
        {
            var candidate = Path.Combine(directory.Trim(), OperatingSystem.IsWindows() ? $"{name}.exe" : name);
            if (File.Exists(candidate))
            {
                return candidate;
            }
        }

        return null;
    }

    private static bool ReadVideoTestFrame(Stream stream, byte[] frame)
    {
        var offset = 0;
        while (offset < frame.Length)
        {
            var read = stream.Read(frame, offset, frame.Length - offset);
            if (read == 0)
            {
                return offset == 0 ? false : throw new IOException("FFmpeg returned a partial video frame.");
            }

            offset += read;
        }

        return true;
    }

    private static byte[] ConvertNv12ToBgra(byte[] nv12, int width, int height)
    {
        var expectedLength = checked(width * height * 3 / 2);
        if (nv12.Length != expectedLength)
        {
            throw new InvalidOperationException(
                $"NV12 frame has {nv12.Length} bytes; expected {expectedLength}.");
        }

        var bgra = new byte[checked(width * height * 4)];
        var chromaOffset = width * height;
        for (var y = 0; y < height; y++)
        {
            var lumaRow = y * width;
            var chromaRow = chromaOffset + ((y / 2) * width);
            for (var x = 0; x < width; x++)
            {
                var luma = nv12[lumaRow + x];
                var chroma = chromaRow + (x & ~1);
                var u = nv12[chroma];
                var v = nv12[chroma + 1];

                // BT.709 limited-range conversion, the normal HD-video path.
                var c = Math.Max(luma - 16, 0);
                var d = u - 128;
                var e = v - 128;
                var red = ((298 * c) + (459 * e) + 128) >> 8;
                var green = ((298 * c) - (55 * d) - (136 * e) + 128) >> 8;
                var blue = ((298 * c) + (541 * d) + 128) >> 8;
                var destination = ((y * width) + x) * 4;
                bgra[destination] = (byte)Math.Clamp(blue, 0, 255);
                bgra[destination + 1] = (byte)Math.Clamp(green, 0, 255);
                bgra[destination + 2] = (byte)Math.Clamp(red, 0, 255);
                bgra[destination + 3] = 255;
            }
        }

        return bgra;
    }
}
