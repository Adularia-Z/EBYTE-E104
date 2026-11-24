// ui_theme.cs — 自绘现代化深色 UI 控件库 (C#5 / .NET Framework 4.8)
using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Text;
using System.Windows.Forms;

namespace E104
{
    // ---------------- 调色板 (VS Code / Fluent 风格) ----------------
    public static class Theme
    {
        public static readonly Color Bg       = Color.FromArgb(24, 24, 27);
        public static readonly Color Panel    = Color.FromArgb(30, 30, 34);
        public static readonly Color Nav      = Color.FromArgb(32, 32, 36);
        public static readonly Color Card     = Color.FromArgb(37, 37, 41);
        public static readonly Color Border   = Color.FromArgb(60, 60, 66);
        public static readonly Color Hover    = Color.FromArgb(52, 52, 58);
        public static readonly Color Press    = Color.FromArgb(22, 22, 25);
        public static readonly Color Accent   = Color.FromArgb(0, 122, 204);
        public static readonly Color AccentHi = Color.FromArgb(28, 151, 234);
        public static readonly Color TxT      = Color.FromArgb(220, 220, 224);
        public static readonly Color Dim      = Color.FromArgb(140, 140, 146);
        public static readonly Color Good     = Color.FromArgb(76, 175, 80);
        public static readonly Color Bad      = Color.FromArgb(235, 87, 73);
        public static readonly Color Warn     = Color.FromArgb(232, 162, 54);
        public static readonly Color TxCol    = Color.FromArgb(86, 156, 214);
        public static readonly Color RxCol    = Color.FromArgb(106, 170, 100);
        public static readonly Color EvCol    = Color.FromArgb(214, 158, 46);
        public static readonly Color SysCol   = Color.FromArgb(126, 126, 130);

        public static readonly Font F      = new Font("Segoe UI", 9F);
        public static readonly Font FSub   = new Font("Segoe UI", 8.25F);
        public static readonly Font FTitle = new Font("Segoe UI", 12F, FontStyle.Bold);
        public static readonly Font FHead  = new Font("Segoe UI", 9.75F, FontStyle.Bold);
        public static readonly Font FMono  = new Font("Consolas", 9F);
        public static readonly Font FMonoS = new Font("Consolas", 8.5F);

        public static void StyleText(TextBox tb)
        {
            tb.BackColor = Color.FromArgb(24, 24, 27);
            tb.ForeColor = TxT;
            tb.BorderStyle = BorderStyle.FixedSingle;
            tb.Font = F;
            tb.Height = 26;
        }
        public static void StyleCombo(ComboBox cb)
        {
            cb.BackColor = Color.FromArgb(24, 24, 27);
            cb.ForeColor = TxT;
            cb.FlatStyle = FlatStyle.Flat;
            cb.Font = F;
            cb.DropDownStyle = ComboBoxStyle.DropDownList;
        }
        public static Label Lbl(string text, bool dim)
        {
            Label l = new Label();
            l.Text = text;
            l.ForeColor = dim ? Dim : TxT;
            l.Font = F;
            l.AutoSize = true;
            return l;
        }
        public static Label Hint(string text)
        {
            Label l = new Label();
            l.Text = "· " + text;
            l.ForeColor = Color.FromArgb(160, 140, 90);
            l.Font = FSub;
            l.AutoSize = true;
            l.MaximumSize = new Size(680, 0);
            return l;
        }
        public static Label Cur()
        {
            Label l = new Label();
            l.Text = "-";
            l.ForeColor = AccentHi;
            l.Font = F;
            l.AutoSize = true;
            return l;
        }
    }

    // ---------------- 自绘按钮 ----------------
    public enum BtnKind { Normal, Accent, Danger, Ghost, Good }

    public class FlatBtn : Control
    {
        public BtnKind Kind = BtnKind.Normal;
        private bool _hover = false, _down = false;

        public FlatBtn(string text, BtnKind kind)
        {
            SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint |
                     ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
            Text = text; Kind = kind;
            Height = 30; Cursor = Cursors.Hand; Font = Theme.F;
        }

        /// <summary>
        /// 绘制层字符净化:GDI+ DrawString 不做字体回退,emoji(代理对)与杂项符号
        /// (0x2600-0x2BFF)在 Segoe UI 中缺字会渲染成“方块”。
        /// 这里把这类字符从绘制文本中剔除,按钮保留干净文字标签。
        /// (导航 NavBtn 用 Segoe UI Symbol 专门绘制,不受影响;标准 Label 走 GDI 自带回退,同样不受影响)
        /// </summary>
        public static string SanitizeText(string s)
        {
            if (string.IsNullOrEmpty(s)) return s;
            StringBuilder sb = new StringBuilder(s.Length);
            for (int i = 0; i < s.Length; i++)
            {
                char c = s[i];
                if (char.IsSurrogate(c)) { if (i + 1 < s.Length) i++; continue; }   // 跳过代理对(emoji)
                int code = (int)c;
                if (code >= 0x2600 && code <= 0x2BFF) continue;                      // 杂项符号区(⚡☠⟳⏸▶…)
                if (code == 0xFE0F || code == 0x200D) continue;                      // 变体选择符/零宽连接符
                sb.Append(c);
            }
            string r = sb.ToString();
            return r.Trim();
        }

