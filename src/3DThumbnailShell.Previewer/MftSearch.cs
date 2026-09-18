using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;

namespace _3DThumbnailShell.Previewer
{
    /// <summary>
    /// Everything 式 NTFS 快速搜索：直接读取卷的主文件表（MFT），跳过目录递归扫描。
    /// 原理：打开卷句柄（"\\\\.\X:"）→ FSCTL_GET_NTFS_VOLUME_DATA 拿到 MFT 起始/长度 →
    /// 对齐读 MFT → 解析 "FILE" 记录（fixup 更新序列还原 + $FILE_NAME 属性父引用/文件名）→
    /// 沿父链重建完整路径 → 按 范围前缀 + 扩展名 + 关键字 过滤。
    /// 需要管理员权限（读卷句柄）；任何一步失败返回 null，由调用方回退普通递归扫描。
    /// MFT 索引按卷缓存（静态），首次构建稍慢（约几秒），后续搜索秒回。
    /// </summary>
    public static class MftSearcher
    {
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern IntPtr CreateFileW(string lpFileName, uint dwDesiredAccess,
            uint dwShareMode, IntPtr lpSecurityAttributes, uint dwCreationDisposition,
            uint dwFlagsAndAttributes, IntPtr hTemplateFile);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool DeviceIoControl(IntPtr hDevice, uint dwIoControlCode,
            IntPtr lpInBuffer, uint nInBufferSize, IntPtr lpOutBuffer, uint nOutBufferSize,
            out uint lpBytesReturned, IntPtr lpOverlapped);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool SetFilePointerEx(IntPtr hFile, long liDistanceToMove,
            IntPtr lpNewFilePointer, uint dwMoveMethod);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool ReadFile(IntPtr hFile, byte[] lpBuffer, uint nNumberOfBytesToRead,
            out uint lpNumberOfBytesRead, IntPtr lpOverlapped);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool CloseHandle(IntPtr hObject);

        private const uint GENERIC_READ = 0x80000000;
        private const uint FILE_SHARE_READ = 0x1;
        private const uint FILE_SHARE_WRITE = 0x2;
        private const uint OPEN_EXISTING = 3;
        private const uint FILE_FLAG_BACKUP_SEMANTICS = 0x02000000;
        private const uint FSCTL_GET_NTFS_VOLUME_DATA = 0x00090064;

        private static readonly object CacheLock = new object();
        private static string _cachedVolume;                       // 形如 "C:\"
        private static Dictionary<long, MftEntry> _cache;
        private static readonly long RootDirRef = 5;               // NTFS 卷根 $Root 的 MFT 记录号

        private struct MftEntry
        {
            public long Parent;   // 父目录 MFT 引用（低 48 位）
            public string Name;   // 文件名（不含路径）
        }

        /// <summary>
        /// 在 rootDir 范围内按 扩展名 + 关键字 搜索，返回匹配文件完整路径列表。
        /// MFT 不可用（无管理员权限/非 NTFS/构建失败）返回 null → 调用方回退普通扫描。
        /// </summary>
        public static List<string> Search(string rootDir, string keyword, string[] exts)
        {
            try
            {
                var vol = Path.GetPathRoot(rootDir);
                if (string.IsNullOrEmpty(vol) || vol.Length < 2 || vol[1] != ':') return null;
                vol = vol.TrimEnd('\\') + "\\";

                var index = GetMftIndex(vol);
                if (index == null) return null;

                var rootNorm = NormalizeRoot(rootDir);
                var extSet = exts == null || exts.Length == 0
                    ? null : new HashSet<string>(exts, StringComparer.OrdinalIgnoreCase);
                var kw = string.IsNullOrEmpty(keyword) ? null : keyword.Trim().ToLowerInvariant();

                var result = new List<string>();
                var sb = new StringBuilder(256);
                foreach (var kv in index)
                {
                    if (kv.Value.Name == null) continue;
                    sb.Clear();
                    if (!BuildPath(kv.Key, kv.Value, index, sb)) continue;
                    var path = vol + sb.ToString();
                    if (!UnderRoot(path, rootNorm)) continue;
                    if (extSet != null && !extSet.Contains(Path.GetExtension(path))) continue;
                    if (kw != null && !path.ToLowerInvariant().Contains(kw)) continue;
                    result.Add(path);
                }
                return result;
            }
            catch
            {
                return null;
            }
        }

