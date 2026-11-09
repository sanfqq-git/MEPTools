using System;
using System.Collections.Generic;

namespace MEPTools
{
    /// <summary>
    /// 按钮配置项模型
    /// </summary>
    public class ButtonConfig
    {
        public string Id { get; set; }
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

                // 高低连接命令
                new ButtonConfig { Text = "高低连接", ToolTip = "有高差的两组MEP 元素，设角度生成连接", CommandType = typeof(MEPTools.MEPConnector.HighLow.Command), PulldownGroup = "连接工具" },

                // 平面共线、转角连接命令
                new ButtonConfig { Text = "平面共线连接", ToolTip = "同轴的两组MEP元素，直接延长并合并", CommandType = typeof(MEPTools.MEPConnector.Planar.Commands.CollinearCommand), PulldownGroup = "连接工具" },
                new ButtonConfig { Text = "平面转角连接", ToolTip = "不平行但有交点的两组MEP元素，延长并生成弯头", CommandType = typeof(MEPTools.MEPConnector.Planar.Commands.CornerCommand), PulldownGroup = "连接工具" },

                // 共线对齐准备
                new ButtonConfig { Text = "共线对齐准备", ToolTip = "将 B 组 MEP 元素在水平面上平移到 A 组轴线上（不合并），用于共线合并前的手动纠偏", CommandType = typeof(MEPTools.MEPConnector.AlignMEPCommand), PulldownGroup = "连接工具" },

                // ================= 2. 选择工具 (下拉菜单) =================
                new ButtonConfig
                {
                    Text = "选择连接组",
                    ToolTip = "选中任意一根 MEP 管后，自动选中与之物理相连的整组管道（穿透弯头/三通/阀门等配件）",
                    CommandType = typeof(MEPTools.MEPConnector.Selection.SelectConnectedCommand),
                    PulldownGroup = "选择工具"
                },
                new ButtonConfig
                {
                    Text = "选择同系统连接组",
                    ToolTip = "在'连接组'基础上，仅选中与起点同系统类型（供水/回水/排风等）的管道；线槽仅按大类匹配",
                    CommandType = typeof(MEPTools.MEPConnector.Selection.SelectConnectedSameSystemCommand),
                    PulldownGroup = "选择工具"
                },

                // ================= 3. 翻弯工具 (下拉菜单) =================
                new ButtonConfig { Text = "高低翻弯", ToolTip = "高低差生成马鞍翻弯", CommandType = typeof(MEPTools.MEPHighLowSaddleBend.Command), PulldownGroup = "翻弯工具" },
                new ButtonConfig { Text = "平面拐弯", ToolTip = "在水平面内两组MEP元素之间生成拐弯侧向翻弯", CommandType = typeof(MEPTools.MEPPlanarLateralBend.Command), PulldownGroup = "翻弯工具" },
                new ButtonConfig { Text = "平面翻弯", ToolTip = "在平面内两组MEP元素之间生成马鞍翻弯，沿着管升降方向点两次，第一点为打断点，对第二点的MEP 元素的相对第一点的方向进行翻弯操作", CommandType = typeof(MEPTools.MEPPlanarSaddleBend.Command), PulldownGroup = "翻弯工具" },
                new ButtonConfig { Text = "高低升降", ToolTip = "沿着管升降方向点两次，第一点为打断点，对第二点的MEP 元素进行抬升或下降操作", CommandType = typeof(MEPTools.MEPHighLowUpDown.Command), PulldownGroup = "翻弯工具" },

                // ================= 4. 对齐与排序 (面板直接展示) =================
                new ButtonConfig { Text = "底部平齐", ToolTip = "将选中的 MEP 元素底部标高对齐到指定的基准", CommandType = typeof(MEPTools.MEPAlignBottomElevation.Command) },
                new ButtonConfig { Text = "分组排序对齐", ToolTip = "按底部标高分组，组内按净间距自动排布", CommandType = typeof(MEPTools.MEPGroupSortAlign.Command) },

                // --- 标记对齐与堆叠（拆分为三个独立按钮） ---
                new ButtonConfig { Text = "标记左对齐并堆叠", ToolTip = "将选中的标记（Tag）统一左对齐，并按当前上下顺序调整间距堆叠", CommandType = typeof(MEPTools.MEPTagAlignLeft.Command) },
                new ButtonConfig { Text = "标记仅左对齐", ToolTip = "仅将选中的标记（Tag）左侧对齐，不改变其垂直位置", CommandType = typeof(MEPTools.MEPTagAlignLeft.CommandAlignLeftOnly) },
                new ButtonConfig { Text = "标记仅堆叠", ToolTip = "保持选中的标记（Tag）现有相对顺序，统一调整上下堆叠间距", CommandType = typeof(MEPTools.MEPTagAlignLeft.CommandStackOnly) },

                new ButtonConfig { Text = "MEPTag60°", ToolTip = "将选中的标记（Tag）修改标记引线为60度折线", CommandType = typeof(MEPTools.TagLeaderStyle60.Command) },


                // ================= 5. 其他辅助工具 (面板直接展示) =================

                // ⭐ 链接墙洞及它的设置打包进 "墙洞工具" 下拉菜单
                new ButtonConfig { Text = "链接墙洞", ToolTip = "在链接模型（如建筑墙）上批量创建墙洞", CommandType = typeof(MEPTools.LinkElementWallHole.Command), PulldownGroup = "墙洞工具" },
                new ButtonConfig { Text = "墙洞设置", ToolTip = "配置墙洞族、类型、标高、参数名、预留偏移等", CommandType = typeof(MEPTools.LinkElementWallHole.Cmd_WallHoleSettings), PulldownGroup = "墙洞工具" },

                // 【批量打断按钮】
                new ButtonConfig { Text = "批量打断管线", ToolTip = "选中多个 MEP 管线后，指定打断点，批量执行保留原 ID 的打断并恢复连接", CommandType = typeof(MEPTools.MEPBatchBreak.Command) },

                new ButtonConfig { Text = "删除重复图元", ToolTip = "查找并删除当前视图中重复的 MEP 图元", CommandType = typeof(MEPTools.RemoveDuplicateElements.Command) },
                new ButtonConfig { Text = "标高重命名", ToolTip = "批量重命名或规范化 Revit 标高名称", CommandType = typeof(MEPTools.RevitElevationRenamer.Command) },
                new ButtonConfig { Text = "系统检查", ToolTip = "检查 MEP 系统的连通性及参数完整性", CommandType = typeof(MEPTools.SystemChecker.Command) },
                new ButtonConfig { Text = "DUCT更新尺寸", ToolTip = "更新DUCT尺寸", CommandType = typeof(MEPTools.DuctResize.Command) },

                // ================= 6. 工作集管理 (下拉菜单) =================
                new ButtonConfig { Text = "改元素到默认工作集", ToolTip = "将选中的 MEP 元素批量修改为当前文档的活动工作集", CommandType = typeof(MEPTools.WorksetManager.CmdChangeWorkset), PulldownGroup = "工作集管理" },
                new ButtonConfig { Text = "提取并设为默认工作集", ToolTip = "选择参考元素，将其所属工作集提取并设为当前文档的默认（活动）工作集", CommandType = typeof(MEPTools.WorksetManager.CmdSetDefaultWorkset), PulldownGroup = "工作集管理" },
            };
        }
    }
}