        protected override void OnMouseEnter(EventArgs e) { _hover = true; Invalidate(); base.OnMouseEnter(e); }
        protected override void OnMouseLeave(EventArgs e) { _hover = false; _down = false; Invalidate(); base.OnMouseLeave(e); }
        protected override void OnMouseDown(MouseEventArgs e) { _down = true; Invalidate(); base.OnMouseDown(e); }
        protected override void OnMouseUp(MouseEventArgs e) { _down = false; Invalidate(); base.OnMouseUp(e); }

        protected override void OnPaint(PaintEventArgs e)
        {
            Graphics g = e.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            Color bg = Theme.Card, fg = Theme.TxT, border = Theme.Border;
            if (Kind == BtnKind.Accent) { bg = _down ? Color.FromArgb(0, 100, 180) : (_hover ? Theme.AccentHi : Theme.Accent); fg = Color.White; border = bg; }
            else if (Kind == BtnKind.Danger) { bg = _down ? Color.FromArgb(120, 30, 26) : (_hover ? Color.FromArgb(80, 24, 20) : Color.FromArgb(56, 22, 19)); fg = Color.FromArgb(240, 128, 116); border = Color.FromArgb(96, 40, 34); }
            else if (Kind == BtnKind.Ghost)
            {
                // 次级按钮:仍有实体背景与细边框(仅比普通按钮更低调),不再完全透明
                bg = _down ? Color.FromArgb(30, 30, 34) : (_hover ? Color.FromArgb(54, 54, 60) : Color.FromArgb(44, 44, 49));
                border = _hover ? Color.FromArgb(90, 90, 98) : Color.FromArgb(64, 64, 70);
            }
            else { bg = _down ? Theme.Press : (_hover ? Theme.Hover : Theme.Card); }
            if (!Enabled) { bg = Color.FromArgb(40, 40, 44); fg = Color.FromArgb(96, 96, 100); border = Color.FromArgb(52, 52, 56); }

            using (SolidBrush b = new SolidBrush(bg)) g.FillRectangle(b, ClientRectangle);
            using (Pen p = new Pen(border)) g.DrawRectangle(p, 0, 0, Width - 1, Height - 1);

            string shown = SanitizeText(Text);
            // 文字宽度超过控件宽度时自动缩小字号,保证不截断
            Font drawFont = Font;
            SizeF sz = g.MeasureString(shown, drawFont);
            if (sz.Width > Width - 8 && shown.Length > 0)
            {
                float scale = (Width - 8f) / sz.Width;
                if (scale < 0.92f)
                {
                    float newSize = Math.Max(6.5f, Font.Size * scale);
                    drawFont = new Font(Font.FontFamily, newSize, Font.Style);
                    sz = g.MeasureString(shown, drawFont);
                }
            }
            using (SolidBrush tb = new SolidBrush(fg))
                g.DrawString(shown, drawFont, tb, Math.Max(2, (Width - sz.Width) / 2f), (Height - sz.Height) / 2f);
            if (!ReferenceEquals(drawFont, Font)) drawFont.Dispose();
        }
    }

    // ---------------- 左侧导航按钮 ----------------
    public class NavBtn : Control
    {
        public string Glyph = "■";
        private bool _hover = false;
        private bool _active = false;

        public NavBtn(string text, string glyph)
        {
            SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint |
                     ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
            Text = text; Glyph = glyph;
            Height = 38; Cursor = Cursors.Hand; Font = Theme.F;
        }

        public bool Active
        {
            get { return _active; }
            set { _active = value; Invalidate(); }
        }

        protected override void OnMouseEnter(EventArgs e) { _hover = true; Invalidate(); base.OnMouseEnter(e); }
        protected override void OnMouseLeave(EventArgs e) { _hover = false; Invalidate(); base.OnMouseLeave(e); }

