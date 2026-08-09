using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;

namespace CloudLight.CodexBridge.Services;

/// <summary>Stores one UTF-8 secret with Windows DPAPI scoped to the current user.</summary>
public sealed class DpapiSecretStore
{
    private const uint CryptProtectUiForbidden = 0x1;
    private readonly byte[] _optionalEntropy;
    private readonly string _displayName;

    public DpapiSecretStore(string secretFile, string entropy, string displayName)
    {
        if (string.IsNullOrWhiteSpace(secretFile)) throw new ArgumentException("密钥文件路径不能为空。", nameof(secretFile));
        if (string.IsNullOrWhiteSpace(entropy)) throw new ArgumentException("DPAPI entropy 不能为空。", nameof(entropy));
        SecretFile = secretFile;
        _optionalEntropy = Encoding.UTF8.GetBytes(entropy);
        _displayName = string.IsNullOrWhiteSpace(displayName) ? "凭据" : displayName;
    }

    public string SecretFile { get; }

    public async Task SaveAsync(string value, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(value)) throw new ArgumentException($"{_displayName}不能为空。", nameof(value));

        var clearBytes = Encoding.UTF8.GetBytes(value);
        byte[] protectedBytes;
        try
        {
            protectedBytes = await Task.Run(() => Transform(clearBytes, protect: true), cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(clearBytes);
        }

        var directory = Path.GetDirectoryName(SecretFile)!;
        Directory.CreateDirectory(directory);
        var temporaryFile = SecretFile + ".tmp";
        try
        {
            await using (var stream = new FileStream(
                temporaryFile, FileMode.Create, FileAccess.Write, FileShare.None, 4096,
                FileOptions.Asynchronous | FileOptions.WriteThrough))
            {
                await stream.WriteAsync(protectedBytes, cancellationToken).ConfigureAwait(false);
                await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
                stream.Flush(flushToDisk: true);
            }
            File.Move(temporaryFile, SecretFile, overwrite: true);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(protectedBytes);
            if (File.Exists(temporaryFile)) File.Delete(temporaryFile);
        }
    }

    public async Task<string?> LoadAsync(CancellationToken cancellationToken = default)
    {
        if (!await Task.Run(() => File.Exists(SecretFile), cancellationToken).ConfigureAwait(false)) return null;
        byte[] protectedBytes;
        try
        {
            protectedBytes = await File.ReadAllBytesAsync(SecretFile, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception exception)
        {
            throw new DpapiSecretException($"无法读取已保存的{_displayName}文件。", exception);
        }
        if (protectedBytes.Length == 0)
            throw new DpapiSecretException($"无法读取已保存的{_displayName}：DPAPI 文件为空或已损坏。");

        byte[] clearBytes;
        try
        {
            try
            {
                clearBytes = Transform(protectedBytes, protect: false);
            }
            catch (Exception exception) when (exception is Win32Exception or CryptographicException)
            {
                throw new DpapiSecretException($"无法读取已保存的{_displayName}：文件可能已损坏或属于其他 Windows 用户。", exception);
            }
        }
        finally
        {
            CryptographicOperations.ZeroMemory(protectedBytes);
        }

        try
        {
            string value;
            try
            {
                value = new UTF8Encoding(false, true).GetString(clearBytes);
            }
            catch (DecoderFallbackException exception)
            {
                throw new DpapiSecretException($"无法读取已保存的{_displayName}：解密内容已损坏。", exception);
            }
            if (string.IsNullOrWhiteSpace(value))
                throw new DpapiSecretException($"无法读取已保存的{_displayName}：解密内容为空。");
            return value;
        }
        finally
        {
            CryptographicOperations.ZeroMemory(clearBytes);
        }
    }

    public Task DeleteAsync(CancellationToken cancellationToken = default) => Task.Run(() =>
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (File.Exists(SecretFile)) File.Delete(SecretFile);
        var temporaryFile = SecretFile + ".tmp";
        if (File.Exists(temporaryFile)) File.Delete(temporaryFile);
    }, cancellationToken);

    private byte[] Transform(byte[] value, bool protect)
    {
        var input = AllocateBlob(value);
        var entropy = AllocateBlob(_optionalEntropy);
        DataBlob output = default;
        try
        {
            var succeeded = protect
                ? CryptProtectData(ref input, null, ref entropy, IntPtr.Zero, IntPtr.Zero, CryptProtectUiForbidden, out output)
                : CryptUnprotectData(ref input, IntPtr.Zero, ref entropy, IntPtr.Zero, IntPtr.Zero, CryptProtectUiForbidden, out output);
            if (!succeeded) throw new Win32Exception(Marshal.GetLastWin32Error(), "Windows DPAPI 操作失败。");

            var result = new byte[output.Size];
            if (output.Size > 0) Marshal.Copy(output.Data, result, 0, output.Size);
            return result;
        }
        finally
        {
            FreeAllocatedBlob(ref input);
            FreeAllocatedBlob(ref entropy);
            FreeLocalBlob(ref output);
        }
    }

    private static DataBlob AllocateBlob(byte[] value)
    {
        if (value.Length == 0) return default;
        var pointer = Marshal.AllocHGlobal(value.Length);
        Marshal.Copy(value, 0, pointer, value.Length);
        return new DataBlob { Size = value.Length, Data = pointer };
    }

    private static void FreeAllocatedBlob(ref DataBlob blob)
    {
        if (blob.Data == IntPtr.Zero) return;
        Marshal.Copy(new byte[blob.Size], 0, blob.Data, blob.Size);
        Marshal.FreeHGlobal(blob.Data);
        blob = default;
    }

    private static void FreeLocalBlob(ref DataBlob blob)
    {
        if (blob.Data == IntPtr.Zero) return;
        Marshal.Copy(new byte[blob.Size], 0, blob.Data, blob.Size);
        _ = LocalFree(blob.Data);
        blob = default;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct DataBlob
    {
        public int Size;
        public IntPtr Data;
    }

    [DllImport("crypt32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CryptProtectData(
        ref DataBlob dataIn, string? description, ref DataBlob optionalEntropy,
        IntPtr reserved, IntPtr prompt, uint flags, out DataBlob dataOut);

    [DllImport("crypt32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CryptUnprotectData(
        ref DataBlob dataIn, IntPtr description, ref DataBlob optionalEntropy,
        IntPtr reserved, IntPtr prompt, uint flags, out DataBlob dataOut);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr LocalFree(IntPtr memory);
}

public sealed class DpapiSecretException(string message, Exception? innerException = null) : Exception(message, innerException)
{
}
