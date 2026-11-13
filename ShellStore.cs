using System.Text.Json;
using System.Text.Json.Serialization;

namespace DshOrbit;

/// <summary>一个搜索引擎：URL 模板里的 {q} 会被替换成关键词。</summary>
internal sealed class SearchEngine
{
    public string Name { get; set; } = "自定义";
    public string Template { get; set; } = "https://www.bing.com/search?q={q}";

    /// <summary>内置图标 key（baidu / bing / google）；自定义引擎为空，用首字母方块。</summary>
    public string Icon { get; set; } = "";
}

/// <summary>新标签页里展示的一条访问记录。</summary>
internal sealed class HistoryEntry
{
    public string Title { get; set; } = "";
    public string Url { get; set; } = "";
    public DateTime VisitedAt { get; set; } = DateTime.Now;
}

/// <summary>外壳的持久化状态：搜索引擎列表 + 访问记录。</summary>
internal sealed class ShellState
{
    public List<SearchEngine> Engines { get; set; } = new();
    public string ActiveEngine { get; set; } = "必应";
    public List<HistoryEntry> History { get; set; } = new();

    /// <summary>新标签页的自定义背景图（绝对路径，空 = 默认渐变）。</summary>
    public string? BackgroundPath { get; set; }

    public static ShellState CreateDefault()
    {
        return new ShellState
        {
            Engines =
            {
                new SearchEngine { Name = "百度", Template = "https://www.baidu.com/s?wd={q}", Icon = "baidu" },
                new SearchEngine { Name = "必应", Template = "https://www.bing.com/search?q={q}", Icon = "bing" },
                new SearchEngine { Name = "谷歌", Template = "https://www.google.com/search?q={q}", Icon = "google" },
            },
            ActiveEngine = "必应",
        };
    }
}

/// <summary>读写 <c>%LOCALAPPDATA%\DSH-Orbit\shell.json</c>。</summary>
internal static class ShellStore
{
    private static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    public static ShellState Load()
    {
        try
        {
            if (!File.Exists(AppPaths.ShellFile))
            {
                return ShellState.CreateDefault();
            }

            var json = File.ReadAllText(AppPaths.ShellFile);
            var state = JsonSerializer.Deserialize<ShellState>(json, Options) ?? ShellState.CreateDefault();
            if (state.Engines.Count == 0)
            {
                state.Engines = ShellState.CreateDefault().Engines;
            }

            // 迁移：早期版本会把 DSH 自己的回环地址写进历史，这里清掉。
            var removed = state.History.RemoveAll(h =>
                Uri.TryCreate(h.Url, UriKind.Absolute, out var uri) && uri.IsLoopback);
            if (removed > 0)
            {
                Save(state);
                AppPaths.Log($"清理了 {removed} 条 DSH 自身的访问记录");
            }

            return state;
        }
        catch (Exception ex)
        {
            AppPaths.Log($"读取 shell.json 失败：{ex.Message}");
            return ShellState.CreateDefault();
        }
    }

    public static void Save(ShellState state)
    {
        try
        {
            var json = JsonSerializer.Serialize(state, Options);
            File.WriteAllText(AppPaths.ShellFile, json);
        }
        catch (Exception ex)
        {
            AppPaths.Log($"写入 shell.json 失败：{ex.Message}");
        }
    }
}
