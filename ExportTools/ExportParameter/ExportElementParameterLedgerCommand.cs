
using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows.Forms;
using Autodesk.Revit.Attributes;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using MEPTools.ExportTools.Common;
using MEPTools.ExportTools.Common.Excel;
using MEPTools.Shared;

namespace MEPTools.ExportTools.ExportParameter
{
    /// <summary>
    /// A. 图元参数台账（宽表：每个参数一列，含类型参数），一个 Excel / 一个 Sheet。
    ///
    ///  - 勾选类别 → 该类别的全部实例图元各占一行
    ///  - 列 = 主键 | 类别 | 族 | 类型 + 实例参数列 + 类型参数列（类型参数列名带「（类型）」后缀，避免同名冲突）
    ///  - 多个类别参数不一致时取并集，缺的留空
    /// 只读，不开启写事务。
    /// </summary>
    [Transaction(TransactionMode.Manual)]
    public class ExportElementParameterLedgerCommand : IExternalCommand
    {
        private const string ColumnKey = "主键";
        private const string ColumnCategory = "类别";
        private const string ColumnFamily = "族";
        private const string ColumnType = "类型";
        private const string TypeSuffix = "（类型）";

        public Result Execute(ExternalCommandData commandData, ref string message, ElementSet elements)
        {
            UIApplication uiapp = commandData.Application;
            Document doc = uiapp.ActiveUIDocument?.Document;
            if (doc == null) return Result.Cancelled;

            MEPLogger.Instance.StartSession("导出图元参数台账");

            try
            {
                List<ExportListItem> items = new List<ExportListItem>();
                foreach (Category category in doc.Settings.Categories)
                {
                    if (category == null || string.IsNullOrWhiteSpace(category.Name)) continue;
                    if (category.CategoryType != CategoryType.Model && category.CategoryType != CategoryType.Annotation) continue;
                    items.Add(new ExportListItem
                    {
                        Key = category.Id.IntegerValue.ToString(),
                        Name = category.Name,
                        Info = category.CategoryType.ToString()
                    });
                }

                if (items.Count == 0)
                {
                    TaskDialog.Show("图元参数台账", "没有可选的类别。");
                    return Result.Cancelled;
                }

                List<string> keys;
                string folder;
                using (var form = new ExportListForm(items, "选择要导出台账的类别",
                    "宽表：每个图元一行、每个参数一列（含类型参数）；类别建议一次别选太多，列会变很宽",
                    false, LinkSelectionForm.GetDefaultOutputFolder()))
                {
                    if (form.ShowDialog() != DialogResult.OK) return Result.Cancelled;
                    keys = form.SelectedKeys;
                    folder = form.OutputFolder;
                }

                // 1) 收集图元
                List<Element> elementList = new List<Element>();
                foreach (string key in keys)
                {
                    int categoryValue;
                    if (!int.TryParse(key, out categoryValue)) continue;
                    try
                    {
                        var collector = new FilteredElementCollector(doc)
                            .OfCategory((BuiltInCategory)categoryValue)
                            .WhereElementIsNotElementType();
                        elementList.AddRange(collector);
                    }
                    catch
                    {
                        // 该类别不支持按 BuiltInCategory 取，跳过
                    }
                }

                if (elementList.Count == 0)
                {
                    TaskDialog.Show("图元参数台账", "勾选的类别里没有图元。");
                    return Result.Cancelled;
                }

                // 2) 先扫一遍，求出列并集（实例参数名 / 类型参数名）
                HashSet<string> instanceColumns = new HashSet<string>(StringComparer.Ordinal);
                HashSet<string> typeColumns = new HashSet<string>(StringComparer.Ordinal);

                foreach (Element element in elementList)
                {
                    CollectParameterNames(element, instanceColumns);
                    ElementType elementType = doc.GetElement(element.GetTypeId()) as ElementType;
                    if (elementType != null) CollectParameterNames(elementType, typeColumns);
                }

                List<string> orderedInstance = instanceColumns.OrderBy(n => n).ToList();
                List<string> orderedType = typeColumns.OrderBy(n => n).ToList();

                SimpleTable table = new SimpleTable(ColumnKey, ColumnCategory, ColumnFamily, ColumnType);
                table.SheetName = "图元参数台账";

                foreach (string name in orderedInstance) table.AddColumn(name);
                foreach (string name in orderedType) table.AddColumn(name + TypeSuffix);

                // 3) 逐图元填值
                foreach (Element element in elementList.OrderBy(e => e.Category != null ? e.Category.Name : string.Empty).ThenBy(e => e.Id.IntegerValue))
                {
                    List<string> row = new List<string>();
                    row.Add(element.Id.IntegerValue.ToString());
                    row.Add(element.Category != null ? element.Category.Name : string.Empty);
                    row.Add(GetFamilyName(doc, element));
                    if (element is ElementType)
                    {
                        row.Add(((ElementType)element).Name);
                    }
                    else
                    {
                        ElementType elementType = doc.GetElement(element.GetTypeId()) as ElementType;
                        row.Add(elementType != null ? elementType.Name : string.Empty);
                    }

                    Dictionary<string, string> instanceValues = ReadParameterValues(element);
                    foreach (string name in orderedInstance) row.Add(instanceValues.ContainsKey(name) ? instanceValues[name] : string.Empty);

                    Dictionary<string, string> typeValues = new Dictionary<string, string>(StringComparer.Ordinal);
                    ElementType type = doc.GetElement(element.GetTypeId()) as ElementType;
                    if (type != null) typeValues = ReadParameterValues(type);
                    foreach (string name in orderedType) row.Add(typeValues.ContainsKey(name) ? typeValues[name] : string.Empty);

                    table.AddRow(row.ToArray());
                }

                string fileName = "图元参数台账_" + DateTime.Now.ToString("yyyyMMdd_HHmm") + ".xlsx";
                string path = ExportFileUtils.GetUniquePath(folder, fileName);
                XlsxTableStore.Save(path, table);

                MEPLogger.Instance.EndSession($"Done: Elements={table.Rows.Count}");
                TaskDialog.Show("图元参数台账",
                    $"已导出 {table.Rows.Count} 个图元，{table.ColumnCount} 列。\n\n📁 {path}");
                return Result.Succeeded;
            }
            catch (Exception ex)
            {
                MEPLogger.Instance.LogException("ExportElementParameterLedger", ex);
                InfoCenterTip.Show("图元参数台账导出失败，请查看日志。");
                return Result.Failed;
            }
        }

