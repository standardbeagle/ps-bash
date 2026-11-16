using System.Management.Automation;
using System.Security.Cryptography;
using PsBash.Core;

namespace PsBash.Cmdlets;

/// <summary>
/// A stream device (<c>/dev/stdout</c>, <c>/dev/stderr</c>, <c>/dev/stdin</c>, <c>/dev/fd/N</c>) reached a file open
/// that has no command to resolve it against. Never a file: the name is one of the running command's own
/// descriptors (<see cref="DevicePath"/>). An <see cref="IOException"/> so the site reports it as a failed open
/// (status 1, nothing created) instead of writing <c>C:\dev\stdout</c>.
/// </summary>
internal sealed class DeviceStreamException(string message) : IOException(message);

/// <summary>
/// What the two file choke points (<see cref="RedirectFileWriter.OpenFile"/>, <see cref="BashFileSystem.OpenRead"/>)
/// open for a <see cref="DevicePath"/> device name instead of a file. Nothing here touches the filesystem except
/// <see cref="DevicePath.NativeDevice(DeviceKind)"/> (the OS's own null/zero/full/random/tty device).
/// </summary>
internal static class DeviceStreams
{
    /// <summary>
    /// The write end of <paramref name="kind"/>. <paramref name="owner"/> is the running cmdlet whose stdout and
    /// stderr the stream kinds name; without one a stream kind throws <see cref="DeviceStreamException"/>.
    /// </summary>
    internal static Stream OpenWrite(DeviceKind kind, int fd, string display, PSCmdlet? owner)
    {
        switch (kind)
        {
            case DeviceKind.Stdout:
            case DeviceKind.Stderr:
                if (owner is null) throw NoOwner(display);
                return new CommandStream(owner, kind == DeviceKind.Stderr);
            case DeviceKind.Fd:
                if (!BashFdTable.IsOpen(fd)) throw new DeviceStreamException("Bad file descriptor");
                return new FdWriteStream(fd, owner);
            case DeviceKind.Stdin:
                // bash can write to a terminal's stdin; a pipe or file opened for reading refuses the open.
                throw new DeviceStreamException("Permission denied");
            case DeviceKind.Full:
                if (DevicePath.NativeDevice(kind) is { } full) return OpenNative(full, FileAccess.Write);
                return new SyntheticStream(SyntheticStream.Mode.Full);
            case DeviceKind.Tty:
                return OpenNative(DevicePath.NativeDevice(kind) ?? "CONOUT$", FileAccess.Write);
            default:
                // Null, Zero, Random: writes are discarded.
                return Stream.Null;
        }
    }

    /// <summary>The read end of <paramref name="kind"/>. A stream kind other than an open fd-table descriptor throws
    /// <see cref="DeviceStreamException"/>: the caller's own stdin is the pipeline's input, not a file.</summary>
    internal static Stream OpenRead(DeviceKind kind, int fd, string display)
    {
        switch (kind)
        {
            case DeviceKind.Null:
                return OpenNative(DevicePath.NativeDevice(kind)!, FileAccess.Read);
            case DeviceKind.Zero:
            case DeviceKind.Full:
            case DeviceKind.Random:
                if (DevicePath.NativeDevice(kind) is { } native) return OpenNative(native, FileAccess.Read);
                return new SyntheticStream(kind == DeviceKind.Random ? SyntheticStream.Mode.Random : SyntheticStream.Mode.Zero);
            case DeviceKind.Tty:
                return OpenNative(DevicePath.NativeDevice(kind) ?? "CONIN$", FileAccess.Read);
            case DeviceKind.Fd:
                return new MemoryStream(RawBytes.GetBytes(BashFdTable.ReadToEnd(fd)), writable: false);
            default:
                throw NoOwner(display);
        }
    }

    private static DeviceStreamException NoOwner(string display) =>
        new($"{BashPath.ToShell(display)} is the command's own stream, not a file this command can open");

