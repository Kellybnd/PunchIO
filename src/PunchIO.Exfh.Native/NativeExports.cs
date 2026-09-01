using System.Runtime.InteropServices;
using PunchIO.Cobol;
using PunchIO.Configuration;
using Microsoft.Extensions.Configuration;

namespace PunchIO.Exfh;

/// <summary>
/// The native entry point a COBOL runtime links against.
/// </summary>
/// <remarks>
/// <para>
/// Published as a NativeAOT shared library exporting <c>EXTFH</c>. Everything
/// below this file is ordinary managed code; this is only the boundary.
/// </para>
/// <para>
/// <strong>Nothing may throw across this boundary.</strong> An exception escaping
/// an <see cref="UnmanagedCallersOnlyAttribute"/> frame terminates the process
/// rather than being caught by the COBOL runtime, so every path here ends in a
/// file status.
/// </para>
/// </remarks>
public static class NativeExports
{
    private static readonly object Gate = new();
    private static Cobol.Exfh? _exfh;
    private static bool _initializationFailed;

    /// <summary>The external file handler entry point.</summary>
    /// <param name="opcode">A pointer to the two-byte operation code.</param>
    /// <param name="fcd">A pointer to the file control description.</param>
    /// <returns>Zero on success, non-zero otherwise.</returns>
    /// <remarks>
    /// The record area is reached through the control block's record-address
    /// pointer, whose offset — like every other offset — is configured in
    /// <see cref="FcdLayout"/>.
    /// </remarks>
    [UnmanagedCallersOnly(EntryPoint = "EXTFH")]
    public static unsafe int Extfh(byte* opcode, byte* fcd)
    {
        try
        {
            if (opcode is null || fcd is null) return 1;

            var handler = Resolve();

            if (handler is null)
            {
                WriteStatus(fcd, FileStatus.AttributeMismatch);
                return 1;
            }

            var opcodeSpan = new ReadOnlySpan<byte>(opcode, 2);

            // The declared length is read before trusting any other field, and
            // clamped: a control block claiming to be enormous is a corrupt one,
            // and reading past it would be reading the COBOL program's memory.
            int declared = (fcd[FcdLayout.Fcd2.LengthOffset] << 8) | fcd[FcdLayout.Fcd2.LengthOffset + 1];
            int length = declared is > 0 and <= 4096 ? declared : FcdLayout.Fcd2.MinimumLength;

            var fcdSpan = new Span<byte>(fcd, length);
            var view = new FcdView(fcdSpan);

            var recordArea = RecordAreaOf(view, fcd);

            lock (Gate)
            {
                return handler.Execute(opcodeSpan, fcdSpan, recordArea);
            }
        }
        catch (Exception)
        {
            // The last frame before native code. There is nothing above that can
            // handle a .NET exception.
            try
            {
                if (fcd is not null) WriteStatus(fcd, FileStatus.PermanentError);
            }
            catch (Exception)
            {
                // Even reporting the failure failed. Returning is all that is left.
            }

            return 1;
        }
    }

    /// <summary>Closes every open file. A COBOL runtime may call this at shutdown.</summary>
    /// <returns>Zero.</returns>
    [UnmanagedCallersOnly(EntryPoint = "EXTFH_SHUTDOWN")]
    public static int Shutdown()
    {
        try
        {
            lock (Gate)
            {
                _exfh?.Dispose();
                _exfh = null;
                _initializationFailed = false;
            }
        }
        catch (Exception)
        {
            // Nothing above can act on a failure to shut down.
        }

        return 0;
    }

    private static unsafe Span<byte> RecordAreaOf(FcdView view, byte* fcd)
    {
        nint address = 0;

        for (int i = 0; i < 8; i++)
            address = (address << 8) | fcd[view.Layout.RecordAddressOffset + i];

        int length = view.MaxRecordLength;

        return address == 0 || length is <= 0 or > 1024 * 1024
            ? Span<byte>.Empty
            : new Span<byte>((void*)address, length);
    }

    private static unsafe void WriteStatus(byte* fcd, FileStatus status)
    {
        fcd[0] = (byte)status.Code[0];
        fcd[1] = (byte)status.Code[1];
    }

    /// <summary>
    /// Builds the handler on first use, from a configuration file named by the
    /// <c>PUNCHIO_CONFIG</c> environment variable or found beside this library.
    /// </summary>
    /// <remarks>
    /// A failure to initialise is latched. A COBOL program calling a hundred
    /// times a second should not re-read a missing file a hundred times a second.
    /// </remarks>
    private static Cobol.Exfh? Resolve()
    {
        lock (Gate)
        {
            if (_exfh is not null) return _exfh;
            if (_initializationFailed) return null;

            try
            {
                string path = Environment.GetEnvironmentVariable("PUNCHIO_CONFIG")
                    ?? Path.Combine(AppContext.BaseDirectory, "punchio.json");

                var configuration = new ConfigurationBuilder()
                    .AddJsonFile(path, optional: false)
                    .Build();

                var provider = new FileProfileProvider(ServiceCollectionExtensions.Bind(configuration));

                _exfh = new Cobol.Exfh(new ProfileProviderResolver(provider));

                return _exfh;
            }
            catch (Exception)
            {
                _initializationFailed = true;
                return null;
            }
        }
    }

    /// <summary>Resolves a COBOL file name against configured profiles.</summary>
    /// <remarks>
    /// The bare file name is tried first, then the name without its extension, so
    /// a program opening <c>CUSTOMER.DAT</c> matches a profile called
    /// <c>CUSTOMER</c>.
    /// </remarks>
    private sealed class ProfileProviderResolver(IFileProfileProvider provider) : IExfhProfileResolver
    {
        public FileProfile? Resolve(string fileName)
        {
            if (string.IsNullOrWhiteSpace(fileName)) return null;

            return provider.Find(fileName)
                ?? provider.Find(Path.GetFileName(fileName))
                ?? provider.Find(Path.GetFileNameWithoutExtension(fileName));
        }
    }
}
