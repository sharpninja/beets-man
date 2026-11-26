using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Runtime.InteropServices.ComTypes;

namespace BeetsMan.LocalCredentials;

/// <summary>Reads Beets Man credentials from the current user's OS credential store.</summary>
/// <remarks>2026-11-25: Share the existing native lookup between host and desktop for FR-RECORDS-001#ac-register and FR-FILESYNC-001#ac-safety.</remarks>
internal static class OperatingSystemCredentialLookup
{
    private const uint GenericCredential = 1;
    private const int MaximumCredentialBytes = 2560;
    private static readonly TimeSpan LookupTimeout = TimeSpan.FromSeconds(3);

    /// <summary>Validates a local vault reference before allowing an OS lookup.</summary>
    internal static bool IsReference(string? reference) => reference is { Length: > 0 and <= 256 } &&
        char.IsAsciiLetterOrDigit(reference[0]) && reference.All(character => char.IsAsciiLetterOrDigit(character) || "-._/:".Contains(character));

    /// <summary>Checks the exact character set accepted for the host bearer credential.</summary>
    internal static bool IsBearerToken(string? token) => token is { Length: > 0 and <= 2048 } &&
        token.All(character => char.IsAsciiLetterOrDigit(character) || "-._~+/=".Contains(character));

    /// <summary>Looks up one local credential by reference without persisting its value.</summary>
    internal static ValueTask<string?> ReadAsync(string reference, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        if (!IsReference(reference))
            throw new ArgumentException("Invalid credential reference.", nameof(reference));
        if (OperatingSystem.IsLinux())
            return ReadLinuxAsync(reference, token);
        if (OperatingSystem.IsWindows())
            return ValueTask.FromResult(ReadWindows(reference));
        throw new PlatformNotSupportedException("Beets Man requires Linux Secret Service or Windows Credential Manager.");
    }

    private static async ValueTask<string?> ReadLinuxAsync(string reference, CancellationToken token)
    {
        var start = new ProcessStartInfo("/usr/bin/secret-tool")
        {
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };
        foreach (var argument in new[] { "lookup", "application", "beets-man", "reference", reference })
            start.ArgumentList.Add(argument);
        using var process = new Process { StartInfo = start };
        try
        {
            if (!process.Start())
                return null;
        }
        catch (Win32Exception)
        {
            return null;
        }

        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token);
        deadline.CancelAfter(LookupTimeout);
        try
        {
            var output = process.StandardOutput.ReadToEndAsync(deadline.Token);
            var error = process.StandardError.BaseStream.CopyToAsync(Stream.Null, deadline.Token);
            await Task.WhenAll(output, error, process.WaitForExitAsync(deadline.Token)).ConfigureAwait(false);
            return process.ExitCode == 0 ? (await output.ConfigureAwait(false)).TrimEnd('\r', '\n') : null;
        }
        catch (OperationCanceledException)
        {
            try { process.Kill(); }
            catch (InvalidOperationException) { /* The lookup already exited. */ }
            token.ThrowIfCancellationRequested();
            return null;
        }
    }

    private static string? ReadWindows(string reference)
    {
        if (!CredRead("BeetsMan/" + reference, GenericCredential, 0, out var pointer))
            return null;
        try
        {
            var credential = Marshal.PtrToStructure<NativeCredential>(pointer);
            var size = credential.BlobSize;
            return credential.Blob != IntPtr.Zero && size is > 0 and <= MaximumCredentialBytes && size % 2 == 0
                ? Marshal.PtrToStringUni(credential.Blob, (int)size / 2)?.TrimEnd('\0')
                : null;
        }
        finally
        {
            CredFree(pointer);
        }
    }

    [DllImport("Advapi32.dll", EntryPoint = "CredReadW", CharSet = CharSet.Unicode, ExactSpelling = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CredRead(string target, uint type, uint flags, out IntPtr credential);

    [DllImport("Advapi32.dll", ExactSpelling = true)]
    private static extern void CredFree(IntPtr buffer);

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeCredential
    {
        public uint Flags;
        public uint Type;
        public IntPtr TargetName;
        public IntPtr Comment;
        public FILETIME LastWritten;
        public uint BlobSize;
        public IntPtr Blob;
        public uint Persist;
        public uint AttributeCount;
        public IntPtr Attributes;
        public IntPtr TargetAlias;
        public IntPtr UserName;
    }
}
