namespace _3DThumbnailShell.Core.Renderer
{
    /// <summary>纯 CPU 渲染的输出：BGRA 像素缓冲（straight alpha，top-down），不依赖 System.Drawing。</summary>
    public sealed class RenderResult
    {
        /// <summary>像素分量总数 = Width * Height * 4。</summary>
        public int Width;
        public int Height;

        /// <summary>BGRA，straight（非预乘）alpha，顶向下顺序。span = Width*Height*4。</summary>
        public byte[] Bgra;
    }
}