        private static void CollectParameterNames(Element element, HashSet<string> names)
        {
            if (element == null) return;
            try
            {
                foreach (Parameter parameter in element.GetOrderedParameters())
                {
                    if (parameter == null || parameter.Definition == null) continue;
                    string name = parameter.Definition.Name;
                    if (!string.IsNullOrWhiteSpace(name)) names.Add(name);
                }
            }
            catch
            {
                // 个别图元读参数会抛异常，跳过
            }
        }

        private static Dictionary<string, string> ReadParameterValues(Element element)
        {
            Dictionary<string, string> values = new Dictionary<string, string>(StringComparer.Ordinal);
            if (element == null) return values;
            try
            {
                foreach (Parameter parameter in element.GetOrderedParameters())
                {
                    if (parameter == null || parameter.Definition == null) continue;
                    string name = parameter.Definition.Name;
                    if (string.IsNullOrWhiteSpace(name)) continue;
                    values[name] = GetParameterValue(parameter);
                }
            }
            catch
            {
                // 忽略
            }
            return values;
        }

        private static string GetParameterValue(Parameter parameter)
        {
            if (parameter == null) return string.Empty;
            try
            {
                switch (parameter.StorageType)
                {
                    case StorageType.String:
                        return parameter.AsString() ?? string.Empty;
                    case StorageType.Integer:
                        string intText = parameter.AsValueString();
                        return string.IsNullOrEmpty(intText) ? parameter.AsInteger().ToString() : intText;
                    case StorageType.Double:
                        string doubleText = parameter.AsValueString();
                        return string.IsNullOrEmpty(doubleText) ? parameter.AsDouble().ToString("G") : doubleText;
                    case StorageType.ElementId:
                        string idText = parameter.AsValueString();
                        if (!string.IsNullOrEmpty(idText)) return idText;
                        ElementId id = parameter.AsElementId();
                        return id == null ? string.Empty : id.IntegerValue.ToString();
                    default:
                        return string.Empty;
                }
            }
            catch
            {
                return string.Empty;
            }
        }

        private static string GetFamilyName(Document doc, Element element)
        {
            try
            {
                ElementType elementType = doc.GetElement(element.GetTypeId()) as ElementType;
                return elementType != null && elementType.FamilyName != null ? elementType.FamilyName : string.Empty;
            }
            catch
            {
                return string.Empty;
            }
        }
    }
}
