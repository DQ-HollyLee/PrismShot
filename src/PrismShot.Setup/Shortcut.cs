using System.Reflection;
using System.Runtime.InteropServices;

namespace PrismShot.Setup;

/// <summary>
/// 建开始菜单快捷方式。
/// </summary>
/// <remarks>
/// <para>
/// 走 <c>WScript.Shell</c> 的后期绑定，而不是手写 <c>IShellLinkW</c> 的 COM 接口声明：
/// 后者要求把 vtable 上 18 个方法按序一字不差地排出来，排错一个就是内存损坏，
/// 而且在本机上根本试不出来 —— 它只在别的机器的 COM 实现上才可能歪。
/// 后期绑定由 IDispatch 按名字查，代价是一次反射，换来的是不可能排错。
/// </para>
/// <para>
/// 代价也认了：Windows Script Host 理论上可以被禁用。所以调用方一律把它当
/// 「锦上添花」，失败只提示不中断 —— 这个程序靠热键活着，开始菜单不是命脉。
/// </para>
/// </remarks>
internal static class Shortcut
{
    private const string ProgId = "WScript.Shell";

    public static void Create(
        string shortcutPath,
        string targetPath,
        string? arguments = null,
        string? workingDirectory = null,
        string? description = null
    )
    {
        Type? type = Type.GetTypeFromProgID(ProgId);
        if (type is null)
        {
            throw new NotSupportedException($"这台机器上没有 {ProgId}，建不了快捷方式。");
        }

        object? shell = Activator.CreateInstance(type);
        if (shell is null)
        {
            throw new NotSupportedException($"无法创建 {ProgId} 实例。");
        }

        try
        {
            object? link = Call(shell, "CreateShortcut", shortcutPath);
            if (link is null)
            {
                throw new NotSupportedException("CreateShortcut 没有返回对象。");
            }

            try
            {
                Put(link, "TargetPath", targetPath);

                if (arguments is not null)
                {
                    Put(link, "Arguments", arguments);
                }

                if (workingDirectory is not null)
                {
                    Put(link, "WorkingDirectory", workingDirectory);
                }

                if (description is not null)
                {
                    Put(link, "Description", description);
                }

                Call(link, "Save");
            }
            finally
            {
                Marshal.FinalReleaseComObject(link);
            }
        }
        finally
        {
            Marshal.FinalReleaseComObject(shell);
        }
    }

    private static object? Call(object instance, string name, params object[] arguments) =>
        instance
            .GetType()
            .InvokeMember(
                name,
                BindingFlags.InvokeMethod,
                null,
                instance,
                arguments,
                null
            );

    private static void Put(object instance, string name, object value) =>
        instance
            .GetType()
            .InvokeMember(
                name,
                BindingFlags.SetProperty,
                null,
                instance,
                new[] { value },
                null
            );
}
