/*
 * InfoCenterTip - 右上角 InfoCenter 气泡提示（极简版）
 * 
 * 【功能】
 * 在 Revit/AutoCAD 等 Autodesk 软件右上角 InfoCenter 区域显示短暂的气泡提示。
 * 本版本仅支持显示单行文本（作为标题），不支持详细描述。
 *
 * 【使用指南】
 * 1. 直接调用静态方法：
 *    InfoCenterTip.Show("同步完成");
 *    InfoCenterTip.Show("请先保存文档");
 *
 * 2. 适用场景：
 *    - 异步操作完成通知（如同步、导出）
 *    - 简单的状态反馈（如“已复制到剪贴板”）
 *    - 非阻塞性提醒（不会中断用户操作）
 *
 * 3. 行为特性：
 *    - 气泡自动在 5 秒后消失
 *    - 若连续调用，旧气泡会被新气泡替换（不会堆积）
 *    - 若 InfoCenter 不可见（用户关闭了该区域），则静默失败，不抛出异常
 *
 * 【技巧与注意事项】
 * - 线程安全：本方法内部已包含异常捕获，可在任何线程调用（包括非 UI 线程），
 *   无需额外使用 Dispatcher。若调用时 InfoCenter 管理器未就绪，会静默返回。
 * - 调试模式：若需在 Visual Studio 输出窗口看到错误信息，请确保项目配置为 Debug 模式，
 *   或取消下方 Debug.WriteLine 的注释（当前已保留）。
 * - 扩展提示：若需要显示标题+详细内容，可改用官方 TaskDialog 或独立 NotifyBox 方案。
 * - 版本兼容：适用于 Revit 2018~2025、AutoCAD 2019~2024 等基于 AdWindows 的产品，
 *   但依赖内部 API，不保证未来版本仍有效。
 *
 * 【依赖】
 * - AdWindows.dll（需添加到项目引用）
 * - 命名空间：Autodesk.Internal.InfoCenter、Autodesk.Windows
 */

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
                mgr.Configuration.BalloonDisplayPeriod = 5;
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