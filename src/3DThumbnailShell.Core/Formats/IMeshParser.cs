using System.IO;
using _3DThumbnailShell.Core.Renderer;

namespace _3DThumbnailShell.Core.Formats
{
    /// <summary>格式解析器约定：按扩展名分发，解析流为统一 MeshData。</summary>
    public interface IMeshParser
    {
        bool CanParse(string extension);
        /// <summary>解析失败返回 false（不抛异常）。成功时 mesh 非空。</summary>
        bool TryParse(Stream stream, out MeshData mesh);
    }
}