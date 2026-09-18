using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Numerics;
using System.Xml;
using _3DThumbnailShell.Core.Renderer;

namespace _3DThumbnailShell.Core.Formats
{
    /// <summary>
    /// 3D Manufacturing Format (3MF)：ZIP 容器内 XML(.model)。
    /// 流式 XmlReader 解析（不建 DOM，单遍扫描），支持标准结构（object 内直接嵌 mesh）
    /// 与生产扩展（Production Extension）：入口 3dmodel.model 通过
    /// &lt;component p:path="..." p:objectid="N" transform="矩阵"&gt; 引用外部子模型。
    /// </summary>
    public sealed class Mf3Reader : IMeshParser
    {
        public bool CanParse(string ext) => ext == ".3mf";

        private static readonly CultureInfo Ci = CultureInfo.InvariantCulture;

        /// <summary>子模型缓存中"主模型入口"的键：component 省略 path 时指向当前模型文件本身。</summary>
        private const string MainModelKey = "__main__";
        private static readonly XmlReaderSettings ReaderSettings = new XmlReaderSettings
        {
            IgnoreComments = true,
            IgnoreWhitespace = true,
            DtdProcessing = DtdProcessing.Ignore
        };

        public bool TryParse(Stream stream, out MeshData mesh)
        {
            mesh = new MeshData { OriginalFormat = "3mf" };
            stream.Position = 0;
            using (var zip = new ZipArchive(stream, ZipArchiveMode.Read, leaveOpen: true))
            {
                // 入口模型：3D/3dmodel.model（优先），否则任意 .model
                var entry = zip.Entries.FirstOrDefault(e =>
                    e.FullName.Equals("3D/3dmodel.model", StringComparison.OrdinalIgnoreCase) ||
                    e.FullName.EndsWith(".model", StringComparison.OrdinalIgnoreCase));
                if (entry == null) return false;

                // 拓竹/Orca 多色：从 project_settings.config 读项目料色（filament_colour）
                var palette = TryReadFilamentPalette(zip);
                // 标准 3MF 多色（PrusaSlicer 等）：resources 里的 <colorgroup>（group id → 颜色列表）
                var colorGroups = ReadColorGroups(entry);
                // Bambu 多材料：model_settings.config 按 object 顶层 <metadata key="extruder"> 的料槽号 → 颜色
                var filaments = ReadObjectFilaments(zip);
                var objectColors = BuildObjectColors(filaments, palette);
                // 细化：同文件 <part> 层按序（与 3dmodel 的 component 一一对应）分配 extruder 1-6 的
                // 多材料工程 → 每个 component 独立料槽色（拓竹切片预览观感）；无 part 时回退 object 色。
                var partColors = BuildPartColors(ReadObjectPartFilaments(zip), palette);
                // 部件类型（普通/负零件/修改器）：model_settings.config 的 <part subtype="...">，按文档序与
                // component 一一对应。修改器（modifier_part）只改打印参数、不是实体，需与普通模型分开渲染。
                var partKinds = ReadObjectPartKinds(zip);
                // 多盘：model_settings.config 的 <plate> 块（Bambu/Orca 工程分盘）
                var plates = ReadPlates(zip);
                // 构建实例：3dmodel.model 的 <build><item>（objectid + transform）
                var items = new List<BuildItem>();
                ReadBuildItems(entry, items);

                // 子模型缓存：path(小写) -> objectId -> SubMesh，只解析一次（跨 item/盘共享）
                var subCache = new Dictionary<string, Dictionary<int, SubMesh>>(
                    StringComparer.OrdinalIgnoreCase);

                if (items.Count == 0)
                {
                    // 无 <build>（纯标准 3MF / 单模型）：直接解析全部 resources object，parentT=null
                    using var es = entry.Open();
                    using var r = XmlReader.Create(es, ReaderSettings);
                    while (r.Read())
                    {
                        if (r.NodeType != XmlNodeType.Element) continue;
                        if (r.LocalName == "object" || r.LocalName == "model")
                        {
                            using var sub = r.ReadSubtree();
                            ParseObjectTree(sub, zip, mesh, false, null, mesh.FaceColors, palette, subCache,
                                objectColors, partColors, colorGroups, null, partKinds);
                        }
                    }
                }
                else
                {
                    // 按 item 实例化：主 MeshData = 全部盘合并（各零件保留床坐标 → 与拓竹一致，
                    // 各盘零件在各自床位置并排显示）；并按 plate 分盘
                    var usedParts = new HashSet<string>();
                    for (var n = 0; n < items.Count; n++)
                        InstantiateItem(entry, zip, items[n].Oid, items[n].Transform, mesh, palette, subCache,
                            objectColors, partColors, colorGroups, usedParts, partKinds);
                    if (plates.Count > 0)
                    {
                        foreach (var pl in plates)
                        {
                            var pm = new MeshData { OriginalFormat = "3mf" };
                            foreach (var it in items)
                            {
                                // item 的 object 属于该盘（plater 的 model_instance.object_id）；不属于任何盘则归第一盘
                                var belong = pl.ObjIds.Contains(it.Oid)
                                    || (pl.ObjIds.Count == 0 && pl == plates[0]);
                                if (!belong) continue;
                                InstantiateItem(entry, zip, it.Oid, it.Transform, pm, palette, subCache,
                                    objectColors, partColors, colorGroups, null, partKinds);
                            }
                            if (pm.Vertices.Count > 0)
                                mesh.Plates.Add(new PlateInfo { Id = pl.Id, Name = pl.Name, Mesh = pm });
                        }
                    }
                    else if (items.Select(i => i.Oid).Distinct().Count() > 1)
                    {
                        // 无 Bambu/Orca 的 plate 元数据（普通 3MF 工程），但含多个独立 object：
                        // 按 object id 分组拆分为"可选盘"，让用户可单独查看每个部件，也可合并。
                        foreach (var grp in items.GroupBy(i => i.Oid))
                        {
                            var pm = new MeshData { OriginalFormat = "3mf" };
                            foreach (var it in grp)
                                InstantiateItem(entry, zip, it.Oid, it.Transform, pm, palette, subCache,
                                    objectColors, partColors, colorGroups, null, partKinds);
                            if (pm.Vertices.Count > 0)
                                mesh.Plates.Add(new PlateInfo { Id = grp.Key, Name = "部件 " + grp.Key, Mesh = pm });
                        }
                    }
                }

                if (mesh.Vertices.Count == 0) return false;
                mesh.Normals = new List<Vector3>();
                if (mesh.Indices.Count == 0) mesh.IsPointCloud = true;
                else while (mesh.FaceModifier.Count < mesh.Indices.Count / 3) mesh.FaceModifier.Add(0); // 与面数对齐
                return true;
            }
        }

        /// <summary>按 build item 实例化：定位 resources 中 id=oid 的 object 子树，以 itemT 为父级矩阵解析其几何。
        /// usedParts 非 null 时（合并盘视图）对完全重合的重复零件去重。</summary>
        private static void InstantiateItem(ZipArchiveEntry entry, ZipArchive zip, int oid,
            Matrix4x4? itemT, MeshData m, List<Vector4> palette,
            Dictionary<string, Dictionary<int, SubMesh>> subCache,
            Dictionary<int, Vector4> objectColors, Dictionary<int, List<Vector4>> partColors,
            Dictionary<int, List<Vector4>> colorGroups, HashSet<string> usedParts,
            Dictionary<int, List<byte>> partKinds)
        {
            using var es = entry.Open();
            using var r = XmlReader.Create(es, ReaderSettings);
            while (r.Read())
            {
                if (r.NodeType != XmlNodeType.Element || r.LocalName != "object") continue;
                var idStr = GetAttr(r, "id");
                if (!int.TryParse(idStr, NumberStyles.Integer, Ci, out var id) || id != oid) continue;
                using var sub = r.ReadSubtree();
                ParseObjectTree(sub, zip, m, false, itemT, m.FaceColors, palette, subCache,
                    objectColors, partColors, colorGroups, usedParts, partKinds);
                return;
            }
        }

