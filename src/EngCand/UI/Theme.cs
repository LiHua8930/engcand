using System;
using System.Drawing;

namespace EngCand
{
    /// <summary>
    /// 视觉样式常量（候选窗/设置窗共用）。⚠ 由 ui-dev 按需补充使用，公共字段可增不可删。
    /// 设计基调：浅色、圆角、细边框、阴影感，贴近 Windows 11 输入法候选条。
    /// </summary>
    public static class Theme
    {
        public static readonly Color Background = Color.FromArgb(250, 250, 252);
        public static readonly Color Border = Color.FromArgb(214, 216, 222);
        public static readonly Color Text = Color.FromArgb(32, 34, 38);
        public static readonly Color SubText = Color.FromArgb(120, 124, 132);
        public static readonly Color Accent = Color.FromArgb(0, 103, 192);
        public static readonly Color SelectedBackground = Color.FromArgb(232, 240, 250);
        public static readonly Color PhraseBadge = Color.FromArgb(240, 244, 250);

        // ---- ui-dev 补充：候选条细节（只增不改） ----

        /// <summary>界面字体族（中文用微软雅黑 UI）。</summary>
        public const string FontFamily = "Microsoft YaHei UI";

        /// <summary>候选条外圆角半径（96 DPI 逻辑像素）。</summary>
        public const int CornerRadius = 10;

        /// <summary>候选条宽度上限（96 DPI 逻辑像素）。</summary>
        public const int MaxPopupWidth = 520;

        /// <summary>鼠标悬停候选时的底色（比 SelectedBackground 更浅一点）。</summary>
        public static readonly Color HoverBackground = Color.FromArgb(236, 242, 250);

        /// <summary>候选序号颜色。</summary>
        public static readonly Color NumberText = Color.FromArgb(126, 132, 142);

        /// <summary>"短语"小标签的文字颜色。</summary>
        public static readonly Color BadgeText = Color.FromArgb(102, 110, 126);

        /// <summary>"短语"小标签的描边颜色。</summary>
        public static readonly Color BadgeBorder = Color.FromArgb(219, 225, 235);

        public static Font DefaultFont(float size = 10f)
        {
            return new Font("Microsoft YaHei UI", size, FontStyle.Regular, GraphicsUnit.Point);
        }

        public static Font NumberFont(float size = 9f)
        {
            return new Font("Segoe UI", size, FontStyle.Bold, GraphicsUnit.Point);
        }

        /// <summary>托盘菜单 / 设置窗口用的界面字体。</summary>
        public static Font UiFont(float size = 9f)
        {
            return new Font(FontFamily, size, FontStyle.Regular, GraphicsUnit.Point);
        }
    }
}
