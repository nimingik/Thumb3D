using System;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using Microsoft.Win32;

namespace _3DThumbnailShell.Previewer
{
    /// <summary>安装/卸载缩略图 Shell 扩展（mscoree 托管 COM 激活）。
    /// 默认写 HKCU\Software\Classes（免管理员）；hklm=true 写 HKLM（需管理员提权后经命令行进入）。</summary>
    internal static class ExtManager
    {
        // 必须与 ThumbnailProvider.cs 的 [Guid] 一致
        private const string Clsid = "6E7A4B2C-8D5F-4A3E-9B1C-2D4F6A8E0C1D";
        // IThumbnailProvider 接口 GUID（实现类别），注册表子键名需带花括号
        private const string ThumbProvCat = "E357FCCD-A995-4576-B01F-234630154E96";
        private static readonly string ThumbProvKey = "{" + ThumbProvCat + "}";
        // 3DThumbnailShell.Shell 的强名称全名（PublicKeyToken 来自 key.snk）
        private const string AssemblyFullName =
            "3DThumbnailShell.Shell, Version=1.0.0.0, Culture=neutral, PublicKeyToken=eeb4c33f653cd125";
        // mscoree 托管 COM 激活时，Class 值必须是类型全名（命名空间.类名），填 GUID 会报 0x80131522
        private const string ClassFullName = "_3DThumbnailShell.Shell.ThumbnailProvider";

        private static readonly string[] Exts =
            { ".stl", ".obj", ".3mf", ".ply", ".off", ".amf", ".glb", ".gltf" };

        [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
        private static extern void SHChangeNotify(int wEventId, uint uFlags, IntPtr dwItem1, IntPtr dwItem2);
        private const int SHCNE_ASSOCCHANGED = 0x08000000;
        private const uint SHCNF_IDLIST = 0x0000;

        /// <summary>安装目录：HKLM -> Program Files\3DThumbnailShell；HKCU -> LocalAppData\Programs\3DThumbnailShell。</summary>
        private static string InstallDir(bool hklm)
        {
            var root = hklm
                ? Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles)
                : Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            return Path.Combine(root, "Programs", "3DThumbnailShell");
        }

        public static string Install(bool hklm)
        {
            // 1) 定位安装源目录：优先 exe 同目录下 shell\ 子目录（发布包，含 net461 配套 Core/Numerics），
            //    其次 exe 同目录，最后仓库 Release 构建产物
            var srcDir = FindShellDir();
            if (srcDir == null)
                return "未找到 3DThumbnailShell.Shell.dll。请将本程序与 shell\\ 目录放在一起，或先构建 Shell 项目。";

            // 2) 复制到稳定安装目录（explorer 加载的是这里的副本，卸载/升级不依赖仓库）
            var dir = InstallDir(hklm);
            Directory.CreateDirectory(dir);
            var srcShell = Path.Combine(srcDir, "3DThumbnailShell.Shell.dll");
            var srcCore = Path.Combine(srcDir, "3DThumbnailShell.Core.dll");
            var srcNumerics = Path.Combine(srcDir, "System.Numerics.Vectors.dll");
            var destShell = Path.Combine(dir, "3DThumbnailShell.Shell.dll");
            File.Copy(srcShell, destShell, true);
            if (File.Exists(srcCore)) File.Copy(srcCore, Path.Combine(dir, "3DThumbnailShell.Core.dll"), true);
            if (File.Exists(srcNumerics)) File.Copy(srcNumerics, Path.Combine(dir, "System.Numerics.Vectors.dll"), true);

            // 3) 写注册表
            WriteRegistry(hklm, destShell);
            SHChangeNotify(SHCNE_ASSOCCHANGED, SHCNF_IDLIST, IntPtr.Zero, IntPtr.Zero);

            return "安装成功\n" +
                   "位置: " + dir + "\n" +
                   "注册: " + (hklm ? "HKLM（所有用户）" : "HKCU（当前用户）") + "\n\n" +
                   "缩略图可能需重启资源管理器或注销重登后生效。";
        }

        public static string Uninstall(bool hklm)
        {
            var removed = DeleteRegistry(hklm);
            SHChangeNotify(SHCNE_ASSOCCHANGED, SHCNF_IDLIST, IntPtr.Zero, IntPtr.Zero);
            return removed
                ? "已卸载（" + (hklm ? "HKLM" : "HKCU") + "），并还原了原有缩略图提供者。\n建议重启资源管理器。"
                : "未找到本扩展的注册项（" + (hklm ? "HKLM" : "HKCU") + "）。";
        }

