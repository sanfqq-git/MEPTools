using System;
using Autodesk.Revit.Attributes;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using MEPTools.Shared;
using System.Windows.Forms;

namespace MEPTools.MEPTagAlignLeft
{
    /// <summary>
    /// 独立的设置命令：用于调整 Tag 堆叠行距。
    /// </summary>
    [Transaction(TransactionMode.Manual)]
    public class CommandSetting : IExternalCommand
    {
        public Result Execute(ExternalCommandData commandData, ref string message, ElementSet elements)
        {
            // 1. 读取当前配置（内部单位：英尺）
            double currentFeet = MEPConfig.GetDouble("TagAlign.RowSpacing", 0.50);

            // 2. 弹出设置窗口
            using (var form = new TagAlignSettingForm(currentFeet))
            {
                if (form.ShowDialog() == DialogResult.OK)
                {
                    // 3. 用户点击确定，将毫米转回英尺（1英尺 = 304.8毫米）
                    double newFeet = form.SpacingInMm / 304.8;
                    
                    // 4. 更新内存并持久化到 MEPTools.config
                    MEPConfig.SetValue("TagAlign.RowSpacing", newFeet.ToString("0.0000"));
                    
                    if (MEPConfig.Save())
                    {
                        InfoCenterTip.Show($"设置已保存，行距：{form.SpacingInMm} mm");
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