        /// <summary>是否为该目录子树内（避免 "C:\Users\foobar" 误配 "C:\Users\foo"）。</summary>
        private static bool UnderRoot(string path, string rootNorm)
        {
            if (rootNorm.Length == 0) return true;
            if (!path.StartsWith(rootNorm, StringComparison.OrdinalIgnoreCase)) return false;
            return path.Length == rootNorm.Length || path[rootNorm.Length] == '\\';
        }

        private static string NormalizeRoot(string rootDir)
        {
            var r = rootDir.TrimEnd('\\');
            return r.Length == 2 ? "" : r; // "C:\" 卷根 → 空（匹配所有）
        }

        /// <summary>沿父引用链把 (idx, entry) 重建为相对卷根的路径（sb 输出，返回是否成功）。</summary>
        private static bool BuildPath(long idx, MftEntry e, Dictionary<long, MftEntry> index, StringBuilder sb)
        {
            var parts = new List<string>(8);
            parts.Add(e.Name);
            var cur = e.Parent;
            var guard = 0;
            var seen = new HashSet<long>();
            while (cur != RootDirRef && cur != 0 && guard++ < 64)
            {
                if (!seen.Add(cur)) return false;                       // 环/损坏引用
                if (!index.TryGetValue(cur, out var pe) || pe.Name == null) return false;
                parts.Add(pe.Name);
                cur = pe.Parent;
            }
            for (var i = parts.Count - 1; i >= 0; i--)
            {
                if (i < parts.Count - 1) sb.Append('\\');
                sb.Append(parts[i]);
            }
            return true;
        }

        /// <summary>获取卷的 MFT 索引（缓存；失败返回 null）。</summary>
        private static Dictionary<long, MftEntry> GetMftIndex(string vol)
        {
            lock (CacheLock)
            {
                if (_cache != null && _cachedVolume == vol) return _cache;
                try
                {
                    _cache = BuildIndex(vol);
                    _cachedVolume = _cache != null ? vol : null;
                }
                catch
                {
                    _cache = null;
                    _cachedVolume = null;
                }
                return _cache;
            }
        }

        private static Dictionary<long, MftEntry> BuildIndex(string vol)
        {
            var h = CreateFileW(vol, GENERIC_READ, FILE_SHARE_READ | FILE_SHARE_WRITE,
                IntPtr.Zero, OPEN_EXISTING, FILE_FLAG_BACKUP_SEMANTICS, IntPtr.Zero);
            if (h == new IntPtr(-1) || h == IntPtr.Zero)
                throw new InvalidOperationException("打开卷句柄失败（需要管理员权限）");

            try
            {
                var dataBuf = Marshal.AllocHGlobal(96);
                try
                {
                    if (!DeviceIoControl(h, FSCTL_GET_NTFS_VOLUME_DATA, IntPtr.Zero, 0,
                            dataBuf, 96, out _, IntPtr.Zero))
                        throw new InvalidOperationException("FSCTL_GET_NTFS_VOLUME_DATA 失败");

                    var bytesPerSector = Marshal.ReadInt32(dataBuf, 40);
                    var bytesPerCluster = Marshal.ReadInt32(dataBuf, 44);
                    var bytesPerRecord = Marshal.ReadInt32(dataBuf, 48);
                    var mftValidLength = Marshal.ReadInt64(dataBuf, 56);
                    var mftStartLcn = Marshal.ReadInt64(dataBuf, 64);
                    if (bytesPerRecord <= 0) bytesPerRecord = 1024;
                    if (bytesPerSector <= 0) bytesPerSector = 512;

                    var mftOffset = mftStartLcn * bytesPerCluster;
                    if (mftValidLength <= 0) throw new InvalidOperationException("MFT 有效长度为零");

                    if (!SetFilePointerEx(h, mftOffset, IntPtr.Zero, 0))
                        throw new InvalidOperationException("定位 MFT 失败");

                    var index = new Dictionary<long, MftEntry>(1024 * 1024);
                    var rec = new byte[bytesPerRecord];
                    long processed = 0;
                    while (processed + bytesPerRecord <= mftValidLength)
                    {
                        if (!ReadFile(h, rec, (uint)bytesPerRecord, out var rd, IntPtr.Zero) || rd == 0)
                            break;
                        processed += bytesPerRecord;
                        if (rd < 64) continue;
                        // "FILE" 签名；0x100 记录为索引/稀疏记录，跳过
                        if (rec[0] != (byte)'F' || rec[1] != (byte)'I' || rec[2] != (byte)'L' || rec[3] != (byte)'E')
                            continue;
                        ParseRecord(rec, bytesPerRecord, bytesPerSector,
                            processed / bytesPerRecord, index);
                    }
                    return index.Count > 0 ? index : null;
                }
                finally
                {
                    Marshal.FreeHGlobal(dataBuf);
                }
            }
            finally
            {
                CloseHandle(h);
            }
        }