        protected override void OnPaint(PaintEventArgs e)
        {
            Graphics g = e.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            bool en = Enabled;
            if (!en)
            {
                using (SolidBrush b = new SolidBrush(Color.FromArgb(24, 24, 27))) { g.FillRectangle(b, ClientRectangle); }
                using (SolidBrush b = new SolidBrush(Color.FromArgb(110, 110, 115)))
                { g.DrawString(Glyph, new Font("Segoe UI Symbol", 10F), b, 14, (Height - 20) / 2f); g.DrawString(Text, Theme.F, b, 40, (Height - 16) / 2f); }
                return;
            }
            if (_active) { using (SolidBrush b = new SolidBrush(Theme.Hover)) g.FillRectangle(b, ClientRectangle); }
            else if (_hover) { using (SolidBrush b = new SolidBrush(Color.FromArgb(44, 44, 50))) g.FillRectangle(b, ClientRectangle); }
            if (_active)
                using (SolidBrush b = new SolidBrush(Theme.AccentHi)) g.FillRectangle(b, 0, 4, 3, Height - 8);
            Color gc = _active ? Theme.AccentHi : Theme.Dim;
            using (SolidBrush b = new SolidBrush(gc))
                g.DrawString(Glyph, new Font("Segoe UI Symbol", 10F), b, 14, (Height - 20) / 2f);
            using (SolidBrush b = new SolidBrush(_active ? Theme.TxT : Color.FromArgb(190, 190, 196)))
                g.DrawString(Text, _active ? Theme.FHead : Theme.F, b, 40, (Height - 16) / 2f);
        }
    }

    // ---------------- 卡片容器 ----------------
    public class Card : Panel
    {
        public string Title = "";
        public string Sub = "";
        public Panel Body;

        public Card(string title, string sub)
        {
            SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint |
                     ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
            Title = title; Sub = sub;
            BackColor = Theme.Card;
            Dock = DockStyle.Top;
            Padding = new Padding(1);
            Body = new Panel();
            Body.BackColor = Theme.Card;
            Body.Dock = DockStyle.Fill;
            Body.Padding = new Padding(0);
            Body.AutoScroll = true;      // 内容超出卡片高度时出现滚动条,避免溢出到下一张卡片造成"重叠"
            Controls.Add(Body);
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            Graphics g = e.Graphics;
            using (Pen p = new Pen(Theme.Border)) g.DrawRectangle(p, 0, 0, Width - 1, Height - 1);
            using (SolidBrush b = new SolidBrush(Theme.TxT)) g.DrawString(Title, Theme.FHead, b, 14, 10);
            if (!string.IsNullOrEmpty(Sub))
            {
                SizeF ts = e.Graphics.MeasureString(Title, Theme.FHead);
                using (SolidBrush sb = new SolidBrush(Theme.Dim)) g.DrawString(Sub, Theme.FSub, sb, 14 + ts.Width + 8, 13);
            }
        }
    }

    // ---------------- 状态胶囊 ----------------
    public class Chip : Control
    {
        private Color _col = Theme.Dim;
        private Color _txt = Theme.TxT;

        public Chip()
        {
            SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint |
                     ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
            Height = 24; Width = 120; Font = Theme.FSub;
        }
        public void Set(string text, Color dot, Color txt) { Text = text; _col = dot; _txt = txt; Invalidate(); }

        protected override void OnPaint(PaintEventArgs e)
        {
            Graphics g = e.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            using (SolidBrush b = new SolidBrush(Color.FromArgb(44, 44, 50)))
                g.FillRectangle(b, ClientRectangle);
            using (Pen p = new Pen(Theme.Border)) g.DrawRectangle(p, 0, 0, Width - 1, Height - 1);
            g.FillEllipse(new SolidBrush(_col), 8, Height / 2 - 4, 8, 8);
            SizeF ts = g.MeasureString(Text, Font);
            using (SolidBrush tb = new SolidBrush(_txt))
                g.DrawString(Text, Font, tb, 22, (Height - ts.Height) / 2f);
        }
    }

    // ---------------- 复选 (深色自绘:勾选为蓝色底+白色对勾,一目了然) ----------------
    public class DarkCheck : CheckBox
    {
        private bool _hover = false;

        public DarkCheck()
        {
            SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint |
                     ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
            ForeColor = Theme.TxT;
            BackColor = Color.Transparent;
            Font = Theme.F;
            AutoSize = false;
            Height = 24;
            Width = 110;
            Cursor = Cursors.Hand;
        }

        public void FitWidth()
        {
            // 中文按字体实际渲染宽度测量,NoPadding 去掉额外留白
            int tw = TextRenderer.MeasureText(Text, Font, Size.Empty,
                       TextFormatFlags.NoPadding | TextFormatFlags.SingleLine).Width;
            Width = 20 + tw + 10;
            Height = Math.Max(22, Height);
        }

        protected override void OnMouseEnter(EventArgs e) { _hover = true; Invalidate(); base.OnMouseEnter(e); }
        protected override void OnMouseLeave(EventArgs e) { _hover = false; Invalidate(); base.OnMouseLeave(e); }
        protected override void OnCheckedChanged(EventArgs e) { Invalidate(); base.OnCheckedChanged(e); }

