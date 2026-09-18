using System;
using System.Drawing;
using System.Drawing.Imaging;
using System.Windows.Forms;
using _3DThumbnailShell.Core.Formats;
using _3DThumbnailShell.Core.Renderer;

namespace _3DThumbnailShell.Previewer
{
    internal static class Program
    {
        [STAThread]
        private static void Main(string[] args)
        {
            // 命令行模式（供"为所有用户安装"提权后二次进入，或脚本调用）:
            //   --install [--hklm]   --uninstall [--hklm]
            // 诊断: --dump-render <前缀> <3mf路径> —— 渲染合并视图+各单盘存 PNG 后退出
            // 诊断: --dump-thumb <前缀> <3mf路径> —— 按 Explorer 缩略图管线（ToYUp + SoftwareRenderer 256px）渲染存 PNG 后退出
            string dumpPrefix = null;
            string dumpThumbPrefix = null;
            var fileArgs = new System.Collections.Generic.List<string>();
            for (var i = 0; i < args.Length; i++)
            {
                switch (args[i].ToLowerInvariant())
                {
                    case "--install":
                    case "--uninstall":
                    {
                        var hklm = Array.IndexOf(args, "--hklm") >= 0;
                        var msg = args[i].ToLowerInvariant() == "--install"
                            ? ExtManager.Install(hklm)
                            : ExtManager.Uninstall(hklm);
                        MessageBox.Show(msg, "缩略图扩展", MessageBoxButtons.OK,
                            MessageBoxIcon.Information);
                        return;
                    }
                    case "--dump-render":
                        if (i + 1 < args.Length) { dumpPrefix = args[++i]; }
                        break;
                    case "--dump-thumb":
                        if (i + 1 < args.Length) { dumpThumbPrefix = args[++i]; }
                        break;
                    default:
                        fileArgs.Add(args[i]);
                        break;
                }
            }

            if (dumpThumbPrefix != null && fileArgs.Count > 0)
            {
                DumpThumbnail(fileArgs[0], dumpThumbPrefix);
                return;
            }

            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);
            string initial = fileArgs.Count > 0 ? fileArgs[0] : null;
            Application.Run(new MainForm(initial, dumpPrefix));
        }

        /// <summary>诊断：按 Explorer 缩略图管线（与 ThumbnailProvider/BatchSaveThumbnails 一致）渲染并存 PNG。</summary>
        private static void DumpThumbnail(string src, string prefix)
        {
            try
            {
                var mesh = MeshParserFactory.ParseFile(src);
                if (mesh == null || mesh.Vertices == null || mesh.Vertices.Count == 0)
                {
                    Console.Error.WriteLine("dump-thumb 失败: 解析无顶点");
                    return;
                }
                // 3MF Z-up → Y-up（与缩略图管线一致）
                for (var i = 0; i < mesh.Vertices.Count; i++)
                {
                    var v = mesh.Vertices[i];
                    mesh.Vertices[i] = new System.Numerics.Vector3(v.X, v.Z, -v.Y);
                }
                // 固定保守 LOD 上限：超大模型缩略图也能几秒降面渲染（与 ThumbnailProvider/BatchSaveThumbnails 一致）
                var res = new SoftwareRenderer().Render(mesh, 256, 300000);
                if (res == null || res.Bgra == null) { Console.Error.WriteLine("dump-thumb 失败: 渲染无输出"); return; }
                using (var bmp = new Bitmap(res.Width, res.Height, PixelFormat.Format32bppArgb))
                {
                    var rect = new Rectangle(0, 0, res.Width, res.Height);
                    var data = bmp.LockBits(rect, ImageLockMode.WriteOnly, PixelFormat.Format32bppArgb);
                    System.Runtime.InteropServices.Marshal.Copy(res.Bgra, 0, data.Scan0, res.Bgra.Length);
                    bmp.UnlockBits(data);
                    bmp.Save(prefix + ".png", ImageFormat.Png);
                }
                Console.WriteLine("dump-thumb 完成: " + prefix + ".png");
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine("dump-thumb 异常: " + ex);
            }
        }
    }
}