        /// <summary>读取 Metadata/model_settings.config 的 &lt;plate&gt; 块（Bambu/Orca 分盘）。</summary>
        private static List<PlateMeta> ReadPlates(ZipArchive zip)
        {
            var result = new List<PlateMeta>();
            var entry = zip.Entries.FirstOrDefault(e =>
                e.FullName.Equals("Metadata/model_settings.config", StringComparison.OrdinalIgnoreCase));
            if (entry == null) return result;
            try
            {
                using var es = entry.Open();
                using var sr = XmlReader.Create(es, ReaderSettings);
                while (sr.Read())
                {
                    if (sr.NodeType != XmlNodeType.Element || sr.LocalName != "plate") continue;
                    var meta = new PlateMeta();
                    using var sub = sr.ReadSubtree();
                    while (sub.Read())
                    {
                        if (sub.NodeType != XmlNodeType.Element) continue;
                        if (sub.LocalName == "metadata")
                        {
                            var key = GetAttr(sub, "key");
                            if (key == "plater_id" && int.TryParse(GetAttr(sub, "value"),
                                    NumberStyles.Integer, Ci, out var pid))
                                meta.Id = pid;
                            else if (key == "plater_name") meta.Name = GetAttr(sub, "value");
                        }
                        else if (sub.LocalName == "model_instance")
                        {
                            using var mi = sub.ReadSubtree();
                            while (mi.Read())
                            {
                                if (mi.NodeType != XmlNodeType.Element || mi.LocalName != "metadata") continue;
                                if (GetAttr(mi, "key") == "object_id" &&
                                    int.TryParse(GetAttr(mi, "value"), NumberStyles.Integer, Ci, out var oid))
                                    meta.ObjIds.Add(oid);
                            }
                        }
                    }
                    result.Add(meta);
                }
            }
            catch
            {
            }
            return result;
        }

        /// <summary>读取 3dmodel.model 的 &lt;build&gt;&lt;item&gt;（objectid + transform；item 即一个构建实例）。</summary>
        private static void ReadBuildItems(ZipArchiveEntry entry, List<BuildItem> items)
        {
            using var es = entry.Open();
            using var r = XmlReader.Create(es, ReaderSettings);
            while (r.Read())
            {
                if (r.NodeType != XmlNodeType.Element || r.LocalName != "item") continue;
                var oidStr = GetAttr(r, "objectid");
                if (!int.TryParse(oidStr, NumberStyles.Integer, Ci, out var oid)) continue;
                items.Add(new BuildItem { Oid = oid, Transform = ParseTransform(GetAttr(r, "transform")) });
            }
        }

        /// <summary>build item：objectid 引用的资源 object + 摆放矩阵。</summary>
        private sealed class BuildItem
        {
            public int Oid;
            public Matrix4x4? Transform;
        }

        /// <summary>plate 元数据：盘号/盘名/该盘引用的 object id 集合。</summary>
        private sealed class PlateMeta
        {
            public int Id;
            public string Name;
            public readonly HashSet<int> ObjIds = new HashSet<int>();
        }

        /// <summary>
        /// 遍历 object/model 子树：捕获内嵌 mesh（标准 3MF，含嵌套 object 里的 mesh）
        /// 与 component 引用（生产扩展）。
        /// 负零件状态用栈跟踪（不能用 ReadSubtree 递归——子树 reader 首个节点是元素自身，
        /// 对 object 再递归会无限循环）：进入 object/model 时入栈，离开时出栈，
        /// mesh 取栈顶状态作为其三角形负零件标记。
        /// 多色：objectColors（objectId → 料槽色）命中当前 object 时，其子树内（含 component
        /// 合并的子模型面）未上色三角形面填充为该颜色；partColors（objectId → 按序 part 料槽色，
        /// 与 component 一一对应）命中时按 component 序号取色（更细粒度，拓竹多材料工程）；
        /// colorGroups 供逐面色 colorgroup 解码。
        /// </summary>
        private static void ParseObjectTree(XmlReader r, ZipArchive zip, MeshData m,
            bool parentNegative, Matrix4x4? parentT, List<Vector4> faceColors, List<Vector4> palette,
            Dictionary<string, Dictionary<int, SubMesh>> subCache,
            Dictionary<int, Vector4> objectColors, Dictionary<int, List<Vector4>> partColors,
            Dictionary<int, List<Vector4>> colorGroups, HashSet<string> usedParts,
            Dictionary<int, List<byte>> partKinds)
        {
            var negStack = new Stack<bool>();
            negStack.Push(parentNegative);
            // 根 object（进入时 reader 位于根元素）：取其料槽色/部件色；嵌套 object 再用栈跟踪
            var colorStack = new Stack<Vector4?>();
            colorStack.Push(ObjectColorOf(r, objectColors));
            var partStack = new Stack<List<Vector4>>();
            partStack.Push(PartColorsOf(r, partColors));
            // 部件类型栈（与 partStack 同步）：普通/负零件/修改器，按 component 序号索引
            var kindStack = new Stack<List<byte>>();
            kindStack.Push(PartKindsOf(r, partKinds));
            var compIdxStack = new Stack<int>();
            compIdxStack.Push(0);

            while (r.Read())
            {
                if (r.NodeType == XmlNodeType.EndElement)
                {
                    if ((r.LocalName == "object" || r.LocalName == "model") && negStack.Count > 1)
                    {
                        negStack.Pop();
                        if (colorStack.Count > 1)
                        {
                            colorStack.Pop();
                            partStack.Pop();
                            kindStack.Pop();
                            compIdxStack.Pop();
                        }
                    }
                    continue;
                }
                if (r.NodeType != XmlNodeType.Element) continue;

                if (r.LocalName == "object" || r.LocalName == "model")
                {
                    // 本级属性决定负零件状态，与父级取或（进入时入栈）
                    var neg = IsNegativeObject(r) || (negStack.Count > 0 ? negStack.Peek() : false);
                    negStack.Push(neg);
                    // 本 object 的料槽色：有则用，无则继承父级（入栈）；部件色/序号栈同步入栈
                    var col = ObjectColorOf(r, objectColors)
                              ?? (colorStack.Count > 0 ? colorStack.Peek() : null);
                    colorStack.Push(col);
                    partStack.Push(PartColorsOf(r, partColors));
                    kindStack.Push(PartKindsOf(r, partKinds));
                    compIdxStack.Push(0);
                }
                else if (r.LocalName == "mesh")
                {
                    var curNeg = negStack.Count > 0 ? negStack.Peek() : false;
                    var curColor = colorStack.Count > 0 ? colorStack.Peek() : (Vector4?)null;
                    var faceStart = faceColors?.Count ?? 0;
                    using var sub = r.ReadSubtree();
                    ReadObjectMesh(sub, m.Vertices, m.Indices, m.FaceNegative, faceColors, parentT, curNeg,
                        palette, colorGroups);
                    while (m.FaceModifier.Count < m.FaceNegative.Count) m.FaceModifier.Add(0); // 内嵌 mesh 无修改器标记
                    // 本 object 料槽色：把本 mesh 新增的未上色面（paint_color/colorgroup 未命中）补齐
                    if (curColor.HasValue && faceColors != null)
                        for (var f = faceStart; f < faceColors.Count; f++)
                            if (faceColors[f].W <= 0f) faceColors[f] = curColor.Value;
                }
                else if (r.LocalName == "component")
                {
                    var curColor = colorStack.Count > 0 ? colorStack.Peek() : (Vector4?)null;
                    // 本 object 有按序部件色（与 component 一一对应）时，按当前序号取该 component 的料槽色
                    var parts = partStack.Count > 0 ? partStack.Peek() : null;
                    var ci = compIdxStack.Count > 0 ? compIdxStack.Peek() : 0;
                    if (parts != null && ci >= 0 && ci < parts.Count) curColor = parts[ci];
                    // 该 component 的部件类型（model_settings 的 part subtype）：负零件/修改器
                    var kinds = kindStack.Count > 0 ? kindStack.Peek() : null;
                    var kind = (kinds != null && ci >= 0 && ci < kinds.Count) ? kinds[ci] : PartKindNormal;
                    ReadComponent(r, zip, m, faceColors, parentT, subCache, palette, colorGroups, curColor,
                        usedParts, kind);
                    if (compIdxStack.Count > 0) compIdxStack.Push(compIdxStack.Pop() + 1);
                }
            }
        }