        protected override void OnPaint(PaintEventArgs e)
        {
            Graphics g = e.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            // 铺底(盖掉基类默认外观,避免文字重影)
            using (SolidBrush bb = new SolidBrush(Color.FromArgb(30, 30, 34)))
                g.FillRectangle(bb, ClientRectangle);

            const int s = 13;
            int cy = Math.Max(2, (Height - s) / 2);
            int cx = 2;
            Rectangle box = new Rectangle(cx, cy, s, s);

            if (Checked)
            {
                using (SolidBrush b = new SolidBrush(Enabled ? Theme.Accent : Color.FromArgb(70, 90, 110)))
                    g.FillRectangle(b, box);
                using (Pen p = new Pen(Enabled ? Theme.AccentHi : Color.FromArgb(90, 90, 96)))
                    g.DrawRectangle(p, box);
                using (Pen ck = new Pen(Color.White, 1.9f))
                {
                    ck.StartCap = LineCap.Round; ck.EndCap = LineCap.Round;
                    g.DrawLines(ck, new Point[]
                    { new Point(cx + 2, cy + 7), new Point(cx + 5, cy + 10), new Point(cx + 11, cy + 3) });
                }
            }
            else
            {
                using (SolidBrush b = new SolidBrush(Color.FromArgb(26, 26, 30)))
                    g.FillRectangle(b, box);
                Color bd = _hover ? Color.FromArgb(150, 150, 158) : Color.FromArgb(96, 96, 104);
                using (Pen p = new Pen(bd, 1.4f)) g.DrawRectangle(p, box);
            }

            Color fg = !Enabled ? Color.FromArgb(110, 110, 116)
                       : (Checked ? Color.White : Color.FromArgb(186, 186, 194));
            TextRenderer.DrawText(g, Text, Font,
                new Rectangle(cx + s + 6, 0, Math.Max(10, Width - (s + 10)), Height),
                fg, TextFormatFlags.Left | TextFormatFlags.VerticalCenter |
                    TextFormatFlags.NoPadding | TextFormatFlags.SingleLine | TextFormatFlags.EndEllipsis);
        }
    }

    // ---------------- 对齐网格行(TableLayoutPanel,缩放不重叠 + 同类元素列对齐) ----------------
    /// <summary>
    /// 参数网格:每行一组 [标签列][控件列][当前值列(可增)][按钮列...]
    /// 列宽按 Percent 分配,窗口缩放时等比伸缩,永不重叠;同类元素天然上下对齐。
    /// </summary>
    public class GridRows : TableLayoutPanel
    {
        public GridRows(int cols, float[] percents)
        {
            ColumnCount = cols;
            RowCount = 0;
            AutoSize = true;
            AutoSizeMode = AutoSizeMode.GrowAndShrink;
            Dock = DockStyle.Top;
            Margin = new Padding(0);
            Padding = new Padding(0, 2, 0, 2);
            BackColor = Color.Transparent;
            for (int i = 0; i < cols; i++)
            {
                float pct = (percents != null && percents.Length > i) ? percents[i] : 100f / cols;
                ColumnStyles.Add(new ColumnStyle(SizeType.Percent, pct));
            }
        }

        /// <summary>按列号精确放置一个控件(避免 null 占位导致索引错位)</summary>
        public void PutCell(Control c, int col, int row)
        {
            if (c == null) return;
            while (RowCount <= row)
            {
                RowStyles.Add(new RowStyle(SizeType.AutoSize));
                RowCount = RowCount + 1;
            }
            Label lb = c as Label;
            if (lb != null) { lb.AutoSize = true; c.Anchor = AnchorStyles.Left | AnchorStyles.Right; }
            c.Margin = new Padding(col == 0 ? 8 : 4, 3, 6, 3);
            Controls.Add(c, col, row);
        }

        /// <summary>跨行/跨列放置控件(用于选项块占两行等场景)</summary>
        public void PutCellSpan(Control c, int col, int row, int colSpan, int rowSpan)
        {
            if (c == null) return;
            while (RowCount < row + rowSpan)
            {
                RowStyles.Add(new RowStyle(SizeType.AutoSize));
                RowCount = RowCount + 1;
            }
            Label lb = c as Label;
            if (lb != null) lb.AutoSize = true;
            // 跨列块(如 2x2 选项)用 Fill 撑满单元格,获得真实宽度 → 子项可据此自适应
            if (colSpan > 1) c.Anchor = AnchorStyles.Left | AnchorStyles.Right | AnchorStyles.Top;
            else c.Anchor = lb != null ? (AnchorStyles.Left | AnchorStyles.Right) : (AnchorStyles.Left | AnchorStyles.Top);
            c.Margin = new Padding(col == 0 ? 8 : 4, 3, 6, 3);
            Controls.Add(c, col, row);
            if (colSpan > 1) SetColumnSpan(c, colSpan);
            if (rowSpan > 1) SetRowSpan(c, rowSpan);
        }

        /// <summary>确保有 n 行(配合 PutCell 使用)</summary>
        public void EnsureRows(int n)
        {
            while (RowCount < n)
            {
                RowStyles.Add(new RowStyle(SizeType.AutoSize));
                RowCount = RowCount + 1;
            }
        }

