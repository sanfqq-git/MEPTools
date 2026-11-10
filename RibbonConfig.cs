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
                new ButtonConfig { Id = "Btn_MEP3TeeConnect",            Text = "高差三通连接",         ToolTip = "有高差的两组MEP 元素，生成高差三通", CommandType = typeof(MEPTools.MEP3TeeConnect.Command), PulldownGroup = "连接工具" },
                new ButtonConfig { Id = "Btn_MEP3TeeCrossConnect",       Text = "高差相交双三通连接",   ToolTip = "高差相交的两组MEP 元素，生成双三通", CommandType = typeof(MEPTools.MEP3TeeCrossConnect.Command), PulldownGroup = "连接工具" },

                // 高低连接 —— 拆分为三个独立角度命令
                new ButtonConfig { Id = "Btn_HighLow45",                 Text = "高低连接 45°",         ToolTip = "有高差的两组MEP 元素，以固定 45° 斜段生成连接", CommandType = typeof(MEPTools.MEPConnector.HighLow.Command45Degree),    PulldownGroup = "连接工具" },
                new ButtonConfig { Id = "Btn_HighLow90",                 Text = "高低连接 90°",         ToolTip = "有高差的两组MEP 元素，以纯垂直方式（90°）生成连接", CommandType = typeof(MEPTools.MEPConnector.HighLow.Command90Degree),    PulldownGroup = "连接工具" },
                new ButtonConfig { Id = "Btn_HighLowCustom",             Text = "高低连接 自定义",      ToolTip = "有高差的两组MEP 元素，输入 1~90° 自定义角度生成连接", CommandType = typeof(MEPTools.MEPConnector.HighLow.CommandCustomAngle), PulldownGroup = "连接工具" },

                // 平面共线、转角连接命令
                new ButtonConfig { Id = "Btn_PlanarCollinear",           Text = "平面共线连接",         ToolTip = "同轴的两组MEP元素，直接延长并合并", CommandType = typeof(MEPTools.MEPConnector.Planar.Commands.CollinearCommand), PulldownGroup = "连接工具" },
                new ButtonConfig { Id = "Btn_PlanarCorner",              Text = "平面转角连接",         ToolTip = "不平行但有交点的两组MEP元素，延长并生成弯头", CommandType = typeof(MEPTools.MEPConnector.Planar.Commands.CornerCommand), PulldownGroup = "连接工具" },

                // 共线对齐准备
                new ButtonConfig { Id = "Btn_AlignMEP",                  Text = "共线对齐准备",         ToolTip = "将 B 组 MEP 元素在水平面上平移到 A 组轴线上（不合并），用于共线合并前的手动纠偏", CommandType = typeof(MEPTools.MEPConnector.AlignMEPCommand), PulldownGroup = "连接工具" },

                // ================= 2. 选择工具 (下拉菜单) =================
                new ButtonConfig
                {
                    Id = "Btn_SelectConnected",
                    Text = "选择连接组",
                    ToolTip = "选中任意一根 MEP 管后，自动选中与之物理相连的整组管道（穿透弯头/三通/阀门等配件）",
                    CommandType = typeof(MEPTools.MEPConnector.Selection.SelectConnectedCommand),
                    PulldownGroup = "选择工具"
                },
                new ButtonConfig
                {
                    Id = "Btn_SelectConnectedSameSystem",
                    Text = "选择同系统连接组",
                    ToolTip = "在'连接组'基础上，仅选中与起点同系统类型（供水/回水/排风等）的管道；线槽仅按大类匹配",
                    CommandType = typeof(MEPTools.MEPConnector.Selection.SelectConnectedSameSystemCommand),
                    PulldownGroup = "选择工具"
                },

                // ================= 3. 翻弯工具 (下拉菜单) =================
                new ButtonConfig { Id = "Btn_HighLowSaddleBend",         Text = "高低翻弯",             ToolTip = "高低差生成马鞍翻弯", CommandType = typeof(MEPTools.MEPHighLowSaddleBend.Command), PulldownGroup = "翻弯工具" },

                // 平面拐弯 —— 拆分为三个独立角度命令
                new ButtonConfig { Id = "Btn_LateralBend45",             Text = "平面拐弯 45°",         ToolTip = "在水平面内两组MEP元素之间，以固定 45° 斜段生成拐弯侧向翻弯", CommandType = typeof(MEPTools.MEPPlanarLateralBend.LateralBend45Command),   PulldownGroup = "翻弯工具" },
                new ButtonConfig { Id = "Btn_LateralBend90",             Text = "平面拐弯 90°",         ToolTip = "在水平面内两组MEP元素之间，以 90° 直角（Z 形）生成拐弯侧向翻弯", CommandType = typeof(MEPTools.MEPPlanarLateralBend.LateralBend90Command),   PulldownGroup = "翻弯工具" },
                new ButtonConfig { Id = "Btn_LateralBendFree",           Text = "平面拐弯 0~90°",       ToolTip = "在水平面内两组MEP元素之间，输入 0~90° 自定义角度生成拐弯侧向翻弯", CommandType = typeof(MEPTools.MEPPlanarLateralBend.LateralBendFreeCommand), PulldownGroup = "翻弯工具" },

                new ButtonConfig { Id = "Btn_PlanarSaddleBend",          Text = "平面翻弯",             ToolTip = "在平面内两组MEP元素之间生成马鞍翻弯，沿着管升降方向点两次，第一点为打断点，对第二点的MEP 元素的相对第一点的方向进行翻弯操作", CommandType = typeof(MEPTools.MEPPlanarSaddleBend.Command), PulldownGroup = "翻弯工具" },

                // 高低升降 —— 拆分为三个独立角度命令
                new ButtonConfig { Id = "Btn_HighLowUpDown45",           Text = "高低升降 45°",         ToolTip = "沿管线方向点击两次（第1点=打断点，第2点=升降侧），以 45° 弯头生成抬升/下降（正数抬升，负数下降）", CommandType = typeof(MEPTools.MEPHighLowUpDown.Command45), PulldownGroup = "翻弯工具" },
                new ButtonConfig { Id = "Btn_HighLowUpDown90",           Text = "高低升降 90°",         ToolTip = "沿管线方向点击两次（第1点=打断点，第2点=升降侧），以 90° 弯头生成纯垂直抬升/下降（正数抬升，负数下降）", CommandType = typeof(MEPTools.MEPHighLowUpDown.Command90), PulldownGroup = "翻弯工具" },
                new ButtonConfig { Id = "Btn_HighLowUpDownCustom",       Text = "高低升降 0~90°",       ToolTip = "沿管线方向点击两次（第1点=打断点，第2点=升降侧），自定义 0~90° 角度生成抬升/下降（角度越小水平段越长）", CommandType = typeof(MEPTools.MEPHighLowUpDown.Command0To90), PulldownGroup = "翻弯工具" },

                // ================= 4. 对齐与排序 (面板直接展示) =================
                new ButtonConfig { Id = "Btn_AlignBottomElevation",      Text = "底部平齐",             ToolTip = "将选中的 MEP 元素底部标高对齐到指定的基准", CommandType = typeof(MEPTools.MEPAlignBottomElevation.Command) },
                new ButtonConfig { Id = "Btn_GroupSortAlign",            Text = "分组排序对齐",         ToolTip = "按底部标高分组，组内按净间距自动排布", CommandType = typeof(MEPTools.MEPGroupSortAlign.Command) },

                // --- 标记对齐与堆叠（拆分为三个独立按钮） ---
                new ButtonConfig { Id = "Btn_TagAlignLeft",              Text = "标记左对齐并堆叠",     ToolTip = "将选中的标记（Tag）统一左对齐，并按当前上下顺序调整间距堆叠", CommandType = typeof(MEPTools.MEPTagAlignLeft.Command) },
                new ButtonConfig { Id = "Btn_TagAlignLeftOnly",          Text = "标记仅左对齐",         ToolTip = "仅将选中的标记（Tag）左侧对齐，不改变其垂直位置", CommandType = typeof(MEPTools.MEPTagAlignLeft.CommandAlignLeftOnly) },
                new ButtonConfig { Id = "Btn_TagStackOnly",              Text = "标记仅堆叠",           ToolTip = "保持选中的标记（Tag）现有相对顺序，统一调整上下堆叠间距", CommandType = typeof(MEPTools.MEPTagAlignLeft.CommandStackOnly) },

                new ButtonConfig { Id = "Btn_TagLeaderStyle60",          Text = "MEPTag60°",            ToolTip = "将选中的标记（Tag）修改标记引线为60度折线", CommandType = typeof(MEPTools.TagLeaderStyle60.Command) },

                // ================= 5. 其他辅助工具 (面板直接展示) =================

                // ⭐ 链接墙洞及它的设置打包进 "墙洞工具" 下拉菜单
                new ButtonConfig { Id = "Btn_LinkElementWallHole",       Text = "链接墙洞",             ToolTip = "在链接模型（如建筑墙）上批量创建墙洞", CommandType = typeof(MEPTools.LinkElementWallHole.Command), PulldownGroup = "墙洞工具" },
                new ButtonConfig { Id = "Btn_WallHoleSettings",          Text = "墙洞设置",             ToolTip = "配置墙洞族、类型、标高、参数名、预留偏移等", CommandType = typeof(MEPTools.LinkElementWallHole.Cmd_WallHoleSettings), PulldownGroup = "墙洞工具" },

                // 【批量打断按钮】
                new ButtonConfig { Id = "Btn_MEPBatchBreak",             Text = "批量打断管线",         ToolTip = "选中多个 MEP 管线后，指定打断点，批量执行保留原 ID 的打断并恢复连接", CommandType = typeof(MEPTools.MEPBatchBreak.Command) },

                new ButtonConfig { Id = "Btn_RemoveDuplicateElements",   Text = "删除重复图元",         ToolTip = "查找并删除当前视图中重复的 MEP 图元", CommandType = typeof(MEPTools.RemoveDuplicateElements.Command) },
                new ButtonConfig { Id = "Btn_ElevationRenamer",          Text = "标高重命名",           ToolTip = "批量重命名或规范化 Revit 标高名称", CommandType = typeof(MEPTools.RevitElevationRenamer.Command) },
                new ButtonConfig { Id = "Btn_SystemChecker",             Text = "系统检查",             ToolTip = "检查 MEP 系统的连通性及参数完整性", CommandType = typeof(MEPTools.SystemChecker.Command) },
                new ButtonConfig { Id = "Btn_DuctResize",                Text = "DUCT更新尺寸",         ToolTip = "更新DUCT尺寸", CommandType = typeof(MEPTools.DuctResize.Command) },

                // ⭐ 新增：原地重制（功能见 RemakeInPlaceCommand.cs）
                new ButtonConfig
                {
                    Id = "Btn_RemakeInPlace",
                    Text = "原地重制",
                    ToolTip = "预选元素时：原地删除并重建（复制→删除，保留参数与几何）；无预选时：选择 LINK 文件中的元素，按空格确认后复制到当前文档",
                    CommandType = typeof(MEPTools.RemakeInPlaceCommands.Command)
                },

                // ⭐ 90° 旋转物体 —— 参考轴版（保留）
                new ButtonConfig
                {
                    Id = "Btn_Rotate90",
                    Text = "90°旋转物体",
                    ToolTip = "选择参考元素提取旋转轴，再选目标 MEP 元素；在弹出的窗体中每按一次空格键，目标元素绕参考轴旋转 90°，可连续翻转",
                    CommandType = typeof(MEPTools.Rotate90.Rotate90Command)
                },

                // ⭐ 90° 旋转物体 —— XYZ 轴版（新增）
                new ButtonConfig
                {
                    Id = "Btn_Rotate90ByAxis",
                    Text = "90°旋转物体(XYZ轴)",
                    ToolTip = "选择目标 MEP 元素后，在窗体中选择绕 X / Y / Z 世界轴旋转（过整体包围盒中心）；每按一次空格键旋转 90°，可连续翻转，ESC 结束",
                    CommandType = typeof(MEPTools.Rotate90ByAxis.Rotate90ByAxisCommand)
                },

                // ================= 6. 工作集管理 (下拉菜单) =================
                new ButtonConfig { Id = "Btn_ChangeWorkset",             Text = "改元素到默认工作集",   ToolTip = "将选中的 MEP 元素批量修改为当前文档的活动工作集", CommandType = typeof(MEPTools.WorksetManager.CmdChangeWorkset), PulldownGroup = "工作集管理" },
                new ButtonConfig { Id = "Btn_SetDefaultWorkset",         Text = "提取并设为默认工作集", ToolTip = "选择参考元素，将其所属工作集提取并设为当前文档的默认（活动）工作集", CommandType = typeof(MEPTools.WorksetManager.CmdSetDefaultWorkset), PulldownGroup = "工作集管理" },
            };
        }
    }
}