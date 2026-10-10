using System;
using System.IO.Pipes;
using System.Runtime.InteropServices;
using System.Security.Principal;
using Microsoft.Win32.SafeHandles;

namespace PrismShot.Core.Ipc;

/// <summary>
/// 建一条「完整性标签被降到 Medium」的命名管道服务端。
/// </summary>
/// <remarks>
/// <para>
/// 为什么需要它：以管理员常驻时（<c>--elevate</c> 之后），管道内核对象会带上 High
/// 完整性标签。Windows 的完整性策略有一条 <c>NoWriteUp</c> —— 低完整性进程不能对
/// 高完整性对象请求<em>写</em>访问，而连一条双向管道既要读也要写。于是普通权限的
/// 命令行去连它，得到的就是一句谁也看不懂的 <c>Access to the path is denied.</c>。
/// </para>
/// <para>
/// 后果比那句话严重得多：<c>--quit</c> / <c>--reload</c> / <c>--screen</c> / <c>--region</c>
/// 全都走这条管道，于是它们<em>全部</em>失效。而热键是管理员进程自己挂的钩子、不经过管道，
/// 截图照常工作 —— 用户会晚很久才发现「命令行怎么都没反应了」。
/// </para>
/// <para>
/// 修法不是去动 UIPI（那道墙本来就该在），而是给管道对象贴一个 Medium 标签：
/// 把标签往下降不需要特权，往上抬才需要。同用户的 Medium 进程于是可以正常连接，
/// 而管道内容是「截一张图 / 重新读配置」这种用户自己发起的事，不存在越权。
/// </para>
/// </remarks>
internal static class MediumLabelPipe
{
    private const uint PipeAccessDuplex = 0x0000_0003;
    private const uint FileFlagOverlapped = 0x4000_0000;
    private const uint PipeTypeMessage = 0x0000_0004;
    private const uint PipeReadModeMessage = 0x0000_0002;
    private const uint PipeWait = 0x0000_0000;
    private const uint SddlRevision = 1;
    private const int BufferBytes = 64 * 1024;

    /// <summary>
    /// 建一条带 Medium 标签的管道。返回 <c>null</c> 表示这一步没成，
    /// 调用方应当退回 <see cref="NamedPipeServerStream"/> 的普通构造 —— 宁可退回
    /// 「只有管理员命令行能用」，也不能让管道整个起不来。
    /// </summary>
    public static NamedPipeServerStream? TryCreate(string pipeName, int maxInstances)
    {
        if (!TryCreateDescriptor(out IntPtr descriptor))
        {
            return null;
        }

        try
        {
            SecurityAttributes attributes = new()
            {
                Length = Marshal.SizeOf<SecurityAttributes>(),
                Descriptor = descriptor,
                InheritHandle = false,
            };

            IntPtr raw = CreateNamedPipeW(
                @"\\.\pipe\" + pipeName,
                PipeAccessDuplex | FileFlagOverlapped,
                PipeTypeMessage | PipeReadModeMessage | PipeWait,
                (uint)Math.Max(1, maxInstances),
                BufferBytes,
                BufferBytes,
                0,
                ref attributes
            );

            if (raw == IntPtr.Zero || raw == new IntPtr(-1))
            {
                Log.Debug($"带 Medium 标签的管道没建起来（GetLastError={Marshal.GetLastWin32Error()}），退回普通管道。");
                return null;
            }

            SafePipeHandle handle = new(raw, ownsHandle: true);
            try
            {
                return new NamedPipeServerStream(
                    PipeDirection.InOut,
                    isAsync: true,
                    isConnected: false,
                    safePipeHandle: handle
                );
            }
            catch (Exception ex)
            {
                // 包进托管对象这一步失败的话，句柄得自己收掉，否则每次重连漏一个。
                handle.Dispose();
                Log.Debug($"管道句柄包装失败：{ex.Message}");
                return null;
            }
        }
        catch (Exception ex)
        {
            Log.Debug($"创建管道失败：{ex.Message}");
            return null;
        }
        finally
        {
            LocalFree(descriptor);
        }
    }

    /// <summary>
    /// 拼出安全描述符：<c>D:</c> 允许当前用户完全控制，<c>S:</c> 把强制完整性标签压到 Medium。
    /// </summary>
    private static bool TryCreateDescriptor(out IntPtr descriptor)
    {
        descriptor = IntPtr.Zero;

        try
        {
            string? sid = WindowsIdentity.GetCurrent().User?.Value;
            if (string.IsNullOrEmpty(sid))
            {
                return false;
            }

            // ME = Medium 强制级别；NW = NoWriteUp。标签定为 Medium 之后，
            // 同为 Medium 的普通命令行既读得也写得，NoWriteUp 不再挡它。
            string sddl = $"D:(A;;GA;;;{sid})S:(ML;;NW;;;ME)";

            if (!ConvertStringSecurityDescriptorToSecurityDescriptorW(sddl, SddlRevision, out IntPtr built, out _))
            {
                Log.Debug($"安全描述符解析失败（GetLastError={Marshal.GetLastWin32Error()}），退回普通管道。");
                return false;
            }

            descriptor = built;
            return true;
        }
        catch (Exception ex)
        {
            Log.Debug($"构造安全描述符失败：{ex.Message}");
            return false;
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct SecurityAttributes
    {
        public int Length;
        public IntPtr Descriptor;
        [MarshalAs(UnmanagedType.Bool)]
        public bool InheritHandle;
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr CreateNamedPipeW(
        string name,
        uint openMode,
        uint pipeMode,
        uint maxInstances,
        uint outBufferSize,
        uint inBufferSize,
        uint defaultTimeout,
        ref SecurityAttributes securityAttributes
    );

    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool ConvertStringSecurityDescriptorToSecurityDescriptorW(
        string sddl,
        uint revision,
        out IntPtr descriptor,
        out uint descriptorLength
    );

    [DllImport("kernel32.dll")]
    private static extern IntPtr LocalFree(IntPtr memory);
}