        /// <summary>在当前行依次放入(数量不超过列数);返回该行索引</summary>
        public int PutRow(params Control[] cells)
        {
            int r = RowCount;
            RowStyles.Add(new RowStyle(SizeType.AutoSize));
            RowCount = r + 1;
            for (int i = 0; i < cells.Length && i < ColumnCount; i++)
            {
                Control c = cells[i];
                if (c == null) continue;
                Label lb = c as Label;
                if (lb != null)
                {
                    lb.AutoSize = true;
                    c.Anchor = AnchorStyles.Left | AnchorStyles.Right;
                }
                // 按钮等固定尺寸控件保留其既有锚点(右对齐),不被拉伸变形
                c.Margin = new Padding(i == 0 ? 8 : 4, 3, 6, 3);
                Controls.Add(c, i, r);
            }
            // 行高由内容决定
            return r;
        }

        public Label Label(string text, bool dim, string tip)
        {
            Label l = new Label();
            l.Text = text;
            l.ForeColor = dim ? Theme.Dim : Theme.TxT;
            l.Font = Theme.F;
            l.AutoSize = true;
            l.TextAlign = ContentAlignment.MiddleLeft;
            if (!string.IsNullOrEmpty(tip)) DarkTips.Set(l, tip);
            return l;
        }
    }

    // ---------------- 自适应参数行(FlowLayoutPanel,替代手写绝对坐标) ----------------
    /// <summary>
    /// 一行自适应控件:内部用 FlowLayoutPanel 从左到右排列,
    /// 控件不重叠、宽度随字体自适应,窗口变宽也不会错位。
    /// </summary>
    public class FlowRow : FlowLayoutPanel
    {
        public FlowRow()
        {
            FlowDirection = FlowDirection.LeftToRight;
            WrapContents = true;                 // 窗口变窄时自动换行,绝不把按钮挤出可视区
            AutoSize = true;
            AutoSizeMode = AutoSizeMode.GrowAndShrink;
            Margin = new Padding(0, 0, 0, 4);
            Padding = new Padding(0);
            BackColor = Color.Transparent;
        }

        public Label AddLabel(string text, bool dim, string tip, string key)
        {
            Label l = new Label();
            l.Text = text;
            l.ForeColor = dim ? Theme.Dim : Theme.TxT;
            l.Font = Theme.F;
            l.AutoSize = true;
            l.Margin = new Padding(0, 6, 6, 0);
            if (!string.IsNullOrEmpty(tip)) DarkTips.Set(l, tip);
            Controls.Add(l);
            return l;
        }

        /// <summary>固定宽度的参数名(整列对齐用),其余用 AddLabel 自适应</summary>
        public Label AddFixedLabel(string text, int width, bool dim)
        {
            Label l = new Label();
            l.Text = text;
            l.ForeColor = dim ? Theme.Dim : Theme.TxT;
            l.Font = Theme.F;
            l.AutoSize = false;
            l.Width = width;
            l.Height = 20;
            l.TextAlign = ContentAlignment.MiddleLeft;
            l.AutoEllipsis = true;
            l.Margin = new Padding(0, 5, 8, 0);
            Controls.Add(l);
            return l;
        }

        public T Add<T>(T c, int marginRight) where T : Control
        {
            // FlowLayoutPanel 下 TextBox/暗色下拉需显式给宽高,否则会被压缩
            if (c is TextBox || c is DarkCombo)
            {
                if (c.Height < 24) c.Height = 26;
            }
            c.Margin = new Padding(0, 3, marginRight, 3);
            Controls.Add(c);
            return c;
        }

        public void Gap(int px)
        {
            Panel sp = new Panel();
            sp.Width = px; sp.Height = 1; sp.BackColor = Color.Transparent;
            sp.Margin = new Padding(0);
            Controls.Add(sp);
        }
    }

    public static class DarkTips
    {
        private static ToolTip _tip;
        private const int MaxW = 420;

        static DarkTips()
        {
            _tip = new ToolTip();
            _tip.OwnerDraw = true;
            _tip.InitialDelay = 250;
            _tip.AutoPopDelay = 15000;
            _tip.ReshowDelay = 120;
            _tip.UseAnimation = false;
            _tip.UseFading = false;
            _tip.Popup += OnPopup;
            _tip.Draw += OnDraw;
        }

        public static void Set(Control c, string text)
        {
            if (c == null || string.IsNullOrEmpty(text)) return;
            _tip.SetToolTip(c, text);
        }

