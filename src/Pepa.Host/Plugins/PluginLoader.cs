using System.Reflection;
using System.Runtime.Loader;
using System.Text.Json.Nodes;
using Pepa.Core.Model;
using Pepa.Core.Nodes;
using Pepa.Core.Plugins;

namespace Pepa.Host.Plugins;

/// <summary>Расширение в «Настройках»: что загружено или почему нет.</summary>
/// <param name="Id">Имя папки расширения.</param>
/// <param name="Error">Расширение не загружено: причина. Его блоков нет, остальная Pepa работает.</param>
public sealed record PluginInfo(string Id, string Name, string? Version, string Folder, IReadOnlyList<string> NodeTypes, string? Error);

/// <summary>Загруженные расширения (для API).</summary>
public sealed class PluginCatalog(string directory, IReadOnlyList<PluginInfo> plugins)
{
    /// <summary>Папка, куда кладут расширения.</summary>
    public string Directory { get; } = directory;

    public IReadOnlyList<PluginInfo> Plugins { get; } = plugins;
}

/// <summary>
/// Загрузка расширений из папки plugins/&lt;имя&gt;/: каждое — в своём контексте загрузки (свои зависимости не мешают Pepa
/// и другим расширениям), но с общими сборками Pepa — иначе контракты (<see cref="IPepaPlugin"/>, блоки) не совпали бы.
/// Расширение принимается целиком или не принимается совсем: блок с занятым или неправильным типом — причина отказа.
/// </summary>
public static class PluginLoader
{
    /// <summary>Префиксы встроенных блоков: расширение не может выдать свой блок за встроенный.</summary>
    private static readonly string[] ReservedPrefixes =
    [
        "browser.", "adspower.", "telegram.", "desktop.", "file.", "folder.", "event.", "resource.", "scenario.", "loop.", "text.",
        "variable.", "test.",
    ];

    /// <remarks>
    /// Журнал при загрузке ещё не готов: итоги пишет хост по <see cref="PluginCatalog"/>. Ни одна ошибка расширения не
    /// мешает запуску Pepa: расширение с ошибкой не загружается, причина видна в «Настройках».
    /// </remarks>
    /// <param name="elevated">
    /// Pepa запущена от имени администратора: расширения не загружаются. Папка расширений — в данных пользователя, туда может
    /// положить сборку любая его программа, и её код получил бы права администратора.
    /// </param>
    public static PluginCatalog Load(string directory, string dataDirectory, NodeRegistry registry, Func<string> workspaceRoot,
        Func<string, string>? resolveInWorkspace = null, bool elevated = false)
    {
        var plugins = new List<PluginInfo>();
        string[] folders;
        try
        {
            folders = System.IO.Directory.Exists(directory) ? System.IO.Directory.GetDirectories(directory) : [];
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            plugins.Add(new PluginInfo("plugins", "Папка расширений", null, directory, [], $"папку не прочитать: {ex.Message}"));
            return new PluginCatalog(directory, plugins);
        }

        foreach (var folder in folders.Order(StringComparer.OrdinalIgnoreCase))
        {
            var id = Path.GetFileName(folder);
            if (elevated)
            {
                plugins.Add(new PluginInfo(id, id, null, folder, [],
                    "Pepa запущена от имени администратора — расширения не загружаются: сборку в папку расширений может положить любая " +
                    "программа без прав администратора. Чтобы пользоваться расширениями, запустите Pepa обычным образом."));
                continue;
            }

            try
            {
                plugins.Add(LoadOne(id, folder, Path.Combine(dataDirectory, "plugins-data", id), registry, workspaceRoot, resolveInWorkspace));
            }
            catch (Exception ex) when (!IsFatal(ex))
            {
                // Код расширения может бросить что угодно — это ошибка расширения, а не Pepa.
                plugins.Add(new PluginInfo(id, id, null, folder, [], $"не удалось загрузить: {ex.Message}"));
            }
        }

        return new PluginCatalog(directory, plugins);
    }