        /// <summary>
        /// 负零件通用判定（3MF 标准/生产扩展）：
        /// - object type 属性 ≠ "model"（如 support/other/solid-support）
        /// - object printable 属性为 0（标准 3MF 不可打印体）
        /// - object 名称含 neg|cut|support|负|辅助|subtract 等镂空/支撑关键字
        /// </summary>
        private static bool IsNegativeObject(XmlReader r)
        {
            var type = GetAttr(r, "type");
            if (!string.IsNullOrEmpty(type) &&
                !type.Equals("model", StringComparison.OrdinalIgnoreCase))
                return true;

            var printable = GetAttr(r, "printable");
            if (!string.IsNullOrEmpty(printable) &&
                printable != "1" && !printable.Equals("true", StringComparison.OrdinalIgnoreCase))
                return true;

            var name = GetAttr(r, "name") ?? GetAttr(r, "Name");
            if (!string.IsNullOrEmpty(name))
            {
                var n = name.ToLowerInvariant();
                if (n.Contains("neg") || n.Contains("cut") || n.Contains("support") ||
                    n.Contains("subtract") || n.Contains("负") || n.Contains("辅助") ||
                    n.Contains("布尔") || n.Contains("镂空") || n.Contains("内腔"))
                    return true;
            }
            return false;
        }

        /// <summary>component 引用：读 p:path / p:objectid / transform，加载外部子模型并解析对应 object。
        /// parentT 为该 object 的 build item 矩阵（打印床摆放坐标），先应用 item 再应用 component
        /// （行主序：v * item * comp）。
        /// usedParts 非 null 时（合并盘视图）：同一零件在同一世界位置被重复引用时只渲染一次，
        /// 避免完全重合的面片互相闪烁（z-fighting）。
        /// objectColor 非 null 时（父 object 的料槽色）把合并进来的未上色子模型面补齐。</summary>
        private static void ReadComponent(XmlReader r, ZipArchive zip, MeshData m,
            List<Vector4> faceColors, Matrix4x4? parentT,
            Dictionary<string, Dictionary<int, SubMesh>> subCache, List<Vector4> palette,
            Dictionary<int, List<Vector4>> colorGroups, Vector4? objectColor,
            HashSet<string> usedParts, byte partKind)
        {
            var path = GetAttr(r, "path");
            var idStr = GetAttr(r, "objectid");
            if (!int.TryParse(idStr, NumberStyles.Integer, Ci, out var oid)) return;
            var t = ParseTransform(GetAttr(r, "transform"));
            // 变换顺序（3MF 规范）：component 的 transform 先作用于自身坐标（进入父对象空间），
            // 再由父级（build item）变换摆放到世界空间 → 行向量约定下必须 t = compT * parentT。
            // 之前写成 parentT * compT，父级含旋转/缩放时子零件偏移被错误放大/旋转 → 部件错位。
            if (parentT.HasValue)
                t = (t ?? Matrix4x4.Identity) * parentT.Value;

            // path 缺省 = 引用同一模型文件内的 object（BambuStudio 组装体 <component objectid="N"/>
            // 通常省略 path，3MF 规范亦允许）→ 用主模型入口解析；显式 path 才是跨子模型文件引用。
            ZipArchiveEntry subEntry;
            string rel;
            if (string.IsNullOrEmpty(path))
            {
                rel = MainModelKey;
                subEntry = zip.Entries.FirstOrDefault(e =>
                    e.FullName.Equals("3D/3dmodel.model", StringComparison.OrdinalIgnoreCase) ||
                    e.FullName.EndsWith(".model", StringComparison.OrdinalIgnoreCase));
            }
            else
            {
                rel = path.TrimStart('/').Replace('/', Path.DirectorySeparatorChar);
                subEntry = zip.GetEntry(rel)
                           ?? zip.Entries.FirstOrDefault(e =>
                               e.FullName.Replace('/', Path.DirectorySeparatorChar)
                                   .Equals(rel, StringComparison.OrdinalIgnoreCase));
            }
            if (subEntry == null) return;
            if (usedParts != null)
            {
                var w = t ?? Matrix4x4.Identity;
                var key = rel + "|" + oid + "|" + w.M41.ToString("R") + "|"
                          + w.M42.ToString("R") + "|" + w.M43.ToString("R");
                if (!usedParts.Add(key)) return;
            }
            if (!subCache.TryGetValue(rel, out var objects))
            {
                objects = ParseSubModel(subEntry, palette, colorGroups, rel == MainModelKey);
                subCache[rel] = objects;
            }

            if (!objects.TryGetValue(oid, out var sm)) return;
            // 应用 transform 并合并进目标 MeshData，索引加起点；负零件/逐面色按子模型逐三角形映射
            var baseIdx = m.Vertices.Count;
            var faceBase = m.FaceNegative.Count; // 本次合并前的面数（FaceNegative / FaceModifier 等长）
            foreach (var v in sm.Vertices)
                m.Vertices.Add(t.HasValue ? Vector3.Transform(v, t.Value) : v);
            foreach (var i in sm.Indices)
                m.Indices.Add(baseIdx + i);
            foreach (var n in sm.FaceNeg)
                m.FaceNegative.Add(n);
            while (m.FaceNegative.Count < m.Indices.Count / 3) m.FaceNegative.Add(0); // 无标记时补 0
            // 部件类型标记：model_settings 的 part subtype 权威覆盖子模型自身的属性判定
            for (var f = faceBase; f < m.FaceNegative.Count; f++)
                m.FaceModifier.Add(partKind == PartKindModifier ? (byte)1 : (byte)0);
            if (partKind == PartKindNegative)
                for (var f = faceBase; f < m.FaceNegative.Count; f++) m.FaceNegative[f] = 1;
            else if (partKind == PartKindModifier)
                for (var f = faceBase; f < m.FaceNegative.Count; f++) m.FaceNegative[f] = 0; // 修改器不是负零件
            var faceStart = faceColors.Count;
            foreach (var fc in sm.FaceColors)
                faceColors.Add(fc);
            while (faceColors.Count < m.Indices.Count / 3) faceColors.Add(Vector4.Zero); // 无上色面补全
            // 父 object 料槽色：把合并进来的未上色面补齐（优先级低于子模型自身的 paint_color/colorgroup）
            if (objectColor.HasValue)
                for (var f = faceStart; f < faceColors.Count; f++)
                    if (faceColors[f].W <= 0f) faceColors[f] = objectColor.Value;
        }