        private static void OnPopup(object sender, PopupEventArgs e)
        {
            Control c = e.AssociatedControl;
            string t = _tip.GetToolTip(c);
            if (string.IsNullOrEmpty(t)) return;
            using (Bitmap b = new Bitmap(1, 1))
            using (Graphics g = Graphics.FromImage(b))
            {
                int lineBreak = t.IndexOf('\n');
                string head = lineBreak >= 0 ? t.Substring(0, lineBreak) : t;
                Size headSz = TextRenderer.MeasureText(g, head, Theme.FHead, new Size(MaxW, 0));
                Size bodySz = lineBreak >= 0
                    ? TextRenderer.MeasureText(g, t.Substring(lineBreak + 1), Theme.FSub, new Size(MaxW - 14, 0))
                    : new Size(0, 0);
                int h = headSz.Height + bodySz.Height + 16;
                int w = Math.Min(MaxW, Math.Max(headSz.Width, bodySz.Width) + 22);
                e.ToolTipSize = new Size(Math.Max(w, 60), Math.Max(h, 30));
            }
        }

        private static void OnDraw(object sender, DrawToolTipEventArgs e)
        {
            Graphics g = e.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            string t = e.ToolTipText ?? "";
            using (SolidBrush b = new SolidBrush(Color.FromArgb(32, 32, 36)))
                g.FillRectangle(b, e.Bounds);
            using (Pen p = new Pen(Theme.Border))
                g.DrawRectangle(p, e.Bounds.X, e.Bounds.Y, e.Bounds.Width - 1, e.Bounds.Height - 1);
            int lineBreak = t.IndexOf('\n');
            string head = lineBreak >= 0 ? t.Substring(0, lineBreak) : t;
            string body = lineBreak >= 0 ? t.Substring(lineBreak + 1) : "";
            TextRenderer.DrawText(g, head, Theme.FHead, new Point(e.Bounds.X + 10, e.Bounds.Y + 6), Theme.AccentHi);
            if (body.Length > 0)
                TextRenderer.DrawText(g, body, Theme.FSub, new Rectangle(e.Bounds.X + 10, e.Bounds.Y + 6 + (int)(Theme.FHead.Height) + 2, e.Bounds.Width - 20, e.Bounds.Height - 12),
                    Theme.TxT, TextFormatFlags.WordBreak | TextFormatFlags.TextBoxControl);
        }
    }

    // ---------------- 全自绘下拉框(深色弹出列表,彻底替换原生 ComboBox) ----------------
    public class DarkComboItems
    {
        private readonly List<string> _l = new List<string>();
        public void Add(string s) { _l.Add(s ?? ""); }
        public void AddRange(object[] a) { if (a == null) return; foreach (object o in a) _l.Add(o == null ? "" : o.ToString()); }
        public void AddRange(string[] a) { if (a == null) return; foreach (string s in a) _l.Add(s ?? ""); }
        public void Clear() { _l.Clear(); }
        public int Count { get { return _l.Count; } }
        public string this[int i] { get { return _l[i]; } }
        internal List<string> List { get { return _l; } }
    }

    public class DarkCombo : Control
    {
        private readonly DarkComboItems _items = new DarkComboItems();
        private int _sel = -1;
        private bool _hover = false;
        private readonly ToolStripDropDown _dd;
        private readonly ComboPopup _pp;

        public DarkCombo()
        {
            SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint |
                     ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
            Height = 26;
            Cursor = Cursors.Hand;
            Font = Theme.F;
            TabStop = false;
            _pp = new ComboPopup(this);
            ToolStripControlHost host = new ToolStripControlHost(_pp);
            host.Margin = new Padding(0); host.AutoSize = false;
            _dd = new ToolStripDropDown();
            _dd.Margin = new Padding(0); _dd.Padding = new Padding(0);
            _dd.Items.Add(host);
            _dd.Opening += delegate { _pp.Sync(); host.Size = _pp.Size; };
        }

        public DarkComboItems Items { get { return _items; } }

        public int SelectedIndex
        {
            get { return _sel; }
            set
            {
                if (value >= 0 && value < _items.Count) { _sel = value; base.Text = _items.List[value]; }
                else { _sel = -1; }
                Invalidate();
            }
        }

        public object SelectedItem
        {
            get { return (_sel >= 0 && _sel < _items.Count) ? (object)_items.List[_sel] : null; }
        }

        /// <summary>设置文本:命中列表项则选中,否则作为游离文本显示</summary>
        public new string Text
        {
            get { return base.Text ?? ""; }
            set
            {
                base.Text = value ?? "";
                _sel = -1;
                for (int i = 0; i < _items.Count; i++)
                    if (string.Compare(_items.List[i], base.Text, StringComparison.OrdinalIgnoreCase) == 0)
                    { _sel = i; break; }
                Invalidate();
            }
        }

        protected override void OnMouseEnter(EventArgs e) { _hover = true; Invalidate(); base.OnMouseEnter(e); }
        protected override void OnMouseLeave(EventArgs e) { _hover = false; Invalidate(); base.OnMouseLeave(e); }

        protected override void OnMouseDown(MouseEventArgs e)
        {
            base.OnMouseDown(e);
            try
            {
                _pp.Sync();
                _dd.Show(this, new Point(0, Height + 1));
            }
            catch { }
        }

