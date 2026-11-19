﻿
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Autodesk.Revit.Attributes;
using Autodesk.Revit.ApplicationServices;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using MEPTools.ExportTools.Common;
using MEPTools.ExportTools.Common.Excel;
using MEPTools.Shared;

namespace MEPTools.ExportTools.ExportParameter
{
    /// <summary>
    /// B. 参数定义清单 → 一个 Excel，三张 Sheet：
    ///   1) 项目参数：项目里已绑定的参数（含绑定类别、实例/类型）
    ///   2) 共享参数：共享参数文件里的定义（含 GUID）
    ///   3) 族类型参数：项目中已载入族、各类型的参数定义（按 族 + 参数名 去重，不遍历族库、不开族文档）
    /// 只读，不开启写事务。
    /// </summary>
    [Transaction(TransactionMode.Manual)]
    public class ExportParameterDefinitionCommand : IExternalCommand
    {
        public Result Execute(ExternalCommandData commandData, ref string message, ElementSet elements)
        {
            UIApplication uiapp = commandData.Application;
            Document doc = uiapp.ActiveUIDocument?.Document;
            if (doc == null) return Result.Cancelled;

            MEPLogger.Instance.StartSession("导出参数定义清单");

            try
            {
                string folder = LinkSelectionForm.GetDefaultOutputFolder();
                List<SimpleTable> sheets = new List<SimpleTable>
                {
                    BuildProjectParameters(doc),
                    BuildSharedParameters(doc),
                    BuildFamilyTypeParameters(doc)
                };

                string fileName = "参数定义清单_" + DateTime.Now.ToString("yyyyMMdd_HHmm") + ".xlsx";
                string path = ExportFileUtils.GetUniquePath(folder, fileName);
                XlsxTableStore.Save(path, sheets);

                MEPLogger.Instance.EndSession($"Done: Sheets={sheets.Count}");
                TaskDialog.Show("参数定义清单",
                    $"已导出参数定义清单（项目参数 / 共享参数 / 族类型参数）。\n\n📁 {path}");
                return Result.Succeeded;
            }
            catch (Exception ex)
            {
                MEPLogger.Instance.LogException("ExportParameterDefinition", ex);
                InfoCenterTip.Show("参数定义清单导出失败，请查看日志。");
                return Result.Failed;
            }
        }

        // ---------- 1) 项目参数 ----------
        private static SimpleTable BuildProjectParameters(Document doc)
        {
            SimpleTable table = new SimpleTable("参数名", "数据类型", "参数分组", "绑定方式", "绑定类别");
            table.SheetName = "项目参数";

            DefinitionBindingMap map = doc.ParameterBindings;
            if (map == null) return table;

            DefinitionBindingMapIterator iterator = map.ForwardIterator();
            iterator.Reset();

            while (iterator.MoveNext())
            {
                Definition definition = iterator.Key;
                Binding binding = iterator.Current as Binding;
                if (definition == null) continue;

                string bindingType = "?";
                if (binding is InstanceBinding) bindingType = "实例";
                else if (binding is TypeBinding) bindingType = "类型";

                string categories = string.Empty;
                ElementBinding elementBinding = binding as ElementBinding;
                if (elementBinding != null && elementBinding.Categories != null)
                {
                    List<string> names = new List<string>();
                    foreach (Category category in elementBinding.Categories)
                        if (category != null) names.Add(category.Name);
                    categories = string.Join("、", names);
                }

                table.AddRow(
                    definition.Name,
                    GetDataTypeName(definition),
                    definition.ParameterGroup.ToString(),
                    bindingType,
                    categories);
            }
            return table;
        }

        // ---------- 2) 共享参数 ----------
        private static SimpleTable BuildSharedParameters(Document doc)
        {
            SimpleTable table = new SimpleTable("组", "参数名", "数据类型", "参数分组", "GUID", "说明");
            table.SheetName = "共享参数";

            Application application = doc.Application;
            string sharedFilePath = null;
            try
            {
                sharedFilePath = application.SharedParametersFilename;
            }
            catch
            {
                sharedFilePath = null;
            }

            table.AddRow("(共享参数文件)", sharedFilePath ?? "(未设置)", string.Empty, string.Empty, string.Empty, string.Empty);
            if (string.IsNullOrWhiteSpace(sharedFilePath) || !File.Exists(sharedFilePath)) return table;

            DefinitionFile definitionFile = null;
            try
            {
                definitionFile = application.OpenSharedParameterFile();
            }
            catch
            {
                definitionFile = null;
            }
            if (definitionFile == null) return table;

            foreach (DefinitionGroup group in definitionFile.Groups)
            {
                if (group == null) continue;
                foreach (Definition definition in group.Definitions)
                {
                    ExternalDefinition external = definition as ExternalDefinition;
                    table.AddRow(
                        group.Name,
                        definition.Name,
                        GetDataTypeName(definition),
                        definition.ParameterGroup.ToString(),
                        external != null ? external.GUID.ToString() : string.Empty,
                        external != null && external.Description != null ? external.Description : string.Empty);
                }
            }
            return table;
        }

        // ---------- 3) 族类型参数（已载入族，按 族+参数名 去重） ----------
        private static SimpleTable BuildFamilyTypeParameters(Document doc)
        {
            SimpleTable table = new SimpleTable("族名", "参数名", "数据类型", "来源", "涉及类型数");
            table.SheetName = "族类型参数";

            HashSet<string> seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            Dictionary<string, HashSet<string>> typeCountByKey = new Dictionary<string, HashSet<string>>(StringComparer.OrdinalIgnoreCase);

            var symbols = new FilteredElementCollector(doc).OfClass(typeof(FamilySymbol)).Cast<FamilySymbol>();
            foreach (FamilySymbol symbol in symbols)
            {
                string familyName = symbol.FamilyName ?? string.Empty;
                try
                {
                    foreach (Parameter parameter in symbol.GetOrderedParameters())
                    {
                        if (parameter == null || parameter.Definition == null) continue;
                        string paramName = parameter.Definition.Name;
                        if (string.IsNullOrWhiteSpace(paramName)) continue;

                        string key = familyName + "|" + paramName;
                        if (!seen.Contains(key))
                        {
                            seen.Add(key);
                            table.AddRow(
                                familyName,
                                paramName,
                                GetDataTypeName(parameter.Definition),
                                parameter.IsShared ? "共享参数" : "族参数",
                                "0");
                            typeCountByKey[key] = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                        }
                        typeCountByKey[key].Add(symbol.Name);
                    }
                }
                catch
                {
                    // 个别族读参数抛异常，跳过
                }
            }

            // 回填「涉及类型数」
            for (int i = 0; i < table.Rows.Count; i++)
            {
                string key = table.Cell(i, 0) + "|" + table.Cell(i, 1);
                if (typeCountByKey.ContainsKey(key))
                    table.Rows[i][4] = typeCountByKey[key].Count.ToString();
            }
            return table;
        }

        private static string GetDataTypeName(Definition definition)
        {
            if (definition == null) return string.Empty;
            try
            {
                ForgeTypeId dataType = definition.GetDataType();
                if (dataType != null && !string.IsNullOrWhiteSpace(dataType.TypeId)) return dataType.TypeId;
            }
            catch
            {
                // 个别定义没有数据类型，留空
            }
            return string.Empty;
        }
    }
}