    private static bool IsFatal(Exception ex) => ex is OutOfMemoryException or StackOverflowException or AccessViolationException;

    private static PluginInfo LoadOne(string id, string folder, string pluginData, NodeRegistry registry, Func<string> workspaceRoot,
        Func<string, string>? resolveInWorkspace)
    {
        PluginInfo Failed(string error, string? name = null, string? version = null) => new(id, name ?? id, version, folder, [], error);

        var files = System.IO.Directory.GetFiles(folder, "*.dll");
        if (files.Length == 0)
        {
            return Failed("в папке нет сборок (.dll).");
        }

        var context = new PluginLoadContext(folder);
        var found = new List<(IPepaPlugin Plugin, string? Version)>();
        var skipped = new List<string>();
        try
        {
            foreach (var file in files.Order(StringComparer.OrdinalIgnoreCase))
            {
                var assemblyName = AssemblyName.GetAssemblyName(file);
                if (PluginLoadContext.IsShared(assemblyName))
                {
                    // Копия сборки Pepa или платформы рядом с расширением — используется общая.
                    skipped.Add(Path.GetFileName(file));
                    continue;
                }

                var assembly = context.LoadFromAssemblyPath(file);
                foreach (var type in assembly.GetExportedTypes().Where(t => t is { IsClass: true, IsAbstract: false } && typeof(IPepaPlugin).IsAssignableFrom(t)))
                {
                    if (type.ContainsGenericParameters || type.GetConstructor(Type.EmptyTypes) is null)
                    {
                        return Failed($"класс {type.FullName} нельзя создать: нужен обычный (не обобщённый) класс с конструктором без параметров.");
                    }

                    found.Add(((IPepaPlugin)Activator.CreateInstance(type)!, assembly.GetName().Version?.ToString()));
                }
            }
        }
        catch (ReflectionTypeLoadException ex)
        {
            return Failed("не удалось загрузить типы: " + string.Join("; ", ex.LoaderExceptions.Where(e => e is not null).Select(e => e!.Message).Distinct()));
        }
        catch (Exception ex) when (!IsFatal(ex))
        {
            return Failed($"не удалось загрузить: {(ex is TargetInvocationException { InnerException: { } inner } ? inner.Message : ex.Message)}");
        }

        if (found.Count == 0)
        {
            return Failed($"ни один класс не реализует {nameof(IPepaPlugin)} (Pepa.Core.Plugins)." +
                          (skipped.Count > 0 ? $" Пропущены сборки с именами сборок Pepa или .NET: {string.Join(", ", skipped)} — переименуйте сборку расширения." : ""));
        }

        return Accept(id, folder, pluginData, found, registry, workspaceRoot, resolveInWorkspace);
    }

    /// <summary>Подключить найденные классы расширения: регистрация, проверка описаний, добавление в реестр (всё или ничего).</summary>
    internal static PluginInfo Accept(string id, string folder, string pluginData, IReadOnlyList<(IPepaPlugin Plugin, string? Version)> found,
        NodeRegistry registry, Func<string> workspaceRoot, Func<string, string>? resolveInWorkspace)
    {
        PluginInfo Failed(string error, string? name = null, string? version = null) => new(id, name ?? id, version, folder, [], error);

        var registration = new PluginRegistration(id, pluginData, workspaceRoot, resolveInWorkspace);
        string name;
        List<PluginNode> nodes;
        try
        {
            foreach (var (plugin, _) in found)
            {
                plugin.Register(registration);
            }

            name = string.Join(", ", found.Select(f => f.Plugin.Name).Where(n => !string.IsNullOrWhiteSpace(n)).DefaultIfEmpty(id));
            // Описание блока читается один раз: проверяется и регистрируется одна и та же копия (свойство расширения могло бы
            // вернуть в следующий раз другое или бросить исключение).
            nodes = registration.Nodes.Select(n => new PluginNode(n ?? throw new InvalidOperationException("расширение добавило пустой блок"), n.Descriptor))
                .ToList();
        }
        catch (Exception ex) when (!IsFatal(ex))
        {
            return Failed($"ошибка при подключении: {ex.Message}");
        }

        var version = found.Select(f => f.Version).FirstOrDefault(v => v is not null);
        if (Problem(registration.Categories, nodes, registry) is { } problem)
        {
            return Failed(problem, name, version);
        }

        foreach (var category in registration.Categories)
        {
            registry.RegisterCategory(category);
        }

        foreach (var node in nodes)
        {
            registry.Register(node);
        }

        return new PluginInfo(id, name, version, folder, nodes.Select(n => n.Descriptor.Type).ToList(), null);
    }