        internal void PickFromPopup(int index)
        {
            SelectedIndex = index;
            try { _dd.Close(); } catch { }
            Focus(); Invalidate();
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            Graphics g = e.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            Color bg = Color.FromArgb(24, 24, 27);
            Color border = _hover && Enabled ? Theme.AccentHi : Theme.Border;
            if (!Enabled)
            {
                using (SolidBrush b = new SolidBrush(Color.FromArgb(44, 44, 48))) g.FillRectangle(b, ClientRectangle);
            }
            else
            {
                using (SolidBrush b = new SolidBrush(bg)) g.FillRectangle(b, ClientRectangle);
            }
            using (Pen p = new Pen(border)) g.DrawRectangle(p, 0, 0, Width - 1, Height - 1);
            Color fg = Enabled ? Theme.TxT : Color.FromArgb(120, 120, 126);
            string s = base.Text ?? "";
            using (SolidBrush tb = new SolidBrush(fg))
                g.DrawString(s, Font, tb, 8, (Height - g.MeasureString(s, Font).Height) / 2f);
            // 右侧箭头(自绘 V 形,不依赖字体字形)
            float cx = Width - 13, cy = Height / 2f;
            using (Pen ap = new Pen(Enabled ? Theme.Dim : Color.FromArgb(100, 100, 106), 1.8f))
            {
                g.DrawLine(ap, cx - 3, cy - 1.6f, cx, cy + 1.4f);
                g.DrawLine(ap, cx, cy + 1.4f, cx + 3, cy - 1.6f);
            }
        }
    }

    // 弹出的深色列表面板(挂在 ToolStripDropDown 上,点外部自动收起)
    internal class ComboPopup : Control
    {
        private readonly DarkCombo _owner;
        private int _hover = -1;
        private int _first = 0;
        private const int RowH = 28;
        private const int MaxVis = 8;

        public ComboPopup(DarkCombo owner)
        {
            _owner = owner;
            SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint |
                     ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
            BackColor = Color.FromArgb(24, 24, 27);
            Font = Theme.F;
        }

        public void Sync()
        {
            int n = _owner.Items.Count;
            int vis = n < MaxVis ? n : MaxVis;
            if (vis <= 0) vis = 1;
            int w = _owner.Width;
            // 弹层宽度:取最宽项
            try
            {
                using (Graphics g = CreateGraphics())
                    for (int i = 0; i < n; i++)
                    {
                        float tw = g.MeasureString(_owner.Items[i], Font).Width + 46;
                        if (tw > w) w = (int)tw;
                    }
            }
            catch { }
            Size = new Size(w, vis * RowH + 2);
            // 尽量让当前选中项可见
            int sel = _owner.SelectedIndex;
            if (sel >= 0)
            {
                if (sel < _first) _first = sel;
                int maxFirst = n - vis;
                if (maxFirst < 0) maxFirst = 0;
                if (sel > _first + vis - 1) _first = sel - vis + 1;
                if (_first > maxFirst) _first = maxFirst;
            }
            else _first = 0;
            _hover = -1;
            Invalidate();
        }

        private int RowOf(int y) { return _first + (y - 1) / RowH; }
        private bool ValidRow(int idx) { return idx >= _first && idx < _first + VisibleRows && idx < _owner.Items.Count; }
        private int VisibleRows
        {
            get
            {
                int n = _owner.Items.Count;
                int vis = n < MaxVis ? n : MaxVis;
                if (vis <= 0) vis = 1;
                return vis;
            }
        }

