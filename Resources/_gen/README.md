# Resources 图标说明

## 命名规则（不能改）

`App.cs` 的 `ApplyIcon()` 是按 `Text` 字段拼文件名的，所以这里每张图的文件名必须严格等于
`RibbonConfig.cs` 里对应按钮的 `Text`：

    Text + "_32.png"   →  PushButton.LargeImage（Ribbon 大按钮）
    Text + "_16.png"   →  PushButton.Image     （下拉项 / 小按钮）

加载路径为 `pack://application:,,,/MEPTools;component/Resources/{文件名}`，
因此 `MEPTools.csproj` 里必须有：

    <Resource Include="Resources\*.png" />

改了按钮显示名（Text）就必须同时重命名这两张图，否则该按钮会静默没有图标（代码里 try/catch 掉了）。

## 重新生成 / 修改样式

图标全部由脚本矢量绘制（无位图抠图），改样式直接改 `_gen/make_icons.py` 里对应的绘制函数，
然后：

    cd Resources\_gen
    python make_icons.py --preview     # 重新生成 + 输出 preview_light.png / preview_dark.png
    python zoom.py "方形引线" "标记仅堆叠"   # 放大检查个别图标

依赖：Python 3 + Pillow（`python -m pip install pillow`）。

## 设计约定

- 底：圆角浅色卡片（深色 Ribbon 主题下也能看清轮廓）
- 蓝 = 主系统 / 选中；青绿 = 次系统 / 平面视图；灰 = 参考、非活动；红 = 删除 / 撤销；橙 = 锁定 / 权限
- 32px 内不写文字（两位数会糊成一片），角度靠几何形态 + 弧线表达
- 16px 版本自动去掉细节（描边、文字、多行 TAG），并把图形放大 1.14 倍
