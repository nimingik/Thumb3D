# Thumb3D（三维缩略图助手）

一个 3D 模型查看与缩略图生成工具：提供 3D 交互预览，并为 Windows 资源管理器生成 3D 文件的缩略图。

## 功能特性

- **3D 交互预览**：旋转 / 缩放 / 平移，多视角一键切换（Gizmo）
- **多格式解析**：支持 STL / 3MF / OBJ / PLY / OFF / AMF / GLB / GLTF / GCODE / STEP 等（目前主要支持3mf与stl）
- **3MF 多盘工程**：可在各盘之间切换或查看合并视图
- **负零件 / 修改器识别**：Bambu/Orca 工程的 negative_part 与 modifier_part 会独立分层显示，可分别调颜色与透明度
- **两种渲染后端**：GPU（Direct3D 11）/ CPU（纯软件光栅化），CPU 后端无需显卡即可运行
- **透明底缩略图**：所见即所得保存，画幅比例可调（1:1 / 4:3 / 16:9）
- **资源管理器缩略图扩展**：安装后直接在资源管理器显示 3D 文件缩略图
- **双主题**：亮色（档案暖白）/ 暗色，实时预览

## 项目结构

```
src/
├── 3DThumbnailShell.Core/        # 核心：格式解析 + 软件光栅化渲染器（netstandard2.0 / net461）
├── 3DThumbnailShell.Previewer/   # 预览器主程序（WinForms，.NET 8 + Direct3D 11）
└── 3DThumbnailShell.Shell/       # 资源管理器缩略图 COM 扩展（.NET Framework 4.8）
```

## 构建

需要 [.NET SDK 8.0](https://dotnet.microsoft.com/download/dotnet/8.0)。

```bash
# 发布预览器（含 Core）
dotnet publish src/3DThumbnailShell.Previewer -c Release -o dist

# 发布缩略图扩展（.NET Framework 4.8）
dotnet build src/3DThumbnailShell.Shell -c Release
```

主要依赖：`Vortice.Direct3D11 3.8.3`（GPU 后端）、`System.Numerics.Vectors 4.5.0`、`Microsoft.NETFramework.ReferenceAssemblies 1.0.3`。

## 使用

运行 `3DThumbnailShell.Previewer.exe`：
- 打开文件，或直接把文件拖进窗口
- 左键拖动旋转、滚轮缩放、右键/中键平移、左键双击复位
- 首次安装"缩略图扩展"后，资源管理器即显示 3D 文件缩略图


## 作者

- 制作者：匿名IK
- Bilibili：<https://space.bilibili.com/41807397>
- GitHub：<https://github.com/nimingik>

## 许可证

[GPL-3.0](LICENSE)
