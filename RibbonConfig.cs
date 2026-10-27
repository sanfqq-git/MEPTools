using System;
using System.Collections.Generic;

namespace MEPTools
{
    /// <summary>
    /// 按钮配置项模型
    /// </summary>
    public class ButtonConfig
    {
        public string Text { get; set; }
        public string ToolTip { get; set; }
        public Type CommandType { get; set; }
        public string PulldownGroup { get; set; }
    }

    /// <summary>
    /// ⭐ 只有这里需要修改：Ribbon 菜单配置中心
    /// </summary>
    public static class RibbonConfig
    {
        public static List<ButtonConfig> GetButtonConfigs()
        {
            return new List<ButtonConfig>
            {
                // ================= 1. 连接工具 (下拉菜单) =================
                new ButtonConfig { Text = "高差三通连接", ToolTip = "有高差的两组MEP 元素，生成高差三通", CommandType = typeof(MEPTools.MEP3TeeConnect.Command), PulldownGroup = "连接工具" },
                new ButtonConfig { Text = "高差相交双三通连接", ToolTip = "高差相交的两组MEP 元素，生成双三通", CommandType = typeof(MEPTools.MEP3TeeCrossConnect.Command), PulldownGroup = "连接工具" },
                new ButtonConfig { Text = "高低连接", ToolTip = "有高差的两组MEP 元素，设角度生成连接", CommandType = typeof(MEPTools.MEPHighLowConnector.Command), PulldownGroup = "连接工具" },
                new ButtonConfig { Text = "平面连接", ToolTip = "水平连接两组MEP元素，可设角度拐弯可0°直接", CommandType = typeof(MEPTools.MEPPlanarConnector.Command), PulldownGroup = "连接工具" },

                // ================= 2. 翻弯工具 (下拉菜单) =================
                new ButtonConfig { Text = "高低翻弯", ToolTip = "高低差生成马鞍翻弯", CommandType = typeof(MEPTools.MEPHighLowSaddleBend.Command), PulldownGroup = "翻弯工具" },
                new ButtonConfig { Text = "平面拐弯", ToolTip = "在水平面内两组MEP元素之间生成拐弯侧向翻弯", CommandType = typeof(MEPTools.MEPPlanarLateralBend.Command), PulldownGroup = "翻弯工具" },
                new ButtonConfig { Text = "平面翻弯", ToolTip = "在平面内两组MEP元素之间生成马鞍翻弯，沿着管升降方向点两次，第一点为打断点，对第二点的MEP 元素的相对第一点的方向进行翻弯操作", CommandType = typeof(MEPTools.MEPPlanarSaddleBend.Command), PulldownGroup = "翻弯工具" },
                new ButtonConfig { Text = "高低升降", ToolTip = "沿着管升降方向点两次，第一点为打断点，对第二点的MEP 元素进行抬升或下降操作", CommandType = typeof(MEPTools.MEPHighLowUpDown.Command), PulldownGroup = "翻弯工具" },

                // ================= 3. 对齐与排序 (面板直接展示) =================
                new ButtonConfig { Text = "底部平齐", ToolTip = "将选中的 MEP 元素底部标高对齐到指定的基准", CommandType = typeof(MEPTools.MEPAlignBottomElevation.Command) },
                new ButtonConfig { Text = "分组排序对齐", ToolTip = "按底部标高分组，组内按净间距自动排布", CommandType = typeof(MEPTools.MEPGroupSortAlign.Command) },
                new ButtonConfig { Text = "标记左对齐", ToolTip = "将选中的标记（Tag）统一左对齐到 MEP 元素", CommandType = typeof(MEPTools.MEPTagAlignLeft.Command) },

                // ================= 4. 其他辅助工具 (面板直接展示) =================
                new ButtonConfig { Text = "链接墙洞", ToolTip = "在链接模型（如建筑墙）上批量创建墙洞", CommandType = typeof(MEPTools.LinkElementWallHole.Command) },
                new ButtonConfig { Text = "删除重复图元", ToolTip = "查找并删除当前视图中重复的 MEP 图元", CommandType = typeof(MEPTools.RemoveDuplicateElements.Command) },
                new ButtonConfig { Text = "标高重命名", ToolTip = "批量重命名或规范化 Revit 标高名称", CommandType = typeof(MEPTools.RevitElevationRenamer.Command) },
                new ButtonConfig { Text = "系统检查", ToolTip = "检查 MEP 系统的连通性及参数完整性", CommandType = typeof(MEPTools.SystemChecker.Command) },
            };
        }
    }
}