        /// <summary>一次性解析子模型：返回 objectId -> SubMesh（含负零件标记与逐面色），不做 transform。
        /// 先解析子模型自己的 colorgroup（无则回退父入口的 colorGroups）。
        /// reuseParentGroups=true（component 省略 path、引用主模型自身）时直接复用父级色组，
        /// 省去对同一大文件重复全量扫描。</summary>
        private static Dictionary<int, SubMesh> ParseSubModel(ZipArchiveEntry entry, List<Vector4> palette,
            Dictionary<int, List<Vector4>> parentGroups, bool reuseParentGroups = false)
        {
            var result = new Dictionary<int, SubMesh>();
            var groups = (reuseParentGroups && parentGroups != null)
                ? parentGroups
                : ReadColorGroups(entry);
            if (groups.Count == 0 && parentGroups != null) groups = parentGroups;
            using (var es = entry.Open())
            using (var sr = XmlReader.Create(es, ReaderSettings))
            {
                while (sr.Read())
                {
                    if (sr.NodeType != XmlNodeType.Element || sr.LocalName != "object") continue;
                    var oidStr = GetAttr(sr, "id");
                    if (!int.TryParse(oidStr, NumberStyles.Integer, Ci, out var id)) continue;
                    var neg = IsNegativeObject(sr);
                    var sm = new SubMesh();
                    using var sub = sr.ReadSubtree();
                    ReadObjectMesh(sub, sm.Vertices, sm.Indices, sm.FaceNeg, sm.FaceColors, null, neg,
                        palette, groups);
                    if (sm.Vertices.Count > 0) result[id] = sm;
                }
            }
            return result;
        }

        /// <summary>子模型中的一个 object 的网格数据（局部索引，未应用 transform）+ 负零件标记 + 逐面色。</summary>
        private sealed class SubMesh
        {
            public readonly List<Vector3> Vertices = new List<Vector3>();
            public readonly List<int> Indices = new List<int>();
            public readonly List<byte> FaceNeg = new List<byte>();
            public readonly List<Vector4> FaceColors = new List<Vector4>();
        }

        /// <summary>
        /// 把一个 object 的 mesh 顶点合并进全局池，三角形索引加上池起点；可选应用 transform。
        /// negative=true 时将该 object 新增的三角形全部标记为负零件（写 faceNeg）。
        /// 逐面颜色优先级：
        ///   1. paint_color（拓竹/Orca 多色）→ 料槽 id → 调色板；
        ///   2. colorgroup 的 pid/p1/p2/p3（PrusaSlicer 等标准 3MF，paint_color 未命中时）→ 三索引颜色均值；
        ///   3. 空标记（回退基础材质 / 由上层按 object 料槽色补齐）。
        /// </summary>
        private static void ReadObjectMesh(XmlReader r, List<Vector3> pos, List<int> tris,
            List<byte> faceNeg, List<Vector4> faceColors, Matrix4x4? t, bool negative, List<Vector4> palette,
            Dictionary<int, List<Vector4>> colorGroups)
        {
            var baseIdx = -1;
            var triStart = -1;
            while (r.Read())
            {
                if (r.NodeType != XmlNodeType.Element) continue;
                if (r.LocalName == "vertices")
                {
                    baseIdx = pos.Count;
                    triStart = tris.Count / 3;
                    using var sub = r.ReadSubtree();
                    while (sub.Read())
                    {
                        if (sub.NodeType == XmlNodeType.Element && sub.LocalName == "vertex")
                        {
                            var v = new Vector3(AttrF(sub, "x"), AttrF(sub, "y"), AttrF(sub, "z"));
                            if (t.HasValue) v = Vector3.Transform(v, t.Value);
                            pos.Add(v);
                        }
                    }
                }
                else if (r.LocalName == "triangles" && baseIdx >= 0)
                {
                    // 边中点缓存：跨三角形复用同一边的中点顶点（key = 无序顶点对），避免展开后共享边裂缝。
                    // 注意必须用自定义 comparer：long 键的默认 GetHashCode()=hi^lo 在本网格的索引模式下
                    // 哈希退化（实测把检查/展开拖到 O(n^2)），PairComparer 做乘法混合保持分布。
                    var midCache = new Dictionary<long, int>(new PairComparer());
                    using var sub = r.ReadSubtree();
                    while (sub.Read())
                    {
                        if (sub.NodeType != XmlNodeType.Element || sub.LocalName != "triangle") continue;
                        int a = AttrI(sub, "v1") + baseIdx;
                        int b = AttrI(sub, "v2") + baseIdx;
                        int c = AttrI(sub, "v3") + baseIdx;
                        if (a < 0 || b < 0 || c < 0 || a >= pos.Count || b >= pos.Count || c >= pos.Count) continue;
                        var paint = GetAttr(sub, "paint_color");
                        // 非叶 paint_color 树：把原始三角形展开成多个带料槽色的子三角形（拓竹/Orca 精确观感）。
                        // 展开失败（树畸形/超深/位流耗尽）→ 回退下面的 majority 逻辑（保持健壮）。
                        if (!string.IsNullOrEmpty(paint))
                        {
                            var leafStates = new List<int>();
                            if (TryExpandPaintColor(paint, a, b, c, pos, tris, leafStates, midCache))
                            {
                                if (faceColors != null)
                                {
                                    bool anyPainted = false;
                                    foreach (var stt in leafStates)
                                        if (stt > 0) { anyPainted = true; break; }
                                    if (!anyPainted && colorGroups != null && colorGroups.Count > 0)
                                    {
                                        // 全部叶子未上色：尝试标准 colorgroup pid 均值色（保持既有回退语义）
                                        var mean = MeanColorForPid(sub, colorGroups);
                                        for (var i = 0; i < leafStates.Count; i++)
                                            faceColors.Add(mean.HasValue ? mean.Value : Vector4.Zero);
                                    }
                                    else
                                    {
                                        foreach (var stt in leafStates)
                                            faceColors.Add(FaceColorForState(stt, palette));
                                    }
                                }
                                continue; // 已展开处理，跳到下一三角形
                            }
                        }
                        tris.Add(a); tris.Add(b); tris.Add(c);
                        // paint_color 优先（拓竹/Orca）；解出 state0（未上色）时再尝试标准 colorgroup pid
                        var faceColor = FaceColorForState(DecodePaintColor(paint), palette);
                        if (faceColor.W <= 0f && colorGroups != null && colorGroups.Count > 0)
                        {
                            var mean = MeanColorForPid(sub, colorGroups);
                            if (mean.HasValue) faceColor = mean.Value;
                        }
                        faceColors?.Add(faceColor);
                    }
                }
            }
            // 标记本 object 的三角形负零件状态（补齐到当前长度）
            if (triStart >= 0 && faceNeg != null)
            {
                while (faceNeg.Count < tris.Count / 3) faceNeg.Add(0);
                if (negative)
                    for (var i = triStart; i < tris.Count / 3; i++) faceNeg[i] = 1;
            }
            // 逐面颜色补全：确保与三角形数量对齐（无属性面 = 空标记）
            if (faceColors != null)
                while (faceColors.Count < tris.Count / 3) faceColors.Add(Vector4.Zero);
        }