    private static FileStream OpenNative(string device, FileAccess access)
    {
        try
        {
            return new FileStream(device, FileMode.Open, access, FileShare.ReadWrite, bufferSize: 0);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException or ArgumentException)
        {
            // No console / no controlling terminal: what open("/dev/tty") reports (ENXIO).
            throw new IOException("No such device or address", ex);
        }
    }

    /// <summary>Endless NUL or random bytes to read; writes are discarded, or fail with ENOSPC for <c>/dev/full</c>.</summary>
    private sealed class SyntheticStream(SyntheticStream.Mode mode) : Stream
    {
        internal enum Mode { Zero, Random, Full }

        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => true;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override int Read(byte[] buffer, int offset, int count) => Read(buffer.AsSpan(offset, count));
        public override int Read(Span<byte> buffer)
        {
            if (mode == Mode.Random) RandomNumberGenerator.Fill(buffer); else buffer.Clear();
            return buffer.Length;
        }
        public override void Write(byte[] buffer, int offset, int count) => Write(buffer.AsSpan(offset, count));
        public override void Write(ReadOnlySpan<byte> buffer)
        {
            if (mode == Mode.Full && buffer.Length > 0) throw new IOException("No space left on device");
        }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
    }

    /// <summary>
    /// The owning cmdlet's stdout or stderr as a stream: what <c>tee /dev/stderr</c>, <c>sort -o /dev/stdout</c> write
    /// into. Stdout bytes become pipeline records (one per line; an unterminated tail is held until more arrives or the
    /// stream closes), so an enclosing pipe or redirect applies exactly as it does to the command's ordinary output.
    /// Written on the pipeline thread only: <see cref="Flush"/> (which the writer's idle timer calls) does nothing.
    /// </summary>
    private sealed class CommandStream(PSCmdlet owner, bool stderr) : Stream
    {
        private string _held = "";

        public override bool CanRead => false;
        public override bool CanSeek => false;
        public override bool CanWrite => true;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => Write(buffer.AsSpan(offset, count));

        public override void Write(ReadOnlySpan<byte> buffer)
        {
            if (buffer.Length == 0) return;
            string text = RawBytes.GetString(buffer);
            if (stderr) { FileSystemHelpers.WriteStderrBytes(owner, text); return; }
            text = _held + text;
            int end = text.LastIndexOf('\n');
            _held = text.Substring(end + 1);
            if (end >= 0) Emit(text.Substring(0, end + 1));
        }

        private void Emit(string text)
        {
            foreach (var line in BashRuntime.EmitBashLines(text)) owner.WriteObject(line);
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing && _held.Length > 0)
            {
                string tail = _held;
                _held = "";
                // A stopped pipeline takes no more output; the tail is lost with the rest of it.
                try { Emit(tail); }
                catch (Exception ex) when (ex is PipelineStoppedException or InvalidOperationException) { }
            }
            base.Dispose(disposing);
        }
    }

    /// <summary><c>/dev/fd/N</c> opened for writing: descriptor N of the fd table, or the stream it duplicates.</summary>
    private sealed class FdWriteStream(int fd, PSCmdlet? owner) : Stream
    {
        private CommandStream? _out;
        private CommandStream? _err;

        public override bool CanRead => false;
        public override bool CanSeek => false;
        public override bool CanWrite => true;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => Write(buffer.AsSpan(offset, count));

        public override void Write(ReadOnlySpan<byte> buffer)
        {
            if (buffer.Length == 0) return;
            int std;
            try { std = BashFdTable.Write(fd, RawBytes.GetString(buffer)); }
            catch (InvalidOperationException ex) { throw new IOException(ex.Message, ex); }
            if (std == 0) return;
            if (owner is null) throw new DeviceStreamException("Bad file descriptor");
            var target = std == 2 ? _err ??= new CommandStream(owner, stderr: true) : _out ??= new CommandStream(owner, stderr: false);
            target.Write(buffer);
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing) { _out?.Dispose(); _err?.Dispose(); }
            base.Dispose(disposing);
        }
    }
}
