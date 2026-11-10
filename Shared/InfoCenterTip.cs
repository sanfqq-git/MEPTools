using Autodesk.Internal.InfoCenter;
using Autodesk.Windows;
using System;

namespace MEPTools.Shared
{
    public static class InfoCenterTip
    {
        /// <summary>
        /// 显示右上角气泡提示（仅标题）
        /// </summary>
        /// <param name="message">要显示的文本（作为标题）</param>
        public static void Show(string message)
        {
            try
            {
                var mgr = ComponentManager.InfoCenterPaletteManager;
                if (mgr?.Configuration == null)
                    return;

                mgr.CloseBalloon(string.Empty);
                // 修改：从配置中心读取显示秒数，默认 5 秒
                mgr.Configuration.BalloonDisplayPeriod = MEPConfig.GetInt("InfoCenter.DisplaySeconds", 5);
                mgr.ShowBalloon(new ResultItem
                {
                    Title = message
                });
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"InfoCenterTip error: {ex.Message}");
            }
        }
    }
}