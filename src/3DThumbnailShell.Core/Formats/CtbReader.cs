using System;
using System.IO;
using _3DThumbnailShell.Core.Renderer;

namespace _3DThumbnailShell.Core.Formats
{
    /// <summary>
    /// CTB (ChituBox) 光固化切片文件解析器。
    /// CTB 文件内含 2 张预览图（原始 RGB，无压缩），直接取较大的那张作为资源管理器缩略图，
    /// 无需 3D 解析/渲染。格式基于 UVtools 公开规范：
    ///   magic  = 0x12FD0019 (LE, offset 0)
    ///   ver    = uint32 (offset 4)
    ///   ...    = 打印参数
    ///   预览图1: width@48 height@52 offset@56
    ///   预览图2: width@60 height@64 offset@68  （取较大者）
    /// 预览图数据为原始 RGB（3 字节/像素，无行对齐）。
    /// </summary>
    public sealed class CtbReader : IMeshParser
    {
        private const uint CtbMagic = 0x12FD0019;

        public bool CanParse(string ext)
        {
            if (string.IsNullOrEmpty(ext)) return false;
            return ext.ToLowerInvariant() == ".ctb";
        }

        public bool TryParse(Stream stream, out MeshData mesh)
        {
            mesh = new MeshData { OriginalFormat = "ctb" };
            try
            {
                using (var br = new BinaryReader(stream, System.Text.Encoding.UTF8, leaveOpen: true))
                {
                    if (stream.Length < 72) return false;
                    var magic = br.ReadUInt32();
                    if (magic != CtbMagic) return false;

                    // 跳过打印参数，直接读两张预览图信息
                    stream.Position = 48;
                    int w1 = br.ReadInt32();
                    int h1 = br.ReadInt32();
                    long off1 = br.ReadUInt32();
                    int w2 = br.ReadInt32();
                    int h2 = br.ReadInt32();
                    long off2 = br.ReadUInt32();

                    // 选较大的预览图
                    int w, h; long off;
                    if ((long)w2 * h2 >= (long)w1 * h1) { w = w2; h = h2; off = off2; }
                    else { w = w1; h = h1; off = off1; }

                    if (w <= 0 || h <= 0 || off <= 0 || off + (long)w * h * 3 > stream.Length)
                        return false;

                    stream.Position = off;
                    var rgb = br.ReadBytes(w * h * 3);
                    if (rgb.Length < w * h * 3) return false;

                    // RGB -> BGRA
                    var bgra = new byte[w * h * 4];
                    for (int i = 0, j = 0; i < rgb.Length; i += 3, j += 4)
                    {
                        bgra[j] = rgb[i + 2];     // B
                        bgra[j + 1] = rgb[i + 1]; // G
                        bgra[j + 2] = rgb[i];     // R
                        bgra[j + 3] = 255;        // A
                    }

                    mesh.PreviewBgra = bgra;
                    mesh.PreviewWidth = w;
                    mesh.PreviewHeight = h;
                    // 给一个空的顶点列表避免 null 检查
                    mesh.Vertices = new System.Collections.Generic.List<System.Numerics.Vector3>();
                    return true;
                }
            }
            catch
            {
                mesh.PreviewBgra = null;
                return false;
            }
        }
    }
}