        /// <summary>解析一条 MFT 文件记录：fixup 还原 + 取 $FILE_NAME 属性的父引用与文件名。</summary>
        private static void ParseRecord(byte[] rec, int recSize, int sectorSize,
            long recordNumber, Dictionary<long, MftEntry> index)
        {
            // Fixup（更新序列号）：磁盘上每个扇区末 2 字节被 USN 覆盖，
            // 原始数据存于 fixup 数组（offset 0x2A 处，首 2 字节 = USN，之后每 2 字节 = 每扇区末 2 字节）
            var usOffset = BitConverter.ToUInt16(rec, 0x2A);
            var usCount = BitConverter.ToUInt16(rec, 0x2C);
            if (usCount >= 2 && usOffset + usCount * 2 <= recSize)
            {
                var sectorCount = recSize / sectorSize;
                var n = Math.Min(usCount - 1, sectorCount - 1);
                for (var i = 1; i <= n; i++)
                {
                    var fixup = BitConverter.ToUInt16(rec, usOffset + i * 2);
                    var pos = i * sectorSize - 2;
                    rec[pos] = (byte)(fixup & 0xFF);
                    rec[pos + 1] = (byte)(fixup >> 8);
                }
            }

            // 遍历属性：从记录头 0x38 的属性列表偏移开始
            var attrOffset = BitConverter.ToUInt16(rec, 0x38);
            var guard = 0;
            while (attrOffset >= 0x38 && attrOffset + 8 <= recSize && guard++ < 32)
            {
                var type = BitConverter.ToUInt32(rec, attrOffset);
                var len = BitConverter.ToUInt32(rec, attrOffset + 4);
                if (type == 0xFFFFFFFF || len < 8) break;   // END 标记
                if (attrOffset + len > recSize) break;

                if (type == 0x30) // $FILE_NAME
                {
                    if (rec[attrOffset + 8] == 0)           // Resident
                    {
                        var valueLen = BitConverter.ToUInt32(rec, attrOffset + 0x10);
                        var valueOff = BitConverter.ToUInt16(rec, attrOffset + 0x14);
                        var vs = attrOffset + valueOff;
                        if (vs + 0x42 <= recSize && valueLen >= 0x42)
                        {
                            var parent = BitConverter.ToInt64(rec, vs) & 0x0000FFFFFFFFFFFFL;
                            var nameLen = rec[vs + 0x40];
                            var ns = rec[vs + 0x41];        // 0=POSIX 1=Win32 2=DOS 3=Win32AndDOS
                            if (vs + 0x42 + nameLen * 2 <= recSize)
                            {
                                // 已有非 DOS 名则保留（短名/截断名优先级最低）
                                var name = Encoding.Unicode.GetString(rec, vs + 0x42, nameLen * 2);
                                if (!index.TryGetValue(recordNumber, out var existing) || ns != 2)
                                    index[recordNumber] = new MftEntry { Parent = parent, Name = name };
                            }
                        }
                    }
                    break; // 一个记录最多一个 $FILE_NAME 就够
                }
                attrOffset = (ushort)(attrOffset + len);
            }
        }
    }
}
