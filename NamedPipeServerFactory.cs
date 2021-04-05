using System.ComponentModel;
using System.IO.Pipes;
using System.Runtime.InteropServices;

namespace TheCloser;

internal static class NamedPipeServerFactory
{
    public static NamedPipeServerStream Create(string pipeName, PipeSecurity pipeSecurity)
    {
        const uint PIPE_ACCESS_DUPLEX = 3;
        const uint FILE_FLAG_OVERLAPPED = 0x40000000;
        const uint PIPE_TYPE_MESSAGE = 4;
        const uint PIPE_READMODE_MESSAGE = 2;

        const uint openMode = PIPE_ACCESS_DUPLEX | FILE_FLAG_OVERLAPPED;
        const uint pipeMode = PIPE_TYPE_MESSAGE | PIPE_READMODE_MESSAGE;

        var securityAttributes = new NativeMethods.SECURITY_ATTRIBUTES();
        securityAttributes.nLength = Marshal.SizeOf(securityAttributes);

        var securityDescriptor = pipeSecurity.GetSecurityDescriptorBinaryForm();
        securityAttributes.lpSecurityDescriptor = Marshal.AllocHGlobal(securityDescriptor.Length);
        Marshal.Copy(securityDescriptor, 0, securityAttributes.lpSecurityDescriptor, securityDescriptor.Length);

        securityAttributes.bInheritHandle = 0;

        var handle = NativeMethods.CreateNamedPipe($"\\\\.\\pipe\\{pipeName}", openMode, pipeMode, 1, 4096, 4096, 0, ref securityAttributes);

        Marshal.FreeHGlobal(securityAttributes.lpSecurityDescriptor);

        if (handle.IsInvalid)
        {
            throw new Win32Exception(Marshal.GetLastWin32Error());
        }

        return new NamedPipeServerStream(PipeDirection.In, true, true, handle);
    }
}
