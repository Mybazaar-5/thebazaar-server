using System;
using System.Collections.Generic;

namespace BazaarModSdk;

/// <summary>
/// 主菜单原生导航抽屉入口描述符（挂载于 NavigationMenuController）。
/// </summary>
public sealed class NativeNavEntryDescriptor
{
    /// <summary>入口唯一 ID。</summary>
    public string Id { get; set; } = string.Empty;

    /// <summary>按钮显示中文标题。</summary>
    public string Title { get; set; } = string.Empty;

    /// <summary>副标题或悬浮提示。</summary>
    public string Tooltip { get; set; } = string.Empty;

    /// <summary>排序权重（数值越小越靠前）。</summary>
    public int Order { get; set; } = 100;

    /// <summary>保留哨兵场景号（需 &gt;= 9001，留 0 则由宿主自动分配）。</summary>
    public int SentinelSceneId { get; set; }

    /// <summary>点击该导航入口后的回调。</summary>
    public Action? OnClick { get; set; }
}

/// <summary>
/// 主大厅模式/预设直达选择器条目描述符（挂载于 PlaymodeSelectionViewComponent）。
/// </summary>
public sealed class NativePlaymodeOptionDescriptor
{
    /// <summary>唯一选项 ID（例如 classic / enhanced / endless_tower / cross_hero / mutator_daily / boss_rush / sandbox）。</summary>
    public string Id { get; set; } = string.Empty;

    /// <summary>显示标题。</summary>
    public string Title { get; set; } = string.Empty;

    /// <summary>简短模式徽章或副标题。</summary>
    public string Subtitle { get; set; } = string.Empty;

    /// <summary>关联玩法模式 ID（用于同步服务端 /custom-rules/me/mode/{id}）。</summary>
    public string ModeId { get; set; } = "Standard";

    /// <summary>关联预设档位 ID（用于同步服务端 /custom-rules/me/preset/{id}）。</summary>
    public string PresetId { get; set; } = "PureOfficial";

    /// <summary>排序权重。</summary>
    public int Order { get; set; } = 100;

    /// <summary>玩家点击切换选中后的回调。</summary>
    public Action? OnSelected { get; set; }
}

/// <summary>
/// 局内 ESC 暂停菜单原生按钮描述符（挂载于 FightMenuDialog）。
/// </summary>
public sealed class NativeMenuButtonDescriptor
{
    /// <summary>按钮唯一 ID。</summary>
    public string Id { get; set; } = string.Empty;

    /// <summary>按钮显示文本。</summary>
    public string Label { get; set; } = string.Empty;

    /// <summary>排序权重。</summary>
    public int Order { get; set; } = 100;

    /// <summary>是否仅在对局中可见（true = 仅局内 ESC 显示，false = 全局可用）。</summary>
    public bool InRunOnly { get; set; }

    /// <summary>动态判定当前是否可用/显示（可选）。</summary>
    public Func<bool>? IsVisible { get; set; }

    /// <summary>点击回调。</summary>
    public Action? OnClick { get; set; }
}

/// <summary>
/// 原生设置页分栏描述符（挂载于 OptionsDialogController）。
/// </summary>
public sealed class NativeOptionsSectionDescriptor
{
    /// <summary>分栏唯一 ID。</summary>
    public string Id { get; set; } = string.Empty;

    /// <summary>分栏标题。</summary>
    public string Title { get; set; } = string.Empty;

    /// <summary>排序权重。</summary>
    public int Order { get; set; } = 100;

    /// <summary>点击设置分栏入口时的动作（例如打开对应设置页签）。</summary>
    public Action? OnOpen { get; set; }
}

/// <summary>
/// 工坊/Mod 中心自定义页签描述符，允许第三方 Mod 向主界面注入独立原生级页签。
/// </summary>
public sealed class NativeWorkshopTabDescriptor
{
    /// <summary>页签唯一 ID。</summary>
    public string Id { get; set; } = string.Empty;

    /// <summary>页签标题。</summary>
    public string Title { get; set; } = string.Empty;

    /// <summary>排序权重。</summary>
    public int Order { get; set; } = 100;

    /// <summary>
    /// 构建页签内容体的回调；参数为容器对象（运行时为 UnityEngine.RectTransform，不强耦合 Unity 类型）。
    /// </summary>
    public Action<object>? BuildBody { get; set; }

    /// <summary>切入该页签时的刷新回调。</summary>
    public Action? OnRefresh { get; set; }
}

/// <summary>
/// 原生界面深度嵌入宿主契约：允许内置模块与第三方 Mod 将入口自然嵌入主大厅、导航抽屉、局内 ESC、设置页与工坊中心。
/// </summary>
public interface INativeUiHost
{
    /// <summary>向全局左侧导航抽屉（NavigationMenuController）注册原生入口。</summary>
    IDisposable RegisterNavDrawerEntry(NativeNavEntryDescriptor descriptor);

    /// <summary>向主大厅模式选择区（PlaymodeSelectionViewComponent）注册模式/预设快捷项。</summary>
    IDisposable RegisterPlaymodeOption(NativePlaymodeOptionDescriptor descriptor);

    /// <summary>向局内 ESC 暂停菜单（FightMenuDialog）注册原生风格按钮。</summary>
    IDisposable RegisterEscapeMenuButton(NativeMenuButtonDescriptor descriptor);

    /// <summary>向原生设置页（OptionsDialogController）注册分栏入口。</summary>
    IDisposable RegisterOptionsSection(NativeOptionsSectionDescriptor descriptor);

    /// <summary>向原生工坊与综合中心注册扩展页签。</summary>
    IDisposable RegisterWorkshopTab(NativeWorkshopTabDescriptor descriptor);

    /// <summary>打开受原生弹窗栈与 ESC 协调托管的弹窗（回调参数为容器对象）。</summary>
    void OpenNativePopup(string popupId, Action<object>? buildContent = null);

    /// <summary>关闭指定弹窗。</summary>
    void CloseNativePopup(string popupId);

    /// <summary>在游戏内原生风格通知条展示轻提示消息。</summary>
    void ShowToast(string message, bool isError = false);

    /// <summary>只读枚举所有已注册的导航抽屉入口。</summary>
    IReadOnlyList<NativeNavEntryDescriptor> GetRegisteredNavEntries();

    /// <summary>只读枚举所有已注册的主大厅模式选项。</summary>
    IReadOnlyList<NativePlaymodeOptionDescriptor> GetRegisteredPlaymodeOptions();

    /// <summary>只读枚举所有已注册的局内 ESC 菜单按钮。</summary>
    IReadOnlyList<NativeMenuButtonDescriptor> GetRegisteredEscapeMenuButtons();

    /// <summary>只读枚举所有已注册的工坊扩展页签。</summary>
    IReadOnlyList<NativeWorkshopTabDescriptor> GetRegisteredWorkshopTabs();
}
