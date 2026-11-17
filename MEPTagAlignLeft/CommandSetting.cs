using System;
using System.Globalization;
using Autodesk.Revit.Attributes;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using MEPTools.Shared;
using System.Windows.Forms;

namespace MEPTools.MEPTagAlignLeft
{
    /// <summary>
    /// 独立的设置命令：用于调整 Tag 堆叠行距。
    /// 【方案A】与 MEPGroupTag 共用同一配置项 TagGroup.LineSpacing（单位：毫米），改一处两处生效。
    /// </summary>
    [Transaction(TransactionMode.Manual)]
    public class CommandSetting : IExternalCommand
    {
        public Result Execute(ExternalCommandData commandData, ref string message, ElementSet elements)
        {
            // 1. 读取共用配置项（TagGroup.LineSpacing，单位：毫米），并转为英尺传给设置窗口（窗口内部按英尺处理）
            double currentMM = MEPConfig.GetDouble("TagGroup.LineSpacing", 200.0);
            double currentFeet = UnitUtils.ConvertToInternalUnits(currentMM, UnitTypeId.Millimeters);

            // 2. 弹出设置窗口
            using (var form = new TagAlignSettingForm(currentFeet))
            {
                if (form.ShowDialog() == DialogResult.OK)
                {
                    // 3. 写回共用配置项（毫米值）。
                    //    注意：必须用 InvariantCulture 格式化，与 MEPConfig.GetDouble 的解析方式一致，
                    //    避免某些系统区域文化下写入 "0,5" 导致再次读取失败。
                    MEPConfig.SetValue("TagGroup.LineSpacing",
                        form.SpacingInMm.ToString("0.0", CultureInfo.InvariantCulture));
                    
                    if (MEPConfig.Save())
                    {
                        InfoCenterTip.Show($"设置已保存（与分组排版共用行距）：{form.SpacingInMm} mm");
                    }
                    else
                    {
                        InfoCenterTip.Show("保存失败，请检查插件目录是否有写入权限。");
                    }
                }
            }

            return Result.Succeeded;
        }
    }
}