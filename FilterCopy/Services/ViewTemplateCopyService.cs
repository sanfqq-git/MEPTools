using System;
using System.Collections.Generic;
using System.Linq;
using Autodesk.Revit.DB;
using MEPTools.FilterCopy.Models;

namespace MEPTools.FilterCopy.Services
{
    /// <summary>一个可复制的视图样板（纯数据，供列表绑定）。</summary>
    public class ViewTemplateInfo
    {
        public Document SourceDocument { get; set; }
        public ElementId TemplateId { get; set; }
        public string TemplateName { get; set; }
        public string ViewTypeName { get; set; }
        public int FilterCount { get; set; }
        public bool IsSelected { get; set; }
    }

    /// <summary>
    /// 跨文档复制视图样板：
    /// 1) 列当前会话所有打开的文档（排除当前文档 / 族文档 / 链接文档）；
    /// 2) 列该文档里全部 IsTemplate 的视图；
    /// 3) ElementTransformUtils.CopyElements(源文档 → 当前文档) 复制样板本体；
    /// 4) 把样板上的过滤器对齐回来：目标文档没同名过滤器就先跨文档复制过滤器，
    ///    再对新样板 AddFilter + SetFilterVisibility + SetFilterOverrides。
    /// 异常隔离到“单个样板 / 单个过滤器”粒度，单个失败不影响其它。
    /// </summary>
    public class ViewTemplateCopyService
    {
        private readonly Document _targetDoc;

        private sealed class FilterSnap
        {
            public ElementId Id;
            public string Name;
            public bool IsVisible;
            public OverrideGraphicSettings Overrides;
        }

        public ViewTemplateCopyService(Document targetDoc)
        {
            _targetDoc = targetDoc;
        }

        // ==================== 1. 源文档 / 样板列表 ====================

        /// <summary>当前会话里所有可作为样板来源的文档（当前文档除外）。</summary>
        public List<Document> CollectSourceDocuments()
        {
            var list = new List<Document>();
            try
            {
                var app = _targetDoc == null ? null : _targetDoc.Application;
                if (app == null) return list;

                foreach (Document d in app.Documents)
                {
                    if (d == null || d.IsLinked || d.IsFamilyDocument) continue;
                    if (IsSameDocument(d, _targetDoc)) continue;
                    if (string.IsNullOrEmpty(d.Title)) continue;
                    list.Add(d);
                }

                list.Sort((a, b) => string.Compare(a.Title, b.Title, StringComparison.OrdinalIgnoreCase));
            }
            catch (Exception)
            {
                // 枚举失败就返回已收集到的部分
            }

            return list;
        }

        private static bool IsSameDocument(Document a, Document b)
        {
            if (a == null || b == null) return false;
            if (ReferenceEquals(a, b)) return true;
            try
            {
                if (a.Equals(b)) return true;
            }
            catch (Exception)
            {
                // 忽略
            }

            string pa = null, pb = null;
            try
            {
                pa = a.PathName;
                pb = b.PathName;
            }
            catch (Exception)
            {
                // 忽略
            }

            return !string.IsNullOrEmpty(pa) && !string.IsNullOrEmpty(pb) &&
                   string.Equals(pa, pb, StringComparison.OrdinalIgnoreCase);
        }

        /// <summary>收集指定文档里的全部视图样板。</summary>
        public List<ViewTemplateInfo> CollectTemplates(Document sourceDoc)
        {
            var list = new List<ViewTemplateInfo>();
            if (sourceDoc == null) return list;

            try
            {
                foreach (View v in new FilteredElementCollector(sourceDoc)
                             .OfClass(typeof(View))
                             .Cast<View>()
                             .Where(v => v != null && v.IsTemplate)
                             .OrderBy(v => v.Name, StringComparer.OrdinalIgnoreCase))
                {
                    int filterCount = 0;
                    try
                    {
                        filterCount = v.GetFilters().Count;
                    }
                    catch (Exception)
                    {
                        filterCount = 0;
                    }

                    list.Add(new ViewTemplateInfo
                    {
                        SourceDocument = sourceDoc,
                        TemplateId = v.Id,
                        TemplateName = v.Name,
                        ViewTypeName = v.ViewType.ToString(),
                        FilterCount = filterCount,
                        IsSelected = false
                    });
                }
            }
            catch (Exception)
            {
                // 列不出来就返回空列表，由 UI 提示
            }

            return list;
        }

        // ==================== 2. 复制（一个事务内完成） ====================

