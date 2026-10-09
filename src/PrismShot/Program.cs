using System;
using System.Runtime.InteropServices;

namespace PrismShot;

/// <summary>
/// 占位入口：先验证 TFM + WinRT 投影 + Vortice 包能不能 restore 与编译，
/// 通了之后真正的入口会被拆到 App.xaml.cs 里。
/// </summary>
internal static class Program
{
    [STAThread]
    private static void Main(string[] args)
    {
        Console.WriteLine("PrismShot bootstrap ok, args=" + args.Length);
    }
}