        private static string FindShellDir()
        {
            var baseDir = AppContext.BaseDirectory;
            // 发布包结构：exe 同目录 shell\ 子目录（Shell.dll + net461 Core.dll + Numerics）
            var pkg = Path.Combine(baseDir, "shell");
            if (File.Exists(Path.Combine(pkg, "3DThumbnailShell.Shell.dll"))) return pkg;

            // 简单散放：exe 同目录
            if (File.Exists(Path.Combine(baseDir, "3DThumbnailShell.Shell.dll"))) return baseDir;

            // 仓库结构兜底：向上找 sln，定位 Shell\bin\Release
            var dir = new DirectoryInfo(baseDir);
            for (var i = 0; i < 6 && dir != null; i++, dir = dir.Parent)
            {
                if (!File.Exists(Path.Combine(dir.FullName, "3DThumbnailShell.sln"))) continue;
                var p = Path.Combine(dir.FullName, "src", "3DThumbnailShell.Shell", "bin", "Release");
                return File.Exists(Path.Combine(p, "3DThumbnailShell.Shell.dll")) ? p : null;
            }
            return null;
        }

        private static void WriteRegistry(bool hklm, string shellPath)
        {
            var codeBase = new Uri(shellPath).AbsoluteUri;
            var hive = hklm ? RegistryHive.LocalMachine : RegistryHive.CurrentUser;
            using var k = RegistryKey.OpenBaseKey(hive, RegistryView.Default).OpenSubKey(@"Software\Classes", true);

            using (var clsid = k.CreateSubKey($@"CLSID\{{{Clsid}}}"))
            {
                clsid.SetValue(null, "3D 缩略图提供者");
                using (var cat = clsid.CreateSubKey(@"Implemented Categories\" + ThumbProvKey)) { }
                using (var inproc = clsid.CreateSubKey("InprocServer32"))
                {
                    inproc.SetValue(null, "mscoree.dll");
                    inproc.SetValue("Class", ClassFullName);
                    inproc.SetValue("Assembly", AssemblyFullName);
                    inproc.SetValue("CodeBase", codeBase);
                    inproc.SetValue("RuntimeVersion", "v4.0.30319");
                    inproc.SetValue("ThreadingModel", "Both");
                }
            }

            foreach (var ext in Exts)
            {
                using var shex = k.CreateSubKey(ext + @"\ShellEx\" + ThumbProvKey);
                var prev = shex.GetValue(null) as string;
                if (!string.IsNullOrEmpty(prev) &&
                    !string.Equals(prev, "{" + Clsid + "}", StringComparison.OrdinalIgnoreCase))
                    shex.SetValue("PreviousDefault", prev);
                shex.SetValue(null, "{" + Clsid + "}");
            }
        }

        private static bool DeleteRegistry(bool hklm)
        {
            var hive = hklm ? RegistryHive.LocalMachine : RegistryHive.CurrentUser;
            using var k = RegistryKey.OpenBaseKey(hive, RegistryView.Default).OpenSubKey(@"Software\Classes", true);
            if (k == null) return false;

            var any = false;
            foreach (var ext in Exts)
            {
                var sub = ext + @"\ShellEx\" + ThumbProvKey;
                using (var shex = k.OpenSubKey(sub, true))
                {
                    if (shex == null) continue;
                    var v = shex.GetValue(null) as string;
                    if (string.Equals(v, "{" + Clsid + "}", StringComparison.OrdinalIgnoreCase))
                    {
                        any = true;
                        var prev = shex.GetValue("PreviousDefault") as string;
                        if (!string.IsNullOrEmpty(prev))
                        {
                            shex.DeleteValue("PreviousDefault", false);
                            shex.SetValue(null, prev);
                        }
                        else
                        {
                            k.DeleteSubKeyTree(sub, false);
                        }
                    }
                }
            }

            var clsidPath = @"CLSID\{" + Clsid + "}";
            using (var clsid = k.OpenSubKey(clsidPath))
            {
                if (clsid != null) { k.DeleteSubKeyTree(clsidPath, false); any = true; }
            }
            return any;
        }
    }
}