        public TransferResult CopyTemplates(IList<ViewTemplateInfo> templates)
        {
            var result = new TransferResult();
            if (templates == null || templates.Count == 0 || _targetDoc == null) return result;

            HashSet<string> usedNames = CollectTemplateNames(_targetDoc);
            var filterMap = CollectFilters(_targetDoc);

            using (var tx = new Transaction(_targetDoc, "复制视图样板"))
            {
                tx.Start();
                bool committed = false;
                try
                {
                    foreach (ViewTemplateInfo info in templates)
                    {
                        try
                        {
                            CopyOne(info, usedNames, filterMap, result);
                        }
                        catch (Exception ex)
                        {
                            result.FailCount++;
                            result.Log.Add("[样板失败] " + SafeName(info) + " — " + ex.Message);
                        }
                    }

                    tx.Commit();
                    committed = true;
                }
                catch (Exception ex)
                {
                    result.Log.Add("[事务失败] 复制视图样板提交失败，已回滚 — " + ex.Message);
                }
                finally
                {
                    if (!committed)
                    {
                        try
                        {
                            if (tx.GetStatus() == TransactionStatus.Started) tx.RollBack();
                        }
                        catch (Exception)
                        {
                            // 忽略
                        }
                    }
                }
            }

            return result;
        }

        private void CopyOne(ViewTemplateInfo info,
                             HashSet<string> usedNames,
                             Dictionary<string, ElementId> filterMap,
                             TransferResult result)
        {
            Document srcDoc = info.SourceDocument;
            Document dstDoc = _targetDoc;

            View srcTpl = srcDoc == null ? null : srcDoc.GetElement(info.TemplateId) as View;
            if (srcTpl == null || !srcTpl.IsTemplate)
            {
                result.FailCount++;
                result.Log.Add("[样板失败] " + SafeName(info) + " — 源文档里找不到该样板（可能已被删除）。");
                return;
            }

            List<FilterSnap> snaps = ReadFilterSnaps(srcDoc, srcTpl);

            // 跨文档复制样板本体
            ICollection<ElementId> copied = ElementTransformUtils.CopyElements(
                srcDoc, new List<ElementId> { info.TemplateId }, dstDoc, Transform.Identity, new CopyPasteOptions());

            View newTpl = null;
            if (copied != null)
            {
                // 复制可能连带生成依赖元素，所以按类型挑出真正的新样板，不盲取第一个
                foreach (ElementId id in copied)
                {
                    View v = dstDoc.GetElement(id) as View;
                    if (v != null && v.IsTemplate)
                    {
                        newTpl = v;
                        break;
                    }
                }
            }

            if (newTpl == null)
            {
                result.FailCount++;
                result.Log.Add("[样板失败] " + SafeName(info) + " — 复制后没有拿到新样板元素。");
                return;
            }

            EnsureUniqueName(newTpl, usedNames);   // 当前文档已有同名样板时给副本加后缀

            int applied = ApplyFilters(srcDoc, dstDoc, newTpl, snaps, filterMap, result);

            result.SuccessCount++;
            result.Log.Add("[样板已复制] " + DocTitle(srcDoc) + " / " + srcTpl.Name +
                           " → 当前文档「" + newTpl.Name + "」，过滤器 " + applied + " 个。");
        }

        // ==================== 3. 过滤器对齐 ====================

        private static List<FilterSnap> ReadFilterSnaps(Document srcDoc, View srcTpl)
        {
            var snaps = new List<FilterSnap>();
            ICollection<ElementId> ids;
            try
            {
                ids = srcTpl.GetFilters();
            }
            catch (Exception)
            {
                return snaps;
            }

            foreach (ElementId id in ids)
            {
                try
                {
                    var fe = srcDoc.GetElement(id) as FilterElement;
                    snaps.Add(new FilterSnap
                    {
                        Id = id,
                        Name = fe == null || string.IsNullOrEmpty(fe.Name) ? id.ToString() : fe.Name,
                        IsVisible = srcTpl.GetFilterVisibility(id),
                        // OverrideGraphicSettings 是引用类型，必须 new 副本
                        Overrides = new OverrideGraphicSettings(srcTpl.GetFilterOverrides(id))
                    });
                }
                catch (Exception)
                {
                    // 单个过滤器读不到就跳过
                }
            }

            return snaps;
        }