        /// <summary>3MF transform="m00 m01 m02 m10 m11 m12 m20 m21 m22 tx ty tz"（行主序 3x4）。</summary>
        private static Matrix4x4? ParseTransform(string s)
        {
            if (string.IsNullOrWhiteSpace(s)) return null;
            var parts = s.Split(new[] { ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length < 12) return null;
            var f = new float[12];
            for (var i = 0; i < 12; i++)
                if (!float.TryParse(parts[i], NumberStyles.Float, Ci, out f[i])) return null;

            // 3MF 行主序 → System.Numerics 行向量约定：Vector3.Transform(v, M) = v * M
            return new Matrix4x4(
                f[0], f[1], f[2], 0f,
                f[3], f[4], f[5], 0f,
                f[6], f[7], f[8], 0f,
                f[9], f[10], f[11], 1f);
        }

        /// <summary>取任意命名空间的属性值（如 p:path）。注意：fallback 遍历后必须恢复元素位置，否则后续 ReadSubtree 报错。</summary>
        private static string GetAttr(XmlReader r, string localName)
        {
            var s = r.GetAttribute(localName);
            if (s != null) return s;
            for (var i = 0; i < r.AttributeCount; i++)
            {
                r.MoveToAttribute(i);
                if (r.LocalName == localName)
                {
                    var v = r.Value;
                    r.MoveToElement();
                    return v;
                }
            }
            r.MoveToElement(); // 未找到也要恢复元素节点（MoveToAttribute 会移动 reader）
            return null;
        }

        private static float AttrF(XmlReader r, string name)
        {
            var s = r.GetAttribute(name);
            if (string.IsNullOrWhiteSpace(s)) return 0f;
            // 容错：个别导出器用逗号小数（受系统区域影响）
            if (s.IndexOf(',') >= 0) s = s.Replace(',', '.');
            return float.TryParse(s, NumberStyles.Float, Ci, out var v) ? v : 0f;
        }

        private static int AttrI(XmlReader r, string name)
        {
            var s = r.GetAttribute(name);
            if (string.IsNullOrEmpty(s)) return -1;
            return int.TryParse(s, NumberStyles.Integer, Ci, out var v) ? v : -1;
        }

        // ================= 拓竹/Orca 多色（paint_color） =================

        /// <summary>
        /// 从 3MF 包内读取项目料色 (project_settings.config) 的 filament_colour 数组 → 调色板。
        /// 拓竹导出格式（JSON）："filament_colour":["#FFFFFF", ...]。轻量解析，无 JSON 依赖。
        /// 找不到/格式不符 → null（渲染回退默认灰或内置分色）。
        /// </summary>
        private static List<Vector4> TryReadFilamentPalette(ZipArchive zip)
        {
            var entry = zip.Entries.FirstOrDefault(e =>
                e.FullName.EndsWith("project_settings.config", StringComparison.OrdinalIgnoreCase));
            if (entry == null) return null;
            try
            {
                string json;
                using (var es = entry.Open())
                using (var sr = new StreamReader(es))
                    json = sr.ReadToEnd();
                return ParseFilamentColors(json);
            }
            catch
            {
                return null;
            }
        }

        /// <summary>解析 filament_colour":["#RRGGBB",...] 为 RGB（alpha=1）。解析不到 → 空列表。</summary>
        private static List<Vector4> ParseFilamentColors(string json)
        {
            var list = new List<Vector4>();
            if (string.IsNullOrEmpty(json)) return list;
            // 精确锚定键名 "filament_colour"（前后各带引号），避免误命中 default_filament_colour / filament_colour_type
            int ki = json.IndexOf("\"filament_colour\"", StringComparison.OrdinalIgnoreCase);
            if (ki < 0) return list;
            int ob = json.IndexOf('[', ki);
            if (ob < 0) return list;
            int cb = json.IndexOf(']', ob);
            if (cb < 0) cb = json.Length;
            var inside = json.Substring(ob + 1, cb - ob - 1);
            var tokens = inside.Split(',');
            foreach (var tk in tokens)
            {
                if (tk.IndexOf('#') < 0) continue;
                var hash = tk.IndexOf('#');
                var hex = tk.Substring(hash + 1).Trim();
                if (hex.Length < 6) continue;
                var r = HexByte(hex, 0); var g = HexByte(hex, 2); var b = HexByte(hex, 4);
                if (r < 0 || g < 0 || b < 0) continue;
                list.Add(new Vector4(r / 255f, g / 255f, b / 255f, 1f));
            }
            return list;
        }

        private static int HexByte(string hex, int i)
        {
            int hi = HexVal(hex[i]); int lo = HexVal(hex[i + 1]);
            if (hi < 0 || lo < 0) return -1;
            return (hi << 4) | lo;
        }

        private static int HexVal(char c)
        {
            if (c >= '0' && c <= '9') return c - '0';
            if (c >= 'a' && c <= 'f') return c - 'a' + 10;
            if (c >= 'A' && c <= 'F') return c - 'A' + 10;
            return -1;
        }

        /// <summary>
        /// 解码 paint_color（TriangleSelector::serialize 的可变长 bitstream）→ 料槽 id（1-based；0=未上色）。
        /// 权威编码（OrcaSlicer 的 serialize/deserialize，'模型'Entrypoint.js 实测验证 7 色全命中）：
        ///  - 每"树节点"恰好 4 bit = 1 nibble = paint_color 的一个 hex 字符；
        ///  - 不规则的树以逆序写入 → 读取须从字符串末尾向头逐字符取 nibble（LSB-first）。
        ///  - 节点 code=split|(top&lt;&lt;2)：split=code&amp;3（0=叶子，1..3=按 1..3 边分裂成 2..4 个子三角）；
        ///    split==0 且 top&lt;3 → state=top（0=NONE/1=ENFORCER/2=BLOCKER）；
        ///    split==0 且 top==3 → 再读 nibble a：a&lt;15 → state=3+a；a==15 → 再读 nibble b → state=18+b；
        ///    split&gt;0 → 递归解码 (split+1) 个子三角形（官方读法，子树逆序填写）。
        /// 每个三角形可能是几十~上千个叶子的细分树，这里递归展开全部叶子后返回<出现最多的料槽 state>
        /// 作为该面的主色（覆盖 18 万单色三角形的精确结果 + 2.6 万细分三角形的近似）。
        /// 已用 signpost 验证：state3→"c0"，state4→"c1"，state18→"cf0"。
        /// </summary>
        private static int DecodePaintColor(string hex)
        {
            if (string.IsNullOrEmpty(hex)) return 0;
            int len = hex.Length;
            // 每个 hex 字符 = 一个 nibble（LSB-first），树逆序写入 → 从末尾向头取，得到逐节点 nibble 序列
            var nib = new int[len];
            for (int i = 0; i < len; i++)
            {
                int v = HexVal(hex[len - 1 - i]);
                if (v < 0) return 0;
                nib[i] = v;
            }
            int pos = 0;

            // 叶子顶层的扩展 state：top==3 时再读若干 nibble
            int DecodeLeafState()
            {
                int a = (pos < len) ? nib[pos++] : 0;
                if (a == 0b1111) // 15 → 再读一 nibble 作为 18+ 的高位扩展
                    return 18 + ((pos < len) ? nib[pos++] : 0);
                return 3 + a;
            }

            // 递归解析节点：返回该节点子树下出现最多的叶子 state
            int Node(int depth)
            {
                if (depth > 24 || pos >= len) return 0;
                int code = nib[pos++];
                int split = code & 0b11;
                int top = code >> 2;
                if (split == 0)
                    return top < 3 ? top : DecodeLeafState();
                // 非叶：递归解码 (split+1) 个子三角形，取主料槽（出现次数最多）
                var cnt = new Dictionary<int, int>();
                for (int c = 0; c <= split; c++)
                {
                    int st = Node(depth + 1);
                    cnt[st] = cnt.TryGetValue(st, out var e) ? e + 1 : 1;
                }
                int best = 0, bestN = -1;
                foreach (var kv in cnt)
                    if (kv.Value > bestN) { bestN = kv.Value; best = kv.Key; }
                return best;
            }

            return Node(0);
        }

        /// <summary>
        /// 把 paint_color（TriangleSelector 序列化流）展开成多个叶子子三角形，各自带料槽 state。
        /// 权威几何规则（OrcaSlicer src/libslic3r/TriangleSelector.cpp）：
        ///  - 每个树节点恰好 4 bit（1 nibble），LSB-first，树逆序写入（读取从 hex 串末到头）；
        ///    code = split_sides | (special_side &lt;&lt; 2)；split_sides=0 为叶子（state 编码同 DecodePaintColor）；
        ///  - 非叶节点：顶点按 special_side 旋转 v0=p[s], v1=p[s+1], v2=p[s+2]，新顶点恒为对应边中点
        ///    （triangle_midpoint_or_allocate：0.5*(v[i]+v[j])），子三角形顶点分配照抄 perform_split()：
        ///    split=1（分裂边 v1-v2，中点 m12）：(v0,v1,m12)、(m12,v2,v0)
        ///    split=2（分裂边 v0-v1 与 v0-v2，中点 m01/m02）：(v0,m01,m02)、(m01,v1,m02)、(v1,v2,m02)
        ///    split=3（special_side 恒 0，三边中点 m01/m12/m20）：(v0,m01,m20)、(m01,v1,m12)、(m12,v2,m20)、(m01,m12,m20)
        ///  - serialize 以 children[split_sides]..children[0] 逆序写 → 流中第 k 个子树对应 children[split_sides-k]
        ///    （几何列表逆序映射）。
        /// 中点在 pos 末尾追加，并经 midCache（无序顶点对）跨三角形复用，保证共享边无裂缝。
        /// 返回 true 表示展开成功（tris/leafStates/pos 已追加）；失败返回 false 且已回滚本次全部修改。
        /// </summary>
        private static bool TryExpandPaintColor(string hex, int a, int b, int c,
            List<Vector3> pos, List<int> tris, List<int> leafStates, Dictionary<long, int> midCache)
        {
            var st = new PaintStream(hex);
            if (!st.Ok) return false;
            int triStart = tris.Count;
            int stateStart = leafStates.Count;
            int posStart = pos.Count;
            var newKeys = new List<long>();
            bool ok = ExpandNode(st, a, b, c, 0, pos, tris, leafStates, midCache, newKeys);
            if (ok && !st.Ok) ok = false;
            if (!ok)
            {
                // 回滚：截断本次追加的三角形/叶子/顶点，并删除本次新增的中点缓存项
                if (tris.Count > triStart) tris.RemoveRange(triStart, tris.Count - triStart);
                if (leafStates.Count > stateStart) leafStates.RemoveRange(stateStart, leafStates.Count - stateStart);
                if (pos.Count > posStart) pos.RemoveRange(posStart, pos.Count - posStart);
                foreach (var k in newKeys) midCache.Remove(k);
                return false;
            }
            return true;
        }

        /// <summary>递归解析一个树节点：叶子写一个子三角形；非叶按 perform_split 规则生成子三角形几何后递归。</summary>
        private static bool ExpandNode(PaintStream st, int p0, int p1, int p2, int depth,
            List<Vector3> pos, List<int> tris, List<int> leafStates,
            Dictionary<long, int> midCache, List<long> newKeys)
        {
            if (depth > 24 || !st.Ok) return false;
            int code = st.Next();
            int split = code & 0b11;
            if (split == 0)
            {
                int top = code >> 2;
                int state = top < 3 ? top : st.LeafState();
                if (!st.Ok) return false;
                tris.Add(p0); tris.Add(p1); tris.Add(p2);
                leafStates.Add(state);
                return true;
            }
            int s = code >> 2;
            int v0 = Pivot(p0, p1, p2, s);
            int v1 = Pivot(p0, p1, p2, (s + 1) % 3);
            int v2 = Pivot(p0, p1, p2, (s + 2) % 3);
            // 子三角形几何（照抄 TriangleSelector::perform_split，顶点顺序保持父三角形绕向）。
            // 流中子树按 children[split_sides]..children[0] 逆序写 → 递归顺序必须从 children[split] 到 children[0]。
            if (split == 1)
            {
                int m12 = Midpoint(pos, midCache, newKeys, v1, v2); // 分裂边 v1-v2（special_side 对面）
                if (!ExpandNode(st, m12, v2, v0, depth + 1, pos, tris, leafStates, midCache, newKeys)) return false;
                if (!ExpandNode(st, v0, v1, m12, depth + 1, pos, tris, leafStates, midCache, newKeys)) return false;
            }
            else if (split == 2)
            {
                int m01 = Midpoint(pos, midCache, newKeys, v0, v1); // 分裂边 v0-v1
                int m02 = Midpoint(pos, midCache, newKeys, v0, v2); // 分裂边 v0-v2
                if (!ExpandNode(st, v1, v2, m02, depth + 1, pos, tris, leafStates, midCache, newKeys)) return false;
                if (!ExpandNode(st, m01, v1, m02, depth + 1, pos, tris, leafStates, midCache, newKeys)) return false;
                if (!ExpandNode(st, v0, m01, m02, depth + 1, pos, tris, leafStates, midCache, newKeys)) return false;
            }
            else
            {
                // split==3（special_side 恒为 0，未旋转）：三条边全分裂
                int m01 = Midpoint(pos, midCache, newKeys, v0, v1);
                int m12 = Midpoint(pos, midCache, newKeys, v1, v2);
                int m20 = Midpoint(pos, midCache, newKeys, v2, v0);
                if (!ExpandNode(st, m01, m12, m20, depth + 1, pos, tris, leafStates, midCache, newKeys)) return false;
                if (!ExpandNode(st, m12, v2, m20, depth + 1, pos, tris, leafStates, midCache, newKeys)) return false;
                if (!ExpandNode(st, m01, v1, m12, depth + 1, pos, tris, leafStates, midCache, newKeys)) return false;
                if (!ExpandNode(st, v0, m01, m20, depth + 1, pos, tris, leafStates, midCache, newKeys)) return false;
            }
            return true;
        }

        /// <summary>取边 (va,vb) 的中点顶点：已存在则复用，否则在 pos 末尾新建并记入缓存。</summary>
        private static int Midpoint(List<Vector3> pos, Dictionary<long, int> cache, List<long> newKeys,
            int va, int vb)
        {
            int lo = va < vb ? va : vb;
            int hi = va < vb ? vb : va;
            long key = ((long)lo << 32) | (uint)hi;
            int m;
            if (cache.TryGetValue(key, out m)) return m;
            var p = 0.5f * (pos[va] + pos[vb]);
            m = pos.Count;
            pos.Add(p);
            cache[key] = m;
            newKeys.Add(key);
            return m;
        }

        /// <summary>long 无序对键的自定义哈希（避免默认 hi^lo 退化）。net461 无 ValueTuple，故用 long + comparer。</summary>
        private sealed class PairComparer : IEqualityComparer<long>
        {
            public bool Equals(long x, long y) => x == y;
            public int GetHashCode(long k)
            {
                int lo = (int)(k & 0xFFFFFFFFL);
                int hi = (int)(k >> 32);
                return lo * 397 ^ hi;
            }
        }

        /// <summary>取三角形三个顶点索引的第 i 个（i∈{0,1,2}）。</summary>
        private static int Pivot(int p0, int p1, int p2, int i)
        {
            if (i == 0) return p0;
            if (i == 1) return p1;
            return p2;
        }

        /// <summary>
        /// paint_color 流：nibble 序列（hex 串逆序，LSB-first），带越界/非法标记。
        /// 直接按下标读原始串（hex[len-1-Pos]），不复制数组——拓竹文件 paint 串可长达 9000+ 字符，
        /// 复制 int[len] 数组在数十万三角形上会造成 GB 级分配压力（实测 benchy 解析 181s 的根因之一）。
        /// </summary>
        private sealed class PaintStream
        {
            private readonly string S;
            private int Pos;
            public bool Ok = true;

            public PaintStream(string hex)
            {
                S = hex;
            }

            public int Next()
            {
                if (Pos < S.Length)
                {
                    int v = HexVal(S[S.Length - 1 - Pos]);
                    Pos++;
                    if (v < 0) { Ok = false; return 0; }
                    return v;
                }
                Ok = false;
                return 0;
            }

            /// <summary>叶子扩展 state：top==3 后的 1~2 nibble 编码（3+a / 18+b）。</summary>
            public int LeafState()
            {
                int a = Next();
                if (a == 0b1111) return 18 + Next();
                return 3 + a;
            }
        }

        /// <summary>标准 3MF colorgroup 的面均值色（pid + p1/p2/p3）；未命中返回 null。</summary>
        private static Vector4? MeanColorForPid(XmlReader sub, Dictionary<int, List<Vector4>> colorGroups)
        {
            if (colorGroups == null || colorGroups.Count == 0) return null;
            var pidStr = GetAttr(sub, "pid");
            if (!int.TryParse(pidStr, NumberStyles.Integer, Ci, out var pid)) return null;
            if (!colorGroups.TryGetValue(pid, out var clist)) return null;
            int p1 = AttrI(sub, "p1"), p2 = AttrI(sub, "p2"), p3 = AttrI(sub, "p3");
            // p1/p2/p3 是该 colorgroup 内颜色索引；越界则忽略该面颜色（保留空标记）
            if (p1 >= 0 && p1 < clist.Count && p2 >= 0 && p2 < clist.Count && p3 >= 0 && p3 < clist.Count)
            {
                var sum = clist[p1] + clist[p2] + clist[p3];
                return new Vector4(sum.X / 3f, sum.Y / 3f, sum.Z / 3f, 1f);
            }
            return null;
        }

        /// <summary>料槽 id → 颜色：有调色板用项目料色；否则用内置分色以便看出上色区域。</summary>
        private static Vector4 FaceColorForState(int state, List<Vector4> palette)
        {
            if (state <= 0) return Vector4.Zero; // 未上色 → 空标记，回退基础材质
            if (palette != null && state - 1 < palette.Count)
            {
                var c = palette[state - 1];
                return new Vector4(c.X, c.Y, c.Z, 1f);
            }
            // 无调色板条目：内置分色（保证多色区域可区分），超出则循环
            int idx = (state - 1) % FallbackFilaments.Length;
            return FallbackFilaments[idx];
        }

        // ================= 标准 3MF ColorGroup（PrusaSlicer 等） =================

        /// <summary>
        /// 扫描入口/子模型的 &lt;resources&gt; 里的 &lt;colorgroup id="N"&gt;（3MF 核心规范 1.2+）：
        /// 组内每个 &lt;color color="#RRGGBB"/&gt; 是一个可选色（支持 #RGB / #RRGGBB，alpha=1）。
        /// 三角形用 pid="N" p1/p2/p3（组内颜色索引，可不同）引用 → 三索引颜色均值 = 该面颜色。
        /// </summary>
        private static Dictionary<int, List<Vector4>> ReadColorGroups(ZipArchiveEntry entry)
        {
            var result = new Dictionary<int, List<Vector4>>();
            if (entry == null) return result;
            try
            {
                using var es = entry.Open();
                using var r = XmlReader.Create(es, ReaderSettings);
                while (r.Read())
                {
                    if (r.NodeType != XmlNodeType.Element || r.LocalName != "colorgroup") continue;
                    var idStr = GetAttr(r, "id");
                    if (!int.TryParse(idStr, NumberStyles.Integer, Ci, out var id)) continue;
                    var list = new List<Vector4>();
                    using var sub = r.ReadSubtree();
                    while (sub.Read())
                    {
                        if (sub.NodeType != XmlNodeType.Element || sub.LocalName != "color") continue;
                        var c = ParseHexColor(GetAttr(sub, "color"));
                        if (c.HasValue) list.Add(c.Value);
                    }
                    if (list.Count > 0) result[id] = list;
                }
            }
            catch
            {
            }
            return result;
        }

        /// <summary>解析 "#RGB" / "#RRGGBB" 为 RGBA（alpha=1）；非法返回 null。</summary>
        private static Vector4? ParseHexColor(string s)
        {
            if (string.IsNullOrWhiteSpace(s)) return null;
            var hex = s.TrimStart('#');
            if (hex.Length == 3) // #RGB：每 nibble 扩展为字节（0xF → 255）
            {
                int r = HexVal(hex[0]), g = HexVal(hex[1]), b = HexVal(hex[2]);
                if (r < 0 || g < 0 || b < 0) return null;
                return new Vector4((r * 17) / 255f, (g * 17) / 255f, (b * 17) / 255f, 1f);
            }
            if (hex.Length >= 6)
            {
                int r = HexByte(hex, 0), g = HexByte(hex, 2), b = HexByte(hex, 4);
                if (r < 0 || g < 0 || b < 0) return null;
                return new Vector4(r / 255f, g / 255f, b / 255f, 1f);
            }
            return null;
        }

        // ================= Bambu 多材料（object 顶层 extruder 料槽） =================

        /// <summary>
        /// 读取 Metadata/model_settings.config：每个 &lt;object id="N"&gt; 顶层（任何 &lt;part&gt; 之前）
        /// 的 &lt;metadata key="extruder" value="E"/&gt; → N → E（1-based 料槽号）。
        /// 无该元数据/文件缺失 → 空字典。
        /// </summary>
        private static Dictionary<int, int> ReadObjectFilaments(ZipArchive zip)
        {
            var result = new Dictionary<int, int>();
            var entry = zip.Entries.FirstOrDefault(e =>
                e.FullName.Equals("Metadata/model_settings.config", StringComparison.OrdinalIgnoreCase));
            if (entry == null) return result;
            try
            {
                using var es = entry.Open();
                using var sr = XmlReader.Create(es, ReaderSettings);
                while (sr.Read())
                {
                    if (sr.NodeType != XmlNodeType.Element || sr.LocalName != "object") continue;
                    var idStr = GetAttr(sr, "id");
                    if (!int.TryParse(idStr, NumberStyles.Integer, Ci, out var id)) continue;
                    using var sub = sr.ReadSubtree();
                    while (sub.Read())
                    {
                        if (sub.NodeType != XmlNodeType.Element) continue;
                        if (sub.LocalName == "part") break; // 只读 object 顶层，进入 part 即停
                        if (sub.LocalName == "metadata" && GetAttr(sub, "key") == "extruder" &&
                            int.TryParse(GetAttr(sub, "value"), NumberStyles.Integer, Ci, out var ext) && ext > 0)
                        {
                            result[id] = ext;
                            break;
                        }
                    }
                }
            }
            catch
            {
            }
            return result;
        }

        /// <summary>object 料槽号 → 颜色：调色板[extruder-1]；调色板缺失/越界用内置 FallbackFilaments 分色。</summary>
        private static Dictionary<int, Vector4> BuildObjectColors(Dictionary<int, int> filaments, List<Vector4> palette)
        {
            var result = new Dictionary<int, Vector4>();
            if (filaments == null) return result;
            foreach (var kv in filaments)
            {
                var idx = kv.Value - 1;
                Vector4 c;
                if (palette != null && idx >= 0 && idx < palette.Count)
                    c = new Vector4(palette[idx].X, palette[idx].Y, palette[idx].Z, 1f);
                else
                {
                    idx = ((idx % FallbackFilaments.Length) + FallbackFilaments.Length) % FallbackFilaments.Length;
                    c = FallbackFilaments[idx];
                }
                result[kv.Key] = c;
            }
            return result;
        }

        /// <summary>当前 object 元素的料槽色（按 id 查 objectColors）；无 id/未命中返回 null。</summary>
        private static Vector4? ObjectColorOf(XmlReader r, Dictionary<int, Vector4> objectColors)
        {
            if (objectColors == null || objectColors.Count == 0) return null;
            var idStr = GetAttr(r, "id");
            if (!int.TryParse(idStr, NumberStyles.Integer, Ci, out var id)) return null;
            return objectColors.TryGetValue(id, out var c) ? c : (Vector4?)null;
        }

        // ================= Bambu 多材料细化（object 内 <part> 级 extruder） =================

        /// <summary>
        /// 读取 Metadata/model_settings.config：每个 &lt;object id="N"&gt; 内按文档序的
        /// &lt;part&gt; 层第一个 &lt;metadata key="extruder" value="E"/&gt; → N → 按序料槽号列表。
        /// 多材料工程（Bambu/Orca）里 object 顶层 extruder 通常恒为 1，真正的 1-6 料槽分配在 part 层；
        /// part 与 3dmodel.model 的 &lt;component&gt; 按文档序一一对应（source_volume_id 递增）。
        /// 无该元数据/文件缺失 → 空字典。
        /// </summary>
        private static Dictionary<int, List<int>> ReadObjectPartFilaments(ZipArchive zip)
        {
            var result = new Dictionary<int, List<int>>();
            var entry = zip.Entries.FirstOrDefault(e =>
                e.FullName.Equals("Metadata/model_settings.config", StringComparison.OrdinalIgnoreCase));
            if (entry == null) return result;
            try
            {
                using var es = entry.Open();
                using var sr = XmlReader.Create(es, ReaderSettings);
                while (sr.Read())
                {
                    if (sr.NodeType != XmlNodeType.Element || sr.LocalName != "object") continue;
                    var idStr = GetAttr(sr, "id");
                    if (!int.TryParse(idStr, NumberStyles.Integer, Ci, out var id)) continue;
                    using var sub = sr.ReadSubtree();
                    while (sub.Read())
                    {
                        if (sub.NodeType != XmlNodeType.Element || sub.LocalName != "part") continue;
                        using var ps = sub.ReadSubtree();
                        int ext = -1;
                        while (ps.Read())
                        {
                            if (ps.NodeType != XmlNodeType.Element) continue;
                            if (ps.LocalName == "metadata" && GetAttr(ps, "key") == "extruder" &&
                                int.TryParse(GetAttr(ps, "value"), NumberStyles.Integer, Ci, out var e2) &&
                                e2 > 0)
                            {
                                ext = e2;
                                break;
                            }
                        }
                        if (ext > 0)
                        {
                            if (!result.TryGetValue(id, out var list))
                                result[id] = list = new List<int>();
                            list.Add(ext);
                        }
                    }
                }
            }
            catch
            {
            }
            return result;
        }

        /// <summary>部件料槽号序列 → 颜色序列（调色板[extruder-1]；调色板缺失/越界用内置 FallbackFilaments 分色）。</summary>
        private static Dictionary<int, List<Vector4>> BuildPartColors(Dictionary<int, List<int>> parts,
            List<Vector4> palette)
        {
            var result = new Dictionary<int, List<Vector4>>();
            if (parts == null) return result;
            foreach (var kv in parts)
            {
                var list = new List<Vector4>();
                foreach (var ext in kv.Value)
                {
                    var idx = ext - 1;
                    Vector4 c;
                    if (palette != null && idx >= 0 && idx < palette.Count)
                        c = new Vector4(palette[idx].X, palette[idx].Y, palette[idx].Z, 1f);
                    else
                    {
                        idx = ((idx % FallbackFilaments.Length) + FallbackFilaments.Length) % FallbackFilaments.Length;
                        c = FallbackFilaments[idx];
                    }
                    list.Add(c);
                }
                if (list.Count > 0) result[kv.Key] = list;
            }
            return result;
        }

        /// <summary>当前 object 元素的按序部件色列表（按 id 查 partColors）；无 id/未命中返回 null。</summary>
        private static List<Vector4> PartColorsOf(XmlReader r, Dictionary<int, List<Vector4>> partColors)
        {
            if (partColors == null || partColors.Count == 0) return null;
            var idStr = GetAttr(r, "id");
            if (!int.TryParse(idStr, NumberStyles.Integer, Ci, out var id)) return null;
            return partColors.TryGetValue(id, out var c) ? c : null;
        }

        // ================= 部件类型（普通 / 负零件 / 修改器） =================

        internal const byte PartKindNormal = MeshData.PartKindNormal;
        internal const byte PartKindNegative = MeshData.PartKindNegative;
        internal const byte PartKindModifier = MeshData.PartKindModifier;

        /// <summary>取当前 object 的部件类型序列（与 partColors 同源的栈式查找）。</summary>
        private static List<byte> PartKindsOf(XmlReader r, Dictionary<int, List<byte>> partKinds)
        {
            if (partKinds == null || partKinds.Count == 0) return null;
            var idStr = GetAttr(r, "id");
            if (!int.TryParse(idStr, NumberStyles.Integer, Ci, out var id)) return null;
            return partKinds.TryGetValue(id, out var k) ? k : null;
        }

        /// <summary>
        /// 读取 Metadata/model_settings.config：每个 &lt;object id="N"&gt; 内按文档序的
        /// &lt;part id="i" subtype="..."&gt; → 部件类型序列（0=普通 normal_part，1=负零件 negative_part，
        /// 2=修改器 modifier_part）。part 与 3dmodel.model 的 &lt;component&gt; 按文档序一一对应
        /// （source_volume_id 递增），故该序列可直接按 component 序号索引。
        /// 缺失 subtype 的 part 记 0，保证序号不错位。无该文件/无 subtype → 空字典（回退属性/名称启发式）。
        /// </summary>
        private static Dictionary<int, List<byte>> ReadObjectPartKinds(ZipArchive zip)
        {
            var result = new Dictionary<int, List<byte>>();
            var entry = zip.Entries.FirstOrDefault(e =>
                e.FullName.Equals("Metadata/model_settings.config", StringComparison.OrdinalIgnoreCase));
            if (entry == null) return result;
            try
            {
                using var es = entry.Open();
                using var sr = XmlReader.Create(es, ReaderSettings);
                while (sr.Read())
                {
                    if (sr.NodeType != XmlNodeType.Element || sr.LocalName != "object") continue;
                    var idStr = GetAttr(sr, "id");
                    if (!int.TryParse(idStr, NumberStyles.Integer, Ci, out var id)) continue;
                    using var sub = sr.ReadSubtree();
                    List<byte> list = null;
                    while (sub.Read())
                    {
                        if (sub.NodeType != XmlNodeType.Element || sub.LocalName != "part") continue;
                        var st = GetAttr(sub, "subtype");
                        byte kind = PartKindNormal;
                        if (!string.IsNullOrEmpty(st))
                        {
                            if (st.IndexOf("modifier", StringComparison.OrdinalIgnoreCase) >= 0)
                                kind = PartKindModifier;
                            else if (st.IndexOf("negative", StringComparison.OrdinalIgnoreCase) >= 0)
                                kind = PartKindNegative;
                        }
                        if (list == null) list = new List<byte>();
                        list.Add(kind);
                    }
                    if (list != null) result[id] = list;
                }
            }
            catch
            {
            }
            return result;
        }

        private static readonly Vector4[] FallbackFilaments =
        {
            new Vector4(0.86f, 0.86f, 0.90f, 1f),  // 1 白
            new Vector4(0.70f, 0.48f, 0.25f, 1f),  // 2 金/棕
            new Vector4(0.45f, 0.52f, 1.00f, 1f),  // 3 蓝
            new Vector4(0.90f, 0.22f, 0.20f, 1f),  // 4 红
            new Vector4(0.50f, 0.80f, 0.40f, 1f),  // 5 绿
            new Vector4(0.75f, 0.35f, 0.80f, 1f)   // 6 紫
        };
    }
}
