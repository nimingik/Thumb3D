using System.Collections.Generic;
using System.Numerics;

namespace _3DThumbnailShell.Core.Renderer
{
    /// <summary>统一几何结构，供所有格式解析器填充、渲染器消费。</summary>
    public sealed class MeshData
    {
        // 面片部件类型（FaceNegative / FaceModifier 的语义与渲染分层）
        public const byte PartKindNormal = 0;    // 普通实体：不透明、写深度
        public const byte PartKindNegative = 1;  // 负零件/镂空体：按不透明度混合叠加
        public const byte PartKindModifier = 2;  // 修改器：按不透明度混合叠加（颜色独立可调）

        /// <summary>顶点位置。网格模式按三角形展开（每三角形3顶点）；点云模式为离散点。</summary>
        public List<Vector3> Vertices = new List<Vector3>();

        /// <summary>三角形索引。网格模式每 3 个一组；点云模式为空。</summary>
        public List<int> Indices = new List<int>();

        /// <summary>逐顶点法线（长度等于 Vertices 时为 Gouraud 后备；否则渲染器用几何面法线做 flat 光照）。</summary>
        public List<Vector3> Normals = new List<Vector3>();

        /// <summary>逐顶点颜色 RGBA（0..1）。可选。</summary>
        public List<Vector4> Colors = new List<Vector4>();

        /// <summary>
        /// 逐三角形颜色 RGBA（0..1；长度 = 三角形数）。
        /// 拓竹/Orca 多色 3MF 的 paint_color 解码后映射到项目调色板（filament）得到。
        /// 与顶点色 Colors 不同：这是每个面的独立颜色（slice 软件的分色）。
        /// 渲染器取色优先级：overrideColor → FaceColors（alpha&gt;0 时）→ 顶点色 → 默认灰。
        /// 某面 alpha=0 表示"无该对象上色"（= 基础材质），渲染回退默认灰。
        /// </summary>
        public List<Vector4> FaceColors = new List<Vector4>();

        /// <summary>true 表示按点云渲染（PLY/OFF 无面时）。</summary>
        public bool IsPointCloud;

        /// <summary>
        /// 线段索引（每 2 个一组，指向 Vertices）。G-code 工具路径等用此模式。
        /// 与 IsLineCloud 配合使用：渲染器对每对顶点画一条带深度测试的线段。
        /// </summary>
        public List<int> Lines = new List<int>();

        /// <summary>true 表示按线段渲染（G-code 工具路径）。</summary>
        public bool IsLineCloud;

        /// <summary>
        /// 逐三角形负零件标记（长度 = Indices.Count/3；1=负零件/镂空体，0=正常实体）。
        /// 由 3MF 解析器按 object type≠model / printable=0 / 名称含 neg|cut|support 等规则填充。
        /// 渲染器对负零件以 25% 不透明度绘制（CPU 用 1/4 点阵 stipple，GPU 用 alpha 0.25 混合）。
        /// </summary>
        public List<byte> FaceNegative = new List<byte>();

        /// <summary>
        /// 逐三角形"修改器零件"标记（长度 = Indices.Count/3；1=修改器，0=普通实体）。
        /// 来源：Bambu/Orca 工程 Metadata/model_settings.config 里 &lt;part subtype="modifier_part"&gt;。
        /// 修改器只改变其体积内的打印参数（墙/填充速度等），不参与布尔运算，与普通模型分开显示：
        /// 渲染器按"可配置颜色 + 不透明度"的叠加层绘制（与负零件同样机制，但颜色/透明度独立可调）。
        /// </summary>
        public List<byte> FaceModifier = new List<byte>();

        public string OriginalFormat;

        /// <summary>嵌入预览图（切片文件如 CTB/PHOTON 内含的层预览位图，BGRA 格式）。
        /// 非 null 时缩略图提供者直接使用此图，跳过 3D 渲染。
        /// 与 PreviewWidth/PreviewHeight 配合。</summary>
        public byte[] PreviewBgra;
        public int PreviewWidth;
        public int PreviewHeight;

        /// <summary>
        /// 3MF 构建盘（plate，Bambu/Orca 工程）列表。Count&gt;1 时预览器可切换查看每一盘；
        /// Count==0 表示单盘/无盘信息（直接渲染自身几何）。
        /// 每盘含独立几何（已应用该盘 item 的 transform），主 MeshData 仍为全部盘的合并视图。
        /// </summary>
        public List<PlateInfo> Plates = new List<PlateInfo>();
    }

    /// <summary>3MF 的一个构建盘（build plate）。</summary>
    public sealed class PlateInfo
    {
        /// <summary>盘序号（plater_id，1 起）。</summary>
        public int Id;

        /// <summary>盘名称（model_settings.config 的 plater_name，可为空）。</summary>
        public string Name;

        /// <summary>该盘的独立几何（顶点/索引已应用本盘 item 的 transform）。</summary>
        public MeshData Mesh = new MeshData();
    }
}