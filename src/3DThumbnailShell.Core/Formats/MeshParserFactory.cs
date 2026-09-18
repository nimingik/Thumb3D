using System.Collections.Generic;
using System.IO;
using System.Linq;
using _3DThumbnailShell.Core.Renderer;

namespace _3DThumbnailShell.Core.Formats
{
    /// <summary>按扩展名分发到具体解析器。</summary>
    public static class MeshParserFactory
    {
        private static readonly List<IMeshParser> Parsers = new List<IMeshParser>
        {
            new StlReader(),
            new ObjReader(),
            new PlyReader(),
            new OffReader(),
            new Mf3Reader(),
            new AmfReader(),
            new GltfReader(),
            new GCodeReader(),
            new CtbReader(),
            new StepReader(),
        };

        public static bool Supports(string path)
        {
            var ext = Path.GetExtension(path ?? "").ToLowerInvariant().TrimStart('.');
            return Parsers.Any(p => p.CanParse("." + ext));
        }

        public static IReadOnlyList<IMeshParser> All => Parsers;

        public static MeshData ParseFile(string path)
        {
            if (string.IsNullOrEmpty(path)) return null;
            var ext = Path.GetExtension(path).ToLowerInvariant();

            // GLTF/GLB 需要文件路径以解析外部 .bin
            if (ext == ".gltf" || ext == ".glb")
            {
                try { return new GltfReader().Parse(path); }
                catch { return null; }
            }

            foreach (var p in Parsers)
            {
                if (!p.CanParse(ext)) continue;
                try
                {
                    using (var fs = File.OpenRead(path))
                        if (p.TryParse(fs, out var m)) return m;
                }
                catch
                {
                    return null;
                }
            }
            return null;
        }
    }
}