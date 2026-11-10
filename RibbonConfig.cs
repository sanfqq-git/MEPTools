using System;
using System.Collections.Generic;

namespace MEPTools
{
    public class ButtonConfig
    {
        public string Id { get; set; }
        public string Text { get; set; }
        public string ToolTip { get; set; }
        public Type CommandType { get; set; }
        public string PulldownGroup { get; set; }
    }

    public static class RibbonConfig
    {
        public static List<ButtonConfig> GetButtonConfigs()
        {
            return new List<ButtonConfig>
            {
                // ================= 1. 连接工具 (下拉菜单) =================
                new ButtonConfig { Id = "Btn_MEP3TeeConnect",            Text = "高差三通连接",         ToolTip = "有高差的两组MEP 元素，生成高差三通", CommandType = typeof(MEPTools.MEP3TeeConnect.Command), PulldownGroup = "连接工具" },
                new ButtonConfig { Id = "Btn_MEP3TeeCrossConnect",       Text = "高差相交双三通连接",   ToolTip = "高差相交的两组MEP 元素，生成双三通", CommandType = typeof(MEPTools.MEP3TeeCrossConnect.Command), PulldownGroup = "连接工具" },
                new ButtonConfig { Id = "Btn_HighLow45",                 Text = "高低连接 45°",         ToolTip = "有高差的两组MEP 元素，以固定 45° 斜段生成连接", CommandType = typeof(MEPTools.MEPConnector.HighLow.Command45Degree),    PulldownGroup = "连接工具" },
                new ButtonConfig { Id = "Btn_HighLow90",                 Text = "高低连接 90°",         ToolTip = "有高差的两组MEP 元素，以纯垂直方式（90°）生成连接", CommandType = typeof(MEPTools.MEPConnector.HighLow.Command90Degree),    PulldownGroup = "连接工具" },
                new ButtonConfig { Id = "Btn_HighLowCustom",             Text = "高低连接 自定义",      ToolTip = "有高差的两组MEP 元素，输入 1~90° 自定义角度生成连接", CommandType = typeof(MEPTools.MEPConnector.HighLow.CommandCustomAngle), PulldownGroup = "连接工具" },
                new ButtonConfig { Id = "Btn_PlanarCollinear",           Text = "平面共线连接",         ToolTip = "同轴的两组MEP元素，直接延长并合并", CommandType = typeof(MEPTools.MEPConnector.Planar.Commands.CollinearCommand), PulldownGroup = "连接工具" },
                new ButtonConfig { Id = "Btn_PlanarCorner",              Text = "平面转角连接",         ToolTip = "不平行但有交点的两组MEP元素，延长并生成弯头", CommandType = typeof(MEPTools.MEPConnector.Planar.Commands.CornerCommand), PulldownGroup = "连接工具" },
                new ButtonConfig { Id = "Btn_AlignMEP",                  Text = "共线对齐准备",         ToolTip = "将 B 组 MEP 元素在水平面上平移到 A 组轴线上（不合并），用于共线合并前的手动纠偏", CommandType = typeof(MEPTools.MEPConnector.AlignMEPCommand), PulldownGroup = "连接工具" },

                // ================= 2. 选择工具 (下拉菜单) =================
                new ButtonConfig { Id = "Btn_SelectConnected",           Text = "选择连接组",           ToolTip = "选中任意一根 MEP 管后，自动选中与之物理相连的整组管道", CommandType = typeof(MEPTools.MEPConnector.Selection.SelectConnectedCommand), PulldownGroup = "选择工具" },
                new ButtonConfig { Id = "Btn_SelectConnectedSameSystem", Text = "选择同系统连接组",     ToolTip = "仅选中与起点同系统类型的管道；线槽按大类匹配", CommandType = typeof(MEPTools.MEPConnector.Selection.SelectConnectedSameSystemCommand), PulldownGroup = "选择工具" },

                // ================= 3. 翻弯工具 (下拉菜单) =================
                new ButtonConfig { Id = "Btn_HighLowSaddleBend",         Text = "高低翻弯",             ToolTip = "高低差生成马鞍翻弯", CommandType = typeof(MEPTools.MEPHighLowSaddleBend.Command), PulldownGroup = "翻弯工具" },
                new ButtonConfig { Id = "Btn_LateralBend45",             Text = "平面拐弯 45°",         ToolTip = "固定 45° 斜段生成拐弯侧向翻弯", CommandType = typeof(MEPTools.MEPPlanarLateralBend.LateralBend45Command),   PulldownGroup = "翻弯工具" },
                new ButtonConfig { Id = "Btn_LateralBend90",             Text = "平面拐弯 90°",         ToolTip = "90° 直角（Z 形）生成拐弯侧向翻弯", CommandType = typeof(MEPTools.MEPPlanarLateralBend.LateralBend90Command),   PulldownGroup = "翻弯工具" },
                new ButtonConfig { Id = "Btn_LateralBendFree",           Text = "平面拐弯 0~90°",       ToolTip = "自定义 0~90° 生成拐弯侧向翻弯", CommandType = typeof(MEPTools.MEPPlanarLateralBend.LateralBendFreeCommand), PulldownGroup = "翻弯工具" },
                new ButtonConfig { Id = "Btn_PlanarSaddleBend",          Text = "平面翻弯",             ToolTip = "平面内生成马鞍翻弯", CommandType = typeof(MEPTools.MEPPlanarSaddleBend.Command), PulldownGroup = "翻弯工具" },
                new ButtonConfig { Id = "Btn_HighLowUpDown45",           Text = "高低升降 45°",         ToolTip = "以 45° 弯头生成抬升/下降", CommandType = typeof(MEPTools.MEPHighLowUpDown.Command45), PulldownGroup = "翻弯工具" },
                new ButtonConfig { Id = "Btn_HighLowUpDown90",           Text = "高低升降 90°",         ToolTip = "以 90° 弯头生成纯垂直抬升/下降", CommandType = typeof(MEPTools.MEPHighLowUpDown.Command90), PulldownGroup = "翻弯工具" },
                new ButtonConfig { Id = "Btn_HighLowUpDownCustom",       Text = "高低升降 0~90°",       ToolTip = "自定义角度生成抬升/下降", CommandType = typeof(MEPTools.MEPHighLowUpDown.Command0To90), PulldownGroup = "翻弯工具" },

                // ================= 4. 对齐与排序 (面板直接展示) =================
                new ButtonConfig { Id = "Btn_AlignBottomElevation",      Text = "底部平齐",             ToolTip = "将选中的 MEP 元素底部标高对齐到指定的基准", CommandType = typeof(MEPTools.MEPAlignBottomElevation.Command) },
                new ButtonConfig { Id = "Btn_GroupSortAlign",            Text = "分组排序对齐",         ToolTip = "按底部标高分组，组内按净间距自动排布", CommandType = typeof(MEPTools.MEPGroupSortAlign.Command) },
                new ButtonConfig { Id = "Btn_TagAlignLeft",              Text = "标记左对齐并堆叠",     ToolTip = "将选中的标记统一左对齐，并按当前上下顺序调整间距堆叠", CommandType = typeof(MEPTools.MEPTagAlignLeft.Command) },
                new ButtonConfig { Id = "Btn_TagAlignLeftOnly",          Text = "标记仅左对齐",         ToolTip = "仅将选中的标记左侧对齐，不改变其垂直位置", CommandType = typeof(MEPTools.MEPTagAlignLeft.CommandAlignLeftOnly) },
                new ButtonConfig { Id = "Btn_TagStackOnly",              Text = "标记仅堆叠",           ToolTip = "保持选中的标记现有相对顺序，统一调整上下堆叠间距", CommandType = typeof(MEPTools.MEPTagAlignLeft.CommandStackOnly) },
                new ButtonConfig { Id = "Btn_TagLeaderStyle60",          Text = "MEPTag60°",            ToolTip = "将选中的标记修改引线为60度折线", CommandType = typeof(MEPTools.TagLeaderStyle60.Command) },

                // ================= 5. 其他辅助工具 =================
                new ButtonConfig { Id = "Btn_LinkElementWallHole",       Text = "链接墙洞",             ToolTip = "在链接模型上批量创建墙洞", CommandType = typeof(MEPTools.LinkElementWallHole.Command), PulldownGroup = "墙洞工具" },
                new ButtonConfig { Id = "Btn_WallHoleSettings",          Text = "墙洞设置",             ToolTip = "配置墙洞族、类型、标高、参数名等", CommandType = typeof(MEPTools.LinkElementWallHole.Cmd_WallHoleSettings), PulldownGroup = "墙洞工具" },
                new ButtonConfig { Id = "Btn_MEPBatchBreak",             Text = "批量打断管线",         ToolTip = "批量执行保留原 ID 的打断并恢复连接", CommandType = typeof(MEPTools.MEPBatchBreak.Command) },
                new ButtonConfig { Id = "Btn_RemoveDuplicateElements",   Text = "删除重复图元",         ToolTip = "查找并删除当前视图中重复的 MEP 图元", CommandType = typeof(MEPTools.RemoveDuplicateElements.Command) },
                new ButtonConfig { Id = "Btn_ElevationRenamer",          Text = "标高重命名",           ToolTip = "批量重命名或规范化 Revit 标高名称", CommandType = typeof(MEPTools.RevitElevationRenamer.Command) },
                new ButtonConfig { Id = "Btn_SystemChecker",             Text = "系统检查",             ToolTip = "检查 MEP 系统的连通性及参数完整性", CommandType = typeof(MEPTools.SystemChecker.Command) },
                new ButtonConfig { Id = "Btn_DuctResize",                Text = "DUCT更新尺寸",         ToolTip = "更新 DUCT 尺寸", CommandType = typeof(MEPTools.DuctResize.Command) },
                new ButtonConfig { Id = "Btn_RemakeInPlace",             Text = "原地重制",             ToolTip = "原地删除并重建或从 LINK 复制", CommandType = typeof(MEPTools.RemakeInPlaceCommands.Command) },
                new ButtonConfig { Id = "Btn_Rotate90",                  Text = "90°旋转物体(参考轴)",  ToolTip = "绕参考轴旋转 90°", CommandType = typeof(MEPTools.Rotate90.Rotate90Command) },
                new ButtonConfig { Id = "Btn_Rotate90ByView",            Text = "90°旋转物体(视图方向)",ToolTip = "按当前视图方向旋转或翻转 90°", CommandType = typeof(MEPTools.Rotate90ByAxis.Rotate90ByAxisCommand) },

                // ================= 6. 工作集管理 =================
                new ButtonConfig { Id = "Btn_ChangeWorkset",             Text = "改元素到默认工作集",   ToolTip = "将选中的 MEP 元素批量修改为当前文档的活动工作集", CommandType = typeof(MEPTools.WorksetManager.CmdChangeWorkset), PulldownGroup = "工作集管理" },
                new ButtonConfig { Id = "Btn_SetDefaultWorkset",         Text = "提取并设为默认工作集", ToolTip = "选择参考元素，将其所属工作集提取并设为默认工作集", CommandType = typeof(MEPTools.WorksetManager.CmdSetDefaultWorkset), PulldownGroup = "工作集管理" },

                // ================= 7. 协同工具 (下拉菜单) =================
                new ButtonConfig
                {
                    Id = "Btn_SyncAndRelinquish",
                    Text = "同步并放权",
                    ToolTip = "上传到中心模型，成功后自动放弃所有权限",
                    CommandType = typeof(MEPTools.SyncRelinquish.Cmd_SyncAndRelinquish),
                    PulldownGroup = "协同工具"
                },
                new ButtonConfig
                {
                    Id = "Btn_RelinquishSelected",
                    Text = "仅放选中",
                    ToolTip = "仅放弃当前选中的图元权限（未修改的图元才有效）",
                    CommandType = typeof(MEPTools.SyncRelinquish.Cmd_RelinquishSelected),
                    PulldownGroup = "协同工具"
                },
                new ButtonConfig
                {
                    Id = "Btn_RelinquishExceptSelected",
                    Text = "仅留选中",
                    ToolTip = "保留当前选中元素的权限，其余全部放弃",
                    CommandType = typeof(MEPTools.SyncRelinquish.Cmd_RelinquishExceptSelected),
                    PulldownGroup = "协同工具"
                },
                new ButtonConfig
                {
                    Id = "Btn_RelinquishNow",
                    Text = "放弃全部",
                    ToolTip = "立刻放弃当前文档中所有已借用的权限",
                    CommandType = typeof(MEPTools.SyncRelinquish.Cmd_RelinquishNow),
                    PulldownGroup = "协同工具"
                },
            };
        }
    }
}