namespace _3DThumbnailShell.Previewer
{
    /// <summary>相机参数快照（与 SoftwareRenderer 的轨道相机完全对应）。</summary>
    public readonly struct CameraParams
    {
        public readonly float Az;      // 方位角
        public readonly float El;      // 仰角
        public readonly float Dist;    // 相机距离
        public readonly float PanX;    // 屏幕像素平移 X
        public readonly float PanY;    // 屏幕像素平移 Y
        public readonly float Zoom;    // 视图缩放（围绕画面中心）

        public CameraParams(float az, float el, float dist, float panX, float panY, float zoom)
        {
            Az = az; El = el; Dist = dist; PanX = panX; PanY = panY; Zoom = zoom;
        }
    }
}