        private static int ApplyFilters(Document srcDoc, Document dstDoc, View newTpl,
                                        List<FilterSnap> snaps,
                                        Dictionary<string, ElementId> filterMap,
                                        TransferResult result)
        {
            if (snaps.Count == 0) return 0;

            var applied = new List<ElementId>();
            try
            {
                applied.AddRange(newTpl.GetFilters());
            }
            catch (Exception)
            {
                // 忽略
            }

            foreach (FilterSnap snap in snaps)
            {
                try
                {
                    ElementId dstFilterId = filterMap.ContainsKey(snap.Name)
                        ? filterMap[snap.Name]
                        : FindFilterByName(dstDoc, snap.Name);

                    // 目标文档没有同名过滤器 → 跨文档复制一个过来
                    if (dstFilterId == ElementId.InvalidElementId)
                        dstFilterId = CopyFilter(srcDoc, dstDoc, snap.Id);

                    if (dstFilterId == ElementId.InvalidElementId)
                    {
                        result.Log.Add("[过滤器跳过] " + newTpl.Name + " / " + snap.Name +
                                       " — 当前文档里无法建立同名过滤器。");
                        continue;
                    }

                    filterMap[snap.Name] = dstFilterId;

                    if (!applied.Contains(dstFilterId))
                    {
                        newTpl.AddFilter(dstFilterId);
                        applied.Add(dstFilterId);
                    }

                    newTpl.SetFilterVisibility(dstFilterId, snap.IsVisible);
                    if (snap.Overrides != null)
                        newTpl.SetFilterOverrides(dstFilterId, new OverrideGraphicSettings(snap.Overrides));
                }
                catch (Exception ex)
                {
                    // 单个过滤器失败不影响其它过滤器
                    result.Log.Add("[过滤器失败] " + newTpl.Name + " / " + snap.Name + " — " + ex.Message);
                }
            }

            return applied.Count;
        }

        private static ElementId CopyFilter(Document srcDoc, Document dstDoc, ElementId srcFilterId)
        {
            try
            {
                ICollection<ElementId> copied = ElementTransformUtils.CopyElements(
                    srcDoc, new List<ElementId> { srcFilterId }, dstDoc, Transform.Identity, new CopyPasteOptions());

                if (copied != null)
                {
                    foreach (ElementId id in copied)
                    {
                        var fe = dstDoc.GetElement(id) as FilterElement;
                        if (fe != null) return fe.Id;
                    }
                }
            }
            catch (Exception)
            {
                // 类别缺失 / 参数不可用：由调用方记日志
            }

            return ElementId.InvalidElementId;
        }

        // ==================== 4. 小工具 ====================

        private static Dictionary<string, ElementId> CollectFilters(Document doc)
        {
            var map = new Dictionary<string, ElementId>(StringComparer.OrdinalIgnoreCase);
            if (doc == null) return map;

            try
            {
                foreach (FilterElement fe in new FilteredElementCollector(doc)
                             .OfClass(typeof(FilterElement))
                             .Cast<FilterElement>())
                {
                    if (fe == null || string.IsNullOrEmpty(fe.Name)) continue;
                    map[fe.Name] = fe.Id;
                }
            }
            catch (Exception)
            {
                // 忽略
            }

            return map;
        }

        private static ElementId FindFilterByName(Document doc, string name)
        {
            if (doc == null || string.IsNullOrEmpty(name)) return ElementId.InvalidElementId;

            try
            {
                foreach (FilterElement fe in new FilteredElementCollector(doc)
                             .OfClass(typeof(FilterElement))
                             .Cast<FilterElement>())
                {
                    if (fe == null) continue;
                    if (string.Equals(fe.Name, name, StringComparison.OrdinalIgnoreCase)) return fe.Id;
                }
            }
            catch (Exception)
            {
                // 忽略
            }

            return ElementId.InvalidElementId;
        }

        private static HashSet<string> CollectTemplateNames(Document doc)
        {
            var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            try
            {
                foreach (View v in new FilteredElementCollector(doc)
                             .OfClass(typeof(View))
                             .Cast<View>()
                             .Where(v => v != null && v.IsTemplate))
                {
                    names.Add(v.Name);
                }
            }
            catch (Exception)
            {
                // 忽略
            }

            return names;
        }

        private static void EnsureUniqueName(View tpl, HashSet<string> usedNames)
        {
            string baseName = tpl.Name;
            if (!usedNames.Contains(baseName))
            {
                usedNames.Add(baseName);
                return;
            }

            for (int i = 2; i < 1000; i++)
            {
                string candidate = baseName + " (" + i + ")";
                if (usedNames.Contains(candidate)) continue;

                try
                {
                    tpl.Name = candidate;
                    usedNames.Add(candidate);
                    return;
                }
                catch (Exception)
                {
                    // 换下一个后缀
                }
            }
        }

        private static string SafeName(ViewTemplateInfo info)
        {
            if (info == null) return "?";
            return string.IsNullOrEmpty(info.TemplateName) ? "?" : info.TemplateName;
        }

        private static string DocTitle(Document doc)
        {
            if (doc == null) return "?";
            try
            {
                return string.IsNullOrEmpty(doc.Title) ? "?" : doc.Title;
            }
            catch (Exception)
            {
                return "?";
            }
        }
    }
}