    /// <summary>Что не так с разделами и блоками расширения (null — всё в порядке).</summary>
    private static string? Problem(IReadOnlyList<NodeCategory> newCategories, IReadOnlyList<PluginNode> nodes, NodeRegistry registry)
    {
        if (nodes.Count == 0)
        {
            return "расширение не добавило ни одного блока.";
        }

        var existing = registry.Categories.Select(c => c.Id).ToHashSet(StringComparer.Ordinal);
        var added = new HashSet<string>(StringComparer.Ordinal);
        foreach (var category in newCategories)
        {
            if (category is null || string.IsNullOrWhiteSpace(category.Id) || string.IsNullOrWhiteSpace(category.Title))
            {
                return "у раздела палитры не задан id или название.";
            }

            // Свой раздел не может заменить встроенный или чужой: палитра Pepa не должна меняться от расширения.
            if (existing.Contains(category.Id) || !added.Add(category.Id))
            {
                return $"раздел палитры «{category.Id}» уже есть в Pepa или в другом расширении — выберите свой id.";
            }
        }

        var types = new HashSet<string>(StringComparer.Ordinal);
        foreach (var node in nodes)
        {
            var descriptor = node.Descriptor;
            var type = descriptor?.Type ?? "";
            if (descriptor is null || type.Trim().Length == 0)
            {
                return "у блока не задан тип.";
            }

            if (!type.Contains('.', StringComparison.Ordinal) || type.StartsWith('.') || type.EndsWith('.'))
            {
                return $"тип блока «{type}» должен быть с префиксом через точку (например, «моя-компания.отчёт»).";
            }

            if (ReservedPrefixes.Any(p => type.StartsWith(p, StringComparison.OrdinalIgnoreCase)))
            {
                return $"тип блока «{type}» начинается с префикса встроенных блоков Pepa — выберите свой.";
            }

            if (registry.FindDescriptor(type) is not null || !types.Add(type))
            {
                return $"блок «{type}» уже есть в Pepa или в другом расширении.";
            }

            if (descriptor.IsStart || descriptor.IsEnd || descriptor.Flow != NodeFlow.Normal)
            {
                return $"блок «{type}»: расширения не могут добавлять «Начало», «Завершение» и блоки ветвления.";
            }

            if (string.IsNullOrWhiteSpace(descriptor.Title) || descriptor.Description is null)
            {
                return $"блок «{type}»: не задано название или описание.";
            }

            if (descriptor.Category is null || !(existing.Contains(descriptor.Category) || added.Contains(descriptor.Category)))
            {
                return $"блок «{type}»: раздела «{descriptor.Category}» нет — добавьте его через AddCategory.";
            }

            if (descriptor.Outputs is not { Count: > 0 } outputs)
            {
                return $"блок «{type}»: у блока нет выходов.";
            }

            if (outputs.Any(o => o is null || string.IsNullOrWhiteSpace(o.Id) || o.Label is null) ||
                outputs.Select(o => o.Id).Distinct(StringComparer.Ordinal).Count() != outputs.Count)
            {
                return $"блок «{type}»: у каждого выхода должен быть свой непустой id и подпись.";
            }

            var fields = descriptor.Fields ?? [];
            if (fields.Any(f => f is null || string.IsNullOrWhiteSpace(f.Name) || f.Label is null) ||
                fields.Select(f => f.Name).Distinct(StringComparer.Ordinal).Count() != fields.Count)
            {
                return $"блок «{type}»: у каждого поля должно быть своё непустое имя и подпись.";
            }

            if (fields.FirstOrDefault(f => f.Kind == FieldKind.Select && f.Options is not { Count: > 0 }) is { } select)
            {
                return $"блок «{type}»: у поля-списка «{select.Name}» нет вариантов.";
            }

            if (fields.FirstOrDefault(f => f.Options?.Any(o => o is null || o.Value is null || o.Label is null) == true) is { } broken)
            {
                return $"блок «{type}»: у поля «{broken.Name}» есть вариант без значения или подписи.";
            }

            if (descriptor.SummaryField is { } summary && fields.All(f => f.Name != summary))
            {
                return $"блок «{type}»: поля «{summary}» (SummaryField) у блока нет.";
            }
        }

        return null;
    }

