using System;
using System.Runtime.InteropServices;

namespace _3DThumbnailShell.Shell.Interop
{
    /// <summary>缩略图 alpha 类型（WTS_ALPHATYPE）。</summary>
    public enum WTS_ALPHATYPE : uint
    {
        WTSAT_UNKNOWN = 0,
        WTSAT_RGB = 1,
        WTSAT_ARGB = 2
    }

    /// <summary>IThumbnailProvider：资源管理器调用以获取文件缩略图。
    /// 注意：由托管类实现（CCW 提供），不能加 ComImport——ComImport 仅用于 RCW 消费端，
    /// 实现端加它会导致 CCW 不通过 QueryInterface 暴露该接口，explorer 原生 QI 失败 → 无缩略图。</summary>
    [ComVisible(true)]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    [Guid("E357FCCD-A995-4576-B01F-234630154E96")]
    public interface IThumbnailProvider
    {
        [PreserveSig]
        int GetThumbnail(uint cx, out IntPtr phbmp, out WTS_ALPHATYPE palpha);
    }

    /// <summary>IInitializeWithFile：资源管理器在 GetThumbnail 前传入文件路径。</summary>
    [ComVisible(true)]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    [Guid("B7D14566-0509-4CCE-A71F-0A554233BD9B")]
    public interface IInitializeWithFile
    {
        [PreserveSig]
        int Initialize([MarshalAs(UnmanagedType.LPWStr)] string pszFilePath, uint grfMode);
    }

    /// <summary>IInitializeWithStream：shell32 对缩略图 handler 优先尝试流式初始化；
    /// 若仅实现 IInitializeWithFile，Windows 11 的 shell32 可能创建实例后直接放弃（现象：只加载、不调用 Initialize）。</summary>
    [ComVisible(true)]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    [Guid("B824B49D-22AC-4161-AC8A-9916E8FA3F7F")]
    public interface IInitializeWithStream
    {
        [PreserveSig]
        int Initialize(System.Runtime.InteropServices.ComTypes.IStream pstream, uint grfMode);
    }
}
