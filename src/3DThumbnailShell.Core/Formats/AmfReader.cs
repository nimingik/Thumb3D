using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Numerics;
using System.Xml.Linq;
using _3DThumbnailShell.Core.Renderer;

namespace _3DThumbnailShell.Core.Formats
{
    /// <summary>Additive Manufacturing File Format (AMF)：XML&lt;coordinate&gt; 与 &lt;triangle&gt;&lt;v1/v2/v3&gt;。可多 mesh 合并。</summary>
    public sealed class AmfReader : IMeshParser
    {
        public bool CanParse(string ext) => ext == ".amf";

        private static readonly CultureInfo Ci = CultureInfo.InvariantCulture;

        public bool TryParse(Stream stream, out MeshData mesh)
        {
            mesh = new MeshData { OriginalFormat = "amf" };
            stream.Position = 0;
            XDocument doc;
            try { doc = XDocument.Load(stream, LoadOptions.None); }
            catch { return false; }

            var root = doc.Root;
            if (root == null || root.Name.LocalName != "amf") return false;

            var pos = new List<Vector3>();
            var tris = new List<int>();

            var coordinateElems = Desc(root, "coordinates").ToList();
            if (coordinateElems.Count == 0) coordinateElems = Desc(root, "coordinate").ToList();
            if (coordinateElems.Count == 0) return false;
            foreach (var v in coordinateElems)
            {
                pos.Add(new Vector3(
                    ChildVal(v, "x"), ChildVal(v, "y"), ChildVal(v, "z")));
            }
            if (pos.Count == 0) return false;

            var triangleElems = Desc(root, "triangle").ToList();
            foreach (var tr in triangleElems)
            {
                int a = ChildIdx(tr, "v1"), b = ChildIdx(tr, "v2"), c = ChildIdx(tr, "v3");
                if (a < 0 || b < 0 || c < 0 || a >= pos.Count || b >= pos.Count || c >= pos.Count) continue;
                tris.Add(a); tris.Add(b); tris.Add(c);
            }

            mesh.Vertices = pos;
            mesh.Indices = tris;
            mesh.Normals = new List<Vector3>();
            if (tris.Count == 0) mesh.IsPointCloud = true;
            return true;
        }

        // 子元素按 LocalName 取值（namespace 无关）
        private static XElement Child(XElement parent, string localName)
        {
            foreach (var e in parent.Elements())
                if (e.Name.LocalName == localName) return e;
            return null;
        }
        private static float ChildVal(XElement parent, string localName)
        {
            var e = Child(parent, localName);
            if (e == null) return 0f;
            return float.Parse(e.Value.Trim(), NumberStyles.Float, Ci);
        }
        private static int ChildIdx(XElement parent, string localName)
        {
            var e = Child(parent, localName);
            if (e == null) return -1;
            return int.Parse(e.Value.Trim(), NumberStyles.Integer, Ci);
        }
        private static IEnumerable<XElement> Desc(XElement root, string localName)
        {
            foreach (var d in root.Descendants())
                if (d.Name.LocalName == localName) yield return d;
        }
    }
}