    /// <summary>
    /// Блок расширения в реестре: описание — прочитанная и проверенная при загрузке копия; остальное — код расширения.
    /// Ошибка в его собственной проверке параметров — замечание проверки, а не сбой проверки всего сценария.
    /// </summary>
    private sealed class PluginNode(INodeExecutor inner, NodeTypeDescriptor descriptor) : INodeExecutor
    {
        public NodeTypeDescriptor Descriptor { get; } = descriptor;

        public IEnumerable<NodeConfigIssue> Validate(NodeDefinition node)
        {
            try
            {
                return inner.Validate(node).ToList();
            }
            catch (Exception ex) when (!IsFatal(ex))
            {
                return [new NodeConfigIssue($"Расширение не смогло проверить блок: {ex.Message}")];
            }
        }

        public ValueTask<JsonObject?> PrepareAsync(NodeExecutionContext context, CancellationToken cancellationToken) =>
            inner.PrepareAsync(context, cancellationToken);

        public ValueTask<NodeResult> ExecuteAsync(NodeExecutionContext context, CancellationToken cancellationToken) =>
            inner.ExecuteAsync(context, cancellationToken);

        public ValueTask<VerificationResult> VerifyAsync(NodeExecutionContext context, CancellationToken cancellationToken) =>
            inner.VerifyAsync(context, cancellationToken);

        public ResourceRequirement? RequiredResource(JsonObject input) => inner.RequiredResource(input);

        public TimeSpan? ExpectedDuration(JsonObject input) => inner.ExpectedDuration(input);
    }

    /// <summary>Свой контекст загрузки: зависимости расширения — из его папки, сборки Pepa и платформы — общие.</summary>
    private sealed class PluginLoadContext(string folder) : AssemblyLoadContext($"pepa-plugin:{Path.GetFileName(folder)}")
    {
        private static readonly HashSet<string> SharedNames = BuildShared();

        public static bool IsShared(AssemblyName name) => name.Name is { } simple && SharedNames.Contains(simple);

        protected override Assembly? Load(AssemblyName assemblyName)
        {
            if (IsShared(assemblyName))
            {
                return null;
            }

            var path = Path.Combine(folder, assemblyName.Name + ".dll");
            return File.Exists(path) ? LoadFromAssemblyPath(path) : null;
        }

        /// <summary>Сборки самой Pepa и платформы .NET (список доверенных сборок приложения).</summary>
        private static HashSet<string> BuildShared()
        {
            var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            if (AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES") is string list)
            {
                foreach (var path in list.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
                {
                    names.Add(Path.GetFileNameWithoutExtension(path));
                }
            }

            foreach (var assembly in Default.Assemblies)
            {
                if (assembly.GetName().Name is { } name)
                {
                    names.Add(name);
                }
            }

            return names;
        }
    }
}