        protected override void OnMouseMove(MouseEventArgs e)
        {
            base.OnMouseMove(e);
            int idx = RowOf(e.Y);
            if (ValidRow(idx) && _hover != idx) { _hover = idx; Invalidate(); }
        }
        protected override void OnMouseClick(MouseEventArgs e)
        {
            base.OnMouseClick(e);
            int idx = RowOf(e.Y);
            if (ValidRow(idx))
            {
                _owner.PickFromPopup(idx);
                _hover = -1;
            }
        }
        protected override void OnMouseWheel(MouseEventArgs e)
        {
            base.OnMouseWheel(e);
            int n = _owner.Items.Count;
            int vis = VisibleRows;
            int maxFirst = n - vis; if (maxFirst < 0) maxFirst = 0;
            _first += (e.Delta > 0 ? -1 : 1);
            if (_first < 0) _first = 0;
            if (_first > maxFirst) _first = maxFirst;
            _hover = -1;
            Invalidate();
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            Graphics g = e.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            int n = _owner.Items.Count;
            if (n == 0)
            {
                using (SolidBrush b = new SolidBrush(Theme.Dim))
                    g.DrawString("(空)", Font, b, 8, 6);
            }
            int vis = VisibleRows;
            for (int i = _first; i < _first + vis && i < n; i++)
            {
                int y = 1 + (i - _first) * RowH;
                bool sel = (i == _owner.SelectedIndex);
                if (i == _hover)
                {
                    using (SolidBrush b = new SolidBrush(Theme.Accent)) g.FillRectangle(b, 1, y, Width - 2, RowH);
                }
                else
                {
                    using (SolidBrush b = new SolidBrush(Color.FromArgb(28, 28, 32))) g.FillRectangle(b, 1, y, Width - 2, RowH);
                    if (sel)
                        using (SolidBrush b = new SolidBrush(Theme.AccentHi))
                            g.FillRectangle(b, 1, y, 3, RowH);
                }
                Color fg = (i == _hover) ? Color.White : (sel ? Theme.AccentHi : Theme.TxT);
                using (SolidBrush tb = new SolidBrush(fg))
                    g.DrawString(_owner.Items[i], Font, tb, 14, y + (RowH - g.MeasureString(_owner.Items[i], Font).Height) / 2f);
            }
            using (Pen p = new Pen(Theme.Border)) g.DrawRectangle(p, 0, 0, Width - 1, Height - 1);
            // 需要滚动时,右侧画滚动条指示
            if (n > vis)
            {
                int trackX = Width - 5;
                using (SolidBrush b = new SolidBrush(Color.FromArgb(46, 46, 52)))
                    g.FillRectangle(b, trackX, 1, 4, Height - 2);
                int maxFirst = n - vis;
                float thumbH = (Height - 4) * vis / (float)n;
                float thumbY = 2 + (Height - 4 - thumbH) * (_first / (float)maxFirst);
                using (SolidBrush b = new SolidBrush(Theme.AccentHi))
                    g.FillRectangle(b, trackX, thumbY, 4, thumbH);
            }
        }
    }

    // ---------------- 放射性按钮(自绘:深色底 + 蓝色选中圆点,选中态一目了然) ----------------
    public class DarkRadio : RadioButton
    {
        private bool _hover = false;

        public DarkRadio()
        {
            SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint |
                     ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
            ForeColor = Theme.TxT;
            BackColor = Color.Transparent;
            Font = Theme.F;
            AutoSize = false;
            Height = 26;
            Width = 120;
            Cursor = Cursors.Hand;
        }

        /// <summary>按文字自动定宽,避免 AutoSize=false 导致的截断</summary>
        public void FitWidth()
        {
            int tw = TextRenderer.MeasureText(Text, Font, Size.Empty,
                       TextFormatFlags.NoPadding | TextFormatFlags.SingleLine).Width;
            Width = 22 + tw + 10;
            Height = Math.Max(26, Height);
        }

        protected override void OnMouseEnter(EventArgs e) { _hover = true; Invalidate(); base.OnMouseEnter(e); }
        protected override void OnMouseLeave(EventArgs e) { _hover = false; Invalidate(); base.OnMouseLeave(e); }

        protected override void OnPaint(PaintEventArgs e)
        {
            Graphics g = e.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            // 先判断是否受支持,再走自绘;否则用系统样式
            if (!AppearanceSupported()) { base.OnPaint(e); return; }

            // 铺底(必须先盖掉基类可能绘制的默认外观)
            using (SolidBrush bb = new SolidBrush(Color.FromArgb(30, 30, 34)))
                g.FillRectangle(bb, ClientRectangle);

            const int d = 14;
            int cy = Math.Max(2, (Height - d) / 2);
            int cx = 2;

            Color ring = Checked ? Theme.AccentHi : (_hover ? Color.FromArgb(150, 150, 158) : Color.FromArgb(120, 120, 128));
            using (Pen p = new Pen(ring, Checked ? 2.0f : 1.4f))
                g.DrawEllipse(p, cx, cy, d, d);

            if (Checked)
            {
                using (SolidBrush b = new SolidBrush(Theme.AccentHi))
                    g.FillEllipse(b, cx + 3.5f, cy + 3.5f, d - 7f, d - 7f);
            }
            else if (_hover)
            {
                using (SolidBrush b = new SolidBrush(Color.FromArgb(70, 148, 148, 160)))
                    g.FillEllipse(b, cx + 4, cy + 4, d - 8, d - 8);
            }

            Color fg = !Enabled ? Color.FromArgb(110, 110, 116)
                       : (Checked ? Color.White : Color.FromArgb(178, 178, 186));
            Font fnt = Checked ? new Font(Font, FontStyle.Bold) : Font;
            TextRenderer.DrawText(g, Text, fnt,
                new Rectangle(cx + d + 6, 0, Math.Max(10, Width - (d + 10)), Height),
                fg, TextFormatFlags.Left | TextFormatFlags.VerticalCenter |
                    TextFormatFlags.NoPadding | TextFormatFlags.SingleLine | TextFormatFlags.EndEllipsis);
            if (Checked) fnt.Dispose();
        }

        private bool AppearanceSupported()
        {
            return true;
        }
    }
}
