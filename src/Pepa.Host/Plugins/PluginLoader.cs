using System.Reflection;
using System.Runtime.Loader;
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

    /// <remarks>Журнал при загрузке ещё не готов: итоги пишет хост по <see cref="PluginCatalog"/>.</remarks>
    public static PluginCatalog Load(string directory, string dataDirectory, NodeRegistry registry, Func<string> workspaceRoot)
    {
        var plugins = new List<PluginInfo>();
        if (!System.IO.Directory.Exists(directory))
        {
            return new PluginCatalog(directory, plugins);
        }

        foreach (var folder in System.IO.Directory.GetDirectories(directory).Order(StringComparer.OrdinalIgnoreCase))
        {
            var id = Path.GetFileName(folder);
            plugins.Add(LoadOne(id, folder, Path.Combine(dataDirectory, "plugins-data", id), registry, workspaceRoot));
        }

        return new PluginCatalog(directory, plugins);
    }

    private static PluginInfo LoadOne(string id, string folder, string pluginData, NodeRegistry registry, Func<string> workspaceRoot)
    {
        PluginInfo Failed(string error, string? name = null, string? version = null) => new(id, name ?? id, version, folder, [], error);

        var files = System.IO.Directory.GetFiles(folder, "*.dll");
        if (files.Length == 0)
        {
            return Failed("в папке нет сборок (.dll).");
        }

        var context = new PluginLoadContext(folder);
        var found = new List<(IPepaPlugin Plugin, string? Version)>();
        try
        {
            foreach (var file in files.Order(StringComparer.OrdinalIgnoreCase))
            {
                var assemblyName = AssemblyName.GetAssemblyName(file);
                if (PluginLoadContext.IsShared(assemblyName))
                {
                    // Копия сборки Pepa или платформы рядом с расширением — используется общая.
                    continue;
                }

                var assembly = context.LoadFromAssemblyPath(file);
                foreach (var type in assembly.GetExportedTypes().Where(t => t is { IsClass: true, IsAbstract: false } && typeof(IPepaPlugin).IsAssignableFrom(t)))
                {
                    if (type.GetConstructor(Type.EmptyTypes) is null)
                    {
                        return Failed($"у класса {type.FullName} нет конструктора без параметров.");
                    }

                    found.Add(((IPepaPlugin)Activator.CreateInstance(type)!, assembly.GetName().Version?.ToString()));
                }
            }
        }
        catch (ReflectionTypeLoadException ex)
        {
            return Failed("не удалось загрузить типы: " + string.Join("; ", ex.LoaderExceptions.Where(e => e is not null).Select(e => e!.Message).Distinct()));
        }
        catch (Exception ex) when (ex is BadImageFormatException or FileLoadException or FileNotFoundException or TypeLoadException or TargetInvocationException
                                       or MissingMethodException)
        {
            return Failed($"не удалось загрузить: {(ex is TargetInvocationException { InnerException: { } inner } ? inner.Message : ex.Message)}");
        }

        if (found.Count == 0)
        {
            return Failed($"ни один класс не реализует {nameof(IPepaPlugin)} (Pepa.Core.Plugins).");
        }

        var registration = new PluginRegistration(id, pluginData, workspaceRoot);
        string name;
        try
        {
            foreach (var (plugin, _) in found)
            {
                plugin.Register(registration);
            }

            name = string.Join(", ", found.Select(f => f.Plugin.Name).Where(n => !string.IsNullOrWhiteSpace(n)).DefaultIfEmpty(id));
        }
        catch (Exception ex)
        {
            return Failed($"ошибка при подключении: {ex.Message}");
        }

        var version = found.Select(f => f.Version).FirstOrDefault(v => v is not null);
        string? problem;
        try
        {
            // Описания блоков — тоже код расширения: ошибка в нём — ошибка расширения, а не Pepa.
            problem = Problem(registration, registry);
        }
        catch (Exception ex)
        {
            problem = $"ошибка в описании блока: {ex.Message}";
        }

        if (problem is not null)
        {
            return Failed(problem, name, version);
        }

        foreach (var category in registration.Categories)
        {
            registry.RegisterCategory(category);
        }

        foreach (var node in registration.Nodes)
        {
            registry.Register(node);
        }

        return new PluginInfo(id, name, version, folder, registration.Nodes.Select(n => n.Descriptor.Type).ToList(), null);
    }

    /// <summary>Что не так с блоками расширения (null — всё в порядке).</summary>
    private static string? Problem(PluginRegistration registration, NodeRegistry registry)
    {
        if (registration.Nodes.Count == 0)
        {
            return "расширение не добавило ни одного блока.";
        }

        var categories = registry.Categories.Select(c => c.Id).Concat(registration.Categories.Select(c => c.Id)).ToHashSet(StringComparer.Ordinal);
        var types = new HashSet<string>(StringComparer.Ordinal);
        foreach (var node in registration.Nodes)
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

            if (!categories.Contains(descriptor.Category))
            {
                return $"блок «{type}»: раздела «{descriptor.Category}» нет — добавьте его через AddCategory.";
            }

            if (descriptor.Outputs.Count == 0)
            {
                return $"блок «{type}»: у блока нет выходов.";
            }
        }

        return null;
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
