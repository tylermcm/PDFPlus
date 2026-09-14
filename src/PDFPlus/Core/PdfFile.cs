using System.IO;
using System.Runtime.InteropServices;
using PDFPlus.Native;
using static PDFPlus.Native.Pdfium;

namespace PDFPlus.Core;

public sealed class PdfPasswordRequiredException(bool wrongPassword)
    : Exception(wrongPassword ? "Incorrect password." : "This document is password protected.")
{
    public bool WrongPassword { get; } = wrongPassword;
}

public sealed class PdfOpenException(string message) : Exception(message);

/// <summary>Process-wide PDFium state. PDFium is single-threaded, so all calls go through <see cref="Sync"/>.</summary>
internal static unsafe class PdfLibrary
{
    public static readonly object Sync = new();
    private static bool _initialized;

    [ThreadStatic] private static Stream? _sink;

    public static void EnsureInitialized()
    {
        lock (Sync)
        {
            if (_initialized) return;
            FPDF_InitLibrary();
            _initialized = true;
        }
    }

    public static void Save(IntPtr document, Stream output, uint flags = 0)
    {
        lock (Sync)
        {
            var writer = new FPDF_FILEWRITE { version = 1, WriteBlock = &WriteBlock };
            _sink = output;
            try
            {
                if (FPDF_SaveAsCopy(document, &writer, flags) == 0)
                    throw new IOException("The PDF engine could not write this document.");
            }
            finally
            {
                _sink = null;
            }
        }
    }

    public static byte[] SaveToBytes(IntPtr document, uint flags = 0)
    {
        using var stream = new MemoryStream();
        Save(document, stream, flags);
        return stream.ToArray();
    }

    /// <summary>Writes to a temp file next to the target, then swaps it in so a failed save never corrupts the original.</summary>
    public static void WriteFileAtomically(string path, Action<Stream> write)
    {
        var directory = Path.GetDirectoryName(Path.GetFullPath(path))!;
        var temp = Path.Combine(directory, $".{Path.GetFileName(path)}.{Guid.NewGuid():N}.tmp");
        try
        {
            using (var stream = new FileStream(temp, FileMode.CreateNew, FileAccess.Write, FileShare.None, 1 << 16))
                write(stream);
            File.Move(temp, path, overwrite: true);
        }
        catch
        {
            try { File.Delete(temp); } catch { /* best effort */ }
            throw;
        }
    }

    [UnmanagedCallersOnly]
    private static int WriteBlock(FPDF_FILEWRITE* self, byte* data, uint size)
    {
        try
        {
            _sink!.Write(new ReadOnlySpan<byte>(data, checked((int)size)));
            return 1;
        }
        catch
        {
            return 0;
        }
    }
}

/// <summary>
/// Owns a PDFium document handle plus the in-memory bytes it was parsed from. PDFium reads the
/// buffer lazily, so the buffer must outlive the handle. Keeping the file in memory also means
/// the original file on disk is never locked and can be overwritten on save.
/// </summary>
internal sealed unsafe class PdfFile : IDisposable
{
    private byte* _buffer;

    public IntPtr Handle { get; private set; }

    private PdfFile(IntPtr handle, byte* buffer)
    {
        Handle = handle;
        _buffer = buffer;
    }

    public static PdfFile Load(string path, string? password) => Load(File.ReadAllBytes(path), password);

    public static PdfFile Load(ReadOnlySpan<byte> data, string? password)
    {
        PdfLibrary.EnsureInitialized();
        var buffer = (byte*)NativeMemory.Alloc((nuint)Math.Max(data.Length, 1));
        data.CopyTo(new Span<byte>(buffer, data.Length));

        lock (PdfLibrary.Sync)
        {
            var handle = FPDF_LoadMemDocument64((IntPtr)buffer, (nuint)data.Length, password);
            if (handle != IntPtr.Zero) return new PdfFile(handle, buffer);

            var error = FPDF_GetLastError();
            NativeMemory.Free(buffer);
            throw error switch
            {
                4 => (Exception)new PdfPasswordRequiredException(password != null),
                3 => new PdfOpenException("This file isn't a PDF, or it's damaged."),
                5 => new PdfOpenException("This PDF uses an unsupported security scheme."),
                _ => new PdfOpenException("The PDF could not be opened."),
            };
        }
    }

    public static PdfFile CreateEmpty()
    {
        PdfLibrary.EnsureInitialized();
        lock (PdfLibrary.Sync)
            return new PdfFile(FPDF_CreateNewDocument(), null);
    }

    public void Dispose()
    {
        lock (PdfLibrary.Sync)
        {
            if (Handle != IntPtr.Zero) FPDF_CloseDocument(Handle);
            Handle = IntPtr.Zero;
        }
        if (_buffer != null)
        {
            NativeMemory.Free(_buffer);
            _buffer = null;
        }
    }
}
